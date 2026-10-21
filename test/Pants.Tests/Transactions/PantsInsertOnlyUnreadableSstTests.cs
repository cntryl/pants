using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Transactions;

public sealed class PantsInsertOnlyUnreadableSstTests
{
    [Fact]
    public async Task ShouldFailInsertCommitWhenExistenceCheckCannotReadSst()
    {
        await AssertInsertFailsClosedWhenSstCannotBeReadAsync(spill: false);
    }

    [Fact]
    public async Task ShouldFailSpilledInsertCommitWhenExistenceCheckCannotReadSst()
    {
        await AssertInsertFailsClosedWhenSstCannotBeReadAsync(spill: true);
    }

    static async Task AssertInsertFailsClosedWhenSstCannotBeReadAsync(bool spill)
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false);
        if (spill)
        {
            options = options
                .WithMemoryBudget(PantsMemoryBudget.FromBytes(4 * 1_024))
                .WithMemtableLimits(1_024)
                .WithTransactionMemoryPool(1_024);
        }

        await using var database = await PantsDatabase.OpenAsync(options);
        var family = database.ColumnFamilies.DefaultFamily;
        await using (var seed = await database.Transactions.BeginAsync(
                         family,
                         PantsTransactionMode.ReadWrite))
        {
            seed.Put("existing"u8.ToArray(), "original"u8.ToArray());
            await seed.CommitAsync(PantsWriteOptions.Sync);
        }

        await database.Maintenance.FlushAsync(family);
        var sstPath = Assert.Single(Directory.GetFiles(
            Path.Combine(directory.Path, "sst"),
            "*.sst"));
        var originalSst = await File.ReadAllBytesAsync(sstPath);
        await File.WriteAllBytesAsync(sstPath, Enumerable.Repeat((byte)0xA5, originalSst.Length).ToArray());

        await using var insert = await database.Transactions.BeginAsync(
            family,
            PantsTransactionMode.ReadWrite);
        insert.Insert(
            "existing"u8.ToArray(),
            spill ? new byte[4_096] : "replacement"u8.ToArray());

        if (spill)
        {
            Assert.NotEmpty(Directory.GetFiles(Path.Combine(directory.Path, "txn"), "*.run"));
        }

        var error = await Assert.ThrowsAnyAsync<PantsException>(() =>
            insert.CommitAsync(PantsWriteOptions.Sync).AsTask());

        Assert.Contains(error.Code, new[] { PantsErrorCode.Corruption, PantsErrorCode.Io });
        await File.WriteAllBytesAsync(sstPath, originalSst);
        await using var reader = await database.Transactions.BeginAsync(
            family,
            PantsTransactionMode.ReadOnly);
        Assert.Equal("original", TestBytes.ToText((await reader.GetAsync("existing"u8.ToArray()))!.Value));
    }
}

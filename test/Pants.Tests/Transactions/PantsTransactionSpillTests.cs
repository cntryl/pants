namespace Cntryl.Pants.Tests;

public sealed class PantsTransactionSpillTests
{
    [Fact]
    public async Task ShouldCommitAndCleanDurableSpillRuns()
    {
        using var directory = new TemporaryDirectory();
        await using IPantsDatabase database = await PantsDatabase.OpenAsync(
            CreateConstrainedOptions(PantsOpenOptions.Local(directory.Path)));
        await using IPantsTransaction transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        byte[] value = GC.AllocateUninitializedArray<byte>(900);
        Array.Fill(value, (byte)'v');
        for (int index = 0; index < 6; index++)
        {
            transaction.Put(TestBytes.FromString($"key-{index}"), value);
        }

        transaction.Put("point"u8.ToArray(), "before"u8.ToArray());
        transaction.Put("point"u8.ToArray(), "after"u8.ToArray());

        string transactionDirectory = Path.Combine(directory.Path, "txn");
        string[] runs = Directory.GetFiles(transactionDirectory, "*.run");
        Assert.NotEmpty(runs);
        Assert.All(runs, path => Assert.Equal("MDGTXN01", ReadMagic(path)));
        Assert.Equal("after", TestBytes.ToText((await transaction.GetAsync("point"u8.ToArray()))!.Value));

        await transaction.CommitAsync(PantsWriteOptions.Sync);

        Assert.False(Directory.Exists(transactionDirectory));
        await using IPantsTransaction reader = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Equal("after", TestBytes.ToText((await reader.GetAsync("point"u8.ToArray()))!.Value));
        Assert.Equal(value, (await reader.GetAsync("key-5"u8.ToArray()))!.Value.ToArray());
    }

    [Fact]
    public async Task ShouldRejectDuplicateInsertAcrossSpillRunsWithoutPublishing()
    {
        using var directory = new TemporaryDirectory();
        await using IPantsDatabase database = await PantsDatabase.OpenAsync(
            CreateConstrainedOptions(PantsOpenOptions.Local(directory.Path)));
        await using IPantsTransaction transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        byte[] value = new byte[900];
        transaction.Insert("duplicate"u8.ToArray(), value);
        transaction.Put("filler"u8.ToArray(), value);
        transaction.Insert("duplicate"u8.ToArray(), "second"u8.ToArray());

        var error = await Assert.ThrowsAsync<PantsInvalidArgumentException>(() =>
            transaction.CommitAsync(PantsWriteOptions.Sync).AsTask());

        Assert.Equal(PantsErrorCode.InvalidArgument, error.Code);
        await using IPantsTransaction reader = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Null(await reader.GetAsync("duplicate"u8.ToArray()));
    }

    [Fact]
    public async Task ShouldNotPartiallyPublishWhenSpillRunCannotBeRead()
    {
        using var directory = new TemporaryDirectory();
        await using IPantsDatabase database = await PantsDatabase.OpenAsync(
            CreateConstrainedOptions(PantsOpenOptions.Local(directory.Path)));
        await using IPantsTransaction transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        byte[] value = new byte[900];
        transaction.Put("first"u8.ToArray(), value);
        transaction.Put("second"u8.ToArray(), value);
        string run = Assert.Single(Directory.GetFiles(Path.Combine(directory.Path, "txn"), "*.run"));
        File.Delete(run);

        PantsException error = await Assert.ThrowsAnyAsync<PantsException>(() =>
            transaction.CommitAsync(PantsWriteOptions.Sync).AsTask());

        Assert.Equal(PantsErrorCode.Io, error.Code);
        await using IPantsTransaction reader = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Null(await reader.GetAsync("first"u8.ToArray()));
        Assert.Null(await reader.GetAsync("second"u8.ToArray()));
    }

    [Fact]
    public async Task ShouldRemoveOrphanedSpillRunsAfterAcquiringWriterLease()
    {
        using var directory = new TemporaryDirectory();
        await using (IPantsDatabase database = await PantsDatabase.OpenAsync(
                         CreateConstrainedOptions(PantsOpenOptions.Local(directory.Path))))
        {
        }

        string transactionDirectory = Path.Combine(directory.Path, "txn");
        Directory.CreateDirectory(transactionDirectory);
        await File.WriteAllTextAsync(Path.Combine(transactionDirectory, "orphan.run"), "orphan");

        await using IPantsDatabase reopened = await PantsDatabase.OpenAsync(
            CreateConstrainedOptions(PantsOpenOptions.Local(directory.Path)));

        Assert.False(Directory.Exists(transactionDirectory));
    }

    [Fact]
    public async Task ShouldReturnResourceLimitInsteadOfSpillingInMemoryMode()
    {
        await using IPantsDatabase database = await PantsDatabase.OpenAsync(
            CreateConstrainedOptions(PantsOpenOptions.InMemory()));
        await using IPantsTransaction transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        byte[] value = new byte[900];
        transaction.Put("first"u8.ToArray(), value);

        PantsException error = Assert.ThrowsAny<PantsException>(() =>
            transaction.Put("second"u8.ToArray(), value));

        Assert.Equal(PantsErrorCode.ResourceLimit, error.Code);
        Assert.Equal(value, (await transaction.GetAsync("first"u8.ToArray()))!.Value.ToArray());
        Assert.Null(await transaction.GetAsync("second"u8.ToArray()));
    }

    private static PantsOpenOptions CreateConstrainedOptions(PantsOpenOptions options) => options
        .WithMemoryBudget(PantsMemoryBudget.FromBytes(4 * 1024))
        .WithMemtableLimits(1024)
        .WithTransactionMemoryPool(1024);

    private static string ReadMagic(string path)
    {
        Span<byte> bytes = stackalloc byte[8];
        using var stream = File.OpenRead(path);
        stream.ReadExactly(bytes);
        return System.Text.Encoding.ASCII.GetString(bytes);
    }
}

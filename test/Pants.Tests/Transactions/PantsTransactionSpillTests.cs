using System.Text;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Transactions;

public sealed class PantsTransactionSpillTests
{
    [Fact]
    public async Task ShouldCommitAndCleanDurableSpillRuns()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(
            CreateConstrainedOptions(PantsOpenOptions.Local(directory.Path)));
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        var value = GC.AllocateUninitializedArray<byte>(900);
        Array.Fill(value, (byte)'v');
        for (var index = 0; index < 6; index++)
        {
            transaction.Put(TestBytes.FromString($"key-{index}"), value);
        }

        transaction.Put("point"u8.ToArray(), "before"u8.ToArray());
        transaction.Put("point"u8.ToArray(), "after"u8.ToArray());

        var transactionDirectory = Path.Combine(directory.Path, "txn");
        var runs = Directory.GetFiles(transactionDirectory, "*.run");
        Assert.NotEmpty(runs);
        Assert.All(runs, path => Assert.Equal("MDGTXN01", ReadMagic(path)));
        Assert.Equal("after", TestBytes.ToText((await transaction.GetAsync("point"u8.ToArray()))!.Value));

        await transaction.CommitAsync(PantsWriteOptions.Sync);

        Assert.False(Directory.Exists(transactionDirectory));
        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Equal("after", TestBytes.ToText((await reader.GetAsync("point"u8.ToArray()))!.Value));
        Assert.Equal(value, (await reader.GetAsync("key-5"u8.ToArray()))!.Value.ToArray());
    }

    [Fact]
    public async Task ShouldRejectDuplicateInsertAcrossSpillRunsWithoutPublishing()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(
            CreateConstrainedOptions(PantsOpenOptions.Local(directory.Path)));
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        var value = new byte[900];
        transaction.Insert("duplicate"u8.ToArray(), value);
        transaction.Put("filler"u8.ToArray(), value);
        transaction.Insert("duplicate"u8.ToArray(), "second"u8.ToArray());

        var error = await Assert.ThrowsAsync<PantsInvalidArgumentException>(() =>
            transaction.CommitAsync(PantsWriteOptions.Sync).AsTask());

        Assert.Equal(PantsErrorCode.InvalidArgument, error.Code);
        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Null(await reader.GetAsync("duplicate"u8.ToArray()));
    }

    [Fact]
    public async Task ShouldNotPartiallyPublishWhenSpillRunCannotBeRead()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(
            CreateConstrainedOptions(PantsOpenOptions.Local(directory.Path)));
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        var instance = Assert.IsType<DatabaseInstance>(database);
        var baselineBytes = instance.TransactionMemoryPool.Used;
        var value = new byte[900];
        transaction.Put("first"u8.ToArray(), value);
        transaction.Put("second"u8.ToArray(), value);
        var run = Assert.Single(Directory.GetFiles(Path.Combine(directory.Path, "txn"), "*.run"));
        File.Delete(run);

        var error = await Assert.ThrowsAnyAsync<PantsException>(() =>
            transaction.CommitAsync(PantsWriteOptions.Sync).AsTask());

        Assert.Equal(PantsErrorCode.Io, error.Code);
        Assert.Equal(baselineBytes, instance.TransactionMemoryPool.Used);
        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Null(await reader.GetAsync("first"u8.ToArray()));
        Assert.Null(await reader.GetAsync("second"u8.ToArray()));
    }

    [Fact]
    public async Task ShouldSpillResidentIntentsToAdmitAssertion()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(
            CreateConstrainedOptions(PantsOpenOptions.Local(directory.Path)));
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put("key"u8.ToArray(), new byte[900]);

        transaction.AssertValue("asserted"u8.ToArray(), new byte[100]);

        Assert.NotEmpty(Directory.GetFiles(Path.Combine(directory.Path, "txn"), "*.run"));
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task ShouldChargeResidentIntentSnapshotUntilEachScanIsDisposed()
    {
        await using var database = await PantsDatabase.OpenAsync(
            CreateConstrainedOptions(PantsOpenOptions.InMemory()));
        var instance = Assert.IsType<DatabaseInstance>(database);
        var baselineBytes = instance.TransactionMemoryPool.Used;
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        for (var index = 0; index < 4; index++)
        {
            transaction.Put(TestBytes.FromString($"snapshot-{index}"), "value"u8.ToArray());
        }

        var intentBytes = instance.TransactionMemoryPool.Used;
        var first = await transaction.ScanAsync(new PantsScanQuery());
        var firstScanBytes = instance.TransactionMemoryPool.Used;
        var second = await transaction.ScanAsync(new PantsScanQuery());
        var secondScanBytes = instance.TransactionMemoryPool.Used;

        try
        {
            var expectedSnapshotBytes = checked((long)IntPtr.Size * 8);
            Assert.Equal(expectedSnapshotBytes, firstScanBytes - intentBytes);
            Assert.Equal(expectedSnapshotBytes, secondScanBytes - firstScanBytes);

            await first.DisposeAsync();
            Assert.Equal(firstScanBytes, instance.TransactionMemoryPool.Used);

            await second.DisposeAsync();
            Assert.Equal(intentBytes, instance.TransactionMemoryPool.Used);
        }
        finally
        {
            await first.DisposeAsync();
            await second.DisposeAsync();
        }

        await transaction.RollbackAsync();
        Assert.Equal(baselineBytes, instance.TransactionMemoryPool.Used);
    }

    [Fact]
    public async Task ShouldRejectScanWhenIntentSnapshotDoesNotFitPoolAndPreservePointReads()
    {
        await using var database = await PantsDatabase.OpenAsync(
            CreateConstrainedOptions(PantsOpenOptions.InMemory()));
        var instance = Assert.IsType<DatabaseInstance>(database);
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        var value = Enumerable.Repeat((byte)'v', 950).ToArray();
        transaction.Put("large"u8.ToArray(), value);
        var intentBytes = instance.TransactionMemoryPool.Used;

        Assert.Equal(value, (await transaction.GetAsync("large"u8.ToArray()))!.Value.ToArray());
        var snapshotCount = (await database.Diagnostics.GetRuntimeMetricsAsync()).ActiveSnapshots;
        var failure = await Assert.ThrowsAsync<PantsResourceLimitException>(async () =>
        {
            var scan = await transaction.ScanAsync(new PantsScanQuery());
            await scan.DisposeAsync();
            throw new InvalidOperationException("The scan should fail before opening.");
        });

        Assert.Equal(PantsErrorCode.ResourceLimit, failure.Code);
        Assert.Equal(intentBytes, instance.TransactionMemoryPool.Used);
        Assert.Equal(
            snapshotCount,
            (await database.Diagnostics.GetRuntimeMetricsAsync()).ActiveSnapshots);
    }

    [Fact]
    public async Task ShouldReportResourceLimitForUnspillableAssertionWithoutLosingSpilledWrites()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(
            CreateConstrainedOptions(PantsOpenOptions.Local(directory.Path)));
        var instance = Assert.IsType<DatabaseInstance>(database);
        var baselineBytes = instance.TransactionMemoryPool.Used;

        await using (var transaction = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            transaction.Put("resident"u8.ToArray(), new byte[900]);

            var error = Assert.Throws<PantsResourceLimitException>(() =>
                transaction.AssertValue("asserted"u8.ToArray(), new byte[1_200]));

            Assert.Equal(PantsErrorCode.ResourceLimit, error.Code);
            Assert.Contains("assertion bytes", error.Message, StringComparison.Ordinal);
            Assert.NotEmpty(Directory.GetFiles(
                Path.Combine(directory.Path, "txn"),
                "*.run"));

            await transaction.CommitAsync(PantsWriteOptions.Sync);
        }

        Assert.Equal(baselineBytes, instance.TransactionMemoryPool.Used);

        await using (var transaction = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            transaction.Put("spillable"u8.ToArray(), new byte[1_200]);
            Assert.NotEmpty(Directory.GetFiles(
                Path.Combine(directory.Path, "txn"),
                "*.run"));
            await transaction.CommitAsync(PantsWriteOptions.Sync);
        }

        Assert.Equal(baselineBytes, instance.TransactionMemoryPool.Used);
        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Equal(900, Assert.IsType<ReadOnlyMemory<byte>>(
            await reader.GetAsync("resident"u8.ToArray())).Length);
        Assert.Equal(1_200, Assert.IsType<ReadOnlyMemory<byte>>(
            await reader.GetAsync("spillable"u8.ToArray())).Length);
        Assert.Null(await reader.GetAsync("asserted"u8.ToArray()));
    }

    [Fact]
    public async Task ShouldRemoveOrphanedSpillRunsAfterAcquiringWriterLease()
    {
        using var directory = new TemporaryDirectory();
        await using (var database = await PantsDatabase.OpenAsync(
                         CreateConstrainedOptions(PantsOpenOptions.Local(directory.Path))))
        {
        }

        var transactionDirectory = Path.Combine(directory.Path, "txn");
        Directory.CreateDirectory(transactionDirectory);
        await File.WriteAllTextAsync(Path.Combine(transactionDirectory, "orphan.run"), "orphan");

        await using var reopened = await PantsDatabase.OpenAsync(
            CreateConstrainedOptions(PantsOpenOptions.Local(directory.Path)));

        Assert.False(Directory.Exists(transactionDirectory));
    }

    [Fact]
    public async Task ShouldReturnResourceLimitInsteadOfSpillingInMemoryMode()
    {
        await using var database = await PantsDatabase.OpenAsync(
            CreateConstrainedOptions(PantsOpenOptions.InMemory()));
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        var value = new byte[900];
        transaction.Put("first"u8.ToArray(), value);

        var error = Assert.ThrowsAny<PantsException>(() =>
            transaction.Put("second"u8.ToArray(), value));

        Assert.Equal(PantsErrorCode.ResourceLimit, error.Code);
        Assert.Equal(value, (await transaction.GetAsync("first"u8.ToArray()))!.Value.ToArray());
        Assert.Null(await transaction.GetAsync("second"u8.ToArray()));
    }

    static PantsOpenOptions CreateConstrainedOptions(PantsOpenOptions options) => options
        .WithMemoryBudget(PantsMemoryBudget.FromBytes(4 * 1024))
        .WithMemtableLimits(1024)
        .WithTransactionMemoryPool(1024);

    static string ReadMagic(string path)
    {
        Span<byte> bytes = stackalloc byte[8];
        using var stream = File.OpenRead(path);
        stream.ReadExactly(bytes);
        return Encoding.ASCII.GetString(bytes);
    }
}

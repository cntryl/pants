using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Transactions;

public sealed class PantsPublicEntryCapacityTests
{
    [Theory]
    [InlineData(false, 34)]
    [InlineData(false, 63)]
    [InlineData(true, 34)]
    [InlineData(true, 63)]
    public async Task ShouldApplyTheHybridSpillFrameBoundBeforeWritingItsBeginMarker(bool provider, int bytesBelowLimit)
    {
        using var directory = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        var options = (provider
                ? PantsOpenOptions.Cloud(directory.Path, new PantsCloudStorageLocation(
                    new PantsAzureBlobProvider("account", "container", new Uri("https://storage.example.test"),
                        new PantsAzureCredentialSource.SasToken("sig=test")), "entry-frame-bound"))
                : PantsOpenOptions.SimulatedCloud(directory.Path, "pants-tests", "entry-frame-bound/"))
            .WithBackgroundCompaction(false)
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(2L * 1024 * 1024 * 1024))
            .WithTransactionMemoryPool(1024)
            .WithMemtableLimits(128L * 1024 * 1024);
        options = provider
            ? options.WithCloudLocalStorageBudget(1024L * 1024 * 1024)
            : options.WithSimulatedCloudLocalStorageBudget(1024L * 1024 * 1024);
        var dependencies = new RuntimeDependencies(cloudHttpClient: client);
        var reject = bytesBelowLimit == 34;
        var value = new byte[64 * 1024 * 1024 - bytesBelowLimit];
        value.AsSpan().Fill((byte)'v');
        await using (var database = await PantsDatabase.OpenForTestingAsync(options, dependencies))
        {
            await PutAsync(database, "stable", "previous"u8.ToArray(), PantsWriteOptions.CloudStrict);
            var before = await database.Diagnostics.GetRuntimeMetricsAsync();
            await using (var rejected = await database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite))
            {
                rejected.Put("key"u8.ToArray(), value);
                Assert.NotEmpty(Directory.GetFiles(Path.Combine(directory.Path, "txn"), "*.run"));
                if (reject)
                {
                    await Assert.ThrowsAsync<PantsInvalidArgumentException>(() => rejected.CommitAsync(PantsWriteOptions.CloudStrict).AsTask());
                }
                else
                {
                    await rejected.CommitAsync(PantsWriteOptions.CloudStrict);
                }
            }
            if (reject)
            {
                Assert.Equal(before.WalAppendCount, (await database.Diagnostics.GetRuntimeMetricsAsync()).WalAppendCount);
            }
            await PutAsync(database, "later", "accepted"u8.ToArray(), PantsWriteOptions.CloudStrict);
        }
        await using var reopened = await PantsDatabase.OpenForTestingAsync(options, dependencies);
        await using var reader = await reopened.Transactions.BeginAsync(reopened.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        Assert.Equal("previous"u8.ToArray(), (await reader.GetAsync("stable"u8.ToArray()))?.ToArray());
        Assert.Equal("accepted"u8.ToArray(), (await reader.GetAsync("later"u8.ToArray()))?.ToArray());
        var actual = await reader.GetAsync("key"u8.ToArray());
        if (reject)
        {
            Assert.Null(actual);
        }
        else
        {
            Assert.True(actual.HasValue);
            Assert.True(actual.Value.Span.SequenceEqual(value));
        }
    }

    [Fact]
    public async Task ShouldKeepLocalSpillAmbiguityAndFenceAfterAnEarlierMarkerWasWritten()
    {
        using var directory = new TemporaryDirectory();
        var options = Options(directory.Path, true);
        var key = new byte[64 * 1024 * 1024 - 34];
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            await PutAsync(database, "stable", "previous"u8.ToArray());
            await using (var rejected = await database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite))
            {
                rejected.Delete(key);
                Assert.NotEmpty(Directory.GetFiles(Path.Combine(directory.Path, "txn"), "*.run"));
                await Assert.ThrowsAsync<PantsCommitOutcomeUnknownException>(() => rejected.CommitAsync(PantsWriteOptions.Sync).AsTask());
            }
            await Assert.ThrowsAsync<PantsFencedException>(() => PutAsync(database, "suffix", "absent"u8.ToArray()));
        }
        await using var reopened = await PantsDatabase.OpenAsync(options);
        await using var reader = await reopened.Transactions.BeginAsync(reopened.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        Assert.Equal("previous"u8.ToArray(), (await reader.GetAsync("stable"u8.ToArray()))?.ToArray());
        Assert.Null(await reader.GetAsync("suffix"u8.ToArray()));
        Assert.Null(await reader.GetAsync(key));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldStageSstLegalTombstonesWithoutRequiringAWalEnvelopeInMemory(bool range)
    {
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory()
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(1024L * 1024 * 1024))
            .WithTransactionMemoryPool(512L * 1024 * 1024));
        await using (var transaction = await database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite))
        {
            if (range)
            {
                var start = new byte[32 * 1024 * 1024];
                var end = new byte[start.Length - 20];
                end[0] = 1;
                transaction.DeleteRange(start, end);
            }
            else
            {
                transaction.Delete(new byte[64 * 1024 * 1024 - 34]);
            }
            await transaction.RollbackAsync();
        }
        await using (var writer = await database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite))
        {
            writer.Put("small"u8.ToArray(), "usable"u8.ToArray());
            await writer.CommitAsync(PantsWriteOptions.Sync);
        }
        await using var reader = await database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        Assert.Equal("usable"u8.ToArray(), (await reader.GetAsync("small"u8.ToArray()))?.ToArray());
    }

    [Fact]
    public async Task ShouldRejectAnUnframeableResidentCommitBeforeAppendAndPreserveTheDatabase()
    {
        using var directory = new TemporaryDirectory();
        var options = Options(directory.Path, false);
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            await PutAsync(database, "stable", "previous"u8.ToArray());
            var before = await database.Diagnostics.GetRuntimeMetricsAsync();
            await using (var rejected = await database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite))
            {
                var value = new byte[64 * 1024 * 1024 - 34];
                value.AsSpan().Fill((byte)'v');
                rejected.Put("key"u8.ToArray(), value);
                await Assert.ThrowsAsync<PantsResourceLimitException>(() => rejected.CommitAsync(PantsWriteOptions.Sync).AsTask());
            }
            Assert.Equal(before.WalAppendCount, (await database.Diagnostics.GetRuntimeMetricsAsync()).WalAppendCount);
            await PutAsync(database, "later", "accepted"u8.ToArray());
        }
        await using var reopened = await PantsDatabase.OpenAsync(options);
        await using var reader = await reopened.Transactions.BeginAsync(reopened.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        Assert.Equal("previous"u8.ToArray(), (await reader.GetAsync("stable"u8.ToArray()))?.ToArray());
        Assert.Equal("accepted"u8.ToArray(), (await reader.GetAsync("later"u8.ToArray()))?.ToArray());
        Assert.Null(await reader.GetAsync("key"u8.ToArray()));
    }

    [Fact]
    public async Task ShouldCommitAndRecoverASpilledSstLimitValueThatDoesNotFitAResidentBatch()
    {
        using var directory = new TemporaryDirectory();
        var options = Options(directory.Path, true);
        var value = new byte[64 * 1024 * 1024 - 34];
        value.AsSpan().Fill((byte)'v');
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            await using var write = await database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite);
            write.Put("key"u8.ToArray(), value);
            Assert.NotEmpty(Directory.GetFiles(Path.Combine(directory.Path, "txn"), "*.run"));
            await write.CommitAsync(PantsWriteOptions.Sync);
            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        }
        await using var reopened = await PantsDatabase.OpenAsync(options);
        await using var reader = await reopened.Transactions.BeginAsync(reopened.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        var actual = await reader.GetAsync("key"u8.ToArray());
        Assert.True(actual.HasValue);
        Assert.True(actual.Value.Span.SequenceEqual(value));
    }

    [Theory]
    [InlineData(256)]
    [InlineData(58)]
    public async Task ShouldCommitFlushAndRecoverTheLargestNativeMidgePublicPayload(int bytesBelowLimit)
    {
        using var directory = new TemporaryDirectory();
        var options = Options(directory.Path, false);
        var value = new byte[64 * 1024 * 1024 - bytesBelowLimit];
        value.AsSpan().Fill((byte)'v');
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            await using var write = await database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite);
            write.Put("key"u8.ToArray(), value);
            await write.CommitAsync(PantsWriteOptions.Sync);
            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
            await database.ShutdownAsync(TestTimeouts.Expected);
        }
        await using var reopened = await PantsDatabase.OpenAsync(options);
        await using var reader = await reopened.Transactions.BeginAsync(reopened.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        var actual = await reader.GetAsync("key"u8.ToArray());
        Assert.True(actual.HasValue);
        Assert.True(actual.Value.Span.SequenceEqual(value));
    }

    static PantsOpenOptions Options(string path, bool spilled) => PantsOpenOptions.Local(path).WithBackgroundCompaction(false)
        .WithMemoryBudget(PantsMemoryBudget.FromBytes(1024L * 1024 * 1024))
        .WithTransactionMemoryPool(spilled ? 1024 : 512L * 1024 * 1024)
        .WithMemtableLimits(128L * 1024 * 1024);

    static async Task PutAsync(IPantsDatabase database, string key, byte[] value, PantsWriteOptions? options = null)
    {
        await using var write = await database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite);
        write.Put(TestBytes.FromString(key), value);
        await write.CommitAsync(options ?? PantsWriteOptions.Sync);
    }
}

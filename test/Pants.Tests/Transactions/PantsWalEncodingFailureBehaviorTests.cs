using Cntryl.Pants.Runtime;
using Cntryl.Pants.Support.Failpoints;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Transactions;

[Collection(RuntimeDiagnosticsTestGroup.Name)]
public sealed class PantsWalEncodingFailureBehaviorTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public async Task ShouldEnforceConfiguredCloudReplayCapacityBeforeAllocatingASequence(bool provider, int scenario)
    {
        using var directory = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        var memoryBytes = scenario switch { 0 => 2L * 1024 * 1024 * 1024, 1 => 4L * 1024 * 1024 * 1024, _ => 128L * 1024 * 1024 };
        var diskBytes = scenario switch { 0 => 2L * 1024 * 1024 * 1024, 1 => 8L * 1024 * 1024, _ => 64L * 1024 * 1024 };
        var valueBytes = scenario switch { 0 => 64 * 1024 * 1024 - 58, 1 => 2 * 1024 * 1024 - 58, _ => 9 * 1024 * 1024 };
        var options = (provider
                ? PantsOpenOptions.Cloud(directory.Path, new PantsCloudStorageLocation(
                    new PantsAzureBlobProvider("account", "container", new Uri("https://storage.example.test"),
                        new PantsAzureCredentialSource.SasToken("sig=test")), "configured-replay"))
                : PantsOpenOptions.SimulatedCloud(directory.Path, "pants-tests", "configured-replay/"))
            .WithBackgroundCompaction(false)
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(memoryBytes))
            .WithTransactionMemoryPool(scenario == 2 ? 1024 : 512L * 1024 * 1024)
            .WithMemtableLimits(scenario == 0 ? 512L * 1024 * 1024 : 32L * 1024 * 1024);
        options = provider ? options.WithCloudLocalStorageBudget(diskBytes) : options.WithSimulatedCloudLocalStorageBudget(diskBytes);
        var dependencies = new RuntimeDependencies(cloudHttpClient: client);
        var value = new byte[valueBytes];
        value.AsSpan().Fill((byte)'v');
        var database = await PantsDatabase.OpenForTestingAsync(options, dependencies);
        try
        {
            Assert.Equal(3UL, await PutAsync(database, "old", "stable"u8.ToArray(), PantsWriteOptions.CloudStrict));
            var before = await database.Diagnostics.GetRuntimeMetricsAsync();
            await using (var write = await BeginAsync(database))
            {
                write.Put("key"u8.ToArray(), value);
                await Assert.ThrowsAsync<PantsResourceLimitException>(() => write.CommitWithSequenceAsync(PantsWriteOptions.CloudStrict).AsTask());
            }
            var after = await database.Diagnostics.GetRuntimeMetricsAsync();
            Assert.Equal(3UL, after.CurrentSequence);
            Assert.Equal(before.WalAppendCount, after.WalAppendCount);
            await AssertOldCutAsync(database, PantsWriteOptions.CloudStrict);
            Assert.Equal(6UL, await PutAsync(database, "suffix", "durable"u8.ToArray(), PantsWriteOptions.CloudStrict));
            await database.ShutdownAsync(TestTimeouts.Expected);
        }
        finally
        {
            await CloseAsync(database);
        }
        await using var reopened = await PantsDatabase.OpenForTestingAsync(options, dependencies);
        await using var reader = await reopened.Transactions.BeginAsync(reopened.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        Assert.Equal("stable"u8.ToArray(), (await reader.GetAsync("old"u8.ToArray()))?.ToArray());
        Assert.Equal("durable"u8.ToArray(), (await reader.GetAsync("suffix"u8.ToArray()))?.ToArray());
        Assert.Null(await reader.GetAsync("key"u8.ToArray()));
        Assert.Equal(6UL, await reader.CommitWithSequenceAsync(PantsWriteOptions.CloudStrict));
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, false, true)]
    [InlineData(true, false, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, false, true)]
    public async Task ShouldRejectTheHybridRawFrameBeforePhysicalEncodingWithoutPoisoningTheWriter(
        bool provider, bool spilled, bool random, bool acceptedBoundary)
    {
        using var directory = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        var options = (provider
                ? PantsOpenOptions.Cloud(directory.Path, new PantsCloudStorageLocation(
                    new PantsAzureBlobProvider("account", "container", new Uri("https://storage.example.test"),
                        new PantsAzureCredentialSource.SasToken("sig=test")), "resident-frame-bound"))
                : PantsOpenOptions.SimulatedCloud(directory.Path, "pants-tests", "resident-frame-bound/"))
            .WithBackgroundCompaction(false)
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(4L * 1024 * 1024 * 1024))
            .WithTransactionMemoryPool(spilled ? 1024 : 512L * 1024 * 1024)
            .WithMemtableLimits(512L * 1024 * 1024);
        options = provider
            ? options.WithCloudLocalStorageBudget(2L * 1024 * 1024 * 1024)
            : options.WithSimulatedCloudLocalStorageBudget(2L * 1024 * 1024 * 1024);
        var dependencies = new RuntimeDependencies(cloudHttpClient: client);
        var value = CreateValue(acceptedBoundary ? 121 : spilled ? 34 : 58, random);
        await using (var database = await PantsDatabase.OpenForTestingAsync(options, dependencies))
        {
            Assert.Equal(3UL, await PutAsync(database, "old", "stable"u8.ToArray(), PantsWriteOptions.CloudStrict));
            var before = await database.Diagnostics.GetRuntimeMetricsAsync();
            await using (var write = await BeginAsync(database))
            {
                write.Put("key"u8.ToArray(), value);
                var transactionDirectory = Path.Combine(directory.Path, "txn");
                Assert.Equal(spilled, Directory.Exists(transactionDirectory) && Directory.EnumerateFiles(transactionDirectory, "*.run").Any());
                if (acceptedBoundary)
                {
                    Assert.Equal(6UL, await write.CommitWithSequenceAsync(PantsWriteOptions.CloudStrict));
                }
                else
                {
                    await Assert.ThrowsAsync<PantsInvalidArgumentException>(() => write.CommitWithSequenceAsync(PantsWriteOptions.CloudStrict).AsTask());
                }
            }
            var after = await database.Diagnostics.GetRuntimeMetricsAsync();
            Assert.Equal(6UL, after.CurrentSequence);
            if (!acceptedBoundary)
            {
                Assert.Equal(before.WalAppendCount, after.WalAppendCount);
                Assert.Equal(before.WalBytesWrittenTotal, after.WalBytesWrittenTotal);
                await AssertOldCutAsync(database, PantsWriteOptions.CloudStrict);
            }
            Assert.Equal(9UL, await PutAsync(database, "suffix", "durable"u8.ToArray(), PantsWriteOptions.CloudStrict));
            Assert.Equal(12UL, await PutAsync(database, "volatile", "accepted"u8.ToArray(), PantsWriteOptions.BestEffort));
            await database.ShutdownAsync(TestTimeouts.Expected);
        }
        await using var reopened = await PantsDatabase.OpenForTestingAsync(options, dependencies);
        await using var reader = await reopened.Transactions.BeginAsync(reopened.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        Assert.Equal("stable"u8.ToArray(), (await reader.GetAsync("old"u8.ToArray()))?.ToArray());
        Assert.Equal("durable"u8.ToArray(), (await reader.GetAsync("suffix"u8.ToArray()))?.ToArray());
        Assert.Equal("accepted"u8.ToArray(), (await reader.GetAsync("volatile"u8.ToArray()))?.ToArray());
        var actual = await reader.GetAsync("key"u8.ToArray());
        if (acceptedBoundary)
        {
            Assert.True(actual.HasValue);
            Assert.True(actual.Value.Span.SequenceEqual(value));
        }
        else
        {
            Assert.Null(actual);
        }
        Assert.Equal(12UL, await reader.CommitWithSequenceAsync(PantsWriteOptions.CloudStrict));
    }

    [Fact]
    public async Task ShouldReturnInvalidArgumentForAnAcceptedOuterFrameEncodingFailure()
    {
        using var directory = new TemporaryDirectory();
        var database = await PantsDatabase.OpenAsync(Options(directory.Path));
        try
        {
            Assert.Equal(3UL, await PutAsync(database, "old", "stable"u8.ToArray(), PantsWriteOptions.Sync));
            var failure = await FailLargeCommitAsync(database, false);
            Assert.IsType<PantsInvalidArgumentException>(failure);
            await AssertOldCutAsync(database);
        }
        finally
        {
            await CloseAsync(database);
        }
    }

    [Fact]
    public async Task ShouldRetainTheAllocatedSequenceAndAllowReadOnlyAndEmptyCompletionAfterEncoderPoison()
    {
        using var directory = new TemporaryDirectory();
        var database = await PantsDatabase.OpenAsync(Options(directory.Path));
        try
        {
            Assert.Equal(3UL, await PutAsync(database, "old", "stable"u8.ToArray(), PantsWriteOptions.Sync));
            var before = await database.Diagnostics.GetRuntimeMetricsAsync();
            _ = await FailLargeCommitAsync(database, false);
            var after = await database.Diagnostics.GetRuntimeMetricsAsync();
            Assert.Equal(6UL, after.CurrentSequence);
            Assert.Equal(before.WalBytesWrittenTotal, after.WalBytesWrittenTotal);
            Assert.Equal(before.WalAppendCount, after.WalAppendCount);
            Assert.Equal(before.WalFsyncCount, after.WalFsyncCount);
            Assert.Equal(PantsEngineHealth.Degraded, after.Health);
            await AssertOldCutAsync(database);
            foreach (var options in new[] { PantsWriteOptions.Buffered, PantsWriteOptions.BestEffort })
            {
                await using var empty = await BeginAsync(database);
                Assert.Equal(3UL, await empty.CommitWithSequenceAsync(options));
            }
            Assert.Equal(6UL, (await database.Diagnostics.GetRuntimeMetricsAsync()).CurrentSequence);
        }
        finally
        {
            await CloseAsync(database);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldRejectLaterMutationsAfterTheAcceptedEncoderFails(bool bestEffort)
    {
        using var directory = new TemporaryDirectory();
        var database = await PantsDatabase.OpenAsync(Options(directory.Path));
        try
        {
            _ = await PutAsync(database, "old", "stable"u8.ToArray(), PantsWriteOptions.Sync);
            _ = await FailLargeCommitAsync(database, false);
            await Assert.ThrowsAsync<PantsRecoveryFailedException>(() => PutAsync(
                database, "suffix", "absent"u8.ToArray(), bestEffort ? PantsWriteOptions.BestEffort : PantsWriteOptions.Sync));
            Assert.Equal(6UL, (await database.Diagnostics.GetRuntimeMetricsAsync()).CurrentSequence);
            await AssertOldCutAsync(database);
        }
        finally
        {
            await CloseAsync(database);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ShouldValidateAssertionOnlyWorkBeforeRequiringAPoisonedPhysicalWriter(int policy)
    {
        using var directory = new TemporaryDirectory();
        var database = await PantsDatabase.OpenAsync(Options(directory.Path));
        try
        {
            _ = await PutAsync(database, "old", "stable"u8.ToArray(), PantsWriteOptions.Sync);
            _ = await FailLargeCommitAsync(database, false);
            await using var assertion = await BeginAsync(database);
            assertion.AssertValue("old"u8.ToArray(), "stable"u8.ToArray());
            var options = policy switch
            {
                0 => PantsWriteOptions.Buffered,
                1 => PantsWriteOptions.BestEffort,
                _ => PantsWriteOptions.Sync
            };
            if (policy == 2)
            {
                await Assert.ThrowsAsync<PantsRecoveryFailedException>(() => assertion.CommitWithSequenceAsync(options).AsTask());
            }
            else
            {
                Assert.Equal(3UL, await assertion.CommitWithSequenceAsync(options));
            }
            Assert.Equal(6UL, (await database.Diagnostics.GetRuntimeMetricsAsync()).CurrentSequence);
            await AssertOldCutAsync(database);
        }
        finally
        {
            await CloseAsync(database);
        }
    }

    [Fact]
    public async Task ShouldRejectAnInvalidAssertionBeforeThePoisonedSyncBoundary()
    {
        using var directory = new TemporaryDirectory();
        var database = await PantsDatabase.OpenAsync(Options(directory.Path));
        try
        {
            _ = await PutAsync(database, "old", "stable"u8.ToArray(), PantsWriteOptions.Sync);
            _ = await FailLargeCommitAsync(database, false);
            await using var assertion = await BeginAsync(database);
            assertion.AssertValue("old"u8.ToArray(), "different"u8.ToArray());
            await Assert.ThrowsAsync<PantsWriteConflictException>(() => assertion.CommitWithSequenceAsync(PantsWriteOptions.Sync).AsTask());
            Assert.Equal(6UL, (await database.Diagnostics.GetRuntimeMetricsAsync()).CurrentSequence);
        }
        finally
        {
            await CloseAsync(database);
        }
    }

    [Fact]
    public async Task ShouldRejectAnEmptySyncThatRequiresThePoisonedPhysicalWriter()
    {
        using var directory = new TemporaryDirectory();
        var database = await PantsDatabase.OpenAsync(Options(directory.Path));
        try
        {
            _ = await PutAsync(database, "old", "stable"u8.ToArray(), PantsWriteOptions.Sync);
            _ = await FailLargeCommitAsync(database, false);
            await using var empty = await BeginAsync(database);
            await Assert.ThrowsAsync<PantsRecoveryFailedException>(() => empty.CommitWithSequenceAsync(PantsWriteOptions.Sync).AsTask());
            Assert.Equal(6UL, (await database.Diagnostics.GetRuntimeMetricsAsync()).CurrentSequence);
            await AssertOldCutAsync(database);
        }
        finally
        {
            await CloseAsync(database);
        }
    }

    [Fact]
    public async Task ShouldFinishPhysicalShutdownAndReleaseTheLeaseBeforeReplayingEncoderPoison()
    {
        using var directory = new TemporaryDirectory();
        var options = Options(directory.Path);
        var database = await PantsDatabase.OpenAsync(options);
        try
        {
            _ = await PutAsync(database, "old", "stable"u8.ToArray(), PantsWriteOptions.Sync);
            _ = await FailLargeCommitAsync(database, false);
            await Assert.ThrowsAsync<PantsRecoveryFailedException>(() => database.ShutdownAsync(TestTimeouts.Expected).AsTask());
            await Assert.ThrowsAsync<PantsRecoveryFailedException>(() => database.ShutdownAsync(TestTimeouts.Expected).AsTask());
            await using var reopened = await PantsDatabase.OpenAsync(options);
            await using var reader = await reopened.Transactions.BeginAsync(reopened.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
            Assert.Equal("stable"u8.ToArray(), (await reader.GetAsync("old"u8.ToArray()))?.ToArray());
            Assert.Null(await reader.GetAsync("key"u8.ToArray()));
            Assert.Null(await reader.GetAsync("suffix"u8.ToArray()));
        }
        finally
        {
            await CloseAsync(database);
        }
    }

    [Fact]
    public async Task ShouldKeepTheLeaseUntilAHeldPhysicalFlushSettlesAfterEncoderPoison()
    {
        using var directory = new TemporaryDirectory();
        using var held = new HeldFlushOutput();
        var options = Options(directory.Path);
        var database = await PantsDatabase.OpenForTestingAsync(options, new RuntimeDependencies(held));
        var flush = Task.CompletedTask;
        try
        {
            _ = await PutAsync(database, "old", "stable"u8.ToArray(), PantsWriteOptions.Sync);
            flush = database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily).AsTask();
            await held.Entered.WaitAsync(TestTimeouts.Expected);
            _ = await FailLargeCommitAsync(database, false);
            await Assert.ThrowsAsync<PantsTimeoutException>(() => database.ShutdownAsync(TimeSpan.Zero).AsTask());
            Assert.False(held.IsReleased);
            await Assert.ThrowsAsync<PantsLeaseHeldException>(() => PantsDatabase.OpenAsync(options).AsTask());
            held.Release();
            await Assert.ThrowsAsync<PantsRecoveryFailedException>(() => database.ShutdownAsync(TestTimeouts.Expected).AsTask());
            await using var reopened = await PantsDatabase.OpenAsync(options);
            await using var reader = await reopened.Transactions.BeginAsync(reopened.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
            Assert.Equal("stable"u8.ToArray(), (await reader.GetAsync("old"u8.ToArray()))?.ToArray());
            Assert.Null(await reader.GetAsync("key"u8.ToArray()));
        }
        finally
        {
            held.Release();
            try
            {
                await flush.WaitAsync(TestTimeouts.Expected);
            }
            catch (PantsException)
            {
                // Shutdown rejects publication waiters, but retains the actual physical worker.
            }
            await CloseAsync(database);
        }
    }

    [Fact]
    public async Task ShouldRejectTheDecodedBatchBeforeAllocationAndKeepTheWriterUsable()
    {
        using var directory = new TemporaryDirectory();
        var options = Options(directory.Path);
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            Assert.Equal(3UL, await PutAsync(database, "old", "stable"u8.ToArray(), PantsWriteOptions.Sync));
            Assert.IsType<PantsResourceLimitException>(await FailLargeCommitAsync(database, true));
            Assert.Equal(3UL, (await database.Diagnostics.GetRuntimeMetricsAsync()).CurrentSequence);
            await AssertOldCutAsync(database);
            foreach (var writeOptions in new[] { PantsWriteOptions.Buffered, PantsWriteOptions.BestEffort })
            {
                await using var empty = await BeginAsync(database);
                Assert.Equal(3UL, await empty.CommitWithSequenceAsync(writeOptions));
            }
            Assert.Equal(6UL, await PutAsync(database, "suffix", "durable"u8.ToArray(), PantsWriteOptions.Sync));
            Assert.Equal(9UL, await PutAsync(database, "volatile", "allowed"u8.ToArray(), PantsWriteOptions.BestEffort));
        }
        await using var reopened = await PantsDatabase.OpenAsync(options);
        await using var reader = await reopened.Transactions.BeginAsync(reopened.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        Assert.Equal("stable"u8.ToArray(), (await reader.GetAsync("old"u8.ToArray()))?.ToArray());
        Assert.Equal("durable"u8.ToArray(), (await reader.GetAsync("suffix"u8.ToArray()))?.ToArray());
        Assert.Null(await reader.GetAsync("key"u8.ToArray()));
    }

    [Fact]
    public async Task ShouldRejectTheDecodedLimitWithoutEnteringPhysicalRollback()
    {
        using var directory = new TemporaryDirectory();
        var failpoints = new ArmableFailpointHandler();
        await using var database = await PantsDatabase.OpenForTestingAsync(Options(directory.Path), new RuntimeDependencies(failpoints));
        Assert.Equal(3UL, await PutAsync(database, "old", "stable"u8.ToArray(), PantsWriteOptions.Sync));
        failpoints.Arm(Failpoint.BeforeWalRollback, static _ => new PantsNoSpaceException("Physical rollback must not start for a rejected logical unit."));
        Assert.IsType<PantsResourceLimitException>(await FailLargeCommitAsync(database, true));
        Assert.Equal(3UL, (await database.Diagnostics.GetRuntimeMetricsAsync()).CurrentSequence);
        Assert.Equal(6UL, await PutAsync(database, "suffix", "durable"u8.ToArray(), PantsWriteOptions.Sync));
    }

    static PantsOpenOptions Options(string path) => PantsOpenOptions.Local(path)
        .WithBackgroundCompaction(false)
        .WithMemoryBudget(PantsMemoryBudget.FromBytes(1024L * 1024 * 1024))
        .WithTransactionMemoryPool(512L * 1024 * 1024)
        .WithMemtableLimits(128L * 1024 * 1024);

    static ValueTask<IPantsTransaction> BeginAsync(IPantsDatabase database) => database.Transactions.BeginAsync(
        database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite);

    static async Task<ulong> PutAsync(IPantsDatabase database, string key, byte[] value, PantsWriteOptions options)
    {
        await using var write = await BeginAsync(database);
        write.Put(TestBytes.FromString(key), value);
        return await write.CommitWithSequenceAsync(options);
    }

    static async Task<PantsException> FailLargeCommitAsync(IPantsDatabase database, bool decodedOverflow)
    {
        var value = CreateValue(decodedOverflow ? 34 : 58, !decodedOverflow);
        await using var write = await BeginAsync(database);
        write.Put("key"u8.ToArray(), value);
        return await Assert.ThrowsAnyAsync<PantsException>(() => write.CommitWithSequenceAsync(PantsWriteOptions.Sync).AsTask());
    }

    static byte[] CreateValue(int bytesBelowLimit, bool random)
    {
        var value = new byte[64 * 1024 * 1024 - bytesBelowLimit];
        if (!random)
        {
            value.AsSpan().Fill((byte)'v');
        }
        else
        {
            var state = 0x12345678U;
            for (var index = 0; index < value.Length; index++)
            {
                state ^= state << 13;
                state ^= state >> 17;
                state ^= state << 5;
                value[index] = (byte)state;
            }
        }
        return value;
    }

    static async Task AssertOldCutAsync(IPantsDatabase database, PantsWriteOptions? options = null)
    {
        await using var reader = await database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        Assert.Equal("stable"u8.ToArray(), (await reader.GetAsync("old"u8.ToArray()))?.ToArray());
        Assert.Null(await reader.GetAsync("key"u8.ToArray()));
        Assert.Null(await reader.GetAsync("suffix"u8.ToArray()));
        Assert.Equal(3UL, await reader.CommitWithSequenceAsync(options ?? PantsWriteOptions.Sync));
    }

    static async Task CloseAsync(IPantsDatabase database)
    {
        try
        {
            await database.ShutdownAsync(TestTimeouts.Expected);
        }
        catch (PantsRecoveryFailedException)
        {
            // The failed physical writer is settled and the lease is already released.
        }
        await database.DisposeAsync();
    }

    sealed class HeldFlushOutput : IFailpointHandler, IDisposable
    {
        readonly ManualResetEventSlim _release = new();
        readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public bool IsReleased => _release.IsSet;

        public void Hit(Failpoint failpoint)
        {
            if (failpoint != Failpoint.AfterFlushOutputDurable)
            {
                return;
            }

            _entered.TrySetResult();
            if (!_release.Wait(TestTimeouts.Expected))
            {
                throw new TimeoutException("The physical flush output was not released.");
            }
        }

        public void Release() => _release.Set();

        public void Dispose()
        {
            _release.Set();
            _release.Dispose();
        }
    }
}

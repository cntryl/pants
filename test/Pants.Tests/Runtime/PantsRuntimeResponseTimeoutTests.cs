using System.Diagnostics;

namespace Cntryl.Pants.Tests.Runtime;

public sealed class PantsRuntimeResponseTimeoutTests
{
    static readonly TimeSpan AssertionTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void ShouldDeriveRuntimeResponseTimeoutFromStorageTimeout()
    {
        Assert.Equal(
            TimeSpan.FromSeconds(60),
            PantsOpenOptions.InMemory().RuntimeResponseTimeout);
        Assert.Equal(
            TimeSpan.FromSeconds(75),
            PantsOpenOptions.InMemory()
                .WithStorageTimeout(TimeSpan.FromSeconds(45))
                .RuntimeResponseTimeout);
        Assert.Equal(
            TimeSpan.MaxValue,
            PantsOpenOptions.InMemory()
                .WithStorageTimeout(TimeSpan.MaxValue)
                .RuntimeResponseTimeout);
    }

    [Fact]
    public void ShouldPreserveExplicitRuntimeResponseTimeoutWhenStorageTimeoutChanges()
    {
        var options = PantsOpenOptions.InMemory()
            .WithRuntimeResponseTimeout(TimeSpan.FromSeconds(90))
            .WithStorageTimeout(TimeSpan.FromSeconds(45));

        Assert.Equal(TimeSpan.FromSeconds(90), options.RuntimeResponseTimeout);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void ShouldRejectInvalidRuntimeResponseTimeoutRelativeToStorageTimeout(
        int responseTimeoutMilliseconds)
    {
        var exception = Assert.Throws<PantsInvalidArgumentException>(() =>
            PantsOpenOptions.InMemory()
                .WithStorageTimeout(TimeSpan.FromMilliseconds(1))
                .WithRuntimeResponseTimeout(TimeSpan.FromMilliseconds(responseTimeoutMilliseconds)));

        Assert.Contains("RuntimeResponseTimeout", exception.Message, StringComparison.Ordinal);
        Assert.Contains("StorageTimeout", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShouldBoundAdmittedRuntimeResponseAndClassifyLateCompletion()
    {
        using var directory = new TemporaryDirectory();
        using var failpoint = new RuntimeMetricsResponseFailpointHandler();
        await using var database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Local(directory.Path)
                .WithStorageTimeout(TimeSpan.FromMilliseconds(5))
                .WithRuntimeResponseTimeout(TimeSpan.FromMilliseconds(100)),
            new RuntimeDependencies(failpoint));
        var started = Stopwatch.GetTimestamp();
        var request = database.GetRuntimeMetricsAsync().AsTask();

        try
        {
            await failpoint.WaitUntilEnteredAsync(AssertionTimeout);
            var exception = await Assert.ThrowsAsync<PantsTimeoutException>(() =>
                request.WaitAsync(AssertionTimeout));

            Assert.InRange(Stopwatch.GetElapsedTime(started), TimeSpan.Zero, TimeSpan.FromSeconds(2));
            Assert.Contains("GetRuntimeMetricsAsync", exception.Message, StringComparison.Ordinal);
            Assert.Contains("request ID", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("00:00:00.100", exception.Message, StringComparison.Ordinal);
            Assert.Contains("outcome is unknown", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            failpoint.Release();
        }

        var metrics = await WaitForLateResponseAsync(database);
        Assert.Equal(0, metrics.RuntimeResponseWaiters);
        Assert.Equal(1, metrics.RuntimeAbandonedRequestsTotal);
        Assert.Equal(1, metrics.RuntimeLateResponsesTotal);
        Assert.Equal(0, metrics.RuntimeAbandonedRequestMetadata);
    }

    [Fact]
    public async Task ShouldPersistAcceptedCommitAfterRuntimeResponseTimesOut()
    {
        using var directory = new TemporaryDirectory();
        using var failpoint = new WriteAdmissionRaceFailpointHandler();
        var options = PantsOpenOptions.Local(directory.Path)
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(512 * 1024))
            .WithMemtableLimits(2 * 1024)
            .WithTransactionMemoryPool(1024)
            .WithStorageTimeout(TimeSpan.FromMilliseconds(5))
            .WithRuntimeResponseTimeout(TimeSpan.FromMilliseconds(100));
        var database = await PantsDatabase.OpenForTestingAsync(
            options,
            new RuntimeDependencies(failpoint));

        await using (var transaction = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            transaction.Put("accepted"u8.ToArray(), "durable"u8.ToArray());
            transaction.Put("spill-a"u8.ToArray(), new byte[900]);
            transaction.Put("spill-b"u8.ToArray(), new byte[900]);
            Assert.NotEmpty(Directory.GetFiles(
                Path.Combine(directory.Path, "txn"),
                "*.run"));
            failpoint.ArmWalAppend();
            var commit = transaction.CommitAsync(PantsWriteOptions.Sync).AsTask();
            await failpoint.WaitForWalAsync(AssertionTimeout);

            try
            {
                var exception = await Assert.ThrowsAsync<PantsTimeoutException>(() =>
                    commit.WaitAsync(AssertionTimeout));
                Assert.Contains("outcome is unknown", exception.Message, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                failpoint.ReleaseWal();
            }
        }

        _ = await WaitForLateResponseAsync(database);
        await using (var readBeforeShutdown = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadOnly))
        {
            Assert.Equal(
                "durable"u8.ToArray(),
                (await readBeforeShutdown.GetAsync("accepted"u8.ToArray()))?.ToArray());
        }

        await database.ShutdownAsync(AssertionTimeout);
        await database.DisposeAsync();

        await using var reopened = await PantsDatabase.OpenAsync(options);
        await using var read = await reopened.BeginTransactionAsync(
            reopened.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Equal(
            "durable"u8.ToArray(),
            (await read.GetAsync("accepted"u8.ToArray()))?.ToArray());
    }

    static async Task<PantsRuntimeMetrics> WaitForLateResponseAsync(IPantsDatabase database)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(AssertionTimeout.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            var metrics = await database.GetRuntimeMetricsAsync();
            if (metrics.RuntimeLateResponsesTotal == 1)
            {
                return metrics;
            }

            await Task.Yield();
        }

        throw new TimeoutException("The abandoned runtime response did not complete.");
    }
}

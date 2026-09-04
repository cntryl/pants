using System.Diagnostics;

namespace Cntryl.Pants.Tests.Cloud;

public sealed class PantsCloudRuntimeDeadlineTests
{
    static readonly TimeSpan AssertionTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ShouldFinishAcceptedCloudStrictDurabilityAfterCallerAbandonsResponse()
    {
        using var directory = new TemporaryDirectory();
        using var failpoint = new BlockingCloudWalUploadFailpointHandler();
        var options = PantsOpenOptions.SimulatedCloud(
                directory.Path,
                "pants-tests",
                "runtime-deadline/")
            .WithStorageTimeout(TimeSpan.FromMilliseconds(5))
            .WithRuntimeResponseTimeout(TimeSpan.FromMilliseconds(100));
        var database = await PantsDatabase.OpenForTestingAsync(
            options,
            new RuntimeDependencies(failpoint));

        await using (var transaction = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            transaction.Put("accepted-key"u8.ToArray(), "accepted-value"u8.ToArray());
            var commit = transaction.CommitAsync(PantsWriteOptions.CloudStrict).AsTask();
            await failpoint.WaitUntilEnteredAsync(AssertionTimeout);

            try
            {
                var exception = await Assert.ThrowsAsync<PantsTimeoutException>(() =>
                    commit.WaitAsync(AssertionTimeout));
                Assert.Contains("outcome is unknown", exception.Message, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("accepted-key", exception.Message, StringComparison.Ordinal);
                Assert.DoesNotContain("accepted-value", exception.Message, StringComparison.Ordinal);
            }
            finally
            {
                failpoint.Release();
            }
        }

        await WaitForLateResponseAsync(database);
        await database.ShutdownAsync(AssertionTimeout);
        await database.DisposeAsync();

        await using var reopened = await PantsDatabase.OpenAsync(options);
        await using var read = await reopened.BeginTransactionAsync(
            reopened.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Equal(
            "accepted-value"u8.ToArray(),
            (await read.GetAsync("accepted-key"u8.ToArray()))?.ToArray());
    }

    static async Task WaitForLateResponseAsync(IPantsDatabase database)
    {
        var started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started) < AssertionTimeout)
        {
            var metrics = await database.GetRuntimeMetricsAsync();
            if (metrics.RuntimeLateResponsesTotal >= 1)
            {
                return;
            }

            await Task.Yield();
        }

        throw new TimeoutException("The accepted cloud obligation did not finish.");
    }

    sealed class BlockingCloudWalUploadFailpointHandler : IFailpointHandler, IDisposable
    {
        readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly ManualResetEventSlim _release = new(false);
        int _hit;

        public void Hit(Failpoint failpoint)
        {
            if (failpoint != Failpoint.BeforeCloudWalUpload ||
                Interlocked.CompareExchange(ref _hit, 1, 0) != 0)
            {
                return;
            }

            _entered.TrySetResult();
            if (!_release.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("Timed out waiting to release cloud WAL upload.");
            }
        }

        public async Task WaitUntilEnteredAsync(TimeSpan timeout) =>
            await _entered.Task.WaitAsync(timeout);

        public void Release() => _release.Set();

        public void Dispose()
        {
            _release.Set();
            _release.Dispose();
        }
    }
}

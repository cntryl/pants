using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

public sealed class PantsCloudProviderInitializationTests
{
    static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldCancelProviderInitializationAndDisposeOpenedStoresWhenStartupDeadlineExpires(
        bool cleanupFails)
    {
        using var directory = new TemporaryDirectory();
        using var cancellation = new CancellationTokenSource();
        var wal = new DisposalTrackingCloudObjectStore(cleanupFails ? new IOException("Cleanup failed.") : null);
        var sst = new DisposalTrackingCloudObjectStore();
        var pending = new DelegatingCloudProvider(async token =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Initialization must be cancelled.");
        });
        var topology = new PantsCloudStorageTopology(
            new PantsCloudStorageLocation(new DelegatingCloudProvider(_ => ValueTask.FromResult<ICloudObjectStore>(wal)), "wal"),
            new PantsCloudStorageLocation(new DelegatingCloudProvider(_ => ValueTask.FromResult<ICloudObjectStore>(sst)), "sst"),
            new PantsCloudStorageLocation(pending, "control"));
        var opening = PantsDatabase.OpenAsync(
            PantsOpenOptions.CloudMulti(directory.Path, topology)
                .WithStorageTimeout(TimeSpan.FromMilliseconds(10))
                .WithRuntimeResponseTimeout(TimeSpan.FromMilliseconds(100)),
            cancellation.Token).AsTask();
        try
        {
            await pending.Started.Task.WaitAsync(Watchdog);
            await Assert.ThrowsAsync<PantsTimeoutException>(() => opening.WaitAsync(Watchdog));

            Assert.True(pending.OpenToken.IsCancellationRequested);
            Assert.True(pending.Finished.Task.IsCompleted);
            Assert.Equal(1, wal.DisposeCount);
            Assert.Equal(1, sst.DisposeCount);
        }
        finally
        {
            await cancellation.CancelAsync();
            _ = await Record.ExceptionAsync(() => opening.WaitAsync(Watchdog));
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ShouldForwardPreflightCancellationOrDeadlineIntoProviderInitialization(
        bool topologyOverload,
        bool callerCancels)
    {
        using var cancellation = new CancellationTokenSource();
        var gate = new TaskCompletionSource<ICloudObjectStore>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new DelegatingCloudProvider(async token => await gate.Task.WaitAsync(token));
        var location = new PantsCloudStorageLocation(provider, "preflight");
        var options = new PantsCloudPreflightOptions(callerCancels ? TimeSpan.FromSeconds(30) : TimeSpan.FromMilliseconds(100));
        var preflight = topologyOverload
            ? PantsCloudStorageTopology.Shared(location).PreflightAsync(options, cancellation.Token).AsTask()
            : location.PreflightAsync(options, cancellation.Token).AsTask();
        try
        {
            await provider.Started.Task.WaitAsync(Watchdog);
            if (callerCancels)
            {
                await cancellation.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preflight.WaitAsync(Watchdog));
            }
            else
            {
                var report = await preflight.WaitAsync(Watchdog);
                Assert.False(report.IsReady);
                Assert.Contains(report.Findings, finding => finding.FailureKind == PantsCloudFailureKind.Timeout);
            }

            Assert.True(provider.OpenToken.CanBeCanceled);
            Assert.True(provider.OpenToken.IsCancellationRequested);
            await provider.Finished.Task.WaitAsync(Watchdog);
        }
        finally
        {
            gate.TrySetCanceled();
            await provider.Finished.Task.WaitAsync(Watchdog);
            _ = await Record.ExceptionAsync(() => preflight.WaitAsync(Watchdog));
        }
    }
}

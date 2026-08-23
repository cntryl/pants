using System.Diagnostics.Metrics;

namespace Pants.Tests;

[Collection(RuntimeDiagnosticsTestGroup.Name)]
public sealed class PantsRuntimeCommandCancellationContractTests
{
    static readonly TimeSpan AssertionTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ShouldNotLeakTransactionWhenBeginCallerCancelsAfterAdmission()
    {
        using var directory = new TemporaryDirectory();
        using var failpoint = new RuntimeMetricsResponseFailpointHandler();
        await using var database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Local(directory.Path).WithCoordinatorQueueCapacityForTesting(1),
            new PantsRuntimeDependencies(failpoint));
        var blockedMetrics = database.GetRuntimeMetricsAsync().AsTask();

        await failpoint.WaitUntilEnteredAsync(AssertionTimeout);
        using var cancellation = new CancellationTokenSource();
        var begin = database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly,
            cancellation.Token).AsTask();
        cancellation.Cancel();
        failpoint.Release();

        _ = await blockedMetrics.WaitAsync(AssertionTimeout);
        IPantsTransaction? transaction = null;
        try
        {
            transaction = await begin.WaitAsync(AssertionTimeout);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }

        if (transaction is not null)
        {
            await transaction.DisposeAsync();
        }

        var metrics = await database.GetRuntimeMetricsAsync()
            .AsTask().WaitAsync(AssertionTimeout);
        Assert.Equal(0, metrics.ActiveSnapshots);

        await database.ShutdownAsync(AssertionTimeout);
    }

    [Fact]
    public async Task ShouldMapAdmittedEngineCancellationToAbortedWithoutRejectAccounting()
    {
        long rejectedCommands = 0;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, activeListener) =>
            {
                if (instrument.Meter.Name == PantsDiagnostics.Meter.Name &&
                    instrument.Name == "pants.runtime.commands_rejected")
                {
                    activeListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, _, _) =>
            Interlocked.Add(ref rejectedCommands, measurement));
        listener.Start();

        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Local(directory.Path),
            new PantsRuntimeDependencies(new EngineCancellationFailpointHandler()));
        await using (var transaction = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            transaction.Put("engine-canceled"u8.ToArray(), "value"u8.ToArray());
            var exception = await Assert.ThrowsAsync<PantsAbortedException>(
                () => transaction.CommitAsync(PantsWriteOptions.Sync).AsTask());

            Assert.IsAssignableFrom<OperationCanceledException>(exception.InnerException);
        }

        Assert.Equal(0, Volatile.Read(ref rejectedCommands));
        await using var accepted = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        accepted.Put("accepted-after-engine-cancel"u8.ToArray(), "value"u8.ToArray());
        await accepted.CommitAsync(PantsWriteOptions.Sync);
    }
}

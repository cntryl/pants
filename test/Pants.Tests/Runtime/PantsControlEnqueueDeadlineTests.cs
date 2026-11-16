using Cntryl.Pants.Observability;
using Cntryl.Pants.Support.Failpoints;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Runtime;

public sealed class PantsControlEnqueueDeadlineTests
{
    static readonly TimeSpan ResponseTimeout = TimeSpan.FromMilliseconds(100);

    public enum ControlRequest
    {
        BeginTransaction,
        ListColumnFamilies,
        Flush,
        WaitForWriteStallClear
    }

    [Theory]
    [InlineData(ControlRequest.BeginTransaction)]
    [InlineData(ControlRequest.ListColumnFamilies)]
    [InlineData(ControlRequest.Flush)]
    [InlineData(ControlRequest.WaitForWriteStallClear)]
    public async Task ShouldTimeOutControlRequestThatCannotEnqueueOnFullRuntimeQueue(ControlRequest kind)
    {
        using var directory = new TemporaryDirectory();
        using var failpoint = new RuntimeMetricsResponseFailpointHandler();
        var time = new ManualSchedulingTimeProvider();
        await using var database = await OpenSaturableAsync(directory.Path, failpoint, time);
        var saturation = await SaturateRuntimeQueueAsync(database, failpoint);
        var request = Task.Run(() => SendAsync(database, kind));

        try
        {
            var completed = await AdvanceUntilCompletedAsync(time, request);

            Assert.True(
                completed,
                $"{kind} was still waiting to enqueue on the full runtime queue after its runtime deadline expired.");
            var exception = await Assert.ThrowsAsync<PantsTimeoutException>(() => request);
            Assert.DoesNotContain("after admission", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("outcome is unknown", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            failpoint.Release();
            await ObserveAsync(request, saturation);
        }
    }

    [Fact]
    public async Task ShouldReportDefiniteTimeoutAndNeverApplyDdlThatCannotEnqueueOnFullRuntimeQueue()
    {
        using var directory = new TemporaryDirectory();
        using var failpoint = new RuntimeMetricsResponseFailpointHandler();
        var time = new ManualSchedulingTimeProvider();
        var database = await OpenSaturableAsync(directory.Path, failpoint, time);
        try
        {
            var saturation = await SaturateRuntimeQueueAsync(database, failpoint);
            var create = Task.Run(async () => _ = await database.ColumnFamilies.CreateAsync("never-admitted"));

            try
            {
                var completed = await AdvanceUntilCompletedAsync(time, create);

                Assert.True(
                    completed,
                    "The DDL request was still waiting to enqueue on the full runtime queue after its " +
                    "runtime deadline expired.");
                // The command never reached the runtime, so its outcome is known: nothing was applied.
                var exception = await Assert.ThrowsAsync<PantsTimeoutException>(() => create);
                Assert.DoesNotContain("after admission", exception.Message, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("outcome is unknown", exception.Message, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                failpoint.Release();
                await ObserveAsync(create, saturation);
            }

            Assert.Null(await database.ColumnFamilies.GetAsync("never-admitted")
                .AsTask().WaitAsync(TestTimeouts.Expected));
            await database.ShutdownAsync(TestTimeouts.Expected);
        }
        finally
        {
            await database.DisposeAsync();
        }

        await using var reopened = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path));
        Assert.Null(await reopened.ColumnFamilies.GetAsync("never-admitted"));
        Assert.DoesNotContain(
            await reopened.ColumnFamilies.ListAsync(),
            family => family.Name == "never-admitted");
    }

    static async Task<IPantsDatabase> OpenSaturableAsync(
        string path,
        RuntimeMetricsResponseFailpointHandler failpoint,
        ManualSchedulingTimeProvider time) =>
        await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Local(path)
                .WithBackgroundCompaction(false)
                .WithCoordinatorQueueCapacityForTesting(1)
                .WithStorageTimeout(TimeSpan.FromMilliseconds(5))
                .WithRuntimeResponseTimeout(ResponseTimeout),
            new RuntimeDependencies(failpoint, runtimeTimeProvider: time));

    /// <summary>
    ///     Holds the runtime owner inside one request and fills the single queue slot behind it, so
    ///     no later request can be admitted until the failpoint is released.
    /// </summary>
    static async Task<Task> SaturateRuntimeQueueAsync(
        IPantsDatabase database,
        RuntimeMetricsResponseFailpointHandler failpoint)
    {
        // Startup queues its own commands before reading the mailbox; cross that barrier first.
        _ = await database.Diagnostics.GetStorageLayoutAsync().AsTask().WaitAsync(TestTimeouts.Expected);
        var held = database.Diagnostics.GetRuntimeMetricsAsync().AsTask();
        await failpoint.WaitUntilEnteredAsync(TestTimeouts.Expected);
        // The owner is parked, so this request synchronously takes the only queue slot.
        var queued = database.Diagnostics.GetStorageLayoutAsync().AsTask();
        return Task.WhenAll(held, queued);
    }

    static async Task SendAsync(IPantsDatabase database, ControlRequest kind)
    {
        var family = database.ColumnFamilies.DefaultFamily;
        switch (kind)
        {
            case ControlRequest.BeginTransaction:
                await using (await database.Transactions.BeginAsync(family, PantsTransactionMode.ReadWrite))
                {
                }

                break;
            case ControlRequest.ListColumnFamilies:
                _ = await database.ColumnFamilies.ListAsync();
                break;
            case ControlRequest.Flush:
                await database.Maintenance.FlushAsync(family);
                break;
            case ControlRequest.WaitForWriteStallClear:
                _ = await database.Maintenance.WaitForWriteStallClearAsync(family, TimeSpan.FromHours(1));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    /// <summary>
    ///     Moves the runtime clock past the request's deadline. A request may not have reached the
    ///     queue when the first advance lands, so the clock keeps moving until the request settles
    ///     or the bounded number of deadlines has elapsed.
    /// </summary>
    static async Task<bool> AdvanceUntilCompletedAsync(ManualSchedulingTimeProvider time, Task request)
    {
        for (var attempt = 0; attempt < 40 && !request.IsCompleted; attempt++)
        {
            time.Advance(ResponseTimeout);
            try
            {
                await request.WaitAsync(TimeSpan.FromMilliseconds(100));
            }
            catch (Exception)
            {
                // Either the request is still pending or it settled; the caller asserts its outcome.
            }
        }

        return request.IsCompleted;
    }

    static async Task ObserveAsync(params Task[] tasks)
    {
        foreach (var task in tasks)
        {
            try
            {
                await task.WaitAsync(TestTimeouts.Expected);
            }
            catch (Exception)
            {
                // Only quiescence matters here; each test asserts the outcome it cares about.
            }
        }
    }
}

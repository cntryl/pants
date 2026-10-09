namespace Cntryl.Pants.Storage;

public sealed class MaintenanceMemoryGateTests
{
    [Fact]
    public async Task ShouldServeWaitersInArrivalOrder()
    {
        var gate = new MaintenanceMemoryGate();
        var first = await gate.EnterAsync(CancellationToken.None);
        var order = new List<string>();

        var wholePool = EnterAndRecordAsync(gate, order, "whole-pool");
        var compaction = EnterAndRecordAsync(gate, order, "compaction");
        first.Dispose();
        await Task.WhenAll(wholePool, compaction).WaitAsync(TestTimeouts.Expected);

        Assert.Equal(["whole-pool", "compaction"], order);
    }

    [Fact]
    public async Task ShouldSkipCanceledWaiterAndServeTheNext()
    {
        var gate = new MaintenanceMemoryGate();
        var first = await gate.EnterAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();

        var canceled = gate.EnterAsync(cancellation.Token).AsTask();
        var next = gate.EnterAsync(CancellationToken.None).AsTask();
        await cancellation.CancelAsync();
        first.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        using var hold = await next.WaitAsync(TestTimeouts.Expected);
    }

    [Fact]
    public async Task ShouldAdmitWholePoolPublicationWhileCompactionRoundsKeepCycling()
    {
        var budget = new ResourceBudget(1024 * 1024);
        var gate = new MaintenanceMemoryGate();
        using var stop = new CancellationTokenSource();
        var rounds = Enumerable.Range(0, 2)
            .Select(_ => CycleCompactionRoundsAsync(gate, budget, stop.Token))
            .ToArray();
        await Task.Delay(TimeSpan.FromMilliseconds(20));

        using (await SstPublicationAdmission.Gated(budget, gate)
                   .AdmitAsync(64L * 1024 * 1024, CancellationToken.None)
                   .AsTask()
                   .WaitAsync(TestTimeouts.Expected))
        {
            Assert.Equal(budget.Limit, budget.Current);
        }

        await stop.CancelAsync();
        await Task.WhenAll(rounds);
    }

    static async Task EnterAndRecordAsync(MaintenanceMemoryGate gate, List<string> order, string name)
    {
        using var hold = await gate.EnterAsync(CancellationToken.None);
        lock (order)
        {
            order.Add(name);
        }
    }

    static async Task CycleCompactionRoundsAsync(
        MaintenanceMemoryGate gate,
        ResourceBudget budget,
        CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                using var hold = await gate.EnterAsync(cancellationToken);
                using var merge = budget.Reserve(budget.Limit / 2);
                await Task.Yield();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}

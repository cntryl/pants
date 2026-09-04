namespace Cntryl.Pants.Runtime;

public sealed class RuntimeResponseRegistryTests
{
    [Fact]
    public async Task ShouldGiveCompletionExactlyOneTerminalOwner()
    {
        var telemetry = new RuntimeTelemetry();
        var registry = new RuntimeResponseRegistry(telemetry, TimeProvider.System);
        var slot = new RuntimeResponseSlot<int>(registry, 1, "CompleteFirst");

        slot.Complete(42);
        slot.Complete(43);
        slot.Fail(new InvalidOperationException("ignored"));

        Assert.False(slot.Abandon(TimeSpan.FromSeconds(1)));
        Assert.Equal(42, await slot.Response);
        Assert.Equal(0, registry.PendingCount);
        Assert.Equal(0, telemetry.RuntimeAbandonedRequests);
        Assert.Equal(0, telemetry.RuntimeLateResponses);
    }

    [Fact]
    public void ShouldClassifyAnAbandonedLateResponseExactlyOnce()
    {
        var telemetry = new RuntimeTelemetry();
        var registry = new RuntimeResponseRegistry(telemetry, TimeProvider.System);
        var slot = new RuntimeResponseSlot<int>(registry, 1, "AbandonFirst");

        Assert.True(slot.Abandon(TimeSpan.FromSeconds(1)));
        slot.Complete(42);
        slot.Complete(43);
        slot.Fail(new InvalidOperationException("ignored"));

        Assert.False(slot.Response.IsCompleted);
        Assert.Equal(0, registry.PendingCount);
        Assert.Equal(0, registry.AbandonedMetadataCount);
        Assert.Equal(1, telemetry.RuntimeAbandonedRequests);
        Assert.Equal(1, telemetry.RuntimeLateResponses);
    }

    [Fact]
    public void ShouldBoundAndExpireAbandonedRequestMetadata()
    {
        var timeProvider = new MutableUtcTimeProvider();
        var telemetry = new RuntimeTelemetry(timeProvider);
        var registry = new RuntimeResponseRegistry(telemetry, timeProvider);

        for (var requestId = 1; requestId <= 1100; requestId++)
        {
            var slot = new RuntimeResponseSlot<bool>(
                registry,
                requestId,
                "BoundedMetadata");
            Assert.True(slot.Abandon(TimeSpan.FromSeconds(1)));
        }

        Assert.Equal(1024, registry.AbandonedMetadataCount);
        Assert.Equal(1100, telemetry.RuntimeAbandonedRequests);

        timeProvider.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(0, registry.AbandonedMetadataCount);
        Assert.Equal(0, registry.PendingCount);
    }

    [Fact]
    public async Task ShouldSerializeConcurrentCompletionAndAbandonment()
    {
        var telemetry = new RuntimeTelemetry();
        var registry = new RuntimeResponseRegistry(telemetry, TimeProvider.System);
        var abandoned = 0;
        const int attempts = 1000;

        await Parallel.ForEachAsync(Enumerable.Range(1, attempts), async (requestId, _) =>
        {
            var slot = new RuntimeResponseSlot<int>(registry, requestId, "Race");
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var complete = Task.Run(
                async () =>
                {
                    await start.Task;
                    slot.Complete(requestId);
                },
                CancellationToken.None);
            var abandon = Task.Run(
                async () =>
                {
                    await start.Task;
                    if (slot.Abandon(TimeSpan.FromSeconds(1)))
                    {
                        Interlocked.Increment(ref abandoned);
                    }
                },
                CancellationToken.None);
            start.SetResult();
            await Task.WhenAll(complete, abandon);
        });

        Assert.Equal(0, registry.PendingCount);
        Assert.Equal(0, registry.AbandonedMetadataCount);
        Assert.Equal(abandoned, telemetry.RuntimeAbandonedRequests);
        Assert.Equal(abandoned, telemetry.RuntimeLateResponses);
    }

    sealed class MutableUtcTimeProvider : TimeProvider
    {
        DateTimeOffset _utcNow = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan elapsed) => _utcNow += elapsed;
    }
}

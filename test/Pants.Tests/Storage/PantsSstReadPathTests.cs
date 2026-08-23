namespace Cntryl.Pants.Tests.Storage;

public sealed class PantsSstReadPathTests
{
    [Fact]
    public void ShouldBuildBlockBloomsWithoutFalseNegativesAndRejectAbsentKeys()
    {
        var entries = Enumerable.Range(0, 256)
            .Select(index => new MidgeSstEntry(
                TestBytes.FromString($"key-{index:D4}"),
                new byte[1024],
                checked((ulong)index + 1),
                null,
                false))
            .ToArray();
        var bytes = MidgeSstCodec.Encode(entries, [], PantsPerformanceGoal.Latency);

        Assert.All(entries, entry =>
        {
            var decision = MidgeSstCodec.GetPointReadDecision(bytes, entry.Key);
            Assert.Equal(1, decision.BloomChecks);
            Assert.Equal(1, decision.CandidateBlocks);
            Assert.Equal(1, decision.BlocksRead);
            Assert.False(decision.Rejected);
        });

        Assert.Contains(
            Enumerable.Range(0, 256),
            index => MidgeSstCodec
                .GetPointReadDecision(bytes, TestBytes.FromString($"key-{index:D4}-absent"))
                .Rejected);
    }
}

using Cntryl.Pants.Reporting;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Performance.Checkpoint;

public sealed class CheckpointCampaignEntryTests
{
    [Fact]
    public async Task ShouldRejectIncompleteCheckpointCampaignBeforeReturningSuccess()
    {
        using var directory = new TemporaryDirectory();

        // Execute the benchmark command; the existing CLI also has a Program type.
        var invocation = typeof(PracticalBenchmarkRunner).Assembly.EntryPoint!.Invoke(null,
            [new[] { "checkpoint-readback", directory.Path }]);
        var exit = invocation is Task<int> pending ? await pending : Assert.IsType<int>(invocation);

        Assert.NotEqual(0, exit);
    }
}

namespace Pants.Tests;

public sealed class HybridStorageBudgetPolicyTests
{
    [Theory]
    [InlineData(89, 0)]
    [InlineData(90, 1)]
    [InlineData(94, 1)]
    [InlineData(95, 2)]
    [InlineData(97, 2)]
    [InlineData(98, 3)]
    public void ShouldClassifyExactWatermarkBoundaries(
        long committedBytes,
        int expected)
    {
        var policy = new HybridStorageBudgetPolicy(100);

        var actual = policy.GetWatermark(committedBytes);

        Assert.Equal((HybridStorageWatermark)expected, actual);
    }

    [Fact]
    public void ShouldUseTwoGibibytesForDefaultLocalCloudBudget()
    {
        Assert.Equal(
            2L * 1024 * 1024 * 1024,
            HybridStorageBudgetPolicy.DefaultMaximumLocalBytes);
    }
}

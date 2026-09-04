namespace Cntryl.Pants.Runtime.Internal;

public sealed class RuntimePlanMemoryValidationTests
{
    [Fact]
    public void ShouldRejectAnExplicitFlushThresholdThatExceedsTheResolvedMemtableSizeLimit()
    {
        var options = PantsOpenOptions.Create(
            new PantsStorageConfiguration.InMemory(),
            memory: new PantsMemoryConfiguration(
                Budget: PantsMemoryBudget.FromBytes(64L * 1024 * 1024),
                BlockCachePolicy: PantsBlockCachePolicy.Lru,
                MemtableFlushThresholdBytes: 4L * 1024 * 1024 * 1024));

        var exception = Assert.Throws<PantsInvalidArgumentException>(() => RuntimePlan.Resolve(options));

        Assert.Contains("flush threshold", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}

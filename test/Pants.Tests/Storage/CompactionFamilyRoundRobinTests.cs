using Cntryl.Pants.Storage.Internal.Compaction.Compaction;

namespace Cntryl.Pants.Storage;

public sealed class CompactionFamilyRoundRobinTests
{
    [Fact]
    public void ShouldRotateCriticalFamiliesAfterTheLastScheduledFamilyAndWrap()
    {
        uint[] familyIds = [2, 0, 1, 2];
        var criticalFamilies = new HashSet<uint> { 0, 1 };

        Assert.Equal(
            [0u, 1u],
            CompactionFamilyRoundRobin.OrderCriticalFamilies(familyIds, criticalFamilies, null));
        Assert.Equal(
            [1u, 0u],
            CompactionFamilyRoundRobin.OrderCriticalFamilies(familyIds, criticalFamilies, 0));
        Assert.Equal(
            [0u, 1u],
            CompactionFamilyRoundRobin.OrderCriticalFamilies(familyIds, criticalFamilies, 1));
    }
}

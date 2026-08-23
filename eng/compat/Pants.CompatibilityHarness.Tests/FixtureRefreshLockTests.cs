using Pants.CompatibilityHarness.Internal;

namespace Pants.CompatibilityHarness.Tests;

public sealed class FixtureRefreshLockTests
{
    [Fact]
    public async Task ShouldSerializeRefreshesThroughGitDirectoryLock()
    {
        using var directory = new CompatibilityTestDirectory();
        var repository = directory.CreateRepository("fixture", "manifest");
        await FixtureRefreshTargetGuardTests.InitializeGitRepositoryAsync(repository.Root);
        var aliasChild = Path.Combine(repository.Root, "alias-child");
        _ = Directory.CreateDirectory(aliasChild);
        using var first = await FixtureRefreshLock.AcquireAsync(
            repository.Root,
            CancellationToken.None);

        _ = await Assert.ThrowsAsync<InvalidOperationException>(
            () => FixtureRefreshLock.AcquireAsync(
                aliasChild,
                CancellationToken.None));

        first.Dispose();
        using var second = await FixtureRefreshLock.AcquireAsync(
            repository.Root,
            CancellationToken.None);
    }
}

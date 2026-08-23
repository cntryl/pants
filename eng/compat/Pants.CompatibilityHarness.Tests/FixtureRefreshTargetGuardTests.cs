using Pants.CompatibilityHarness.Internal;

namespace Pants.CompatibilityHarness.Tests;

public sealed class FixtureRefreshTargetGuardTests
{
    [Fact]
    public async Task ShouldRequireForceGivenDirtyRefreshTarget()
    {
        using var directory = new CompatibilityTestDirectory();
        var repository = directory.CreateRepository("fixture", "manifest");
        await InitializeGitRepositoryAsync(repository.Root);
        File.WriteAllText(repository.ContractManifest, "changed-manifest");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => FixtureRefreshTargetGuard.EnsureSafeAsync(
                repository,
                forceRefresh: false,
                CancellationToken.None));

        Assert.Contains("uncommitted changes", exception.Message, StringComparison.Ordinal);
        await FixtureRefreshTargetGuard.EnsureSafeAsync(
            repository,
            forceRefresh: true,
            CancellationToken.None);
    }

    internal static async Task InitializeGitRepositoryAsync(string repositoryRoot)
    {
        _ = await ProcessRunner.RunAsync(
            "git",
            ["init", "--quiet", repositoryRoot],
            repositoryRoot,
            CancellationToken.None);
        _ = await ProcessRunner.RunAsync(
            "git",
            ["-C", repositoryRoot, "add", "."],
            repositoryRoot,
            CancellationToken.None);
        _ = await ProcessRunner.RunAsync(
            "git",
            [
                "-C",
                repositoryRoot,
                "-c",
                "user.name=Pants Tests",
                "-c",
                "user.email=pants-tests@example.invalid",
                "commit",
                "--quiet",
                "-m",
                "baseline"
            ],
            repositoryRoot,
            CancellationToken.None);
    }
}

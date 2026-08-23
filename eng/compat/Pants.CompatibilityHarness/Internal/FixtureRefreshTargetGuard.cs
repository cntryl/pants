namespace Cntryl.Pants.CompatibilityHarness.Internal;

internal static class FixtureRefreshTargetGuard
{
    public static async Task EnsureSafeAsync(
        PantsRepositoryPaths repository,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);

        var fixturePath = Path.GetRelativePath(
            repository.Root,
            repository.CompatibilityFixtures);
        var manifestPath = Path.GetRelativePath(
            repository.Root,
            repository.ContractManifest);
        var status = await ProcessRunner.RunAsync(
            "git",
            [
                "-C",
                repository.Root,
                "status",
                "--porcelain=v1",
                "--untracked-files=all",
                "--",
                fixturePath,
                manifestPath
            ],
            repository.Root,
            cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(status.StandardOutput))
        {
            return;
        }

        if (!forceRefresh)
        {
            throw new InvalidOperationException(
                status.FormatEvidence(
                    "Compatibility refresh targets contain uncommitted changes. Review or "
                    + "commit them before refreshing, or pass '--force' to replace them."));
        }

        Console.Error.WriteLine(
            "warning: --force is replacing uncommitted compatibility fixtures or manifest changes.");
    }
}

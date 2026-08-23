using System.Globalization;
using Cntryl.Pants.Observability;

namespace Cntryl.Pants.CompatibilityHarness.Internal;

internal static class FixtureRefreshRunner
{
    public static async Task RunAsync(
        string midgeCheckoutPath,
        bool forceRefresh,
        bool checkBaseline,
        CancellationToken cancellationToken)
    {
        var repository = PantsRepositoryPaths.Find();
        using var refreshLock = await FixtureRefreshLock.AcquireAsync(
            repository.Root,
            cancellationToken).ConfigureAwait(false);
        FixtureRefreshPublisher.Recover(repository, forceRefresh);
        await FixtureRefreshTargetGuard.EnsureSafeAsync(
            repository,
            forceRefresh,
            cancellationToken).ConfigureAwait(false);

        using var buildDirectory = new QualificationTemporaryDirectory();
        var midge = await MidgeCheckoutBuilder.BuildAsync(
            midgeCheckoutPath,
            buildDirectory.RootPath,
            MidgeDriverBuildMode.FixtureRefresh,
            cancellationToken).ConfigureAwait(false);
        Console.WriteLine(
            $"Midge driver build: {midge.BuildTime.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture)}s "
            + $"({MidgeCheckoutBuilder.RequiredCommit})");

        using var workspace = new FixtureRefreshWorkspace(repository);
        CopyCanonicalFixtures(midge.CheckoutPath, workspace.CompatibilityFixtures);
        CopyDriverDependencyLock(repository.Root, workspace.CompatibilityFixtures);
        await EmitFixturesAsync(midge, workspace.CompatibilityFixtures, cancellationToken)
            .ConfigureAwait(false);
        CompatibilityDatabaseFixtureDescriptorWriter.Write(workspace.CompatibilityFixtures);
        CompatibilityFixtureMetadataWriter.Write(workspace.CompatibilityFixtures);
        ContractManifestRefresher.Write(
            repository.ContractManifest,
            midge.CheckoutPath,
            workspace.ContractManifest);
        await ValidateDatabaseFixturesAsync(
            midge,
            workspace.CompatibilityFixtures,
            cancellationToken).ConfigureAwait(false);

        if (checkBaseline)
        {
            CompatibilityBaselineComparer.EnsureEquivalent(repository, workspace);
            Console.WriteLine(
                $"committed compatibility baseline matches "
                + $"{MidgeCheckoutBuilder.RequiredCommit}");
            return;
        }

        FixtureRefreshPublisher.Publish(repository, workspace);
        Console.WriteLine(
            $"refreshed compatibility fixtures and contract manifest at "
            + $"{MidgeCheckoutBuilder.RequiredCommit}");
    }

    static void CopyCanonicalFixtures(string midgeCheckoutPath, string fixtureRoot)
    {
        var source = Path.Combine(midgeCheckoutPath, "tests", "fixtures", "compatibility");
        var destination = Path.Combine(
            fixtureRoot,
            "Midge",
            MidgeCheckoutBuilder.RequiredCommit);
        DirectoryCopier.Copy(source, destination);
    }

    static void CopyDriverDependencyLock(string repositoryRoot, string fixtureRoot)
    {
        var source = Path.Combine(
            repositoryRoot,
            "eng",
            "compat",
            "MidgeDriver",
            "Cargo.lock");
        var destinationDirectory = Path.Combine(
            fixtureRoot,
            "Tooling",
            MidgeCheckoutBuilder.RequiredCommit);
        _ = Directory.CreateDirectory(destinationDirectory);
        File.Copy(source, Path.Combine(destinationDirectory, "Cargo.lock"), overwrite: false);
    }

    static async Task EmitFixturesAsync(
        MidgeDriverBuild midge,
        string fixtureRoot,
        CancellationToken cancellationToken)
    {
        var wireDestination = Path.Combine(
            fixtureRoot,
            "Wire",
            MidgeCheckoutBuilder.RequiredCommit);
        _ = await ProcessRunner.RunAsync(
            midge.ExecutablePath,
            ["emit-wire-goldens", wireDestination],
            midge.CheckoutPath,
            cancellationToken).ConfigureAwait(false);

        var storageDestination = Path.Combine(
            fixtureRoot,
            "Storage",
            MidgeCheckoutBuilder.RequiredCommit);
        _ = await ProcessRunner.RunAsync(
            midge.ExecutablePath,
            ["emit-storage-goldens", storageDestination],
            midge.CheckoutPath,
            cancellationToken).ConfigureAwait(false);
    }

    static async Task ValidateDatabaseFixturesAsync(
        MidgeDriverBuild midge,
        string fixtureRoot,
        CancellationToken cancellationToken)
    {
        var sha = MidgeCheckoutBuilder.RequiredCommit;
        var databasePaths = new[]
        {
            Path.Combine(fixtureRoot, "Midge", sha, "v3_populated_v4_sst_db"),
            Path.Combine(
                fixtureRoot,
                "Storage",
                sha,
                "databases",
                "midge-structured-v4-db")
        };

        foreach (var databasePath in databasePaths)
        {
            var before = DirectoryTreeFingerprint.Compute(databasePath);
            var pantsReport = await PantsDatabase.VerifyPathAsync(databasePath, cancellationToken)
                .ConfigureAwait(false);
            if (pantsReport.Health != PantsEngineHealth.Healthy || !pantsReport.Authoritative)
            {
                throw new InvalidDataException(
                    $"Pants rejected generated database fixture '{databasePath}' as "
                    + $"{pantsReport.Health}, authoritative={pantsReport.Authoritative}.");
            }

            _ = await ProcessRunner.RunAsync(
                midge.ExecutablePath,
                ["local-verify", databasePath],
                midge.CheckoutPath,
                cancellationToken).ConfigureAwait(false);
            var after = DirectoryTreeFingerprint.Compute(databasePath);
            if (!StringComparer.Ordinal.Equals(before, after))
            {
                throw new InvalidDataException(
                    $"Offline verification mutated generated database fixture '{databasePath}'.");
            }
        }
    }
}

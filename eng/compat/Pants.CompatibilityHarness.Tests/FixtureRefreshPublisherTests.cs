using System.Security.Cryptography;
using System.Text.Json;
using Pants.CompatibilityHarness.Internal;

namespace Pants.CompatibilityHarness.Tests;

public sealed class FixtureRefreshPublisherTests
{
    static readonly JsonSerializerOptions TransactionJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [Fact]
    public void ShouldPublishFixtureAndManifestAsSingleTransaction()
    {
        using var directory = new CompatibilityTestDirectory();
        var repository = directory.CreateRepository("old-fixture", "old-manifest");
        using var workspace = new FixtureRefreshWorkspace(repository);
        File.WriteAllText(
            Path.Combine(workspace.CompatibilityFixtures, "fixture.txt"),
            "new-fixture");
        File.WriteAllText(workspace.ContractManifest, "new-manifest");

        FixtureRefreshPublisher.Publish(repository, workspace);

        Assert.Equal(
            "new-fixture",
            File.ReadAllText(Path.Combine(repository.CompatibilityFixtures, "fixture.txt")));
        Assert.Equal("new-manifest", File.ReadAllText(repository.ContractManifest));
        AssertTransactionArtifactsAbsent(repository);
    }

    [Fact]
    public void ShouldRefuseUnknownPostInterruptionEditsUnlessForced()
    {
        using var directory = new CompatibilityTestDirectory();
        var repository = directory.CreateRepository("old-fixture", "old-manifest");
        PrepareInterruptedTransaction(repository, "new-fixture", "new-manifest", "prepared");
        var postInterruptionPath = Path.Combine(
            repository.CompatibilityFixtures,
            "post-interruption.txt");
        File.WriteAllText(postInterruptionPath, "user-edit");

        var exception = Assert.Throws<InvalidOperationException>(
            () => FixtureRefreshPublisher.Recover(repository, forceRefresh: false));

        Assert.Contains("unrecognized hash", exception.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(postInterruptionPath));
        Assert.True(Directory.Exists($"{repository.CompatibilityFixtures}.refresh-backup"));

        FixtureRefreshPublisher.Recover(repository, forceRefresh: true);

        Assert.Equal(
            "old-fixture",
            File.ReadAllText(Path.Combine(repository.CompatibilityFixtures, "fixture.txt")));
        Assert.Equal("old-manifest", File.ReadAllText(repository.ContractManifest));
        AssertTransactionArtifactsAbsent(repository);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void ShouldRollbackEveryPreparedPublicationBoundary(int completedMoves)
    {
        using var directory = new CompatibilityTestDirectory();
        var repository = directory.CreateRepository("old-fixture", "old-manifest");
        PrepareInterruptedTransaction(
            repository,
            "new-fixture",
            "new-manifest",
            "prepared",
            completedMoves);

        FixtureRefreshPublisher.Recover(repository, forceRefresh: false);

        Assert.Equal(
            "old-fixture",
            File.ReadAllText(Path.Combine(repository.CompatibilityFixtures, "fixture.txt")));
        Assert.Equal("old-manifest", File.ReadAllText(repository.ContractManifest));
        AssertTransactionArtifactsAbsent(repository);
    }

    [Fact]
    public void ShouldPreserveUnknownBackupGivenRecoveryHashMismatch()
    {
        using var directory = new CompatibilityTestDirectory();
        var repository = directory.CreateRepository("old-fixture", "old-manifest");
        PrepareInterruptedTransaction(repository, "new-fixture", "new-manifest", "prepared");
        var backupFixture = Path.Combine(
            $"{repository.CompatibilityFixtures}.refresh-backup",
            "fixture.txt");
        File.WriteAllText(backupFixture, "unknown-backup");

        var exception = Assert.Throws<InvalidDataException>(
            () => FixtureRefreshPublisher.Recover(repository, forceRefresh: true));

        Assert.Contains("fixture backup", exception.Message, StringComparison.Ordinal);
        Assert.Equal("unknown-backup", File.ReadAllText(backupFixture));
        Assert.Equal(
            "new-fixture",
            File.ReadAllText(Path.Combine(repository.CompatibilityFixtures, "fixture.txt")));
    }

    [Fact]
    public void ShouldCompleteCleanupGivenCommittedPublicationWhenRecoveryRuns()
    {
        using var directory = new CompatibilityTestDirectory();
        var repository = directory.CreateRepository("old-fixture", "old-manifest");
        PrepareInterruptedTransaction(repository, "new-fixture", "new-manifest", "committed");

        FixtureRefreshPublisher.Recover(repository, forceRefresh: false);

        Assert.Equal(
            "new-fixture",
            File.ReadAllText(Path.Combine(repository.CompatibilityFixtures, "fixture.txt")));
        Assert.Equal("new-manifest", File.ReadAllText(repository.ContractManifest));
        AssertTransactionArtifactsAbsent(repository);
    }

    [Fact]
    public void ShouldCompleteCleanupGivenCommittedBackupDeletionWasInterrupted()
    {
        using var directory = new CompatibilityTestDirectory();
        var repository = directory.CreateRepository("old-fixture", "old-manifest");
        PrepareInterruptedTransaction(repository, "new-fixture", "new-manifest", "committed");
        var fixturesBackup = $"{repository.CompatibilityFixtures}.refresh-backup";
        var fixturesCleanup = $"{repository.CompatibilityFixtures}.refresh-cleanup";
        Directory.Move(fixturesBackup, fixturesCleanup);
        File.Delete(Path.Combine(fixturesCleanup, "fixture.txt"));

        FixtureRefreshPublisher.Recover(repository, forceRefresh: false);

        Assert.Equal(
            "new-fixture",
            File.ReadAllText(Path.Combine(repository.CompatibilityFixtures, "fixture.txt")));
        Assert.Equal("new-manifest", File.ReadAllText(repository.ContractManifest));
        AssertTransactionArtifactsAbsent(repository);
    }

    [Fact]
    public void ShouldCompleteRollbackGivenPreparedDiscardDeletionWasInterrupted()
    {
        using var directory = new CompatibilityTestDirectory();
        var repository = directory.CreateRepository("old-fixture", "old-manifest");
        PrepareInterruptedTransaction(repository, "new-fixture", "new-manifest", "prepared");
        var fixturesBackup = $"{repository.CompatibilityFixtures}.refresh-backup";
        var fixturesDiscard = $"{repository.CompatibilityFixtures}.refresh-discard";
        Directory.Move(repository.CompatibilityFixtures, fixturesDiscard);
        Directory.Move(fixturesBackup, repository.CompatibilityFixtures);
        File.Delete(Path.Combine(fixturesDiscard, "fixture.txt"));

        FixtureRefreshPublisher.Recover(repository, forceRefresh: false);

        Assert.Equal(
            "old-fixture",
            File.ReadAllText(Path.Combine(repository.CompatibilityFixtures, "fixture.txt")));
        Assert.Equal("old-manifest", File.ReadAllText(repository.ContractManifest));
        AssertTransactionArtifactsAbsent(repository);
    }

    [Fact]
    public void ShouldPreserveChangedCommittedTargetsUnlessRecoveryIsForced()
    {
        using var directory = new CompatibilityTestDirectory();
        var repository = directory.CreateRepository("old-fixture", "old-manifest");
        PrepareInterruptedTransaction(repository, "new-fixture", "new-manifest", "committed");
        var liveFixture = Path.Combine(repository.CompatibilityFixtures, "fixture.txt");
        File.WriteAllText(liveFixture, "post-commit-edit");

        var exception = Assert.Throws<InvalidOperationException>(
            () => FixtureRefreshPublisher.Recover(repository, forceRefresh: false));

        Assert.Contains("changed after publication", exception.Message, StringComparison.Ordinal);
        Assert.Equal("post-commit-edit", File.ReadAllText(liveFixture));
        Assert.True(Directory.Exists($"{repository.CompatibilityFixtures}.refresh-backup"));

        FixtureRefreshPublisher.Recover(repository, forceRefresh: true);

        Assert.Equal("post-commit-edit", File.ReadAllText(liveFixture));
        Assert.Equal("new-manifest", File.ReadAllText(repository.ContractManifest));
        AssertTransactionArtifactsAbsent(repository);
    }

    static void PrepareInterruptedTransaction(
        PantsRepositoryPaths repository,
        string nextFixture,
        string nextManifest,
        string state,
        int completedMoves = 4)
    {
        var previousFixturesSha256 = DirectoryTreeFingerprint.Compute(
            repository.CompatibilityFixtures);
        var previousManifestSha256 = ComputeFileSha256(repository.ContractManifest);
        var nextFixtures = Path.Combine(repository.TestProject, "next-fixtures");
        var nextManifestPath = Path.Combine(repository.TestProject, "next-manifest.json");
        _ = Directory.CreateDirectory(nextFixtures);
        File.WriteAllText(Path.Combine(nextFixtures, "fixture.txt"), nextFixture);
        File.WriteAllText(nextManifestPath, nextManifest);
        var transaction = new FixtureRefreshTransaction(
            1,
            state,
            previousFixturesSha256,
            previousManifestSha256,
            DirectoryTreeFingerprint.Compute(nextFixtures),
            ComputeFileSha256(nextManifestPath));

        if (completedMoves == 0)
        {
            WriteTransactionMarker(repository, transaction);
            return;
        }

        Directory.Move(
            repository.CompatibilityFixtures,
            $"{repository.CompatibilityFixtures}.refresh-backup");
        if (completedMoves == 1)
        {
            WriteTransactionMarker(repository, transaction);
            return;
        }

        File.Move(
            repository.ContractManifest,
            $"{repository.ContractManifest}.refresh-backup");
        if (completedMoves == 2)
        {
            WriteTransactionMarker(repository, transaction);
            return;
        }

        Directory.Move(nextFixtures, repository.CompatibilityFixtures);
        if (completedMoves == 3)
        {
            WriteTransactionMarker(repository, transaction);
            return;
        }

        File.Move(nextManifestPath, repository.ContractManifest);
        WriteTransactionMarker(repository, transaction);
    }

    static void WriteTransactionMarker(
        PantsRepositoryPaths repository,
        FixtureRefreshTransaction transaction)
    {
        File.WriteAllText(
            $"{repository.ContractManifest}.refresh-transaction",
            JsonSerializer.Serialize(transaction, TransactionJsonOptions));
    }

    static void AssertTransactionArtifactsAbsent(PantsRepositoryPaths repository)
    {
        Assert.False(Directory.Exists($"{repository.CompatibilityFixtures}.refresh-backup"));
        Assert.False(Directory.Exists($"{repository.CompatibilityFixtures}.refresh-cleanup"));
        Assert.False(Directory.Exists($"{repository.CompatibilityFixtures}.refresh-discard"));
        Assert.False(File.Exists($"{repository.ContractManifest}.refresh-backup"));
        Assert.False(File.Exists($"{repository.ContractManifest}.refresh-cleanup"));
        Assert.False(File.Exists($"{repository.ContractManifest}.refresh-transaction"));
        Assert.False(File.Exists($"{repository.ContractManifest}.refresh-transaction.tmp"));
    }

    static string ComputeFileSha256(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
}

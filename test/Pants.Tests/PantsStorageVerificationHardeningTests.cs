namespace Pants.Tests;

public sealed class PantsStorageVerificationHardeningTests
{
    [Fact]
    public async Task ShouldNotCreateStorageDirectoriesGivenOfflineVerification()
    {
        using var directory = new TemporaryDirectory();
        await WriteEmptyFixtureAsync(directory.Path);

        PantsStorageVerificationReport report = await PantsDatabase.VerifyPathAsync(directory.Path);

        Assert.Equal(PantsEngineHealth.Healthy, report.Health);
        Assert.False(Directory.Exists(Path.Combine(directory.Path, "wal")));
        Assert.False(Directory.Exists(Path.Combine(directory.Path, "sst")));
    }

    [Theory]
    [InlineData("../escape.sst")]
    [InlineData("/tmp/escape.sst")]
    public async Task ShouldRejectUnsafeSstNameGivenPersistedIntent(string unsafeName)
    {
        using var directory = new TemporaryDirectory();
        await WriteEmptyFixtureAsync(directory.Path);
        string intent = $$"""
            [
              {
                "CompactionApplied": {
                  "removed": ["{{unsafeName}}"],
                  "added": []
                }
              }
            ]
            """;
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "intent_log.json"), intent);

        await Assert.ThrowsAsync<PantsCorruptionException>(
            () => PantsDatabase.VerifyPathAsync(directory.Path).AsTask());
    }

    [Fact]
    public async Task ShouldFenceLayoutMutationsWhileOnlineVerificationOwnsBarrier()
    {
        using var directory = new TemporaryDirectory();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PantsStorageVerificationDelegate verifier = async (_, cancellationToken) =>
        {
            started.SetResult();
            await release.Task.WaitAsync(cancellationToken);
            return new PantsStorageVerificationReport(
                0,
                1,
                0,
                0,
                0,
                null,
                0,
                0,
                0,
                true,
                PantsEngineHealth.Healthy,
                []);
        };
        await using IPantsDatabase database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Local(directory.Path),
            new PantsRuntimeDependencies(storageVerifier: verifier));
        Task<PantsStorageVerificationReport> verification = database
            .VerifyStorageAsync(TimeSpan.FromSeconds(5))
            .AsTask();
        await started.Task;

        await Assert.ThrowsAsync<PantsBusyException>(
            () => database.CreateColumnFamilyAsync("blocked").AsTask());
        release.SetResult();
        Assert.Equal(PantsEngineHealth.Healthy, (await verification).Health);
        Assert.Equal("allowed", (await database.CreateColumnFamilyAsync("allowed")).Name);
    }

    [Fact]
    public async Task ShouldReleaseVerificationBarrierGivenCallerDeadline()
    {
        using var directory = new TemporaryDirectory();
        PantsStorageVerificationDelegate verifier = async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable verifier continuation.");
        };
        await using IPantsDatabase database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Local(directory.Path),
            new PantsRuntimeDependencies(storageVerifier: verifier));

        await Assert.ThrowsAsync<PantsTimeoutException>(
            () => database.VerifyStorageAsync(TimeSpan.FromMilliseconds(10)).AsTask());

        Assert.Equal("after-timeout", (await database.CreateColumnFamilyAsync("after-timeout")).Name);
    }

    private static async Task WriteEmptyFixtureAsync(string path)
    {
        await File.WriteAllTextAsync(
            Path.Combine(path, "FORMAT"),
            "midge-format-version=3\n");
        await File.WriteAllTextAsync(
            Path.Combine(path, "manifest.json"),
            """
            {
              "last_persisted_sequence": 0,
              "files": [],
              "column_families": [],
              "next_wal_seq": 1,
              "next_sst_seqs": {},
              "edit_checkpoint_id": 0
            }
            """);
        await File.WriteAllBytesAsync(Path.Combine(path, "manifest.journal"), []);
    }
}

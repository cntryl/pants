using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pants.Tests;

public sealed class PantsStorageVerificationHardeningTests
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    [Fact]
    public async Task ShouldNotCreateStorageDirectoriesGivenOfflineVerification()
    {
        using var directory = new TemporaryDirectory();
        await WriteEmptyFixtureAsync(directory.Path);

        var report = await PantsDatabase.VerifyPathAsync(directory.Path);

        Assert.Equal(PantsEngineHealth.Healthy, report.Health);
        Assert.Equal(0, report.ManifestFilesVerified);
        Assert.False(Directory.Exists(Path.Combine(directory.Path, "wal")));
        Assert.False(Directory.Exists(Path.Combine(directory.Path, "sst")));
    }

    [Fact]
    public Task ShouldRejectParentSstNameGivenPersistedIntent() =>
        AssertUnsafeIntentSstNameRejectedAsync(
            "../escape.sst",
            """
            {
              "SstAdded": {
                "file_meta": {
                  "name": "../escape.sst"
                }
              }
            }
            """);

    [Fact]
    public Task ShouldRejectAbsoluteSstNameGivenPersistedIntent() =>
        AssertUnsafeIntentSstNameRejectedAsync(
            "/tmp/escape.sst",
            """
            {
              "CompactionApplied": {
                "removed": ["/tmp/escape.sst"],
                "added": []
              }
            }
            """);

    static async Task AssertUnsafeIntentSstNameRejectedAsync(
        string unsafeName,
        string intentEntry)
    {
        using var directory = new TemporaryDirectory();
        await WriteEmptyFixtureAsync(directory.Path);
        var intent = $"[{intentEntry}]";
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "intent_log.json"), intent);

        var exception = await Assert.ThrowsAsync<PantsCorruptionException>(
            () => PantsDatabase.VerifyPathAsync(directory.Path).AsTask());

        Assert.Contains(unsafeName, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../escape.sst")]
    [InlineData("/tmp/escape.sst")]
    public async Task ShouldRejectUnsafeSstNameGivenPersistedManifest(string unsafeName)
    {
        using var directory = new TemporaryDirectory();
        await WriteSingleSstFixtureAsync(
            directory.Path,
            file => file.Name = unsafeName);

        await Assert.ThrowsAsync<PantsCorruptionException>(
            () => PantsDatabase.VerifyPathAsync(directory.Path).AsTask());
    }

    [Fact]
    public async Task ShouldRejectOwnedSstGivenManifestChecksumIsMissing()
    {
        using var directory = new TemporaryDirectory();
        await WriteSingleSstFixtureAsync(
            directory.Path,
            file => file.ContentCrc32C = null);

        await Assert.ThrowsAsync<PantsCorruptionException>(
            () => PantsDatabase.VerifyPathAsync(directory.Path).AsTask());
    }

    [Fact]
    public async Task ShouldRejectOwnedSstGivenManifestKeyRangeDiffersFromContents()
    {
        using var directory = new TemporaryDirectory();
        await WriteSingleSstFixtureAsync(
            directory.Path,
            file => file.SmallestKey = "different"u8.ToArray()
                .Select(static value => (int)value)
                .ToArray());

        await Assert.ThrowsAsync<PantsCorruptionException>(
            () => PantsDatabase.VerifyPathAsync(directory.Path).AsTask());
    }

    [Fact]
    public async Task ShouldRejectOwnedSstGivenManifestSequenceRangeDiffersFromContents()
    {
        using var directory = new TemporaryDirectory();
        await WriteSingleSstFixtureAsync(
            directory.Path,
            file => file.LargestSequence = 8);

        await Assert.ThrowsAsync<PantsCorruptionException>(
            () => PantsDatabase.VerifyPathAsync(directory.Path).AsTask());
    }

    [Fact]
    public async Task ShouldReportDegradedHealthGivenOfflineVerificationFindsOrphanSst()
    {
        using var directory = new TemporaryDirectory();
        await WriteEmptyFixtureAsync(directory.Path);
        var sstDirectory = Directory.CreateDirectory(Path.Combine(directory.Path, "sst"));
        await File.WriteAllTextAsync(Path.Combine(sstDirectory.FullName, "orphan.sst"), "orphan");

        var report = await PantsDatabase.VerifyPathAsync(directory.Path);

        Assert.Equal(PantsEngineHealth.Degraded, report.Health);
        Assert.Contains(
            report.Warnings,
            warning => warning.Contains("orphan.sst", StringComparison.Ordinal));
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
        var verification = database
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

    static async Task WriteEmptyFixtureAsync(string path)
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

    static async Task WriteSingleSstFixtureAsync(
        string path,
        Action<MidgeFileMeta>? configure = null)
    {
        var key = "key"u8.ToArray();
        var bytes = MidgeSstCodec.Encode(
            [new MidgeSstEntry(key, "value"u8.ToArray(), 7, null, false)],
            [],
            PantsPerformanceGoal.Latency);
        const string name = "000000_00_00000000000000000001.sst";
        var file = new MidgeFileMeta
        {
            Name = name,
            Level = 0,
            SizeBytes = checked((ulong)bytes.Length),
            ContentCrc32C = MidgeDiskFormat.Crc32C(bytes),
            ColumnFamilyId = 0,
            SstSequence = 1,
            SmallestKey = key.Select(static value => (int)value).ToArray(),
            LargestKey = key.Select(static value => (int)value).ToArray(),
            SmallestSequence = 7,
            LargestSequence = 7
        };
        configure?.Invoke(file);
        var manifest = new MidgeManifest
        {
            LastPersistedSequence = 7,
            Files = [file],
            NextSstSeqs = new Dictionary<uint, ulong> { [0] = 2 },
            EditCheckpointId = 1
        };

        await File.WriteAllTextAsync(Path.Combine(path, "FORMAT"), "midge-format-version=3\n");
        await File.WriteAllTextAsync(
            Path.Combine(path, "manifest.json"),
            JsonSerializer.Serialize(manifest, JsonOptions));
        await File.WriteAllBytesAsync(Path.Combine(path, "manifest.journal"), []);
        Directory.CreateDirectory(Path.Combine(path, "sst"));
        await File.WriteAllBytesAsync(Path.Combine(path, "sst", name), bytes);
    }
}

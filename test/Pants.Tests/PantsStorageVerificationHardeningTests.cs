using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pants.Tests;

public sealed class PantsStorageVerificationHardeningTests
{
    static readonly TimeSpan AssertionTimeout = TimeSpan.FromSeconds(5);
    static readonly TimeSpan VerifierSafetyTimeout = TimeSpan.FromSeconds(10);
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
    public async Task ShouldReportLocalStorageAsAuthoritativeGivenRecoveryIntentsRemain()
    {
        using var directory = new TemporaryDirectory();
        await WriteEmptyFixtureAsync(directory.Path);
        await File.WriteAllTextAsync(
            Path.Combine(directory.Path, "intent_log.json"),
            """
            [
              {
                "WalSynced": {
                  "segment_id": 7,
                  "seqno": 11
                }
              }
            ]
            """);

        var report = await PantsDatabase.VerifyPathAsync(directory.Path);

        Assert.True(report.Authoritative);
        Assert.Equal(1, report.IntentEntriesLoaded);
    }

    [Fact]
    public async Task ShouldVerifyOnlineLayoutWithinCallerTimeout()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path));

        var report = await database.VerifyStorageAsync(TimeSpan.FromSeconds(2));

        Assert.True(report.Authoritative);
    }

    [Fact]
    public async Task ShouldHonorCallerTimeoutGivenVerifierBlocksBeforeReturning()
    {
        using var directory = new TemporaryDirectory();
        using var release = new ManualResetEventSlim(initialState: false);
        var barrierAcquired = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PantsVerificationBarrierResponseDelegate barrierResponse = () =>
        {
            barrierAcquired.SetResult();
            return ValueTask.CompletedTask;
        };
        PantsStorageVerificationDelegate verifier = (_, _) =>
        {
            started.SetResult();
            if (!release.Wait(VerifierSafetyTimeout, CancellationToken.None))
            {
                throw new TimeoutException("Timed out waiting to release the verifier.");
            }

            return ValueTask.FromResult(HealthyReport());
        };
        await using IPantsDatabase database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Local(directory.Path),
            new PantsRuntimeDependencies(
                storageVerifier: verifier,
                verificationBarrierResponse: barrierResponse));
        var verification = database.VerifyStorageAsync(TimeSpan.FromSeconds(1)).AsTask();

        try
        {
            await barrierAcquired.Task.WaitAsync(AssertionTimeout);
            await started.Task.WaitAsync(AssertionTimeout);
            Assert.False(verification.IsCompleted);
            await Assert.ThrowsAsync<PantsTimeoutException>(
                () => verification.WaitAsync(AssertionTimeout));
            await Assert.ThrowsAsync<PantsBusyException>(
                () => database.CreateColumnFamilyAsync("still-pinned").AsTask());
        }
        finally
        {
            release.Set();
        }

        _ = await CreateColumnFamilyEventuallyAsync(database, "after-blocking-verifier");
    }

    [Fact]
    public async Task ShouldReportCloudOnlineVerificationAsNonAuthoritative()
    {
        using var directory = new TemporaryDirectory();
        await using IPantsDatabase database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.SimulatedCloud(directory.Path, "verification", "cloud/"),
            new PantsRuntimeDependencies(storageVerifier: (_, _) =>
                ValueTask.FromResult(HealthyReport())));

        var report = await database.VerifyStorageAsync(TimeSpan.FromSeconds(2));

        Assert.False(report.Authoritative);
    }

    [Fact]
    public async Task ShouldPreserveDegradedRuntimeHealthGivenOnlineFilesVerifyHealthy()
    {
        using var directory = new TemporaryDirectory();
        await using IPantsDatabase database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Local(directory.Path),
            new PantsRuntimeDependencies(storageVerifier: (_, _) =>
                ValueTask.FromResult(HealthyReport())));
        var sstDirectory = Directory.CreateDirectory(Path.Combine(directory.Path, "sst"));
        await File.WriteAllTextAsync(Path.Combine(sstDirectory.FullName, "orphan.sst"), "orphan");

        var report = await database.VerifyStorageAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(PantsEngineHealth.Degraded, report.Health);
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
        try
        {
            await started.Task.WaitAsync(AssertionTimeout);
            await Assert.ThrowsAsync<PantsBusyException>(
                () => database.CreateColumnFamilyAsync("blocked").AsTask());
            await Assert.ThrowsAsync<PantsBusyException>(
                () => database.SetBackgroundCompactionAsync(false).AsTask());
        }
        finally
        {
            release.TrySetResult();
        }

        Assert.Equal(PantsEngineHealth.Healthy, (await verification).Health);
        Assert.Equal("allowed", (await database.CreateColumnFamilyAsync("allowed")).Name);
    }

    [Fact]
    public async Task ShouldRetainVerificationBarrierUntilTimedOutVerifierCompletes()
    {
        using var directory = new TemporaryDirectory();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PantsStorageVerificationDelegate verifier = async (_, _) =>
        {
            started.SetResult();
            await release.Task;
            return HealthyReport();
        };
        await using IPantsDatabase database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Local(directory.Path),
            new PantsRuntimeDependencies(storageVerifier: verifier));
        await using var transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put("key"u8.ToArray(), "value"u8.ToArray());
        var verification = database.VerifyStorageAsync(TimeSpan.FromMilliseconds(25)).AsTask();

        try
        {
            await started.Task.WaitAsync(AssertionTimeout);
            await Assert.ThrowsAsync<PantsTimeoutException>(
                () => verification.WaitAsync(AssertionTimeout));
            await Assert.ThrowsAsync<PantsBusyException>(
                () => database.CreateColumnFamilyAsync("still-pinned").AsTask());
            await Assert.ThrowsAsync<PantsBusyException>(
                () => transaction.CommitAsync(PantsWriteOptions.Sync).AsTask());
        }
        finally
        {
            release.TrySetResult();
            _ = await CreateColumnFamilyEventuallyAsync(database, "after-verification");
        }

        var created = await database.GetColumnFamilyAsync("after-verification");

        Assert.Equal("after-verification", Assert.IsAssignableFrom<IPantsColumnFamily>(created).Name);
    }

    [Fact]
    public async Task ShouldRetainVerificationBarrierUntilCancelledVerifierCompletes()
    {
        using var directory = new TemporaryDirectory();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PantsStorageVerificationDelegate verifier = async (_, _) =>
        {
            started.SetResult();
            await release.Task;
            return HealthyReport();
        };
        await using IPantsDatabase database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Local(directory.Path),
            new PantsRuntimeDependencies(storageVerifier: verifier));
        using var cancellation = new CancellationTokenSource();
        var verification = database
            .VerifyStorageAsync(TimeSpan.FromSeconds(5), cancellation.Token)
            .AsTask();

        try
        {
            await started.Task.WaitAsync(AssertionTimeout);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => verification);
            await Assert.ThrowsAsync<PantsBusyException>(
                () => database.CreateColumnFamilyAsync("still-pinned").AsTask());
        }
        finally
        {
            release.TrySetResult();
            _ = await CreateColumnFamilyEventuallyAsync(database, "after-cancellation");
        }
    }

    [Fact]
    public async Task ShouldTimeOutWaitingForVerificationBarrierWithoutStartingVerifier()
    {
        using var directory = new TemporaryDirectory();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocations = 0;
        PantsStorageVerificationDelegate verifier = async (_, _) =>
        {
            Interlocked.Increment(ref invocations);
            started.SetResult();
            await release.Task;
            return HealthyReport();
        };
        await using IPantsDatabase database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Local(directory.Path),
            new PantsRuntimeDependencies(storageVerifier: verifier));
        var firstVerification = database
            .VerifyStorageAsync(TimeSpan.FromSeconds(5))
            .AsTask();

        try
        {
            await started.Task.WaitAsync(AssertionTimeout);
            await Assert.ThrowsAsync<PantsTimeoutException>(
                () => database
                    .VerifyStorageAsync(TimeSpan.FromMilliseconds(25))
                    .AsTask()
                    .WaitAsync(AssertionTimeout));
            Assert.Equal(1, Volatile.Read(ref invocations));
        }
        finally
        {
            release.TrySetResult();
            _ = await firstVerification;
        }

        _ = await CreateColumnFamilyEventuallyAsync(database, "barrier-reaped");
        Assert.Equal(1, Volatile.Read(ref invocations));
    }

    [Fact]
    public async Task ShouldReleaseVerificationBarrierGivenAcquireResponseIsLost()
    {
        using var directory = new TemporaryDirectory();
        var responseStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseResponse = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        PantsVerificationBarrierResponseDelegate barrierResponse = async () =>
        {
            responseStarted.SetResult();
            await releaseResponse.Task;
        };
        var invocations = 0;
        PantsStorageVerificationDelegate verifier = (_, _) =>
        {
            Interlocked.Increment(ref invocations);
            return ValueTask.FromResult(HealthyReport());
        };
        await using IPantsDatabase database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Local(directory.Path),
            new PantsRuntimeDependencies(
                storageVerifier: verifier,
                verificationBarrierResponse: barrierResponse));
        var verification = database.VerifyStorageAsync(TimeSpan.FromMilliseconds(25)).AsTask();

        try
        {
            await responseStarted.Task.WaitAsync(AssertionTimeout);
            await Assert.ThrowsAsync<PantsTimeoutException>(
                () => verification.WaitAsync(AssertionTimeout));
            Assert.Equal(0, Volatile.Read(ref invocations));
        }
        finally
        {
            releaseResponse.TrySetResult();
        }

        _ = await CreateColumnFamilyEventuallyAsync(database, "barrier-reaped");
        Assert.Equal(0, Volatile.Read(ref invocations));
    }

    [Fact]
    public async Task ShouldReleaseVerificationBarrierGivenAcquireResponseFails()
    {
        using var directory = new TemporaryDirectory();
        var failpoint = new ArmableFailpointHandler();
        failpoint.Arm(PantsFailpoint.BeforeVerificationBarrierResponse);
        await using IPantsDatabase database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Local(directory.Path),
            new PantsRuntimeDependencies(failpoint));

        await Assert.ThrowsAsync<PantsIOException>(
            () => database.VerifyStorageAsync(TimeSpan.FromSeconds(1)).AsTask());

        Assert.Equal("barrier-released", (await database
            .CreateColumnFamilyAsync("barrier-released")).Name);
    }

    [Fact]
    public async Task ShouldRetainPrimaryLeaseGivenShutdownOverlapsTimedOutVerification()
    {
        using var directory = new TemporaryDirectory();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PantsStorageVerificationDelegate verifier = async (_, _) =>
        {
            started.SetResult();
            await release.Task;
            return HealthyReport();
        };
        await using var database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Local(directory.Path),
            new PantsRuntimeDependencies(storageVerifier: verifier));
        var verification = database.VerifyStorageAsync(TimeSpan.FromMilliseconds(25)).AsTask();

        try
        {
            await started.Task.WaitAsync(AssertionTimeout);
            await Assert.ThrowsAsync<PantsTimeoutException>(
                () => verification.WaitAsync(AssertionTimeout));
            await Assert.ThrowsAsync<PantsTimeoutException>(
                () => database.ShutdownAsync(TimeSpan.FromMilliseconds(25)).AsTask());
            await Assert.ThrowsAsync<PantsBusyException>(
                () => database.CreateColumnFamilyAsync("closing").AsTask());
            await Assert.ThrowsAsync<PantsLeaseHeldException>(
                () => PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path)).AsTask());
        }
        finally
        {
            release.TrySetResult();
        }

        await using var reopened = await OpenEventuallyAsync(directory.Path);

        Assert.True(reopened.IsPrimaryLeaseHealthy);
    }

    [Fact]
    public async Task ShouldGiveConcurrentShutdownCallersIndependentDeadlines()
    {
        using var directory = new TemporaryDirectory();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PantsStorageVerificationDelegate verifier = async (_, _) =>
        {
            started.SetResult();
            await release.Task;
            return HealthyReport();
        };
        await using var database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Local(directory.Path),
            new PantsRuntimeDependencies(storageVerifier: verifier));
        var verification = database.VerifyStorageAsync(TimeSpan.FromMilliseconds(25)).AsTask();

        var longShutdown = Task.CompletedTask;
        try
        {
            await started.Task.WaitAsync(AssertionTimeout);
            await Assert.ThrowsAsync<PantsTimeoutException>(
                () => verification.WaitAsync(AssertionTimeout));
            var shortShutdown = database.ShutdownAsync(TimeSpan.FromMilliseconds(25)).AsTask();
            longShutdown = database.ShutdownAsync(TimeSpan.FromSeconds(2)).AsTask();
            await Assert.ThrowsAsync<PantsTimeoutException>(() => shortShutdown);
            Assert.False(longShutdown.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
        }

        await longShutdown;
    }

    static async Task<IPantsColumnFamily> CreateColumnFamilyEventuallyAsync(
        IPantsDatabase database,
        string name)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            try
            {
                return await database.CreateColumnFamilyAsync(name, timeout.Token);
            }
            catch (PantsBusyException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(5), timeout.Token);
            }
        }
    }

    static async Task<IPantsDatabase> OpenEventuallyAsync(string path)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            try
            {
                return await PantsDatabase.OpenAsync(
                    PantsOpenOptions.Local(path),
                    timeout.Token);
            }
            catch (PantsLeaseHeldException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(5), timeout.Token);
            }
        }
    }

    static PantsStorageVerificationReport HealthyReport() => new(
        0,
        0,
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

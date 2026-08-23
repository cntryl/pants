using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Cntryl.Pants.Tests.Runtime;

[Collection(RuntimeDiagnosticsTestGroup.Name)]
public sealed class PantsBackgroundFlushPipelineTests
{
    static readonly TimeSpan AssertionTimeout = TimeSpan.FromSeconds(2);
    static readonly TimeSpan BackgroundWorkTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ShouldKeepForegroundResponsiveWhileSstBuildIsBlocked()
    {
        using var directory = new TemporaryDirectory();
        using var failpoint = new FlushPipelineFailpointHandler(
            Failpoint.BeforeFlushBuild);
        await using var database = await OpenAsync(directory.Path, failpoint);
        var family = await database.CreateColumnFamilyAsync("background-build");
        await SeedVisibleValuesAsync(database, family);
        await using var oldSnapshot = await database.BeginTransactionAsync(
            family,
            PantsTransactionMode.ReadOnly);
        try
        {
            var rotation = RotateWithMixedOperationsAsync(database, family).AsTask();
            await failpoint.WaitUntilEnteredAsync(AssertionTimeout);

            await rotation.WaitAsync(AssertionTimeout);
            var blocked = await database.GetRuntimeMetricsAsync().AsTask().WaitAsync(AssertionTimeout);
            await AssertVisibilityWhileFlushIsBlockedAsync(database, family, oldSnapshot);
            await CommitAsync(
                    database,
                    family,
                    "foreground-followup"u8.ToArray(),
                    "committed"u8.ToArray())
                .AsTask().WaitAsync(AssertionTimeout);

            Assert.Equal(1, blocked.FlushInFlight);
            Assert.True(blocked.ImmutableMemtables >= 1);
            Assert.Equal(0, blocked.FlushBuildCount);

            failpoint.Release();
            await database.FlushAsync(family).AsTask().WaitAsync(AssertionTimeout);
            var finished = await database.GetRuntimeMetricsAsync();
            Assert.True(finished.FlushBuildCount >= 1);
            Assert.True(finished.FlushPublishCount >= 1);
            Assert.Equal(0, finished.FlushInFlight);
        }
        finally
        {
            failpoint.Release();
        }
    }

    [Fact]
    public async Task ShouldKeepForegroundResponsiveWhilePublicationIsBlocked()
    {
        using var directory = new TemporaryDirectory();
        using var failpoint = new FlushPipelineFailpointHandler(
            Failpoint.BeforeFlushManifestPublish);
        await using var database = await OpenAsync(directory.Path, failpoint);
        var family = await database.CreateColumnFamilyAsync("background-publish");
        await SeedVisibleValuesAsync(database, family);
        await using var oldSnapshot = await database.BeginTransactionAsync(
            family,
            PantsTransactionMode.ReadOnly);
        try
        {
            var rotation = RotateWithMixedOperationsAsync(database, family).AsTask();
            await failpoint.WaitUntilEnteredAsync(AssertionTimeout);

            await rotation.WaitAsync(AssertionTimeout);
            var blocked = await database.GetRuntimeMetricsAsync().AsTask().WaitAsync(AssertionTimeout);
            await AssertVisibilityWhileFlushIsBlockedAsync(database, family, oldSnapshot);
            await CommitAsync(
                    database,
                    family,
                    "foreground-followup"u8.ToArray(),
                    "committed"u8.ToArray())
                .AsTask().WaitAsync(AssertionTimeout);

            Assert.True(blocked.FlushBuildCount >= 1);
            Assert.Equal(0, blocked.FlushPublishCount);
            Assert.Equal(1, blocked.FlushInFlight);

            failpoint.Release();
            await database.FlushAsync(family).AsTask().WaitAsync(AssertionTimeout);
            var finished = await database.GetRuntimeMetricsAsync();
            Assert.True(finished.FlushPublishCount >= 1);
            Assert.Equal(0, finished.FlushInFlight);
        }
        finally
        {
            failpoint.Release();
        }
    }

    [Fact]
    public async Task ShouldBoundMemtablePipelineWhileFlushPublicationIsBlocked()
    {
        const int flushThresholdBytes = 128 * 1024;
        const int keyAndEntryOverheadBytes = 65;
        using var directory = new TemporaryDirectory();
        using var failpoint = new FlushPipelineFailpointHandler(
            Failpoint.BeforeFlushManifestPublish);
        await using var database = await OpenAsync(directory.Path, failpoint);
        var family = await database.CreateColumnFamilyAsync("bounded-pipeline");
        var value = new byte[flushThresholdBytes - keyAndEntryOverheadBytes];
        try
        {
            await CommitAsync(database, family, new byte[] { 1 }, value);
            await failpoint.WaitUntilEnteredAsync(AssertionTimeout);
            await CommitAsync(database, family, new byte[] { 2 }, value);

            var stalled = await Assert.ThrowsAsync<PantsWriteStallException>(() =>
                CommitAsync(database, family, new byte[] { 3 }, value).AsTask());
            var blocked = await database.GetRuntimeMetricsAsync()
                .AsTask()
                .WaitAsync(AssertionTimeout);

            Assert.Equal(PantsErrorCode.WriteStall, stalled.Code);
            Assert.Equal(2, blocked.ImmutableMemtables);
            Assert.Equal(2L * flushThresholdBytes, blocked.TotalMemtableBytes);
            Assert.True(blocked.WriteStalled);
            Assert.True(blocked.WriteStallsMemoryTotal >= 1);
            Assert.False(await database.WaitForWriteStallClearAsync(family, TimeSpan.Zero));

            failpoint.Release();
            Assert.True(await database.WaitForWriteStallClearAsync(
                family,
                AssertionTimeout));
            await database.FlushAsync(family).AsTask().WaitAsync(AssertionTimeout);
            Assert.Equal(0, (await database.GetRuntimeMetricsAsync()).TotalMemtableBytes);
        }
        finally
        {
            failpoint.Release();
        }
    }

    [Fact]
    public async Task ShouldAdmitCommitThatRacesWriteStallHint()
    {
        using var directory = new TemporaryDirectory();
        using var failpoint = new WriteAdmissionRaceFailpointHandler();
        await using var database = await OpenAsync(directory.Path, failpoint);
        var family = await database.CreateColumnFamilyAsync("serialized-admission");
        var value = new byte[160 * 1024];
        await using var oldest = await database.BeginTransactionAsync(
            family,
            PantsTransactionMode.ReadWrite);
        await using var fillsPipeline = await database.BeginTransactionAsync(
            family,
            PantsTransactionMode.ReadWrite);
        await using var racesHint = await database.BeginTransactionAsync(
            family,
            PantsTransactionMode.ReadWrite);
        oldest.Put(new byte[] { 1 }, value);
        fillsPipeline.Put(new byte[] { 2 }, value);
        racesHint.Put(new byte[] { 3 }, value);

        try
        {
            await oldest.CommitAsync(PantsWriteOptions.Sync);
            await failpoint.WaitForFlushAsync(AssertionTimeout);
            failpoint.ArmWalAppend();

            var fillsPipelineCommit = fillsPipeline
                .CommitAsync(PantsWriteOptions.Sync)
                .AsTask();
            await failpoint.WaitForWalAsync(AssertionTimeout);
            var racingCommit = racesHint
                .CommitAsync(PantsWriteOptions.Sync)
                .AsTask();

            failpoint.ReleaseWal();
            await fillsPipelineCommit.WaitAsync(AssertionTimeout);
            await racingCommit.WaitAsync(AssertionTimeout);
            await using var afterHint = await database.BeginTransactionAsync(
                family,
                PantsTransactionMode.ReadWrite);
            afterHint.Put(new byte[] { 4 }, value);
            var stalled =
                await Assert.ThrowsAsync<PantsWriteStallException>(() =>
                    afterHint.CommitAsync(PantsWriteOptions.Sync).AsTask());
            var blocked = await database.GetRuntimeMetricsAsync();

            Assert.Equal(PantsErrorCode.WriteStall, stalled.Code);
            Assert.Equal(3, blocked.ImmutableMemtables);
            Assert.True(blocked.WriteStalled);
            await using var read = await database.BeginTransactionAsync(
                family,
                PantsTransactionMode.ReadOnly);
            Assert.NotNull(await read.GetAsync(new byte[] { 3 }));
            Assert.Null(await read.GetAsync(new byte[] { 4 }));
        }
        finally
        {
            failpoint.ReleaseWal();
            failpoint.ReleaseFlush();
        }
    }

    [Fact]
    public async Task ShouldKeepFlushOutputStagedUntilLeaseValidatedPublication()
    {
        using var directory = new TemporaryDirectory();
        using var recoveryDirectory = new TemporaryDirectory();
        using var failpoint = new BlockingThrowingFlushFailpointHandler(
            Failpoint.BeforeFlushPublication);
        var database = await OpenAsync(directory.Path, failpoint);
        var family = await database.CreateColumnFamilyAsync("staged-publication");
        try
        {
            await CommitAsync(
                database,
                family,
                "staged-key"u8.ToArray(),
                new byte[160 * 1024]);
            await failpoint.WaitUntilEnteredAsync(AssertionTimeout);

            Assert.Empty(Directory.GetFiles(Path.Combine(directory.Path, "sst"), "*.sst"));
            Assert.Single(Directory.GetFiles(
                Path.Combine(directory.Path, "sst", ".flush-staging"),
                "*.tmp"));
            CopyCrashImage(directory.Path, recoveryDirectory.Path);
            ExpireWriterLease(recoveryDirectory.Path);

            failpoint.Release();
            _ = await WaitForMetricsAsync(
                database,
                static metrics => metrics.FlushFailuresTotal >= 1,
                AssertionTimeout);
        }
        finally
        {
            failpoint.Release();
            await database.DisposeAsync();
        }

        Assert.Empty(Directory.GetFiles(Path.Combine(recoveryDirectory.Path, "sst"), "*.sst"));
        var staleStagingPath = Assert.Single(Directory.GetFiles(
            Path.Combine(recoveryDirectory.Path, "sst", ".flush-staging"),
            "*.tmp"));

        await using var reopened = await PantsDatabase.OpenAsync(CreateOptions(recoveryDirectory.Path));
        var reopenedFamily = Assert.IsAssignableFrom<IPantsColumnFamily>(
            await reopened.GetColumnFamilyAsync("staged-publication"));
        await using var read = await reopened.BeginTransactionAsync(
            reopenedFamily,
            PantsTransactionMode.ReadOnly);
        Assert.NotNull(await read.GetAsync("staged-key"u8.ToArray()));
        Assert.False(File.Exists(staleStagingPath));
        await reopened.FlushAsync(reopenedFamily).AsTask().WaitAsync(AssertionTimeout);
        Assert.Empty(Directory.GetFiles(
            Path.Combine(recoveryDirectory.Path, "sst", ".flush-staging"),
            "*.tmp"));
    }

    [Fact]
    public async Task ShouldNotPublishFlushAuthorityBeforeSstDirectorySync()
    {
        using var directory = new TemporaryDirectory();
        using var failpoint = new BlockingThrowingFlushFailpointHandler(
            Failpoint.BeforeFlushDirectorySync);
        await using var database = await OpenAsync(directory.Path, failpoint);
        var family = await database.CreateColumnFamilyAsync("directory-sync");
        await CommitAsync(
            database,
            family,
            "directory-sync-key"u8.ToArray(),
            "value"u8.ToArray());

        try
        {
            var flush = database.FlushAsync(family).AsTask();
            await failpoint.WaitUntilEnteredAsync(AssertionTimeout);

            Assert.DoesNotContain(
                (await database.GetStorageLayoutAsync()).Levels.SelectMany(static level => level.Files),
                file => file.ColumnFamilyId == family.Id);

            failpoint.Release();
            await Assert.ThrowsAsync<PantsIOException>(() => flush);
        }
        finally
        {
            failpoint.Release();
        }

        await database.FlushAsync(family).AsTask().WaitAsync(AssertionTimeout);
        Assert.Contains(
            (await database.GetStorageLayoutAsync()).Levels.SelectMany(static level => level.Files),
            file => file.ColumnFamilyId == family.Id);
    }

    [Fact]
    public async Task ShouldValidateFinalizedFlushOutputBeforePublishingAuthority()
    {
        using var directory = new TemporaryDirectory();
        var failpoint = new CorruptingFlushOutputFailpointHandler(directory.Path);
        var database = await OpenAsync(directory.Path, failpoint);
        try
        {
            var family = await database.CreateColumnFamilyAsync("validate-finalized-output");
            await CommitAsync(
                database,
                family,
                "corrupt-output"u8.ToArray(),
                "value"u8.ToArray());

            var error = await Assert.ThrowsAsync<PantsCorruptionException>(() =>
                database.FlushAsync(family).AsTask());
            var layout = await database.GetStorageLayoutAsync();
            using var intent = JsonDocument.Parse(
                await File.ReadAllBytesAsync(Path.Combine(directory.Path, "intent_log.json")));

            Assert.Equal(PantsErrorCode.Corruption, error.Code);
            Assert.DoesNotContain(
                layout.Levels.SelectMany(static level => level.Files),
                file => file.ColumnFamilyId == family.Id);
            Assert.Empty(intent.RootElement.EnumerateArray());
            Assert.Single(Directory.GetFiles(Path.Combine(directory.Path, "sst"), "*.sst"));
        }
        finally
        {
            await database.DisposeAsync();
        }

        await using var reopened = await PantsDatabase.OpenAsync(CreateOptions(directory.Path));
        var recoveredFamily = Assert.IsAssignableFrom<IPantsColumnFamily>(
            await reopened.GetColumnFamilyAsync("validate-finalized-output"));
        var recovered = await reopened.GetRuntimeMetricsAsync();
        await using var read = await reopened.BeginTransactionAsync(
            recoveredFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Equal(PantsEngineHealth.Healthy, recovered.Health);
        Assert.Equal(0, recovered.SstCount);
        Assert.True(recovered.TotalMemtableBytes > 0);
        Assert.Empty(Directory.GetFiles(Path.Combine(directory.Path, "sst"), "*.sst"));
        Assert.Equal(
            "value",
            TestBytes.ToText((await read.GetAsync("corrupt-output"u8.ToArray()))!.Value));
    }

    [Fact]
    public async Task ShouldFenceFlushPublicationAfterLeaseLoss()
    {
        using var directory = new TemporaryDirectory();
        using var failpoint = new FlushPipelineFailpointHandler(
            Failpoint.BeforeFlushPublication);
        var leaseLost = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var options = CreateOptions(directory.Path).WithLeaseLossCallback(leaseLost.SetResult);
        await using var database = await PantsDatabase.OpenForTestingAsync(
            options,
            new RuntimeDependencies(
                failpoint,
                leaseHeartbeatInterval: TimeSpan.FromHours(1)));
        var family = await database.CreateColumnFamilyAsync("fenced-publication");
        try
        {
            await CommitAsync(
                database,
                family,
                "fenced-key"u8.ToArray(),
                new byte[160 * 1024]);
            await failpoint.WaitUntilEnteredAsync(AssertionTimeout);
            FenceWriterLease(directory.Path);

            failpoint.Release();
            await leaseLost.Task.WaitAsync(AssertionTimeout);
            _ = await WaitForMetricsAsync(
                database,
                static metrics => metrics.FlushFailuresTotal >= 1,
                AssertionTimeout);

            Assert.False(database.IsPrimaryLeaseHealthy);
            Assert.Empty(Directory.GetFiles(Path.Combine(directory.Path, "sst"), "*.sst"));
            Assert.DoesNotContain(
                (await database.GetStorageLayoutAsync()).Levels.SelectMany(static level => level.Files),
                file => file.ColumnFamilyId == family.Id);
            using var intent = JsonDocument.Parse(
                await File.ReadAllBytesAsync(Path.Combine(directory.Path, "intent_log.json")));
            Assert.Equal(0, intent.RootElement.GetArrayLength());
        }
        finally
        {
            failpoint.Release();
        }
    }

    [Fact]
    public async Task ShouldKeepWalRecordTriggeredCommitResponsiveWhilePublicationIsBlocked()
    {
        using var directory = new TemporaryDirectory();
        using var failpoint = new FlushPipelineFailpointHandler(
            Failpoint.BeforeFlushManifestPublish);
        var options = CreateOptions(directory.Path).WithFlushAfterWalRecordsForTesting(1);
        await using var database = await PantsDatabase.OpenForTestingAsync(
            options,
            new RuntimeDependencies(failpoint));
        var family = await database.CreateColumnFamilyAsync("wal-record-flush");
        try
        {
            var rotation = CommitAsync(
                    database,
                    family,
                    "rotation"u8.ToArray(),
                    "committed"u8.ToArray())
                .AsTask();
            await failpoint.WaitUntilEnteredAsync(AssertionTimeout);

            await rotation.WaitAsync(AssertionTimeout);
            await CommitAsync(
                    database,
                    family,
                    "followup"u8.ToArray(),
                    "committed"u8.ToArray())
                .AsTask()
                .WaitAsync(AssertionTimeout);

            var blocked = await database.GetRuntimeMetricsAsync()
                .AsTask()
                .WaitAsync(AssertionTimeout);
            Assert.True(blocked.ImmutableMemtables >= 1);

            failpoint.Release();
            await database.FlushAsync(family).AsTask().WaitAsync(AssertionTimeout);
        }
        finally
        {
            failpoint.Release();
        }
    }

    [Fact]
    public async Task ShouldRetryRetainedImmutableAfterFlushWorkerFailure()
    {
        using var directory = new TemporaryDirectory();
        var failpoint = new PersistentThrowingFlushFailpointHandler(
            Failpoint.BeforeFlushBuild);
        await using var database = await OpenAsync(directory.Path, failpoint);
        var family = await database.CreateColumnFamilyAsync("worker-failure");

        await CommitAsync(
            database,
            family,
            "failed-value"u8.ToArray(),
            new byte[160 * 1024]);
        await failpoint.WaitUntilEnteredAsync(AssertionTimeout);
        var failed = await WaitForMetricsAsync(
            database,
            static metrics => metrics.FlushFailuresTotal >= 1,
            AssertionTimeout);

        Assert.True(failed.ImmutableMemtables >= 1);

        failpoint.Release();
        await database.FlushAsync(family).AsTask().WaitAsync(AssertionTimeout);

        var finished = await database.GetRuntimeMetricsAsync();
        Assert.True(finished.FlushFailuresTotal >= 1);
        Assert.True(finished.FlushRetriesTotal >= 1);
        Assert.True(failpoint.HitCount >= 1);
        Assert.Equal(0, finished.ImmutableMemtables);
        Assert.Equal(0, finished.FlushInFlight);
    }

    [Fact]
    public async Task ShouldRecordNoSpaceGivenBackgroundFlushWorkerFailure()
    {
        using var directory = new TemporaryDirectory();
        var failpoint = new NoSpaceFlushFailpointHandler();
        await using var database = await OpenAsync(directory.Path, failpoint);
        await CommitAsync(
            database,
            database.DefaultColumnFamily,
            "no-space-flush"u8.ToArray(),
            new byte[160 * 1024]);
        await failpoint.WaitUntilEnteredAsync(AssertionTimeout);
        var failed = await WaitForMetricsAsync(
            database,
            static metrics => metrics.FlushFailuresTotal >= 1,
            AssertionTimeout);

        Assert.True(failed.NoSpaceEvents >= 1);
        Assert.True(failed.WriteStallsNoSpaceTotal >= 1);

        failpoint.Release();
        await database.FlushAsync(database.DefaultColumnFamily)
            .AsTask()
            .WaitAsync(AssertionTimeout);
    }

    [Fact]
    public async Task ShouldRetryRetainedImmutableAutomaticallyAfterTransientFlushFailure()
    {
        using var directory = new TemporaryDirectory();
        using var failpoint = new FlushPipelineFailpointHandler(
            Failpoint.BeforeFlushBuild,
            true);
        await using var database = await OpenAsync(directory.Path, failpoint);
        var family = await database.CreateColumnFamilyAsync("automatic-flush-retry");

        await CommitAsync(
            database,
            family,
            "retry-without-maintenance"u8.ToArray(),
            new byte[160 * 1024]);
        _ = await WaitForMetricsAsync(
            database,
            static metrics => metrics.FlushFailuresTotal >= 1,
            AssertionTimeout);

        var recovered = await WaitForMetricsAsync(
            database,
            static metrics =>
                metrics.FlushRetriesTotal >= 1 && metrics.ImmutableMemtables == 0,
            AssertionTimeout);

        Assert.True(recovered.FlushPublishCount >= 1);
        Assert.Equal(1, recovered.FlushEnqueuedTotal);
        Assert.Equal(0, recovered.FlushInFlight);
        Assert.Equal(1, recovered.SstCount);
    }

    [Fact]
    public async Task ShouldRetainSuccessfulFlushBuildAcrossPublicationRetry()
    {
        using var directory = new TemporaryDirectory();
        using var failpoint = new FlushPipelineFailpointHandler(
            Failpoint.BeforeFlushManifestPublish,
            true);
        await using var database = await OpenAsync(directory.Path, failpoint);
        var family = await database.CreateColumnFamilyAsync("retained-flush-build");
        await CommitAsync(
            database,
            family,
            "retained-build"u8.ToArray(),
            new byte[160 * 1024]);

        var recovered = await WaitForMetricsAsync(
            database,
            static metrics =>
                metrics.FlushFailuresTotal >= 1 &&
                metrics.FlushRetriesTotal >= 1 &&
                metrics.ImmutableMemtables == 0,
            BackgroundWorkTimeout);

        Assert.Equal(1, recovered.FlushEnqueuedTotal);
        Assert.Equal(1, recovered.FlushBuildCount);
        Assert.Equal(2, recovered.FlushPublishCount);
        Assert.True(recovered.FlushRetriesTotal >= 1);
    }

    [Fact]
    public async Task ShouldRemoveUnownedFinalizedFlushOutputWhenRecoveringCrashImage()
    {
        using var directory = new TemporaryDirectory();
        using var recoveryDirectory = new TemporaryDirectory();
        using var failpoint = new BlockingThrowingFlushFailpointHandler(
            Failpoint.AfterFlushFinalizationBeforeIntent);
        var database = await OpenAsync(directory.Path, failpoint);
        var family = await database.CreateColumnFamilyAsync("finalized-before-intent");
        try
        {
            await CommitAsync(
                database,
                family,
                "recovered-from-wal"u8.ToArray(),
                new byte[160 * 1024]);
            await failpoint.WaitUntilEnteredAsync(AssertionTimeout);
            Assert.Single(Directory.GetFiles(Path.Combine(directory.Path, "sst"), "*.sst"));
            Assert.Empty((await database.GetStorageLayoutAsync()).Levels.SelectMany(static level => level.Files));

            CopyCrashImage(directory.Path, recoveryDirectory.Path);
            ExpireWriterLease(recoveryDirectory.Path);
        }
        finally
        {
            failpoint.Release();
            await database.DisposeAsync();
        }

        Assert.Single(Directory.GetFiles(Path.Combine(recoveryDirectory.Path, "sst"), "*.sst"));
        await using var reopened = await PantsDatabase.OpenAsync(CreateOptions(recoveryDirectory.Path));
        Assert.Empty(Directory.GetFiles(Path.Combine(recoveryDirectory.Path, "sst"), "*.sst"));
        var recoveredFamily = Assert.IsAssignableFrom<IPantsColumnFamily>(
            await reopened.GetColumnFamilyAsync("finalized-before-intent"));
        await using var read = await reopened.BeginTransactionAsync(
            recoveredFamily,
            PantsTransactionMode.ReadOnly);
        Assert.NotNull(await read.GetAsync("recovered-from-wal"u8.ToArray()));
    }

    [Fact]
    public async Task ShouldRetainUnownedStartupSstAndReportDegradedWhenCleanupFails()
    {
        using var directory = new TemporaryDirectory();
        var options = CreateOptions(directory.Path);
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            await CommitAsync(
                database,
                database.DefaultColumnFamily,
                "owned"u8.ToArray(),
                "value"u8.ToArray());
            await database.FlushAsync(database.DefaultColumnFamily);
        }

        var canonical = Assert.Single(Directory.GetFiles(
            Path.Combine(directory.Path, "sst"),
            "*.sst"));
        var orphan = Path.Combine(directory.Path, "sst", "orphan.sst");
        File.Copy(canonical, orphan);
        var failpoint = new PersistentThrowingFlushFailpointHandler(
            Failpoint.BeforeStartupResidueDelete);

        await using var reopened = await PantsDatabase.OpenForTestingAsync(
            options,
            new RuntimeDependencies(failpoint));
        failpoint.Release();
        var metrics = await reopened.GetRuntimeMetricsAsync();

        Assert.True(File.Exists(orphan));
        Assert.Equal(PantsEngineHealth.Degraded, metrics.Health);
        Assert.Equal(1, metrics.ObsoleteFileBacklog);
    }

    [Fact]
    public async Task ShouldRemoveStaleMetadataTemporaryFilesWhenReopening()
    {
        using var directory = new TemporaryDirectory();
        var options = CreateOptions(directory.Path);
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            await database.ShutdownAsync(AssertionTimeout);
        }

        var temporaryPaths = new[]
        {
            Path.Combine(directory.Path, "manifest.json.tmp"),
            Path.Combine(directory.Path, "manifest.snapshot.json.tmp"),
            Path.Combine(directory.Path, "intent_log.json.tmp")
        };
        foreach (var path in temporaryPaths)
        {
            await File.WriteAllTextAsync(path, "stale");
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);

        Assert.All(temporaryPaths, path => Assert.False(File.Exists(path)));
        Assert.Equal(PantsEngineHealth.Healthy, (await reopened.GetRuntimeMetricsAsync()).Health);
    }

    [Fact]
    public async Task ShouldRemoveStaleSstTemporaryFilesWhenReopening()
    {
        using var directory = new TemporaryDirectory();
        var options = CreateOptions(directory.Path);
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            await database.ShutdownAsync(AssertionTimeout);
        }

        var sstTemporary = Path.Combine(directory.Path, "sst", "orphan.sst.tmp");
        var stagingResidue = Path.Combine(
            directory.Path,
            "sst",
            ".flush-staging",
            "nested",
            "1-1.sst");
        Directory.CreateDirectory(Path.GetDirectoryName(stagingResidue)!);
        await File.WriteAllTextAsync(sstTemporary, "stale");
        await File.WriteAllTextAsync(stagingResidue, "stale");

        await using var reopened = await PantsDatabase.OpenAsync(options);

        Assert.False(File.Exists(sstTemporary));
        Assert.False(File.Exists(stagingResidue));
        var stagingDirectory = Path.Combine(directory.Path, "sst", ".flush-staging");
        Assert.True(Directory.Exists(stagingDirectory));
        Assert.False(Directory.Exists(Path.GetDirectoryName(stagingResidue)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(
            stagingDirectory,
            "*",
            SearchOption.AllDirectories));
        Assert.Equal(PantsEngineHealth.Healthy, (await reopened.GetRuntimeMetricsAsync()).Health);
    }

    [Fact]
    public async Task ShouldRemoveStaleCloudRecoveryDirectoryWhenReopening()
    {
        using var directory = new TemporaryDirectory();
        var options = CreateOptions(directory.Path);
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            await database.ShutdownAsync(AssertionTimeout);
        }

        var recoveryDirectory = Path.Combine(directory.Path, "cloud_recovery", "nested");
        Directory.CreateDirectory(recoveryDirectory);
        await File.WriteAllTextAsync(Path.Combine(recoveryDirectory, "stale.sst"), "stale");

        await using var reopened = await PantsDatabase.OpenAsync(options);

        Assert.False(Directory.Exists(Path.Combine(directory.Path, "cloud_recovery")));
        Assert.Equal(PantsEngineHealth.Healthy, (await reopened.GetRuntimeMetricsAsync()).Health);
    }

    [Fact]
    public async Task ShouldAutoFlushRecoveredMemtableWithoutForegroundWrite()
    {
        using var directory = new TemporaryDirectory();
        using var recoveryDirectory = new TemporaryDirectory();
        using var failpoint = new FlushPipelineFailpointHandler(
            Failpoint.BeforeFlushPublication);
        var database = await OpenAsync(directory.Path, failpoint);
        var family = await database.CreateColumnFamilyAsync("recovered-auto-flush");
        try
        {
            await CommitAsync(
                database,
                family,
                new byte[] { 1 },
                new byte[160 * 1024]);
            await failpoint.WaitUntilEnteredAsync(AssertionTimeout);
            await CommitAsync(
                database,
                family,
                new byte[] { 2 },
                new byte[160 * 1024]);

            var blocked = await database.GetRuntimeMetricsAsync();
            Assert.Equal(2, blocked.ImmutableMemtables);
            Assert.True(blocked.WriteStalled);
            CopyCrashImage(directory.Path, recoveryDirectory.Path);
            ExpireWriterLease(recoveryDirectory.Path);
        }
        finally
        {
            failpoint.Release();
            await database.DisposeAsync();
        }

        await using var reopened = await PantsDatabase.OpenAsync(
            CreateOptions(recoveryDirectory.Path));
        var recoveredFamily = Assert.IsAssignableFrom<IPantsColumnFamily>(
            await reopened.GetColumnFamilyAsync("recovered-auto-flush"));
        var drained = await WaitForMetricsAsync(
            reopened,
            static metrics =>
                metrics.SstCount >= 1 &&
                metrics.ImmutableMemtables == 0 &&
                metrics.TotalMemtableBytes == 0 &&
                !metrics.WriteStalled,
            AssertionTimeout);

        Assert.True(drained.FlushPublishCount >= 1);
        await CommitAsync(
            reopened,
            recoveredFamily,
            new byte[] { 3 },
            "after-recovery"u8.ToArray());
    }

    [Fact]
    public async Task ShouldAbortStartupBeforeDeletingResidueWhenWriterLeaseIsFenced()
    {
        using var directory = new TemporaryDirectory();
        var options = CreateOptions(directory.Path);
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            await database.ShutdownAsync(AssertionTimeout);
        }

        var residue = Path.Combine(directory.Path, "cloud_recovery", "nested", "stale.sst");
        Directory.CreateDirectory(Path.GetDirectoryName(residue)!);
        await File.WriteAllTextAsync(residue, "stale");
        using var failpoint = new FlushPipelineFailpointHandler(
            Failpoint.BeforeStartupResidueDelete);
        var opening = PantsDatabase.OpenForTestingAsync(
            options,
            new RuntimeDependencies(
                failpoint,
                leaseHeartbeatInterval: TimeSpan.FromHours(1))).AsTask();
        await failpoint.WaitUntilEnteredAsync(AssertionTimeout);
        FenceWriterLease(directory.Path);
        failpoint.Release();

        var opened = (IPantsDatabase?)null;
        var fenced = (PantsFencedException?)null;
        try
        {
            opened = await opening.WaitAsync(AssertionTimeout);
        }
        catch (PantsFencedException exception)
        {
            fenced = exception;
        }
        finally
        {
            if (opened is not null)
            {
                await opened.DisposeAsync();
            }
        }

        Assert.NotNull(fenced);
        Assert.Equal(PantsErrorCode.Fenced, fenced.Code);
        Assert.True(File.Exists(residue));

        ExpireWriterLease(directory.Path);
        await using var recovered = await PantsDatabase.OpenAsync(options);
        Assert.False(Directory.Exists(Path.Combine(directory.Path, "cloud_recovery")));
    }

    [Fact]
    public async Task ShouldRetryOldestImmutableBeforePublishingYoungerGeneration()
    {
        using var directory = new TemporaryDirectory();
        using var failpoint = new OrderedFlushRetryFailpointHandler();
        await using var database = await OpenAsync(directory.Path, failpoint);
        var family = await database.CreateColumnFamilyAsync("ordered-flush-retry");
        try
        {
            await CommitAsync(
                database,
                family,
                "oldest"u8.ToArray(),
                new byte[160 * 1024]);
            await failpoint.WaitForFirstAsync(AssertionTimeout);

            await CommitAsync(
                database,
                family,
                "younger"u8.ToArray(),
                new byte[160 * 1024]);
            failpoint.ReleaseFirst();
            await failpoint.WaitForSecondAsync(AssertionTimeout);

            var blocked = await database.GetRuntimeMetricsAsync();
            Assert.Equal(1, blocked.FlushInFlight);
            Assert.Equal(1, blocked.FlushQueueDepth);

            var publishedBeforeRetry = Directory.GetFiles(
                    Path.Combine(directory.Path, "sst"),
                    "*.sst")
                .Select(static path => Path.GetFileName(path)!)
                .Order(StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(
                ["000001_00_00000000000000000001.sst"],
                publishedBeforeRetry);

            failpoint.ReleaseSecond();
            var recovered = await WaitForMetricsAsync(
                database,
                static metrics => metrics.SstCount == 2 && metrics.ImmutableMemtables == 0,
                AssertionTimeout);
            Assert.True(recovered.FlushRetriesTotal >= 1);
        }
        finally
        {
            failpoint.ReleaseFirst();
            failpoint.ReleaseSecond();
        }
    }

    [Fact]
    public async Task ShouldRetainWriterLeaseUntilBlockedFlushWorkerExits()
    {
        using var directory = new TemporaryDirectory();
        using var failpoint = new FlushPipelineFailpointHandler(
            Failpoint.BeforeFlushManifestPublish);
        var options = CreateOptions(directory.Path);
        var database = await PantsDatabase.OpenForTestingAsync(
            options,
            new RuntimeDependencies(failpoint));
        try
        {
            var family = await database.CreateColumnFamilyAsync("shutdown-fence");
            var rotation = CommitAsync(
                    database,
                    family,
                    "blocked"u8.ToArray(),
                    new byte[160 * 1024])
                .AsTask();
            await failpoint.WaitUntilEnteredAsync(AssertionTimeout);
            await rotation.WaitAsync(AssertionTimeout);
            _ = await WaitForMetricsAsync(
                database,
                static metrics => metrics.FlushBuildCount >= 1 && metrics.FlushInFlight == 1,
                AssertionTimeout);

            var firstShutdown = await Assert.ThrowsAsync<PantsTimeoutException>(() =>
                database.ShutdownAsync(TimeSpan.FromMilliseconds(50)).AsTask());
            var contendingOpen = await Assert.ThrowsAsync<PantsLeaseHeldException>(() =>
                PantsDatabase.OpenAsync(options).AsTask());

            Assert.Equal(PantsErrorCode.Timeout, firstShutdown.Code);
            Assert.Equal(PantsErrorCode.LeaseHeld, contendingOpen.Code);

            failpoint.Release();
            await database.ShutdownAsync(AssertionTimeout);
            await using var reopened = await PantsDatabase.OpenAsync(options);
            await reopened.ShutdownAsync(AssertionTimeout);
        }
        finally
        {
            failpoint.Release();
            await database.DisposeAsync();
        }
    }

    [Fact]
    public async Task ShouldRetainFailedImmutableForRecoveryWithoutRetryingOnShutdown()
    {
        using var directory = new TemporaryDirectory();
        var failpoint = new PersistentThrowingFlushFailpointHandler(
            Failpoint.BeforeFlushBuild);
        var options = CreateOptions(directory.Path);
        var database = await PantsDatabase.OpenForTestingAsync(
            options,
            new RuntimeDependencies(failpoint));
        var shutdownCompleted = false;
        try
        {
            await CommitAsync(
                database,
                database.DefaultColumnFamily,
                "failed-immutable"u8.ToArray(),
                new byte[160 * 1024]);
            await failpoint.WaitUntilEnteredAsync(AssertionTimeout);
            _ = await WaitForMetricsAsync(
                database,
                static metrics =>
                    metrics.FlushFailuresTotal >= 1 && metrics.FlushInFlight == 0,
                AssertionTimeout);

            await database.ShutdownAsync(AssertionTimeout);
            shutdownCompleted = true;
        }
        finally
        {
            failpoint.Release();
            if (!shutdownCompleted)
            {
                try
                {
                    await database.DisposeAsync();
                }
                catch (PantsException)
                {
                }
            }
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);
        await using var read = await reopened.BeginTransactionAsync(
            reopened.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        Assert.NotNull(await read.GetAsync("failed-immutable"u8.ToArray()));
    }

    [Fact]
    public async Task ShouldFailHeldFlushAndWriteStallWaitersWhenShutdownStarts()
    {
        using var directory = new TemporaryDirectory();
        using var failpoint = new FlushPipelineFailpointHandler(
            Failpoint.BeforeFlushPublication);
        var options = CreateOptions(directory.Path);
        var database = await PantsDatabase.OpenForTestingAsync(
            options,
            new RuntimeDependencies(failpoint));
        var shutdownCompleted = false;
        try
        {
            var blockedFamily = await database.CreateColumnFamilyAsync("shutdown-running");
            var queuedFamily = await database.CreateColumnFamilyAsync("shutdown-queued");
            await CommitAsync(
                database,
                blockedFamily,
                "running"u8.ToArray(),
                new byte[160 * 1024]);
            await failpoint.WaitUntilEnteredAsync(AssertionTimeout);
            await CommitAsync(
                database,
                queuedFamily,
                "queued"u8.ToArray(),
                new byte[160 * 1024]);

            var flushWaiter = database.FlushAsync(queuedFamily).AsTask();
            var compactionWaiter = database.CompactAllAsync().AsTask();
            var dropWaiter = database
                .DropColumnFamilyDiscardingUnflushedAsync(queuedFamily)
                .AsTask();
            var stallWaiter = database.WaitForWriteStallClearAsync(
                    queuedFamily,
                    TimeSpan.FromMinutes(1))
                .AsTask();
            _ = await database.GetRuntimeMetricsAsync();
            var firstShutdown = await Assert.ThrowsAsync<PantsTimeoutException>(() =>
                database.ShutdownAsync(TimeSpan.FromMilliseconds(50)).AsTask());
            var flushError = await Assert.ThrowsAsync<PantsBusyException>(() =>
                flushWaiter.WaitAsync(AssertionTimeout));
            var compactionError = await Assert.ThrowsAsync<PantsBusyException>(() =>
                compactionWaiter.WaitAsync(AssertionTimeout));
            var dropError = await Assert.ThrowsAsync<PantsBusyException>(() =>
                dropWaiter.WaitAsync(AssertionTimeout));
            var stallError = await Assert.ThrowsAsync<PantsBusyException>(() =>
                stallWaiter.WaitAsync(AssertionTimeout));

            Assert.Equal(PantsErrorCode.Timeout, firstShutdown.Code);
            Assert.Equal(PantsErrorCode.Busy, flushError.Code);
            Assert.Equal(PantsErrorCode.Busy, compactionError.Code);
            Assert.Equal(PantsErrorCode.Busy, dropError.Code);
            Assert.Equal(PantsErrorCode.Busy, stallError.Code);

            failpoint.Release();
            await database.ShutdownAsync(AssertionTimeout);
            shutdownCompleted = true;
        }
        finally
        {
            failpoint.Release();
            if (!shutdownCompleted)
            {
                try
                {
                    await database.DisposeAsync();
                }
                catch (PantsException)
                {
                }
            }
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);
        foreach (var familyName in new[] { "shutdown-running", "shutdown-queued" })
        {
            var family = Assert.IsAssignableFrom<IPantsColumnFamily>(
                await reopened.GetColumnFamilyAsync(familyName));
            await using var read = await reopened.BeginTransactionAsync(
                family,
                PantsTransactionMode.ReadOnly);
            Assert.NotNull(await read.GetAsync(
                familyName == "shutdown-running"
                    ? "running"u8.ToArray()
                    : "queued"u8.ToArray()));
        }
    }

    [Fact]
    public async Task ShouldRetryShutdownWalDurabilityBoundaryAfterFailure()
    {
        using var directory = new TemporaryDirectory();
        var failpoint = new RetryingShutdownBoundaryFailpointHandler();
        var options = CreateOptions(directory.Path);
        var database = await PantsDatabase.OpenForTestingAsync(
            options,
            new RuntimeDependencies(failpoint));
        await using (var transaction = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            transaction.Put("buffered-shutdown"u8.ToArray(), "value"u8.ToArray());
            await transaction.CommitAsync(PantsWriteOptions.Buffered);
        }

        var firstFailure = await Assert.ThrowsAsync<PantsIOException>(() =>
            database.ShutdownAsync(AssertionTimeout).AsTask());
        Assert.Equal(PantsErrorCode.Io, firstFailure.Code);
        Assert.Equal(1, failpoint.HitCount);

        await database.ShutdownAsync(AssertionTimeout);
        Assert.Equal(2, failpoint.HitCount);

        await using var reopened = await PantsDatabase.OpenAsync(options);
        await using var read = await reopened.BeginTransactionAsync(
            reopened.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Equal(
            "value",
            TestBytes.ToText((await read.GetAsync("buffered-shutdown"u8.ToArray()))!.Value));
    }

    [Fact]
    public async Task ShouldHonorShutdownDeadlineWhileWalBoundaryRemainsInFlight()
    {
        using var directory = new TemporaryDirectory();
        using var failpoint = new FlushPipelineFailpointHandler(
            Failpoint.BeforeShutdownWalDurabilityBoundary);
        var options = CreateOptions(directory.Path);
        var database = await PantsDatabase.OpenForTestingAsync(
            options,
            new RuntimeDependencies(failpoint));
        var shutdownCompleted = false;
        var firstShutdown = Task.CompletedTask;
        try
        {
            await using (var transaction = await database.BeginTransactionAsync(
                             database.DefaultColumnFamily,
                             PantsTransactionMode.ReadWrite))
            {
                transaction.Put("deadline-boundary"u8.ToArray(), "value"u8.ToArray());
                await transaction.CommitAsync(PantsWriteOptions.Buffered);
            }

            var started = Stopwatch.GetTimestamp();
            firstShutdown = database
                .ShutdownAsync(TimeSpan.FromMilliseconds(50))
                .AsTask();
            await failpoint.WaitUntilEnteredAsync(AssertionTimeout);
            var timeout = await Assert.ThrowsAsync<PantsTimeoutException>(() =>
                firstShutdown.WaitAsync(AssertionTimeout));
            var contender = await Assert.ThrowsAsync<PantsLeaseHeldException>(() =>
                PantsDatabase.OpenAsync(options).AsTask());
            Assert.Equal(PantsErrorCode.Timeout, timeout.Code);
            Assert.Equal(PantsErrorCode.LeaseHeld, contender.Code);
            Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromMilliseconds(500));

            failpoint.Release();
            await database.ShutdownAsync(AssertionTimeout);
            shutdownCompleted = true;
        }
        finally
        {
            failpoint.Release();
            try
            {
                await firstShutdown;
            }
            catch (PantsException)
            {
            }

            if (!shutdownCompleted)
            {
                try
                {
                    await database.DisposeAsync();
                }
                catch (PantsException)
                {
                }
            }
        }
    }

    [Fact]
    public async Task ShouldNotWaitForUnstartedImmutableAfterFencedFlushWorkerExits()
    {
        using var directory = new TemporaryDirectory();
        using var failpoint = new FlushPipelineFailpointHandler(
            Failpoint.BeforeFlushPublication);
        var options = CreateOptions(directory.Path)
            .WithShutdownTimeout(TimeSpan.FromMilliseconds(100));
        var database = await PantsDatabase.OpenForTestingAsync(
            options,
            new RuntimeDependencies(
                failpoint,
                leaseHeartbeatInterval: TimeSpan.FromHours(1)));
        var shutdownCompleted = false;
        try
        {
            var family = await database.CreateColumnFamilyAsync("fenced-shutdown");
            await CommitAsync(
                database,
                family,
                "oldest"u8.ToArray(),
                new byte[160 * 1024]);
            await failpoint.WaitUntilEnteredAsync(AssertionTimeout);
            await CommitAsync(
                database,
                family,
                "younger"u8.ToArray(),
                new byte[160 * 1024]);

            var queued = await database.GetRuntimeMetricsAsync();
            Assert.Equal(2, queued.ImmutableMemtables);
            Assert.Equal(1, queued.FlushInFlight);
            Assert.Equal(1, queued.FlushQueueDepth);

            FenceWriterLease(directory.Path);
            var firstShutdown = await Assert.ThrowsAsync<PantsTimeoutException>(() =>
                database.ShutdownAsync(TimeSpan.FromMilliseconds(50)).AsTask());
            Assert.Equal(PantsErrorCode.Timeout, firstShutdown.Code);
            await Assert.ThrowsAsync<PantsLeaseHeldException>(() =>
                PantsDatabase.OpenAsync(options).AsTask());

            failpoint.Release();
            await database.ShutdownAsync(AssertionTimeout);
            shutdownCompleted = true;
        }
        finally
        {
            failpoint.Release();
            if (!shutdownCompleted)
            {
                try
                {
                    await database.DisposeAsync();
                }
                catch (PantsException)
                {
                }
            }
        }

        ExpireWriterLease(directory.Path);
        await using var reopened = await PantsDatabase.OpenAsync(options);
        var recoveredFamily = Assert.IsAssignableFrom<IPantsColumnFamily>(
            await reopened.GetColumnFamilyAsync("fenced-shutdown"));
        await using (var read = await reopened.BeginTransactionAsync(
                         recoveredFamily,
                         PantsTransactionMode.ReadOnly))
        {
            Assert.NotNull(await read.GetAsync("oldest"u8.ToArray()));
            Assert.NotNull(await read.GetAsync("younger"u8.ToArray()));
        }

        await reopened.ShutdownAsync(AssertionTimeout);
    }

    [Fact]
    public async Task ShouldDeferDropUntilImmutableFlushPipelineCompletes()
    {
        using var directory = new TemporaryDirectory();
        using var failpoint = new FlushPipelineFailpointHandler(
            Failpoint.BeforeFlushBuild);
        await using var database = await OpenAsync(directory.Path, failpoint);
        var family = await database.CreateColumnFamilyAsync("drop-after-flush");
        await CommitAsync(
            database,
            family,
            "drop-key"u8.ToArray(),
            "drop-value"u8.ToArray());
        try
        {
            var flush = database.FlushAsync(family).AsTask();
            await failpoint.WaitUntilEnteredAsync(AssertionTimeout);

            var drop = database.DropColumnFamilyAsync(family).AsTask();
            var blocked = await database.GetRuntimeMetricsAsync().AsTask().WaitAsync(AssertionTimeout);

            Assert.False(drop.IsCompleted);
            Assert.Equal(1, blocked.FlushInFlight);
            Assert.Equal(1, blocked.ImmutableMemtables);

            failpoint.Release();
            await flush.WaitAsync(AssertionTimeout);
            await drop.WaitAsync(AssertionTimeout);

            var finished = await database.GetRuntimeMetricsAsync();
            Assert.Equal(0, finished.FlushInFlight);
            Assert.Equal(0, finished.ImmutableMemtables);
            var stagingDirectory = Path.Combine(directory.Path, "sst", ".flush-staging");
            Assert.True(
                !Directory.Exists(stagingDirectory) ||
                !Directory.EnumerateFileSystemEntries(stagingDirectory).Any());
        }
        finally
        {
            failpoint.Release();
        }
    }

    [Fact]
    public async Task ShouldDeferCompactionUntilImmutableFlushPipelineCompletes()
    {
        using var directory = new TemporaryDirectory();
        using var failpoint = new FlushPipelineFailpointHandler(
            Failpoint.BeforeFlushPublication);
        await using var database = await OpenAsync(directory.Path, failpoint);
        var family = await database.CreateColumnFamilyAsync("compact-after-flush");
        try
        {
            var rotation = CommitAsync(
                    database,
                    family,
                    "compact-key"u8.ToArray(),
                    new byte[160 * 1024])
                .AsTask();
            await failpoint.WaitUntilEnteredAsync(AssertionTimeout);
            await rotation.WaitAsync(AssertionTimeout);

            var compact = database.CompactAllAsync().AsTask();
            var blocked = await database.GetRuntimeMetricsAsync()
                .AsTask()
                .WaitAsync(AssertionTimeout);

            Assert.False(compact.IsCompleted);
            Assert.Equal(1, blocked.FlushInFlight);
            Assert.Equal(1, blocked.ImmutableMemtables);

            failpoint.Release();
            await compact.WaitAsync(AssertionTimeout);

            var finished = await database.GetRuntimeMetricsAsync();
            Assert.Equal(0, finished.FlushInFlight);
            Assert.Equal(0, finished.ImmutableMemtables);
            await using var read = await database.BeginTransactionAsync(
                family,
                PantsTransactionMode.ReadOnly);
            Assert.NotNull(await read.GetAsync("compact-key"u8.ToArray()));
        }
        finally
        {
            failpoint.Release();
        }
    }

    [Fact]
    public async Task ShouldRetryCompactionAdmissionWhenCommitArrivesAfterImmutableDrain()
    {
        using var directory = new TemporaryDirectory();
        using var failpoint = new FlushPipelineFailpointHandler(
            Failpoint.BeforeCompactionAdmission);
        await using var database = await OpenAsync(directory.Path, failpoint);
        var family = await database.CreateColumnFamilyAsync("compact-admission-race");
        try
        {
            var compact = database.CompactAllAsync().AsTask();
            await failpoint.WaitUntilEnteredAsync(AssertionTimeout);

            await CommitAsync(
                    database,
                    family,
                    "racing-commit"u8.ToArray(),
                    "committed"u8.ToArray())
                .AsTask()
                .WaitAsync(AssertionTimeout);

            failpoint.Release();
            await compact.WaitAsync(AssertionTimeout);

            var finished = await database.GetRuntimeMetricsAsync();
            Assert.True(finished.FlushBuildCount >= 1);
            Assert.True(finished.FlushPublishCount >= 1);
            Assert.Equal(0, finished.ImmutableMemtables);
            await using var read = await database.BeginTransactionAsync(
                family,
                PantsTransactionMode.ReadOnly);
            Assert.Equal(
                "committed"u8.ToArray(),
                (await read.GetAsync("racing-commit"u8.ToArray()))?.ToArray());
        }
        finally
        {
            failpoint.Release();
        }
    }

    [Fact]
    public async Task ShouldRecheckImmutableFlushesAtDropAdmission()
    {
        using var directory = new TemporaryDirectory();
        using var failpoint = new DropPipelineRaceFailpointHandler();
        await using var database = await OpenAsync(directory.Path, failpoint);
        var family = await database.CreateColumnFamilyAsync("drop-admission-race");
        try
        {
            var drop = database.DropColumnFamilyDiscardingUnflushedAsync(family).AsTask();
            await failpoint.WaitForDropAdmissionAsync(AssertionTimeout);

            await CommitAsync(
                    database,
                    family,
                    "late-flush"u8.ToArray(),
                    new byte[160 * 1024])
                .AsTask()
                .WaitAsync(AssertionTimeout);
            await failpoint.WaitForFlushPublicationAsync(AssertionTimeout);

            failpoint.ReleaseDropAdmission();
            var blocked = await database.GetRuntimeMetricsAsync()
                .AsTask()
                .WaitAsync(AssertionTimeout);

            Assert.False(drop.IsCompleted);
            Assert.Equal(1, blocked.ImmutableMemtables);

            failpoint.ReleaseFlushPublication();
            await drop.WaitAsync(AssertionTimeout);

            var finished = await database.GetRuntimeMetricsAsync();
            Assert.Equal(0, finished.ImmutableMemtables);
            Assert.DoesNotContain(
                (await database.GetStorageLayoutAsync()).Levels.SelectMany(static level => level.Files),
                file => file.ColumnFamilyId == family.Id);
        }
        finally
        {
            failpoint.ReleaseDropAdmission();
            failpoint.ReleaseFlushPublication();
        }
    }

    [Fact]
    public async Task ShouldKeepRacingActiveMemtableOutOfDeferredCompaction()
    {
        using var directory = new TemporaryDirectory();
        using var failpoint = new FlushPipelineFailpointHandler(
            Failpoint.BeforeCompactionAdmission);
        var options = CreateOptions(directory.Path)
            .WithBackgroundCompaction(true)
            .WithCompaction(new PantsCompactionConfiguration(L0FileCountTrigger: 1));
        await using var database = await PantsDatabase.OpenForTestingAsync(
            options,
            new RuntimeDependencies(failpoint));
        var family = await database.CreateColumnFamilyAsync("deferred-compact-race");
        try
        {
            await CommitAsync(
                database,
                family,
                "compaction-input"u8.ToArray(),
                new byte[160 * 1024]);
            await failpoint.WaitUntilEnteredAsync(AssertionTimeout);

            await CommitAsync(
                    database,
                    family,
                    "racing-active"u8.ToArray(),
                    "committed"u8.ToArray())
                .AsTask()
                .WaitAsync(AssertionTimeout);

            failpoint.Release();
            var compacted = await WaitForMetricsAsync(
                database,
                static metrics => metrics.CompactionsRun >= 1,
                AssertionTimeout);

            Assert.True(compacted.TotalMemtableBytes > 0);
            Assert.DoesNotContain(
                (await database.GetStorageLayoutAsync()).Levels,
                static level => level.Level == 0);
            await Assert.ThrowsAsync<PantsBusyException>(() =>
                database.DropColumnFamilyAsync(family).AsTask());
            await database.DropColumnFamilyDiscardingUnflushedAsync(family)
                .AsTask()
                .WaitAsync(AssertionTimeout);
        }
        finally
        {
            failpoint.Release();
        }
    }

    [Fact]
    public async Task ShouldClearOnlyPublishedFlushIntentAfterRetry()
    {
        using var directory = new TemporaryDirectory();
        using var failpoint = new FlushPipelineFailpointHandler(
            Failpoint.AfterFlushManifestPublish,
            true);
        await using var database = await OpenAsync(directory.Path, failpoint);
        var family = await database.CreateColumnFamilyAsync("published-retry-intent");
        await File.WriteAllTextAsync(
            Path.Combine(directory.Path, "intent_log.json"),
            """
            [
              {
                "CompactionPlanned": {
                  "input_files": ["000000_00_00000000000000000099.sst"]
                }
              },
              {
                "FuturePublication": {
                  "opaque": true
                }
              },
              {
                "FlushPublish": "uncertain"
              }
            ]
            """);

        await CommitAsync(
            database,
            family,
            "published-before-failure"u8.ToArray(),
            new byte[160 * 1024]);
        _ = await WaitForMetricsAsync(
            database,
            static metrics => metrics.FlushFailuresTotal >= 1,
            AssertionTimeout);

        await database.FlushAsync(family).AsTask().WaitAsync(AssertionTimeout);

        using var intent = JsonDocument.Parse(
            await File.ReadAllBytesAsync(Path.Combine(directory.Path, "intent_log.json")));
        var retained = intent.RootElement.EnumerateArray().ToArray();
        Assert.Equal(3, retained.Length);
        Assert.Contains(retained, static entry => entry.TryGetProperty("CompactionPlanned", out _));
        Assert.Contains(retained, static entry => entry.TryGetProperty("FuturePublication", out _));
        Assert.Contains(retained, static entry => entry.TryGetProperty("FlushPublish", out _));
    }

    [Fact]
    public async Task ShouldRejectPublishedFlushRetryWhenCanonicalContentDiffers()
    {
        using var directory = new TemporaryDirectory();
        using var failpoint = new PublishedFlushRetryValidationFailpointHandler();
        await using var database = await OpenAsync(directory.Path, failpoint);
        var family = await database.CreateColumnFamilyAsync("published-retry-validation");
        try
        {
            await CommitAsync(
                database,
                family,
                "published-before-corruption"u8.ToArray(),
                new byte[160 * 1024]);
            await failpoint.WaitForRetryValidationAsync(AssertionTimeout);
            var sstPath = Assert.Single(Directory.GetFiles(
                Path.Combine(directory.Path, "sst"),
                "*.sst"));
            var expected = await File.ReadAllBytesAsync(sstPath);
            var corrupted = expected.ToArray();
            corrupted[^1] ^= 0xFF;
            await File.WriteAllBytesAsync(sstPath, corrupted);

            var retry = database.FlushAsync(family).AsTask();
            failpoint.Release();
            await Assert.ThrowsAsync<PantsCorruptionException>(() => retry);

            await File.WriteAllBytesAsync(sstPath, expected);
            await database.FlushAsync(family).AsTask().WaitAsync(AssertionTimeout);
        }
        finally
        {
            failpoint.Release();
        }
    }

    [Fact]
    public async Task ShouldDrainImmutableFlushBeforeOnlineVerificationAdmission()
    {
        using var directory = new TemporaryDirectory();
        using var failpoint = new FlushPipelineFailpointHandler(
            Failpoint.BeforeFlushPublication);
        var verifierStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        StorageVerificationDelegate verifier = (_, _) =>
        {
            verifierStarted.TrySetResult();
            return ValueTask.FromResult(new PantsStorageVerificationReport(
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
                []));
        };
        await using var database = await PantsDatabase.OpenForTestingAsync(
            CreateOptions(directory.Path),
            new RuntimeDependencies(failpoint, verifier));
        var family = await database.CreateColumnFamilyAsync("verify-after-flush");
        try
        {
            await CommitAsync(
                database,
                family,
                "verify-key"u8.ToArray(),
                new byte[160 * 1024]);
            await failpoint.WaitUntilEnteredAsync(AssertionTimeout);

            var verification = database.VerifyStorageAsync(AssertionTimeout).AsTask();
            var blocked = await database.GetRuntimeMetricsAsync()
                .AsTask()
                .WaitAsync(AssertionTimeout);

            Assert.False(verification.IsCompleted);
            Assert.False(verifierStarted.Task.IsCompleted);
            Assert.Equal(1, blocked.ImmutableMemtables);

            failpoint.Release();
            var report = await verification.WaitAsync(AssertionTimeout);
            Assert.True(report.Authoritative);
        }
        finally
        {
            failpoint.Release();
        }
    }

    [Fact]
    public async Task ShouldRearmDeferredCompactionWhenSignalArrivesDuringReset()
    {
        using var directory = new TemporaryDirectory();
        using var failpoint = new DeferredCompactionRaceFailpointHandler();
        var options = CreateOptions(directory.Path)
            .WithBackgroundCompaction(true)
            .WithCompaction(new PantsCompactionConfiguration(L0FileCountTrigger: 1));
        await using var database = await PantsDatabase.OpenForTestingAsync(
            options,
            new RuntimeDependencies(failpoint));
        var family = await database.CreateColumnFamilyAsync("compaction-signal-race");
        try
        {
            await CommitAsync(
                database,
                family,
                "first-generation"u8.ToArray(),
                new byte[160 * 1024]);
            await failpoint.WaitForCompactionAdmissionAsync(AssertionTimeout);

            await CommitAsync(
                database,
                family,
                "second-generation"u8.ToArray(),
                new byte[160 * 1024]);
            await failpoint.WaitForFlushPublicationAsync(AssertionTimeout);

            failpoint.ReleaseCompactionAdmission();
            await failpoint.WaitForSignalResetAsync(AssertionTimeout);
            failpoint.ReleaseFlushPublication();
            _ = await WaitForMetricsAsync(
                database,
                static metrics =>
                    metrics.ImmutableMemtables == 0 && metrics.FlushPublishCount >= 2,
                AssertionTimeout);

            failpoint.ReleaseSignalReset();
            var compacted = await WaitForMetricsAsync(
                database,
                static metrics => metrics.CompactionsRun >= 1,
                AssertionTimeout);
            Assert.True(compacted.CompactionsRun >= 1);
        }
        finally
        {
            failpoint.ReleaseCompactionAdmission();
            failpoint.ReleaseFlushPublication();
            failpoint.ReleaseSignalReset();
        }
    }

    [Fact]
    public async Task ShouldDeferBackgroundCompactionWhileVerificationPinsLayout()
    {
        using var directory = new TemporaryDirectory();
        using var failpoint = new VerificationCompactionRaceFailpointHandler();
        var verifierEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseVerifier = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        StorageVerificationDelegate verifier = async (_, cancellationToken) =>
        {
            verifierEntered.TrySetResult();
            await releaseVerifier.Task.WaitAsync(cancellationToken);
            return CreateAuthoritativeVerificationReport();
        };
        var options = CreateOptions(directory.Path)
            .WithBackgroundCompaction(true)
            .WithCompaction(new PantsCompactionConfiguration(L0FileCountTrigger: 1));
        await using var database = await PantsDatabase.OpenForTestingAsync(
            options,
            new RuntimeDependencies(failpoint, verifier));
        var family = await database.CreateColumnFamilyAsync("verification-compaction-race");
        Task<PantsStorageVerificationReport>? verification = null;
        try
        {
            await CommitAsync(
                database,
                family,
                "compaction-input"u8.ToArray(),
                new byte[160 * 1024]);
            await failpoint.WaitUntilEnteredAsync(AssertionTimeout);

            verification = database.VerifyStorageAsync(AssertionTimeout).AsTask();
            await verifierEntered.Task.WaitAsync(AssertionTimeout);
            failpoint.Release();

            var pinned = await database.GetRuntimeMetricsAsync()
                .AsTask()
                .WaitAsync(AssertionTimeout);
            Assert.Equal(0, pinned.CompactionsRun);

            releaseVerifier.TrySetResult();
            Assert.True((await verification.WaitAsync(AssertionTimeout)).Authoritative);
            var compacted = await WaitForMetricsAsync(
                database,
                static metrics => metrics.CompactionsRun >= 1,
                AssertionTimeout);
            Assert.True(compacted.CompactionsRun >= 1);
        }
        finally
        {
            failpoint.Release();
            releaseVerifier.TrySetResult();
            if (verification is not null)
            {
                _ = await verification.WaitAsync(AssertionTimeout);
            }
        }
    }

    [Fact]
    public async Task ShouldDeferExplicitFlushCompactionWhenVerificationWinsAdmissionRace()
    {
        using var directory = new TemporaryDirectory();
        using var failpoint = new FlushPipelineFailpointHandler(
            Failpoint.BeforeFlushCompactionAdmission);
        var verifierEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseVerifier = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        StorageVerificationDelegate verifier = async (_, cancellationToken) =>
        {
            verifierEntered.TrySetResult();
            await releaseVerifier.Task.WaitAsync(cancellationToken);
            return CreateAuthoritativeVerificationReport();
        };
        var options = CreateOptions(directory.Path)
            .WithCompaction(new PantsCompactionConfiguration(L0FileCountTrigger: 1));
        await using var database = await PantsDatabase.OpenForTestingAsync(
            options,
            new RuntimeDependencies(failpoint, verifier));
        var family = await database.CreateColumnFamilyAsync("flush-verification-race");
        Task<PantsStorageVerificationReport>? verification = null;
        try
        {
            await CommitAsync(
                database,
                family,
                "flush-input"u8.ToArray(),
                "value"u8.ToArray());
            var flush = database.FlushAsync(family).AsTask();
            await failpoint.WaitUntilEnteredAsync(AssertionTimeout);
            await database.SetBackgroundCompactionAsync(true);

            verification = database.VerifyStorageAsync(AssertionTimeout).AsTask();
            await verifierEntered.Task.WaitAsync(AssertionTimeout);
            failpoint.Release();
            await flush.WaitAsync(AssertionTimeout);

            Assert.Equal(0, (await database.GetRuntimeMetricsAsync()).CompactionsRun);

            releaseVerifier.TrySetResult();
            Assert.True((await verification.WaitAsync(AssertionTimeout)).Authoritative);
            _ = await WaitForMetricsAsync(
                database,
                static metrics => metrics.CompactionsRun >= 1,
                AssertionTimeout);
        }
        finally
        {
            failpoint.Release();
            releaseVerifier.TrySetResult();
            if (verification is not null)
            {
                _ = await verification.WaitAsync(AssertionTimeout);
            }
        }
    }

    [Fact]
    public async Task ShouldDeferReadAmplificationCompactionWhileVerificationPinsLayout()
    {
        using var directory = new TemporaryDirectory();
        var verifierEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseVerifier = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        StorageVerificationDelegate verifier = async (_, cancellationToken) =>
        {
            verifierEntered.TrySetResult();
            await releaseVerifier.Task.WaitAsync(cancellationToken);
            return CreateAuthoritativeVerificationReport();
        };
        var options = PantsOpenOptions.Local(directory.Path)
            .WithPerformanceGoal(PantsPerformanceGoal.Throughput)
            .WithWorkloadProfile(PantsWorkloadProfile.WriteHeavy)
            .WithBackgroundCompaction(false);
        await using var database = await PantsDatabase.OpenForTestingAsync(
            options,
            new RuntimeDependencies(storageVerifier: verifier));
        for (var generation = 0; generation < 6; generation++)
        {
            await CommitAsync(
                database,
                database.DefaultColumnFamily,
                "hot-key"u8.ToArray(),
                TestBytes.FromString($"value-{generation:D2}"));
            await database.FlushAsync(database.DefaultColumnFamily);
        }

        await database.SetBackgroundCompactionAsync(true);
        var verification = database.VerifyStorageAsync(AssertionTimeout).AsTask();
        try
        {
            await verifierEntered.Task.WaitAsync(AssertionTimeout);
            await using var read = await database.BeginTransactionAsync(
                database.DefaultColumnFamily,
                PantsTransactionMode.ReadOnly);
            Assert.NotNull(await read.GetAsync("hot-key"u8.ToArray()));

            Assert.Equal(0, (await database.GetRuntimeMetricsAsync()).CompactionsRun);

            releaseVerifier.TrySetResult();
            Assert.True((await verification.WaitAsync(AssertionTimeout)).Authoritative);
            _ = await WaitForMetricsAsync(
                database,
                static metrics => metrics.CompactionsRun >= 1,
                AssertionTimeout);
        }
        finally
        {
            releaseVerifier.TrySetResult();
            _ = await verification.WaitAsync(AssertionTimeout);
        }
    }

    [Fact]
    public async Task ShouldKeepActiveMemtableOutOfReadAmplificationCompaction()
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.Local(directory.Path)
            .WithPerformanceGoal(PantsPerformanceGoal.Throughput)
            .WithWorkloadProfile(PantsWorkloadProfile.WriteHeavy)
            .WithBackgroundCompaction(false);
        await using var database = await PantsDatabase.OpenAsync(options);
        for (var generation = 0; generation < 6; generation++)
        {
            await CommitAsync(
                database,
                database.DefaultColumnFamily,
                "hot-key"u8.ToArray(),
                TestBytes.FromString($"value-{generation:D2}"));
            await database.FlushAsync(database.DefaultColumnFamily);
        }

        await CommitAsync(
            database,
            database.DefaultColumnFamily,
            "racing-active"u8.ToArray(),
            "unflushed"u8.ToArray());
        Assert.True((await database.GetRuntimeMetricsAsync()).TotalMemtableBytes > 0);
        await database.SetBackgroundCompactionAsync(true);

        await using var read = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        Assert.NotNull(await read.GetAsync("hot-key"u8.ToArray()));
        Assert.Equal(
            "unflushed",
            TestBytes.ToText((await read.GetAsync("racing-active"u8.ToArray()))!.Value));

        var compacted = await database.GetRuntimeMetricsAsync();
        Assert.True(compacted.CompactionsRun >= 1);
        Assert.True(compacted.TotalMemtableBytes > 0);
        Assert.Equal(0, compacted.WalPendingWrites);
    }

    [Fact]
    public async Task ShouldFenceCompactionBeforeManifestAuthoritySwitch()
    {
        using var directory = new TemporaryDirectory();
        using var failpoint = new FlushPipelineFailpointHandler(
            Failpoint.BeforeCompactionManifestPublish);
        var options = CreateOptions(directory.Path)
            .WithCompaction(new PantsCompactionConfiguration(L0FileCountTrigger: 1));
        await using var database = await PantsDatabase.OpenForTestingAsync(
            options,
            new RuntimeDependencies(
                failpoint,
                leaseHeartbeatInterval: TimeSpan.FromHours(1)));
        var family = await database.CreateColumnFamilyAsync("fenced-compaction");
        for (var generation = 0; generation < 2; generation++)
        {
            await CommitAsync(
                database,
                family,
                TestBytes.FromString($"key-{generation}"),
                TestBytes.FromString($"value-{generation}"));
            await database.FlushAsync(family);
        }

        try
        {
            var compaction = database.CompactAllAsync().AsTask();
            await failpoint.WaitUntilEnteredAsync(AssertionTimeout);
            FenceWriterLease(directory.Path);
            failpoint.Release();

            var fenced = await Assert.ThrowsAsync<PantsFencedException>(() => compaction);
            Assert.Equal(PantsErrorCode.Fenced, fenced.Code);
            Assert.False(database.IsPrimaryLeaseHealthy);
            Assert.Equal(
                2,
                (await database.GetStorageLayoutAsync()).Levels
                .SelectMany(static level => level.Files)
                .Count(static file => file.Level == 0));
        }
        finally
        {
            failpoint.Release();
        }
    }

    [Fact]
    public async Task ShouldPreserveUnrelatedIntentsWhenCompactionCompletes()
    {
        using var directory = new TemporaryDirectory();
        var options = CreateOptions(directory.Path)
            .WithCompaction(new PantsCompactionConfiguration(L0FileCountTrigger: 1));
        await using var database = await PantsDatabase.OpenAsync(options);
        var family = await database.CreateColumnFamilyAsync("compaction-intent-coexistence");
        for (var generation = 0; generation < 2; generation++)
        {
            await CommitAsync(
                database,
                family,
                TestBytes.FromString($"key-{generation}"),
                TestBytes.FromString($"value-{generation}"));
            await database.FlushAsync(family);
        }

        await File.WriteAllTextAsync(
            Path.Combine(directory.Path, "intent_log.json"),
            """
            [
              {
                "CompactionPlanned": {
                  "input_files": ["000000_00_00000000000000000099.sst"]
                }
              },
              {
                "FuturePublication": {
                  "opaque": true
                }
              }
            ]
            """);

        await database.CompactAllAsync();

        using var intent = JsonDocument.Parse(
            await File.ReadAllBytesAsync(Path.Combine(directory.Path, "intent_log.json")));
        var retained = intent.RootElement.EnumerateArray().ToArray();
        Assert.Equal(2, retained.Length);
        Assert.Contains(retained, static entry => entry.TryGetProperty("CompactionPlanned", out _));
        Assert.Contains(retained, static entry => entry.TryGetProperty("FuturePublication", out _));
    }

    [Fact]
    public async Task ShouldTreatAuthoritativeCompactionCheckpointFailureAsDegradedSuccess()
    {
        using var directory = new TemporaryDirectory();
        using var recoveryDirectory = new TemporaryDirectory();
        var failpoint = new CompactionCheckpointFailpointHandler();
        var options = CreateOptions(directory.Path)
            .WithCompaction(new PantsCompactionConfiguration(L0FileCountTrigger: 2));
        string? publishedName = null;
        await using (var database = await PantsDatabase.OpenForTestingAsync(
                         options,
                         new RuntimeDependencies(failpoint)))
        {
            for (var index = 0; index < 2; index++)
            {
                await CommitBestEffortAsync(
                    database,
                    database.DefaultColumnFamily,
                    TestBytes.FromString($"key-{index}"),
                    TestBytes.FromString($"value-{index}"));
                await database.FlushAsync(database.DefaultColumnFamily);
            }

            var inputNames = (await database.GetStorageLayoutAsync()).Levels
                .SelectMany(static level => level.Files)
                .Select(static file => file.Name)
                .ToArray();
            Assert.Equal(2, inputNames.Length);

            await database.CompactAllAsync();

            var metrics = await database.GetRuntimeMetricsAsync();
            var layout = await database.GetStorageLayoutAsync();
            var file = Assert.Single(layout.Levels.SelectMany(static level => level.Files));
            publishedName = file.Name;
            using var intent = JsonDocument.Parse(
                await File.ReadAllBytesAsync(Path.Combine(directory.Path, "intent_log.json")));
            Assert.Equal(PantsEngineHealth.Degraded, metrics.Health);
            Assert.Equal(PantsEngineHealth.Degraded, layout.Health);
            Assert.True(file.Level > 0);
            var publication = Assert.Single(intent.RootElement.EnumerateArray())
                .GetProperty("CompactionPublish");
            Assert.Equal("ManifestPublished", publication.GetProperty("phase").GetString());
            Assert.Equal(
                inputNames.Order(StringComparer.Ordinal),
                publication.GetProperty("removed").EnumerateArray()
                    .Select(static name => name.GetString()!)
                    .Order(StringComparer.Ordinal));
            Assert.Equal(file.Name, publication.GetProperty("added")[0].GetProperty("name").GetString());
            Assert.All(inputNames, name => Assert.False(File.Exists(
                Path.Combine(directory.Path, "sst", name))));
            Assert.NotEqual(0, new FileInfo(Path.Combine(directory.Path, "manifest.journal")).Length);
            var fenced = await Assert.ThrowsAsync<PantsBusyException>(() =>
                database.CompactAllAsync().AsTask());
            Assert.Equal(PantsErrorCode.Busy, fenced.Code);

            CopyCrashImage(directory.Path, recoveryDirectory.Path);
            ExpireWriterLease(recoveryDirectory.Path);
        }

        await using var reopened = await PantsDatabase.OpenAsync(
            CreateOptions(recoveryDirectory.Path));
        Assert.Equal(PantsEngineHealth.Healthy, (await reopened.GetRuntimeMetricsAsync()).Health);
        var recoveredFile = Assert.Single(
            (await reopened.GetStorageLayoutAsync()).Levels.SelectMany(static level => level.Files));
        Assert.Equal(publishedName, recoveredFile.Name);
        Assert.True(recoveredFile.Level > 0);
        using (var intent = JsonDocument.Parse(
                   await File.ReadAllBytesAsync(Path.Combine(recoveryDirectory.Path, "intent_log.json"))))
        {
            Assert.Empty(intent.RootElement.EnumerateArray());
        }

        await using var read = await reopened.BeginTransactionAsync(
            reopened.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        for (var index = 0; index < 2; index++)
        {
            Assert.Equal(
                $"value-{index}",
                TestBytes.ToText((await read.GetAsync(TestBytes.FromString($"key-{index}")))!.Value));
        }
    }

    [Fact]
    public async Task ShouldSupersedeFailedCompactionIntentWhenRetryUsesNewOutputIdentity()
    {
        using var directory = new TemporaryDirectory();
        using var failpoint = new FlushPipelineFailpointHandler(
            Failpoint.BeforeCompactionManifestPublish,
            true);
        var options = CreateOptions(directory.Path)
            .WithBackgroundCompaction(false)
            .WithCompaction(new PantsCompactionConfiguration(L0FileCountTrigger: 2));
        await using (var database = await PantsDatabase.OpenForTestingAsync(
                         options,
                         new RuntimeDependencies(failpoint)))
        {
            for (var index = 0; index < 2; index++)
            {
                await CommitAsync(
                    database,
                    database.DefaultColumnFamily,
                    TestBytes.FromString($"key-{index}"),
                    TestBytes.FromString($"value-{index}"));
                await database.FlushAsync(database.DefaultColumnFamily);
            }

            await Assert.ThrowsAnyAsync<PantsException>(() => database.CompactAllAsync().AsTask());

            using var intent = JsonDocument.Parse(
                await File.ReadAllBytesAsync(Path.Combine(directory.Path, "intent_log.json")));
            var publication = Assert.Single(intent.RootElement.EnumerateArray())
                .GetProperty("CompactionPublish");
            var supersededOutput = publication.GetProperty("added")[0]
                .GetProperty("name")
                .GetString()!;

            await CommitBestEffortAsync(
                database,
                database.DefaultColumnFamily,
                "key-2"u8.ToArray(),
                "value-2"u8.ToArray());
            await database.FlushAsync(database.DefaultColumnFamily);
            await database.CompactAllAsync();

            Assert.False(File.Exists(Path.Combine(directory.Path, "sst", supersededOutput)));
            using var cleared = JsonDocument.Parse(
                await File.ReadAllBytesAsync(Path.Combine(directory.Path, "intent_log.json")));
            Assert.Empty(cleared.RootElement.EnumerateArray());
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);
        await using var read = await reopened.BeginTransactionAsync(
            reopened.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        for (var index = 0; index < 3; index++)
        {
            Assert.Equal(
                $"value-{index}",
                TestBytes.ToText((await read.GetAsync(TestBytes.FromString($"key-{index}")))!.Value));
        }
    }

    [Fact]
    public async Task ShouldNotPublishCompactionIntentBeforeSstDirectorySync()
    {
        using var directory = new TemporaryDirectory();
        using var failpoint = new FlushPipelineFailpointHandler(
            Failpoint.BeforeCompactionDirectorySync);
        var options = CreateOptions(directory.Path)
            .WithCompaction(new PantsCompactionConfiguration(L0FileCountTrigger: 1));
        await using var database = await PantsDatabase.OpenForTestingAsync(
            options,
            new RuntimeDependencies(failpoint));
        var family = await database.CreateColumnFamilyAsync("compaction-directory-sync");
        for (var generation = 0; generation < 2; generation++)
        {
            await CommitAsync(
                database,
                family,
                TestBytes.FromString($"key-{generation}"),
                TestBytes.FromString($"value-{generation}"));
            await database.FlushAsync(family);
        }

        try
        {
            var compaction = database.CompactAllAsync().AsTask();
            await failpoint.WaitUntilEnteredAsync(AssertionTimeout);
            using (var intent = JsonDocument.Parse(
                       await File.ReadAllBytesAsync(Path.Combine(directory.Path, "intent_log.json"))))
            {
                Assert.Empty(intent.RootElement.EnumerateArray());
            }

            using var manifest = JsonDocument.Parse(
                await File.ReadAllBytesAsync(
                    Path.Combine(directory.Path, "manifest.snapshot.json")));
            Assert.Equal(
                2,
                manifest.RootElement.GetProperty("files")
                    .EnumerateArray()
                    .Count(static file => file.GetProperty("level").GetUInt32() == 0));

            failpoint.Release();
            await compaction.WaitAsync(AssertionTimeout);
        }
        finally
        {
            failpoint.Release();
        }
    }

    [Fact]
    public async Task ShouldRetainCompactionRecoveryEvidenceAfterLeaseLoss()
    {
        using var directory = new TemporaryDirectory();
        using var failpoint = new FlushPipelineFailpointHandler(
            Failpoint.AfterCompactionManifestPublish);
        var options = CreateOptions(directory.Path)
            .WithCompaction(new PantsCompactionConfiguration(L0FileCountTrigger: 1));
        await using var database = await PantsDatabase.OpenForTestingAsync(
            options,
            new RuntimeDependencies(
                failpoint,
                leaseHeartbeatInterval: TimeSpan.FromHours(1)));
        var family = await database.CreateColumnFamilyAsync("compaction-recovery-evidence");
        for (var generation = 0; generation < 2; generation++)
        {
            await CommitAsync(
                database,
                family,
                TestBytes.FromString($"key-{generation}"),
                TestBytes.FromString($"value-{generation}"));
            await database.FlushAsync(family);
        }

        try
        {
            var compaction = database.CompactAllAsync().AsTask();
            await failpoint.WaitUntilEnteredAsync(AssertionTimeout);
            FenceWriterLease(directory.Path);
            failpoint.Release();

            await Assert.ThrowsAsync<PantsFencedException>(() => compaction);
            using var intent = JsonDocument.Parse(
                await File.ReadAllBytesAsync(Path.Combine(directory.Path, "intent_log.json")));
            Assert.Contains(
                intent.RootElement.EnumerateArray(),
                static entry => entry.TryGetProperty("CompactionPublish", out _));
        }
        finally
        {
            failpoint.Release();
        }
    }

    [Fact]
    public async Task ShouldDiscardDroppedColumnFamilyMutableOperations()
    {
        using var directory = new TemporaryDirectory();
        var options = CreateOptions(directory.Path);
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            for (var index = 0; index < 3; index++)
            {
                var discarded = await database.CreateColumnFamilyAsync($"discarded-{index}");
                await CommitAsync(
                    database,
                    discarded,
                    "discarded"u8.ToArray(),
                    "unflushed"u8.ToArray());
                await database.DropColumnFamilyDiscardingUnflushedAsync(discarded);
            }

            var survivor = await database.CreateColumnFamilyAsync("survivor");
            await CommitAsync(
                database,
                survivor,
                "surviving-key"u8.ToArray(),
                "surviving-value"u8.ToArray());
            await database.FlushAsync(survivor).AsTask().WaitAsync(AssertionTimeout);

            var reclaimed = await database.GetRuntimeMetricsAsync();
            Assert.Equal(0, reclaimed.ImmutableMemtables);
            Assert.Equal(0, reclaimed.TotalMemtableBytes);
            Assert.Equal(reclaimed.CurrentSequence, reclaimed.ManifestLastPersistedSequence);
            Assert.Equal(0, reclaimed.WalPendingWrites);
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);
        Assert.Null(await reopened.GetColumnFamilyAsync("discarded-0"));
        var recoveredSurvivor = Assert.IsAssignableFrom<IPantsColumnFamily>(
            await reopened.GetColumnFamilyAsync("survivor"));
        await using var read = await reopened.BeginTransactionAsync(
            recoveredSurvivor,
            PantsTransactionMode.ReadOnly);
        Assert.Equal(
            "surviving-value"u8.ToArray(),
            (await read.GetAsync("surviving-key"u8.ToArray()))?.ToArray());
    }

    [Fact]
    public async Task ShouldRecoverUnflushedFamilyWhenAnotherFamilyPublishesSharedWalFrontier()
    {
        using var directory = new TemporaryDirectory();
        var options = CreateOptions(directory.Path);
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            var flushed = await database.CreateColumnFamilyAsync("frontier-flushed");
            var pending = await database.CreateColumnFamilyAsync("frontier-pending");
            await CommitAsync(
                database,
                flushed,
                "flushed-key"u8.ToArray(),
                "flushed-value"u8.ToArray());
            await CommitAsync(
                database,
                pending,
                "pending-key"u8.ToArray(),
                "pending-value"u8.ToArray());

            await database.FlushAsync(flushed).AsTask().WaitAsync(AssertionTimeout);

            var partial = await database.GetRuntimeMetricsAsync();
            Assert.Equal(partial.CurrentSequence, partial.ManifestLastPersistedSequence);
            Assert.Equal(0, partial.WalPendingWrites);
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);
        var recoveredPending = Assert.IsAssignableFrom<IPantsColumnFamily>(
            await reopened.GetColumnFamilyAsync("frontier-pending"));
        await using var read = await reopened.BeginTransactionAsync(
            recoveredPending,
            PantsTransactionMode.ReadOnly);
        Assert.Equal(
            "pending-value"u8.ToArray(),
            (await read.GetAsync("pending-key"u8.ToArray()))?.ToArray());
        Assert.True((await reopened.GetRuntimeMetricsAsync()).TotalMemtableBytes > 0);
    }

    [Fact]
    public async Task ShouldAdvanceManifestFrontierWhenPublishedFlushRetryLeavesAnotherFamilyMutable()
    {
        using var directory = new TemporaryDirectory();
        using var failpoint = new BlockingThrowingFlushFailpointHandler(
            Failpoint.AfterFlushManifestPublish);
        await using var database = await OpenAsync(directory.Path, failpoint);
        var flushed = await database.CreateColumnFamilyAsync("retry-frontier-flushed");
        var pending = await database.CreateColumnFamilyAsync("retry-frontier-pending");
        await CommitAsync(
            database,
            flushed,
            "flushed-key"u8.ToArray(),
            "flushed-value"u8.ToArray());
        await CommitAsync(
            database,
            pending,
            "pending-key"u8.ToArray(),
            "pending-value"u8.ToArray());

        try
        {
            var firstFlush = database.FlushAsync(flushed).AsTask();
            await failpoint.WaitUntilEnteredAsync(AssertionTimeout);
            failpoint.Release();
            await Assert.ThrowsAsync<PantsIOException>(() => firstFlush);

            await database.FlushAsync(flushed).AsTask().WaitAsync(AssertionTimeout);
            var partial = await database.GetRuntimeMetricsAsync();
            Assert.Equal(partial.CurrentSequence, partial.ManifestLastPersistedSequence);
            Assert.Equal(0, partial.WalPendingWrites);
        }
        finally
        {
            failpoint.Release();
        }
    }

    [Fact]
    public async Task ShouldTreatAuthoritativeFlushCheckpointFailureAsDegradedSuccess()
    {
        using var directory = new TemporaryDirectory();
        using var recoveryDirectory = new TemporaryDirectory();
        var failpoint = new ArmableFailpointHandler();
        var options = CreateOptions(directory.Path);
        string? publishedName = null;
        await using (var database = await PantsDatabase.OpenForTestingAsync(
                         options,
                         new RuntimeDependencies(failpoint)))
        {
            await CommitBestEffortAsync(
                database,
                database.DefaultColumnFamily,
                "journal-authoritative"u8.ToArray(),
                "value"u8.ToArray());
            failpoint.Arm(Failpoint.BeforeManifestCheckpointReplace);

            await database.FlushAsync(database.DefaultColumnFamily);

            var metrics = await database.GetRuntimeMetricsAsync();
            var layout = await database.GetStorageLayoutAsync();
            var file = Assert.Single(layout.Levels.SelectMany(static level => level.Files));
            publishedName = file.Name;
            using var intent = JsonDocument.Parse(
                await File.ReadAllBytesAsync(Path.Combine(directory.Path, "intent_log.json")));
            Assert.Equal(PantsEngineHealth.Degraded, metrics.Health);
            Assert.Equal(PantsEngineHealth.Degraded, layout.Health);
            Assert.Equal(0, metrics.ImmutableMemtables);
            Assert.Equal(database.DefaultColumnFamily.Id, file.ColumnFamilyId);
            Assert.NotEqual(0, new FileInfo(Path.Combine(directory.Path, "manifest.journal")).Length);
            Assert.Empty(intent.RootElement.EnumerateArray());

            CopyCrashImage(directory.Path, recoveryDirectory.Path);
            ExpireWriterLease(recoveryDirectory.Path);
        }

        await using var reopened = await PantsDatabase.OpenAsync(
            CreateOptions(recoveryDirectory.Path));
        var recovered = await reopened.GetRuntimeMetricsAsync();
        var recoveredFile = Assert.Single(
            (await reopened.GetStorageLayoutAsync()).Levels.SelectMany(static level => level.Files));
        await using var read = await reopened.BeginTransactionAsync(
            reopened.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Equal(PantsEngineHealth.Healthy, recovered.Health);
        Assert.Equal(publishedName, recoveredFile.Name);
        Assert.Equal(0, recoveredFile.Level);
        Assert.NotNull(recoveredFile.LargestSequence);
        Assert.True(recovered.ManifestLastPersistedSequence >= recoveredFile.LargestSequence);
        Assert.Equal(recovered.CurrentSequence, recovered.ManifestLastPersistedSequence);
        Assert.Equal(
            "value",
            TestBytes.ToText((await read.GetAsync("journal-authoritative"u8.ToArray()))!.Value));
    }

    [Fact]
    public async Task ShouldPrioritizeWriteStalledHealthOverDegradedHealth()
    {
        const int flushThresholdBytes = 128 * 1024;
        const int keyAndEntryOverheadBytes = 65;
        using var directory = new TemporaryDirectory();
        using var failpoint = new DegradedWriteStallFailpointHandler();
        await using var database = await OpenAsync(directory.Path, failpoint);
        var family = await database.CreateColumnFamilyAsync("degraded-write-stall");
        var value = new byte[flushThresholdBytes - keyAndEntryOverheadBytes];
        await CommitBestEffortAsync(
            database,
            family,
            "degrade"u8.ToArray(),
            "value"u8.ToArray());
        failpoint.FailNextCheckpoint();
        await database.FlushAsync(family);
        Assert.Equal(
            PantsEngineHealth.Degraded,
            (await database.GetRuntimeMetricsAsync()).Health);

        try
        {
            failpoint.BlockNextFlushPublication();
            await CommitAsync(database, family, new byte[] { 1 }, value);
            await failpoint.WaitForFlushAsync(AssertionTimeout);
            await CommitAsync(database, family, new byte[] { 2 }, value);

            var metrics = await database.GetRuntimeMetricsAsync();
            var layout = await database.GetStorageLayoutAsync();

            Assert.True(metrics.WriteStalled);
            Assert.Equal(PantsEngineHealth.WriteStalled, metrics.Health);
            Assert.Equal(PantsEngineHealth.WriteStalled, layout.Health);
        }
        finally
        {
            failpoint.ReleaseFlush();
        }
    }

    [Fact]
    public async Task ShouldRecoverSequenceFloorAfterAuthoritativeCheckpointFailure()
    {
        using var directory = new TemporaryDirectory();
        var failpoint = new ArmableFailpointHandler();
        var options = CreateOptions(directory.Path);
        var committedSequence = 0L;
        await using (var database = await PantsDatabase.OpenForTestingAsync(
                         options,
                         new RuntimeDependencies(failpoint)))
        {
            await CommitAsync(
                database,
                database.DefaultColumnFamily,
                "durable"u8.ToArray(),
                "value"u8.ToArray());
            committedSequence = (await database.GetRuntimeMetricsAsync()).CurrentSequence;
            failpoint.Arm(Failpoint.BeforeManifestCheckpointReplace);

            await database.FlushAsync(database.DefaultColumnFamily);

            var degraded = await database.GetRuntimeMetricsAsync();
            Assert.Equal(PantsEngineHealth.Degraded, degraded.Health);
            Assert.Equal(0, degraded.ImmutableMemtables);
            Assert.True(committedSequence > 0);
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);
        var recovered = await reopened.GetRuntimeMetricsAsync();
        var largestSequence = Assert.Single(
                (await reopened.GetStorageLayoutAsync()).Levels.SelectMany(static level => level.Files))
            .LargestSequence;
        Assert.NotNull(largestSequence);
        Assert.True(recovered.CurrentSequence >= largestSequence);
        Assert.True(recovered.ManifestLastPersistedSequence >= largestSequence);
        await using (var read = await reopened.BeginTransactionAsync(
                         reopened.DefaultColumnFamily,
                         PantsTransactionMode.ReadOnly))
        {
            Assert.Equal(
                "value",
                TestBytes.ToText((await read.GetAsync("durable"u8.ToArray()))!.Value));
        }

        await CommitAsync(
            reopened,
            reopened.DefaultColumnFamily,
            "after-recovery"u8.ToArray(),
            "new-value"u8.ToArray());
        Assert.True((await reopened.GetRuntimeMetricsAsync()).CurrentSequence > committedSequence);
    }

    [Fact]
    public async Task ShouldPublishStableManifestSnapshotsDuringConcurrentLayoutReads()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(CreateOptions(directory.Path));
        var family = await database.CreateColumnFamilyAsync("manifest-snapshot-stress");
        var start = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        async Task WriteAsync()
        {
            await start.Task;
            for (var index = 0; index < 8; index++)
            {
                while (true)
                {
                    try
                    {
                        await CommitAsync(
                            database,
                            family,
                            TestBytes.FromString($"key-{index:D2}"),
                            new byte[132 * 1024]);
                        break;
                    }
                    catch (PantsWriteStallException)
                    {
                        Assert.True(await database.WaitForWriteStallClearAsync(
                            family,
                            AssertionTimeout));
                    }
                }
            }
        }

        async Task ReadLayoutAsync()
        {
            await start.Task;
            for (var index = 0; index < 32; index++)
            {
                _ = await database.GetRuntimeMetricsAsync();
                _ = await database.GetStorageLayoutAsync();
            }
        }

        var work = new[]
        {
            WriteAsync(),
            ReadLayoutAsync(),
            ReadLayoutAsync(),
            ReadLayoutAsync()
        };
        start.TrySetResult();
        await Task.WhenAll(work).WaitAsync(TimeSpan.FromSeconds(5));
        await database.FlushAsync(family).AsTask().WaitAsync(AssertionTimeout);

        var layout = await database.GetStorageLayoutAsync();
        var names = layout.Levels.SelectMany(static level => level.Files)
            .Select(static file => file.Name)
            .ToArray();
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(0, (await database.GetRuntimeMetricsAsync()).ImmutableMemtables);
    }

    static PantsOpenOptions CreateOptions(string path) =>
        PantsOpenOptions.Local(path)
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(2 * 1024 * 1024))
            .WithMemtableLimits(512 * 1024, 128 * 1024)
            .WithTransactionMemoryPool(512 * 1024)
            .WithBackgroundCompaction(false);

    static ValueTask<IPantsDatabase> OpenAsync(
        string path,
        IFailpointHandler failpoints) =>
        PantsDatabase.OpenForTestingAsync(
            CreateOptions(path),
            new RuntimeDependencies(failpoints));

    static async ValueTask SeedVisibleValuesAsync(
        IPantsDatabase database,
        IPantsColumnFamily family)
    {
        await using var transaction = await database.BeginTransactionAsync(
            family,
            PantsTransactionMode.ReadWrite);
        transaction.Put("overwrite"u8.ToArray(), "old"u8.ToArray());
        transaction.Put("point-delete"u8.ToArray(), "old"u8.ToArray());
        transaction.Put("range-b"u8.ToArray(), "old"u8.ToArray());
        await transaction.CommitAsync(PantsWriteOptions.Sync);
    }

    static async ValueTask RotateWithMixedOperationsAsync(
        IPantsDatabase database,
        IPantsColumnFamily family)
    {
        await using var transaction = await database.BeginTransactionAsync(
            family,
            PantsTransactionMode.ReadWrite);
        transaction.DeleteRange("range-a"u8.ToArray(), "range-z"u8.ToArray());
        transaction.Put("overwrite"u8.ToArray(), "new"u8.ToArray());
        transaction.Delete("point-delete"u8.ToArray());
        transaction.Put("rotation-payload"u8.ToArray(), new byte[160 * 1024]);
        await transaction.CommitAsync(PantsWriteOptions.Sync);
    }

    static async ValueTask AssertVisibilityWhileFlushIsBlockedAsync(
        IPantsDatabase database,
        IPantsColumnFamily family,
        IPantsTransaction oldSnapshot)
    {
        await using var current = await database.BeginTransactionAsync(
            family,
            PantsTransactionMode.ReadOnly);
        Assert.Equal("new"u8.ToArray(), (await current.GetAsync("overwrite"u8.ToArray()))?.ToArray());
        Assert.Null(await current.GetAsync("point-delete"u8.ToArray()));
        Assert.Null(await current.GetAsync("range-b"u8.ToArray()));
        Assert.Equal("old"u8.ToArray(), (await oldSnapshot.GetAsync("overwrite"u8.ToArray()))?.ToArray());
        Assert.Equal("old"u8.ToArray(), (await oldSnapshot.GetAsync("point-delete"u8.ToArray()))?.ToArray());
        Assert.Equal("old"u8.ToArray(), (await oldSnapshot.GetAsync("range-b"u8.ToArray()))?.ToArray());
    }

    static async ValueTask CommitAsync(
        IPantsDatabase database,
        IPantsColumnFamily family,
        ReadOnlyMemory<byte> key,
        ReadOnlyMemory<byte> value)
    {
        await using var transaction = await database.BeginTransactionAsync(
            family,
            PantsTransactionMode.ReadWrite);
        transaction.Put(key, value);
        await transaction.CommitAsync(PantsWriteOptions.Sync);
    }

    static async ValueTask CommitBestEffortAsync(
        IPantsDatabase database,
        IPantsColumnFamily family,
        ReadOnlyMemory<byte> key,
        ReadOnlyMemory<byte> value)
    {
        await using var transaction = await database.BeginTransactionAsync(
            family,
            PantsTransactionMode.ReadWrite);
        transaction.Put(key, value);
        await transaction.CommitAsync(PantsWriteOptions.BestEffort);
    }

    static async ValueTask<PantsRuntimeMetrics> WaitForMetricsAsync(
        IPantsDatabase database,
        Func<PantsRuntimeMetrics, bool> predicate,
        TimeSpan timeout)
    {
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            var remaining = timeout - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException("Timed out waiting for runtime metrics.");
            }

            var metrics = await database.GetRuntimeMetricsAsync().AsTask().WaitAsync(remaining);
            if (predicate(metrics))
            {
                return metrics;
            }

            await Task.Yield();
        }
    }

    static void FenceWriterLease(string path)
    {
        var leasePath = Path.Combine(path, ".midge_leader");
        var fields = File.ReadAllLines(leasePath)
            .Select(static line => line.Split(": ", 2))
            .Where(static parts => parts.Length == 2)
            .ToDictionary(static parts => parts[0], static parts => parts[1], StringComparer.Ordinal);
        var nextEpoch = checked(ulong.Parse(
            fields["epoch"],
            CultureInfo.InvariantCulture) + 1);
        File.WriteAllText(
            leasePath,
            $"epoch: {nextEpoch}\nholder_id: fenced-writer\nacquired_at: {DateTimeOffset.UtcNow:O}\n");
    }

    static void CopyCrashImage(string source, string destination)
    {
        foreach (var sourceDirectory in Directory.EnumerateDirectories(
                     source,
                     "*",
                     SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(
                destination,
                Path.GetRelativePath(source, sourceDirectory)));
        }

        foreach (var sourceFile in Directory.EnumerateFiles(
                     source,
                     "*",
                     SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(source, sourceFile);
            if (relativePath is "LOCK" or ".midge_leader.lock")
            {
                continue;
            }

            var destinationFile = Path.Combine(destination, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
            File.Copy(sourceFile, destinationFile, true);
        }
    }

    static void ExpireWriterLease(string path)
    {
        var leasePath = Path.Combine(path, ".midge_leader");
        var lines = File.ReadAllLines(leasePath)
            .Select(line => line.StartsWith("acquired_at: ", StringComparison.Ordinal)
                ? "acquired_at: 1970-01-01T00:00:00Z"
                : line)
            .ToArray();
        File.WriteAllLines(leasePath, lines);
    }

    static PantsStorageVerificationReport CreateAuthoritativeVerificationReport() => new(
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
}

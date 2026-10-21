using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json.Nodes;
using Cntryl.Pants.Storage.Internal;
using Cntryl.Pants.Support.Failpoints;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Compatibility;

public sealed class PantsBackupTests
{
    [Fact]
    public async Task ShouldRestoreCommittedValuesGivenValidLocalBackup()
    {
        using var directory = new TemporaryDirectory();
        var sourcePath = Path.Combine(directory.Path, "source");
        var artifactPath = Path.Combine(directory.Path, "backup");
        var restoredPath = Path.Combine(directory.Path, "restored");
        await using (var source = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(sourcePath)))
        {
            await using var transaction = await source.Transactions.BeginAsync(
                source.ColumnFamilies.DefaultFamily,
                PantsTransactionMode.ReadWrite);
            transaction.Put("key"u8.ToArray(), "value"u8.ToArray());
            await transaction.CommitAsync(PantsWriteOptions.Sync);

            var manifest = await source.BackupAsync(artifactPath, TimeSpan.FromSeconds(10));

            Assert.Equal(PantsBackupStorageKind.Local, manifest.StorageKind);
            Assert.True(manifest.DurabilityFrontier > 0);
            Assert.Contains(manifest.Objects, item => item.Path == "wal/wal.log");
        }

        await PantsDatabase.RestoreBackupAsync(artifactPath, PantsOpenOptions.Local(restoredPath));

        await using var restored = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(restoredPath));
        await using var reader = await restored.Transactions.BeginAsync(
            restored.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);

        Assert.Equal("value"u8.ToArray(),
            Assert.IsType<ReadOnlyMemory<byte>>(await reader.GetAsync("key"u8.ToArray())).ToArray());
    }

    [Fact]
    public async Task ShouldPreserveCrossFamilyFrontierGivenConcurrentBackupCapture()
    {
        using var directory = new TemporaryDirectory();
        var sourcePath = Path.Combine(directory.Path, "source");
        var artifactPath = Path.Combine(directory.Path, "backup");
        var restoredPath = Path.Combine(directory.Path, "restored");
        await using (var source = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(sourcePath)))
        {
            var events = await source.ColumnFamilies.CreateAsync("events");
            var checkpoint = await source.ColumnFamilies.CreateAsync("checkpoint");
            await CommitAsync(source, events, U64(0), "event"u8.ToArray());
            await CommitAsync(source, checkpoint, "frontier"u8.ToArray(), U64(0));

            using var running = new CancellationTokenSource();
            var writerStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var writer = Task.Run(async () =>
            {
                ulong next = 1;
                while (!running.IsCancellationRequested)
                {
                    await CommitAsync(
                        source,
                        events,
                        U64(next),
                        "event"u8.ToArray(),
                        retryBusy: true);
                    await CommitAsync(
                        source,
                        checkpoint,
                        "frontier"u8.ToArray(),
                        U64(next),
                        retryBusy: true);
                    writerStarted.TrySetResult();
                    next++;
                    await Task.Yield();
                }
            });

            await writerStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            PantsBackupManifest manifest;
            try
            {
                manifest = await source.BackupAsync(artifactPath, TimeSpan.FromSeconds(10));
            }
            finally
            {
                running.Cancel();
                await writer;
            }

            Assert.True(manifest.DurabilityFrontier > 0);
        }

        await PantsDatabase.RestoreBackupAsync(artifactPath, PantsOpenOptions.Local(restoredPath));
        await using var restored = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(restoredPath));
        var restoredEvents = await restored.ColumnFamilies.GetAsync("events");
        Assert.NotNull(restoredEvents);
        var checkpointFamily = await restored.ColumnFamilies.GetAsync("checkpoint");
        Assert.NotNull(checkpointFamily);
        await using var reader = await restored.Transactions.BeginAsync(
            restoredEvents,
            PantsTransactionMode.ReadOnly);
        await using var checkpointReader = await restored.Transactions.BeginAsync(
            checkpointFamily,
            PantsTransactionMode.ReadOnly);
        var encodedFrontier = await checkpointReader.GetAsync("frontier"u8.ToArray());
        Assert.NotNull(encodedFrontier);
        var frontier = BinaryPrimitives.ReadUInt64BigEndian(encodedFrontier!.Value.Span);
        for (ulong sequence = 0; sequence <= frontier; sequence++)
        {
            Assert.Equal(
                "event"u8.ToArray(),
                Assert.IsType<ReadOnlyMemory<byte>>(
                    await reader.GetAsync(U64(sequence))).ToArray());
        }
    }

    [Fact]
    public async Task ShouldRestoreSplitSimulatedCloudLayoutGivenValidBackup()
    {
        using var directory = new TemporaryDirectory();
        var sourcePath = Path.Combine(directory.Path, "source");
        var artifactPath = Path.Combine(directory.Path, "backup");
        var restoredPath = Path.Combine(directory.Path, "restored");
        var sourceOptions = PantsOpenOptions.SimulatedCloud(sourcePath, "bucket", "prefix");
        await using (var source = await PantsDatabase.OpenAsync(sourceOptions))
        {
            await CommitAsync(
                source,
                source.ColumnFamilies.DefaultFamily,
                "key"u8.ToArray(),
                "value"u8.ToArray(),
                PantsWriteOptions.CloudAsync);
            await source.Maintenance.FlushAsync(source.ColumnFamilies.DefaultFamily);

            var manifest = await source.BackupAsync(artifactPath, TimeSpan.FromSeconds(10));

            Assert.Equal(PantsBackupStorageKind.CloudSimulated, manifest.StorageKind);
            Assert.Contains(manifest.Objects, item => item.Path.StartsWith(
                "cloud_store/",
                StringComparison.Ordinal));
            Assert.All(manifest.Objects, item => Assert.DoesNotContain('\\', item.Path));
        }

        var restoreOptions = PantsOpenOptions.SimulatedCloud(restoredPath, "bucket", "prefix");
        await PantsDatabase.RestoreBackupAsync(artifactPath, restoreOptions);
        await using var restored = await PantsDatabase.OpenAsync(restoreOptions);
        Assert.Equal("value"u8.ToArray(), await ReadAsync(
            restored,
            restored.ColumnFamilies.DefaultFamily,
            "key"u8.ToArray()));
    }

    [Fact]
    public async Task ShouldNotPublishBackupGivenCorruptSourceSst()
    {
        using var directory = new TemporaryDirectory();
        var sourcePath = Path.Combine(directory.Path, "source");
        var artifactPath = Path.Combine(directory.Path, "backup");
        await using var source = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(sourcePath));
        await CommitAsync(
            source,
            source.ColumnFamilies.DefaultFamily,
            "key"u8.ToArray(),
            "value"u8.ToArray());
        await source.Maintenance.FlushAsync(source.ColumnFamilies.DefaultFamily);
        var sstPath = Directory.EnumerateFiles(Path.Combine(sourcePath, "sst"), "*.sst")
            .Single();
        var bytes = await File.ReadAllBytesAsync(sstPath);
        bytes[^1] ^= 1;
        await File.WriteAllBytesAsync(sstPath, bytes);

        await Assert.ThrowsAsync<PantsCorruptionException>(() =>
            source.BackupAsync(artifactPath, TimeSpan.FromSeconds(10)).AsTask());

        Assert.False(Directory.Exists(artifactPath));
    }

    [Fact]
    public async Task ShouldRejectBackupOverlapBeforeChangingSource()
    {
        using var directory = new TemporaryDirectory();
        var sourcePath = Path.Combine(directory.Path, "source");
        await using var source = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(sourcePath));
        await CommitAsync(
            source,
            source.ColumnFamilies.DefaultFamily,
            "key"u8.ToArray(),
            "value"u8.ToArray());
        var walBefore = await File.ReadAllBytesAsync(Path.Combine(sourcePath, "wal", "wal.log"));

        await Assert.ThrowsAsync<PantsInvalidArgumentException>(() =>
            source.BackupAsync(
                Path.Combine(sourcePath, "nested", "backup"),
                TimeSpan.FromSeconds(10)).AsTask());

        Assert.Equal(walBefore, await File.ReadAllBytesAsync(Path.Combine(sourcePath, "wal", "wal.log")));
        Assert.False(Directory.Exists(Path.Combine(sourcePath, "nested")));
    }

    [Fact]
    public async Task ShouldRejectRestoreOverlapWithoutChangingArtifact()
    {
        using var directory = new TemporaryDirectory();
        var sourcePath = Path.Combine(directory.Path, "source");
        var artifactPath = Path.Combine(directory.Path, "backup");
        await using (var source = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(sourcePath)))
        {
            await source.BackupAsync(artifactPath, TimeSpan.FromSeconds(10));
        }

        var before = ReadTree(artifactPath);
        await Assert.ThrowsAsync<PantsInvalidArgumentException>(() =>
            PantsDatabase.RestoreBackupAsync(
                artifactPath,
                PantsOpenOptions.Local(Path.Combine(artifactPath, "nested", "restored"))).AsTask());

        Assert.Equal(before, ReadTree(artifactPath));
    }

    [Fact]
    public async Task ShouldLeaveRestoreTargetAbsentGivenCorruptBackupObject()
    {
        using var directory = new TemporaryDirectory();
        var artifactPath = Path.Combine(directory.Path, "backup");
        var targetPath = Path.Combine(directory.Path, "restored");
        await using (var source = await PantsDatabase.OpenAsync(
                         PantsOpenOptions.Local(Path.Combine(directory.Path, "source"))))
        {
            await source.BackupAsync(artifactPath, TimeSpan.FromSeconds(10));
        }

        var marker = Path.Combine(artifactPath, "objects", "FORMAT");
        var bytes = await File.ReadAllBytesAsync(marker);
        bytes[0] ^= 1;
        await File.WriteAllBytesAsync(marker, bytes);

        await Assert.ThrowsAsync<PantsCorruptionException>(() =>
            PantsDatabase.RestoreBackupAsync(artifactPath, PantsOpenOptions.Local(targetPath)).AsTask());

        Assert.False(Directory.Exists(targetPath));
    }

    [Fact]
    public async Task ShouldLeaveRestoreTargetAbsentGivenIncompleteBackupArtifact()
    {
        using var directory = new TemporaryDirectory();
        var artifactPath = Path.Combine(directory.Path, "backup");
        var targetPath = Path.Combine(directory.Path, "restored");
        await using (var source = await PantsDatabase.OpenAsync(
                         PantsOpenOptions.Local(Path.Combine(directory.Path, "source"))))
        {
            await source.BackupAsync(artifactPath, TimeSpan.FromSeconds(10));
        }

        var document = JsonNode.Parse(await File.ReadAllTextAsync(
            Path.Combine(artifactPath, "backup.json")))!.AsObject();
        var firstObject = document["objects"]!.AsArray()[0]!.AsObject()["path"]!.GetValue<string>();
        File.Delete(Path.Combine(artifactPath, "objects", firstObject.Replace('/', Path.DirectorySeparatorChar)));

        await Assert.ThrowsAsync<PantsCorruptionException>(() =>
            PantsDatabase.RestoreBackupAsync(artifactPath, PantsOpenOptions.Local(targetPath)).AsTask());

        Assert.False(Directory.Exists(targetPath));
    }

    [Fact]
    public async Task ShouldRejectBackupWhenRootHasNoHostDirectory()
    {
        using var directory = new TemporaryDirectory();
        await using var memory = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());

        await Assert.ThrowsAsync<PantsNotSupportedException>(() =>
            memory.BackupAsync(
                Path.Combine(directory.Path, "backup"),
                TimeSpan.FromSeconds(10)).AsTask());

        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task ShouldRejectLinkAliasesBeforeMutation()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        var sourcePath = Path.Combine(directory.Path, "source");
        var aliasPath = Path.Combine(directory.Path, "alias");
        var danglingPath = Path.Combine(directory.Path, "dangling");
        await using var source = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(sourcePath));
        Directory.CreateSymbolicLink(aliasPath, sourcePath);
        File.CreateSymbolicLink(danglingPath, Path.Combine(sourcePath, "missing"));

        await Assert.ThrowsAsync<PantsInvalidArgumentException>(() =>
            source.BackupAsync(Path.Combine(aliasPath, "backup"), TimeSpan.FromSeconds(10)).AsTask());
        await Assert.ThrowsAnyAsync<PantsException>(() =>
            source.BackupAsync(Path.Combine(danglingPath, "backup"), TimeSpan.FromSeconds(10)).AsTask());

        Assert.False(Directory.Exists(Path.Combine(sourcePath, "backup")));
    }

    [Fact]
    public async Task ShouldRejectParentTraversalThroughMissingOrRegularAncestor()
    {
        using var directory = new TemporaryDirectory();
        var sourcePath = Path.Combine(directory.Path, "source");
        var regularAncestor = Path.Combine(directory.Path, "regular-file");
        await File.WriteAllTextAsync(regularAncestor, "retain");
        await using (var source = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(sourcePath)))
        {
            foreach (var ancestor in new[] { "missing", "regular-file" })
            {
                await Assert.ThrowsAsync<PantsInvalidArgumentException>(() =>
                    source.BackupAsync(
                        Path.Combine(directory.Path, ancestor, "..", "backup"),
                        TimeSpan.FromSeconds(10)).AsTask());
            }
        }

        Assert.Equal("retain", await File.ReadAllTextAsync(regularAncestor));
        Assert.False(Directory.Exists(Path.Combine(directory.Path, "missing")));
        Assert.False(File.Exists(Path.Combine(directory.Path, "backup")));
    }

    [Fact]
    public async Task ShouldRejectRelativeSimulatedCloudBackupAndRestoreOverlap()
    {
        using var directory = new TemporaryDirectory();
        var sourcePath = Path.Combine(directory.Path, "source");
        var artifactPath = Path.Combine(directory.Path, "backup");
        var sourceOptions = PantsOpenOptions.SimulatedCloud(sourcePath, "bucket", "prefix");
        await using var source = await PantsDatabase.OpenAsync(sourceOptions);
        var relativeSourceTarget = Path.Combine(
            Path.GetRelativePath(Environment.CurrentDirectory, sourcePath),
            "nested",
            "backup");

        await Assert.ThrowsAsync<PantsInvalidArgumentException>(() =>
            source.BackupAsync(relativeSourceTarget, TimeSpan.FromSeconds(10)).AsTask());
        await source.BackupAsync(artifactPath, TimeSpan.FromSeconds(10));
        var artifactBefore = ReadTree(artifactPath);
        var relativeRestoreTarget = Path.Combine(
            Path.GetRelativePath(Environment.CurrentDirectory, artifactPath),
            "nested",
            "restored");

        await Assert.ThrowsAsync<PantsInvalidArgumentException>(() =>
            PantsDatabase.RestoreBackupAsync(
                artifactPath,
                PantsOpenOptions.SimulatedCloud(relativeRestoreTarget, "bucket", "prefix"))
                .AsTask());

        Assert.Equal(artifactBefore, ReadTree(artifactPath));
    }

    [Fact]
    public async Task ShouldCaptureOnlyRetainedWalSegmentsGivenOrphansInWalDirectory()
    {
        using var directory = new TemporaryDirectory();
        var sourcePath = Path.Combine(directory.Path, "source");
        await using var source = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(sourcePath));
        await CommitAsync(
            source,
            source.ColumnFamilies.DefaultFamily,
            "key"u8.ToArray(),
            "value"u8.ToArray());
        var orphan = Path.Combine(sourcePath, "wal", "00000000000000000099.wal");
        await File.WriteAllTextAsync(orphan, "orphan");

        var manifest = await source.BackupAsync(
            Path.Combine(directory.Path, "backup"),
            TimeSpan.FromSeconds(10));

        Assert.DoesNotContain(manifest.Objects, item => item.Path.EndsWith(
            "00000000000000000099.wal",
            StringComparison.Ordinal));
        Assert.Contains(manifest.Objects, item => item.Path == "wal/wal.log");
    }

    [Fact]
    public async Task ShouldRejectBackslashInSerializedBackupInventoryPath()
    {
        using var directory = new TemporaryDirectory();
        var sourcePath = Path.Combine(directory.Path, "source");
        var artifactPath = Path.Combine(directory.Path, "backup");
        var restorePath = Path.Combine(directory.Path, "restored");
        await using (var source = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(sourcePath)))
        {
            await source.BackupAsync(artifactPath, TimeSpan.FromSeconds(10));
        }

        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(
            Path.Combine(artifactPath, "backup.json")))!.AsObject();
        manifest["objects"]!.AsArray()[0]!.AsObject()["path"] = "wal\\wal.log";
        await File.WriteAllTextAsync(
            Path.Combine(artifactPath, "backup.json"),
            manifest.ToJsonString());

        await Assert.ThrowsAsync<PantsCorruptionException>(() =>
            PantsDatabase.RestoreBackupAsync(
                artifactPath,
                PantsOpenOptions.Local(restorePath)).AsTask());

        Assert.False(Directory.Exists(restorePath));
    }

    [Fact]
    public async Task ShouldPreserveCapturedManifestJournalWhenLiveJournalIsTruncated()
    {
        using var directory = new TemporaryDirectory();
        var sourcePath = Path.Combine(directory.Path, "source");
        var objectsPath = Path.Combine(directory.Path, "staging", "objects");
        Directory.CreateDirectory(sourcePath);
        Directory.CreateDirectory(objectsPath);
        var journalPath = Path.Combine(sourcePath, "manifest.journal");
        var capturedJournal = "durable manifest edit"u8.ToArray();
        await File.WriteAllBytesAsync(journalPath, capturedJournal);

        using var pinned = await BackupArtifactStore.PinFilesAsync(
            sourcePath,
            [journalPath],
            objectsPath,
            CancellationToken.None);
        await File.WriteAllBytesAsync(journalPath, []);

        Assert.Equal(
            capturedJournal,
            await File.ReadAllBytesAsync(Path.Combine(objectsPath, "manifest.journal")));
    }

    [Fact]
    public async Task ShouldPinNestedWalFileUsingPortableSeparators()
    {
        using var directory = new TemporaryDirectory();
        var sourcePath = Path.Combine(directory.Path, "source");
        var objectsPath = Path.Combine(directory.Path, "staging", "objects");
        var walPath = Path.Combine(sourcePath, "wal", "00000000000000000001.wal");
        Directory.CreateDirectory(Path.GetDirectoryName(walPath)!);
        Directory.CreateDirectory(objectsPath);
        await File.WriteAllBytesAsync(walPath, "sealed wal"u8.ToArray());

        using var pinned = await BackupArtifactStore.PinFilesAsync(
            sourcePath,
            [walPath],
            objectsPath,
            CancellationToken.None);

        Assert.Equal("wal/00000000000000000001.wal", Assert.Single(pinned.Files).RelativePath);
    }

    [Fact]
    public async Task ShouldPreserveTargetCreatedImmediatelyBeforePublication()
    {
        foreach (var restore in new[] { false, true })
        {
            using var directory = new TemporaryDirectory();
            var sourcePath = Path.Combine(directory.Path, "source");
            var artifactPath = Path.Combine(directory.Path, "backup");
            var targetPath = Path.Combine(directory.Path, "target");
            var failpoint = restore ? Failpoint.BeforeRestorePublish : Failpoint.BeforeBackupPublish;
            var handler = new CallbackFailpointHandler(failpoint, () =>
            {
                Directory.CreateDirectory(targetPath);
                File.WriteAllText(Path.Combine(targetPath, "retain"), "foreign");
            });
            await using var source = await PantsDatabase.OpenForTestingAsync(
                PantsOpenOptions.Local(sourcePath),
                new RuntimeDependencies(failpoints: handler));
            if (restore)
            {
                await source.BackupAsync(artifactPath, TimeSpan.FromSeconds(10));
            }

            var result = restore
                ? PantsDatabase.RestoreBackupForTestingAsync(
                    artifactPath,
                    PantsOpenOptions.Local(targetPath),
                    handler).AsTask()
                : source.BackupAsync(targetPath, TimeSpan.FromSeconds(10)).AsTask();

            await Assert.ThrowsAsync<PantsInvalidArgumentException>(() => result);

            Assert.Equal("foreign", await File.ReadAllTextAsync(Path.Combine(targetPath, "retain")));
            Assert.DoesNotContain(
                Directory.EnumerateDirectories(directory.Path),
                path => Path.GetFileName(path).StartsWith(".midge-", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task ShouldPreserveSymlinkTargetCreatedImmediatelyBeforePublication()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        foreach (var restore in new[] { false, true })
        {
            using var directory = new TemporaryDirectory();
            var sourcePath = Path.Combine(directory.Path, "source");
            var artifactPath = Path.Combine(directory.Path, "backup");
            var targetPath = Path.Combine(directory.Path, "target");
            var foreignPath = Path.Combine(directory.Path, "foreign");
            Directory.CreateDirectory(foreignPath);
            var markerPath = Path.Combine(foreignPath, "retain");
            await File.WriteAllTextAsync(markerPath, "foreign");
            var failpoint = restore ? Failpoint.BeforeRestorePublish : Failpoint.BeforeBackupPublish;
            var handler = new CallbackFailpointHandler(failpoint, () =>
                Directory.CreateSymbolicLink(targetPath, foreignPath));
            await using var source = await PantsDatabase.OpenForTestingAsync(
                PantsOpenOptions.Local(sourcePath),
                new RuntimeDependencies(failpoints: handler));
            if (restore)
            {
                await source.BackupAsync(artifactPath, TimeSpan.FromSeconds(10));
            }

            var result = restore
                ? PantsDatabase.RestoreBackupForTestingAsync(
                    artifactPath,
                    PantsOpenOptions.Local(targetPath),
                    handler).AsTask()
                : source.BackupAsync(targetPath, TimeSpan.FromSeconds(10)).AsTask();

            await Assert.ThrowsAsync<PantsInvalidArgumentException>(() => result);

            Assert.Equal(foreignPath, new DirectoryInfo(targetPath).LinkTarget);
            Assert.Equal("foreign", await File.ReadAllTextAsync(markerPath));
        }
    }

    [Fact]
    public async Task ShouldAllowWritesAfterCaptureGivenBackupCopyIsBlocked()
    {
        using var directory = new TemporaryDirectory();
        var handler = new BlockingBackupCopyFailpointHandler();
        var options = PantsOpenOptions.Local(Path.Combine(directory.Path, "source"));
        await using var source = await PantsDatabase.OpenForTestingAsync(
            options,
            new RuntimeDependencies(failpoints: handler));
        var artifactPath = Path.Combine(directory.Path, "backup");
        await CommitAsync(source, source.ColumnFamilies.DefaultFamily, "before"u8.ToArray(), "yes"u8.ToArray());
        var baseline = await source.BackupAsync(
            Path.Combine(directory.Path, "baseline"),
            TimeSpan.FromSeconds(10));

        handler.Arm();
        var backup = source.BackupAsync(artifactPath, TimeSpan.FromSeconds(10)).AsTask();
        await handler.Blocked.WaitAsync(TimeSpan.FromSeconds(10));
        var write = CommitAsync(
            source,
            source.ColumnFamilies.DefaultFamily,
            "after"u8.ToArray(),
            "yes"u8.ToArray(),
            retryBusy: true).AsTask();
        Exception? writeFailure = null;
        try
        {
            await write.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception exception)
        {
            writeFailure = exception;
        }
        finally
        {
            handler.Release();
        }

        var manifest = await backup;
        await write;
        Assert.Null(writeFailure);
        Assert.Equal(baseline.DurabilityFrontier, manifest.DurabilityFrontier);
        Assert.True(DateTimeOffset.Parse(manifest.CapturedAt, CultureInfo.InvariantCulture) <= handler.BlockedAt);
        Assert.Equal("yes"u8.ToArray(), await ReadAsync(
            source,
            source.ColumnFamilies.DefaultFamily,
            "after"u8.ToArray()));

        var restoredPath = Path.Combine(directory.Path, "restored");
        await PantsDatabase.RestoreBackupAsync(artifactPath, PantsOpenOptions.Local(restoredPath));
        await using var restored = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(restoredPath));
        await using var reader = await restored.Transactions.BeginAsync(
            restored.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Null(await reader.GetAsync("after"u8.ToArray()));
        Assert.Equal("yes"u8.ToArray(),
            Assert.IsType<ReadOnlyMemory<byte>>(await reader.GetAsync("before"u8.ToArray())).ToArray());
    }

    [Fact]
    public async Task ShouldNotPublishBackupGivenInterruptedObjectMaterialization()
    {
        using var directory = new TemporaryDirectory();
        var handler = new ArmableFailpointHandler();
        handler.Arm(Failpoint.AfterBackupObjectCopy);
        await using var source = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Local(Path.Combine(directory.Path, "source")),
            new RuntimeDependencies(failpoints: handler));
        var artifactPath = Path.Combine(directory.Path, "backup");

        await Assert.ThrowsAsync<IOException>(() =>
            source.BackupAsync(artifactPath, TimeSpan.FromSeconds(10)).AsTask());

        Assert.False(Directory.Exists(artifactPath));
        Assert.DoesNotContain(
            Directory.EnumerateDirectories(directory.Path),
            path => Path.GetFileName(path).StartsWith(".midge-backup-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ShouldNotPublishRestoreTargetGivenInterruptedObjectCopy()
    {
        using var directory = new TemporaryDirectory();
        var artifactPath = Path.Combine(directory.Path, "backup");
        var targetPath = Path.Combine(directory.Path, "restored");
        await using (var source = await PantsDatabase.OpenAsync(
                         PantsOpenOptions.Local(Path.Combine(directory.Path, "source"))))
        {
            await source.BackupAsync(artifactPath, TimeSpan.FromSeconds(10));
        }

        var handler = new ArmableFailpointHandler();
        handler.Arm(Failpoint.AfterRestoreObjectCopy);
        await Assert.ThrowsAsync<IOException>(() =>
            PantsDatabase.RestoreBackupForTestingAsync(
                artifactPath,
                PantsOpenOptions.Local(targetPath),
                handler).AsTask());

        Assert.False(Directory.Exists(targetPath));
        Assert.DoesNotContain(
            Directory.EnumerateDirectories(directory.Path),
            path => Path.GetFileName(path).StartsWith(".midge-restore-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ShouldPreserveForeignStageWhenRetryingRestore()
    {
        using var directory = new TemporaryDirectory();
        var artifactPath = Path.Combine(directory.Path, "backup");
        await using (var source = await PantsDatabase.OpenAsync(
                         PantsOpenOptions.Local(Path.Combine(directory.Path, "source"))))
        {
            await source.BackupAsync(artifactPath, TimeSpan.FromSeconds(10));
        }

        var foreign = Path.Combine(directory.Path, $".midge-restore-{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(foreign);
        var marker = Path.Combine(foreign, "owned-by-another-attempt");
        await File.WriteAllTextAsync(marker, "retain");
        await PantsDatabase.RestoreBackupAsync(
            artifactPath,
            PantsOpenOptions.Local(Path.Combine(directory.Path, "restored")));

        Assert.Equal("retain", await File.ReadAllTextAsync(marker));
    }

    [Fact]
    public async Task ShouldPublishOnlyOneRestoreWhenAttemptsShareTarget()
    {
        using var directory = new TemporaryDirectory();
        var artifactPath = Path.Combine(directory.Path, "backup");
        var targetPath = Path.Combine(directory.Path, "restored");
        await using (var source = await PantsDatabase.OpenAsync(
                         PantsOpenOptions.Local(Path.Combine(directory.Path, "source"))))
        {
            await CommitAsync(
                source,
                source.ColumnFamilies.DefaultFamily,
                "key"u8.ToArray(),
                "value"u8.ToArray());
            await source.BackupAsync(artifactPath, TimeSpan.FromSeconds(10));
        }

        async Task<bool> RestoreAsync()
        {
            try
            {
                await PantsDatabase.RestoreBackupAsync(
                    artifactPath,
                    PantsOpenOptions.Local(targetPath));
                return true;
            }
            catch (PantsInvalidArgumentException)
            {
                return false;
            }
        }

        var attempts = await Task.WhenAll(Task.Run(RestoreAsync), Task.Run(RestoreAsync));

        Assert.Single(attempts, static result => result);
        Assert.Single(attempts, static result => !result);
        await using var restored = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(targetPath));
        Assert.Equal("value"u8.ToArray(), await ReadAsync(
            restored,
            restored.ColumnFamilies.DefaultFamily,
            "key"u8.ToArray()));
    }

    static (string Path, string Content)[] ReadTree(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .OrderBy(path => Path.GetRelativePath(root, path), StringComparer.Ordinal)
            .Select(path => (
                Path.GetRelativePath(root, path),
                Convert.ToHexString(File.ReadAllBytes(path))))
            .ToArray();

    static async ValueTask CommitAsync(
        IPantsDatabase database,
        IPantsColumnFamily family,
        byte[] key,
        byte[] value,
        PantsWriteOptions? options = null,
        bool retryBusy = false)
    {
        do
        {
            try
            {
                await using var transaction = await database.Transactions.BeginAsync(
                    family,
                    PantsTransactionMode.ReadWrite);
                transaction.Put(key, value);
                await transaction.CommitAsync(options ?? PantsWriteOptions.Sync);
                return;
            }
            catch (PantsBusyException) when (retryBusy)
            {
                await Task.Yield();
            }
        } while (retryBusy);
    }

    static async ValueTask<byte[]> ReadAsync(
        IPantsDatabase database,
        IPantsColumnFamily family,
        byte[] key)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            family,
            PantsTransactionMode.ReadOnly);
        var value = await transaction.GetAsync(key);
        return Assert.IsType<ReadOnlyMemory<byte>>(value).ToArray();
    }

    static byte[] U64(ulong value)
    {
        var bytes = new byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        return bytes;
    }

    sealed class BlockingBackupCopyFailpointHandler : IFailpointHandler
    {
        readonly TaskCompletionSource _blocked = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int _armed;

        public Task Blocked => _blocked.Task;

        public DateTimeOffset BlockedAt { get; private set; }

        public void Arm() => Volatile.Write(ref _armed, 1);

        public void Hit(Failpoint failpoint)
        {
            if (failpoint != Failpoint.BeforeBackupObjectCopy ||
                Interlocked.Exchange(ref _armed, 0) == 0)
            {
                return;
            }

            BlockedAt = DateTimeOffset.UtcNow;
            _blocked.TrySetResult();
            _release.Task.GetAwaiter().GetResult();
        }

        public void Release() => _release.TrySetResult();
    }

    sealed class CallbackFailpointHandler(Failpoint target, Action callback) : IFailpointHandler
    {
        int _fired;

        public void Hit(Failpoint failpoint)
        {
            if (failpoint == target && Interlocked.Exchange(ref _fired, 1) == 0)
            {
                callback();
            }
        }
    }
}

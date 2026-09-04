using System.Buffers.Binary;
using System.Text.Json;

namespace Cntryl.Pants.Tests.Storage;

public sealed class PantsDiskStorageTests
{
    [Fact]
    public async Task ShouldRecoverAtomicCommitFromMidgeWalAfterReopen()
    {
        using var directory = new TemporaryDirectory();
        await using (var database = await OpenAsync(directory.Path))
        {
            await using var transaction = await database.BeginTransactionAsync(
                database.DefaultColumnFamily,
                PantsTransactionMode.ReadWrite);
            transaction.Put(TestBytes.FromString("a"), TestBytes.FromString("one"));
            transaction.Put(TestBytes.FromString("b"), TestBytes.FromString("two"));
            await transaction.CommitAsync(PantsWriteOptions.Sync);
        }

        await using var reopened = await OpenAsync(directory.Path);
        await using var reader = await reopened.BeginTransactionAsync(
            reopened.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Equal("one", TestBytes.ToText((await reader.GetAsync(TestBytes.FromString("a")))!.Value));
        Assert.Equal("two", TestBytes.ToText((await reader.GetAsync(TestBytes.FromString("b")))!.Value));
    }

    [Fact]
    public async Task ShouldWriteFormatV3WalTlvAndSstV4()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenAsync(directory.Path);
        await using (var transaction = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            transaction.Put(TestBytes.FromString("key"), TestBytes.FromString("value"));
            await transaction.CommitAsync(PantsWriteOptions.Sync);
        }

        var wal = await ReadSharedFileAsync(Path.Combine(directory.Path, "wal", "wal.log"));
        Assert.Equal((byte)'M', wal[8]);
        Assert.Equal((byte)'W', wal[9]);
        Assert.Equal(1, wal[10]);
        Assert.Equal(
            "midge-format-version=3\n",
            await File.ReadAllTextAsync(Path.Combine(directory.Path, "FORMAT")));

        await database.FlushAsync(database.DefaultColumnFamily);
        var sstPath = Assert.Single(Directory.GetFiles(Path.Combine(directory.Path, "sst"), "*.sst"));
        var sst = await File.ReadAllBytesAsync(sstPath);
        Assert.Equal(4u, BitConverter.ToUInt32(sst, sst.Length - 20));
        Assert.Equal(0xdb4775248b80fb57ul, BitConverter.ToUInt64(sst, sst.Length - 12));
    }

    static async ValueTask<byte[]> ReadSharedFileAsync(string path)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var data = GC.AllocateUninitializedArray<byte>(checked((int)stream.Length));
        await stream.ReadExactlyAsync(data);
        return data;
    }

    [Theory]
    [InlineData(PantsPerformanceGoal.Latency, 1)]
    [InlineData(PantsPerformanceGoal.Throughput, 2)]
    [InlineData(PantsPerformanceGoal.Economy, 3)]
    public async Task ShouldApplyMidgeCompressionPolicyAndRoundTrip(
        PantsPerformanceGoal performanceGoal,
        byte expectedAlgorithm)
    {
        using var directory = new TemporaryDirectory();
        var value = Enumerable.Repeat((byte)'v', 16 * 1024).ToArray();
        await using (var database = await PantsDatabase.OpenAsync(
                         PantsOpenOptions.Local(directory.Path).WithPerformanceGoal(performanceGoal)))
        {
            await using var transaction = await database.BeginTransactionAsync(
                database.DefaultColumnFamily,
                PantsTransactionMode.ReadWrite);
            transaction.Put("compressed"u8.ToArray(), value);
            await transaction.CommitAsync(PantsWriteOptions.Sync);
            await database.FlushAsync(database.DefaultColumnFamily);
        }

        var sstPath = Assert.Single(Directory.GetFiles(Path.Combine(directory.Path, "sst"), "*.sst"));
        var sst = await File.ReadAllBytesAsync(sstPath);
        Assert.Equal(expectedAlgorithm, ReadFirstBlockAlgorithm(sst));

        await using var reopened = await OpenAsync(directory.Path);
        await using var reader = await reopened.BeginTransactionAsync(
            reopened.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Equal(value, (await reader.GetAsync("compressed"u8.ToArray()))!.Value.ToArray());
    }

    [Fact]
    public async Task ShouldLeaveSubthresholdSstBlocksRaw()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path).WithPerformanceGoal(PantsPerformanceGoal.Economy));
        await using (var transaction = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            transaction.Put("small"u8.ToArray(), "value"u8.ToArray());
            await transaction.CommitAsync(PantsWriteOptions.Sync);
        }

        await database.FlushAsync(database.DefaultColumnFamily);
        var sstPath = Assert.Single(Directory.GetFiles(Path.Combine(directory.Path, "sst"), "*.sst"));

        Assert.Equal(0, ReadFirstBlockAlgorithm(await File.ReadAllBytesAsync(sstPath)));
    }

    [Fact]
    public async Task ShouldRoundTripExtendedV4KeyDeltaAcrossFlushAndReopen()
    {
        using var directory = new TemporaryDirectory();
        var key = Enumerable.Repeat((byte)'k', 70_000).ToArray();
        await using (var database = await OpenAsync(directory.Path))
        {
            await using var transaction = await database.BeginTransactionAsync(
                database.DefaultColumnFamily,
                PantsTransactionMode.ReadWrite);
            transaction.Put(key, "value"u8.ToArray());
            await transaction.CommitAsync(PantsWriteOptions.Sync);
            await database.FlushAsync(database.DefaultColumnFamily);
        }

        await using var reopened = await OpenAsync(directory.Path);
        await using var reader = await reopened.BeginTransactionAsync(
            reopened.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);

        Assert.Equal("value", TestBytes.ToText((await reader.GetAsync(key))!.Value));
    }

    [Fact]
    public async Task ShouldClassifyStructurallyCorruptSstAsCorruptionOnVerification()
    {
        using var directory = new TemporaryDirectory();
        string sstPath;
        await using (var database = await OpenAsync(directory.Path))
        {
            await using var transaction = await database.BeginTransactionAsync(
                database.DefaultColumnFamily,
                PantsTransactionMode.ReadWrite);
            transaction.Put("key"u8.ToArray(), "value"u8.ToArray());
            await transaction.CommitAsync(PantsWriteOptions.Sync);
            await database.FlushAsync(database.DefaultColumnFamily);
            sstPath = Assert.Single(Directory.GetFiles(Path.Combine(directory.Path, "sst"), "*.sst"));
        }

        var bytes = await File.ReadAllBytesAsync(sstPath);
        bytes[0] ^= 0xff;
        await File.WriteAllBytesAsync(sstPath, bytes);

        // Byte 0 falls inside the file's first data block, not its footer/metadata/index. Normal
        // (bounded) recovery only positionally reads those trailing structural sections — see
        // LocalDiskStoreBoundedRecoveryTests — so this corruption class is now an explicitly
        // deferred one, per issue #219's acceptance criteria: Open succeeds, but an explicit
        // verification pass still detects it, and so would the first read of the affected block.
        var verification =
            await Assert.ThrowsAnyAsync<PantsException>(() => PantsDatabase.VerifyPathAsync(directory.Path).AsTask());
        Assert.Equal(PantsErrorCode.Corruption, verification.Code);
        Assert.IsType<PantsCorruptionException>(verification);

        await using var reopened = await OpenAsync(directory.Path);
        await using var reader = await reopened.BeginTransactionAsync(
            reopened.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        await Assert.ThrowsAnyAsync<PantsException>(
            () => reader.GetAsync("key"u8.ToArray()).AsTask());
    }

    [Fact]
    public async Task ShouldMakeSafeDropExplicitAboutUnflushedData()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenAsync(directory.Path);
        var family = await database.CreateColumnFamilyAsync("records");
        await using (var transaction = await database.BeginTransactionAsync(
                         family,
                         PantsTransactionMode.ReadWrite))
        {
            transaction.Put(TestBytes.FromString("key"), TestBytes.FromString("value"));
            await transaction.CommitAsync(PantsWriteOptions.Sync);
        }

        var busy = await Assert.ThrowsAnyAsync<PantsException>(() => database.DropColumnFamilyAsync(family).AsTask());
        Assert.Equal(PantsErrorCode.Busy, busy.Code);

        await database.FlushAsync(family);
        await database.DropColumnFamilyAsync(family);
        Assert.Null(await database.GetColumnFamilyAsync("records"));
    }

    [Fact]
    public async Task ShouldSkipWalForBestEffortUntilExplicitFlush()
    {
        using var directory = new TemporaryDirectory();
        await using (var database = await OpenAsync(directory.Path))
        {
            await using var transaction = await database.BeginTransactionAsync(
                database.DefaultColumnFamily,
                PantsTransactionMode.ReadWrite);
            transaction.Put(TestBytes.FromString("ephemeral"), TestBytes.FromString("value"));
            await transaction.CommitAsync(PantsWriteOptions.BestEffort);
            Assert.Equal(0, new FileInfo(Path.Combine(directory.Path, "wal", "wal.log")).Length);
        }

        await using var reopened = await OpenAsync(directory.Path);
        await using var reader = await reopened.BeginTransactionAsync(
            reopened.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Null(await reader.GetAsync(TestBytes.FromString("ephemeral")));
    }

    [Fact]
    public async Task ShouldVerifyLiveAndOfflineStorageWithoutMutation()
    {
        using var directory = new TemporaryDirectory();
        PantsStorageVerificationReport online;
        await using (var database = await OpenAsync(directory.Path))
        {
            await using (var transaction = await database.BeginTransactionAsync(
                             database.DefaultColumnFamily,
                             PantsTransactionMode.ReadWrite))
            {
                transaction.Put(TestBytes.FromString("key"), TestBytes.FromString("value"));
                await transaction.CommitAsync(PantsWriteOptions.Sync);
            }

            await database.FlushAsync(database.DefaultColumnFamily);
            online = await database.VerifyStorageAsync(TimeSpan.FromSeconds(5));
        }

        var offline = await PantsDatabase.VerifyPathAsync(directory.Path);

        Assert.Equal(PantsEngineHealth.Healthy, online.Health);
        Assert.Equal(1, online.SstFilesVerified);
        Assert.Equal(online.SstFilesVerified, offline.SstFilesVerified);
        Assert.True(offline.Authoritative);
    }

    [Fact]
    public async Task ShouldRejectSecondWriterForSameLocalPath()
    {
        using var directory = new TemporaryDirectory();
        await using var first = await OpenAsync(directory.Path);
        var stagingResidue = Path.Combine(
            directory.Path,
            "sst",
            ".flush-staging",
            "owned-by-first.tmp");
        await File.WriteAllTextAsync(stagingResidue, "do not remove");

        var error = await Assert.ThrowsAnyAsync<PantsException>(() => OpenAsync(directory.Path).AsTask());

        Assert.Equal(PantsErrorCode.LeaseHeld, error.Code);
        Assert.IsType<PantsLeaseHeldException>(error);
        Assert.True(File.Exists(stagingResidue));
    }

    [Fact]
    public async Task LeaseTakeoverHonorsConfiguredClockSkewTolerance()
    {
        using var directory = new TemporaryDirectory();
        var acquiredAt = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(40);
        await WriteLeaseRecordAsync(directory.Path, 41, "crashed-writer", acquiredAt);

        var held = await Assert.ThrowsAsync<PantsLeaseHeldException>(() =>
            PantsDatabase.OpenAsync(PantsOpenOptions
                .Local(directory.Path)
                .WithLeaseClockSkewTolerance(TimeSpan.FromSeconds(15))).AsTask());

        Assert.Equal(PantsErrorCode.LeaseHeld, held.Code);
        await using var takenOver = await PantsDatabase.OpenAsync(PantsOpenOptions
            .Local(directory.Path)
            .WithLeaseClockSkewTolerance(TimeSpan.Zero));
        Assert.True(takenOver.IsPrimaryLeaseHealthy);
    }

    [Fact]
    public async Task LeaseEpochExhaustionHasDedicatedPublicError()
    {
        using var directory = new TemporaryDirectory();
        await WriteLeaseRecordAsync(
            directory.Path,
            ulong.MaxValue,
            "exhausted-writer",
            DateTimeOffset.UnixEpoch);

        var error =
            await Assert.ThrowsAsync<PantsLeaseEpochExhaustedException>(() => OpenAsync(directory.Path).AsTask());

        Assert.Equal(PantsErrorCode.LeaseEpochExhausted, error.Code);
    }

    [Fact]
    public async Task ShouldCompactMultipleL0FilesAndPreserveLatestValue()
    {
        using var directory = new TemporaryDirectory();
        await using (var database = await OpenAsync(directory.Path))
        {
            for (var index = 0; index < 3; index++)
            {
                await using var transaction = await database.BeginTransactionAsync(
                    database.DefaultColumnFamily,
                    PantsTransactionMode.ReadWrite);
                transaction.Put(TestBytes.FromString("key"), TestBytes.FromString($"value-{index}"));
                await transaction.CommitAsync(PantsWriteOptions.Buffered);
                await database.FlushAsync(database.DefaultColumnFamily);
            }

            await database.CompactAllAsync();
        }

        await using var reopened = await OpenAsync(directory.Path);
        await using var reader = await reopened.BeginTransactionAsync(
            reopened.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Equal(
            "value-2",
            TestBytes.ToText((await reader.GetAsync(TestBytes.FromString("key")))!.Value));
        Assert.Single(Directory.GetFiles(Path.Combine(directory.Path, "sst"), "*.sst"));
    }

    [Fact]
    public async Task ShouldPreservePointAndRangeDeletionsAcrossCompactionAndReopen()
    {
        using var directory = new TemporaryDirectory();
        await using (var database = await OpenAsync(directory.Path))
        {
            await using (var writer = await database.BeginTransactionAsync(
                             database.DefaultColumnFamily,
                             PantsTransactionMode.ReadWrite))
            {
                writer.Put("a"u8.ToArray(), "point-delete"u8.ToArray());
                writer.Put("b"u8.ToArray(), "range-delete"u8.ToArray());
                writer.Put("c"u8.ToArray(), "survivor"u8.ToArray());
                await writer.CommitAsync(PantsWriteOptions.Buffered);
            }

            await database.FlushAsync(database.DefaultColumnFamily);

            await using (var deleting = await database.BeginTransactionAsync(
                             database.DefaultColumnFamily,
                             PantsTransactionMode.ReadWrite))
            {
                deleting.Delete("a"u8.ToArray());
                deleting.DeleteRange("b"u8.ToArray(), "c"u8.ToArray());
                await deleting.CommitAsync(PantsWriteOptions.Buffered);
            }

            await database.FlushAsync(database.DefaultColumnFamily);
            await database.CompactAllAsync();
        }

        await using var reopened = await OpenAsync(directory.Path);
        await using var reader = await reopened.BeginTransactionAsync(
            reopened.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);

        Assert.Null(await reader.GetAsync("a"u8.ToArray()));
        Assert.Null(await reader.GetAsync("b"u8.ToArray()));
        Assert.Equal("survivor", TestBytes.ToText((await reader.GetAsync("c"u8.ToArray()))!.Value));
        Assert.Single(Directory.GetFiles(Path.Combine(directory.Path, "sst"), "*.sst"));
    }

    [Fact]
    public async Task ShouldRemoveStreamedCompactionOutputsWhenALaterEntryExceedsTheBudget()
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.Local(directory.Path)
            .WithBackgroundCompaction(false)
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(8L * 1024 * 1024))
            .WithTransactionMemoryPool(2L * 1024 * 1024)
            .WithCompaction(new PantsCompactionConfiguration(
                L0FileCountTrigger: 100,
                TargetSstSizeBytes: 16 * 1024));
        await using var database = await PantsDatabase.OpenAsync(options);
        await using (var transaction = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            for (var index = 0; index < 100; index++)
            {
                transaction.Put(
                    TestBytes.FromString($"a-{index:D4}"),
                    Enumerable.Repeat((byte)index, 256).ToArray());
            }

            await transaction.CommitAsync(PantsWriteOptions.Buffered);
        }

        await database.FlushAsync(database.DefaultColumnFamily);
        await using (var transaction = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            transaction.Put("z-oversized"u8.ToArray(), new byte[32 * 1024]);
            await transaction.CommitAsync(PantsWriteOptions.Buffered);
        }

        await database.FlushAsync(database.DefaultColumnFamily);
        var originals = Directory.GetFiles(Path.Combine(directory.Path, "sst"), "*.sst");
        Assert.Equal(2, originals.Length);

        await Assert.ThrowsAsync<PantsResourceLimitException>(() =>
            database.CompactAllAsync().AsTask());

        Assert.Equal(
            originals.Order(StringComparer.Ordinal),
            Directory.GetFiles(Path.Combine(directory.Path, "sst"), "*.sst")
                .Order(StringComparer.Ordinal));
        Assert.Empty(Directory.GetFiles(
            Path.Combine(directory.Path, "sst", ".flush-staging"),
            "*.tmp"));
        Assert.Equal(PantsEngineHealth.Healthy, (await database.GetRuntimeMetricsAsync()).Health);
    }

    [Fact]
    public async Task ShouldRetainCompactionInputsUntilActiveSnapshotCloses()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenAsync(directory.Path);
        await using (var seed = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            seed.Put("key"u8.ToArray(), "old"u8.ToArray());
            await seed.CommitAsync(PantsWriteOptions.Buffered);
        }

        await database.FlushAsync(database.DefaultColumnFamily);
        var pinnedInput = Assert.Single(Directory.GetFiles(Path.Combine(directory.Path, "sst"), "*.sst"));
        await using var snapshot = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);

        for (var generation = 1; generation <= 2; generation++)
        {
            await using var overwrite = await database.BeginTransactionAsync(
                database.DefaultColumnFamily,
                PantsTransactionMode.ReadWrite);
            overwrite.Put("key"u8.ToArray(), TestBytes.FromString($"new-{generation}"));
            await overwrite.CommitAsync(PantsWriteOptions.Buffered);
            await database.FlushAsync(database.DefaultColumnFamily);
        }

        await database.CompactAllAsync();
        var pinnedMetrics = await database.GetRuntimeMetricsAsync();

        Assert.True(File.Exists(pinnedInput));
        Assert.Equal("old", TestBytes.ToText((await snapshot.GetAsync("key"u8.ToArray()))!.Value));
        Assert.True(pinnedMetrics.PinnedSsts > 0);
        Assert.Equal(PantsEngineHealth.Healthy, pinnedMetrics.Health);

        await snapshot.DisposeAsync();

        Assert.False(File.Exists(pinnedInput));
        Assert.Equal(0, (await database.GetRuntimeMetricsAsync()).PinnedSsts);
    }

    [Fact]
    public async Task ShouldKeepOldVersionFilesAliveWhenDirectReadBeginsBeforeCompactionPublication()
    {
        using var directory = new TemporaryDirectory();
        using var failpoint = new FlushPipelineFailpointHandler(
            Failpoint.AfterCompactionObsoleteFilesRetired);
        var options = PantsOpenOptions.Local(directory.Path)
            .WithBackgroundCompaction(false)
            .WithCompaction(new PantsCompactionConfiguration(L0FileCountTrigger: 2));
        await using var database = await PantsDatabase.OpenForTestingAsync(
            options,
            new RuntimeDependencies(failpoint));
        for (var generation = 0; generation < 2; generation++)
        {
            await using var write = await database.BeginTransactionAsync(
                database.DefaultColumnFamily,
                PantsTransactionMode.ReadWrite);
            write.Put("key"u8.ToArray(), TestBytes.FromString($"value-{generation}"));
            await write.CommitAsync(PantsWriteOptions.Buffered);
            await database.FlushAsync(database.DefaultColumnFamily);
        }

        var inputPaths = Directory.GetFiles(Path.Combine(directory.Path, "sst"), "*.sst");
        Assert.Equal(2, inputPaths.Length);

        try
        {
            var compaction = database.CompactAllAsync().AsTask();
            await failpoint.WaitUntilEnteredAsync(TimeSpan.FromSeconds(10));
            var pinned = await database.BeginTransactionAsync(
                database.DefaultColumnFamily,
                PantsTransactionMode.ReadOnly);

            failpoint.Release();
            await compaction.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal("value-1", TestBytes.ToText((await pinned.GetAsync("key"u8.ToArray()))!.Value));
            Assert.All(inputPaths, static path => Assert.True(File.Exists(path)));

            await pinned.DisposeAsync();

            Assert.All(inputPaths, static path => Assert.False(File.Exists(path)));
        }
        finally
        {
            failpoint.Release();
        }
    }

    [Fact]
    public async Task ShouldFailStrictRecoveryButSalvageValidWalPrefix()
    {
        using var directory = new TemporaryDirectory();
        await using (var database = await OpenAsync(directory.Path))
        {
            for (var index = 0; index < 2; index++)
            {
                await using var transaction = await database.BeginTransactionAsync(
                    database.DefaultColumnFamily,
                    PantsTransactionMode.ReadWrite);
                transaction.Put(TestBytes.FromString($"key-{index}"), TestBytes.FromString($"value-{index}"));
                await transaction.CommitAsync(PantsWriteOptions.Sync);
            }
        }

        var walPath = Path.Combine(directory.Path, "wal", "wal.log");
        var wal = await File.ReadAllBytesAsync(walPath);
        var firstPayloadLength = checked((int)BitConverter.ToUInt32(wal, 0));
        var secondFrameOffset = 8 + firstPayloadLength;
        wal[secondFrameOffset + 4] ^= 0xff;
        await File.WriteAllBytesAsync(walPath, wal);

        var strict = await Assert.ThrowsAnyAsync<PantsException>(() =>
            OpenWithRecoveryAsync(directory.Path, PantsRecoveryPolicy.Strict).AsTask());
        Assert.Equal(PantsErrorCode.RecoveryFailed, strict.Code);

        await using var salvaged = await OpenWithRecoveryAsync(
            directory.Path,
            PantsRecoveryPolicy.Salvage);
        var metrics = await salvaged.GetRuntimeMetricsAsync();
        await using var reader = await salvaged.BeginTransactionAsync(
            salvaged.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);

        Assert.Equal(PantsEngineHealth.SalvageMode, metrics.Health);
        Assert.Equal(1, metrics.SalvageModeOpens);
        Assert.Equal("value-0", TestBytes.ToText((await reader.GetAsync("key-0"u8.ToArray()))!.Value));
        Assert.Null(await reader.GetAsync("key-1"u8.ToArray()));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(directory.Path, "wal"), "*.salvage-retained*"));
    }

    [Fact]
    public async Task StrictRecoveryTruncatesZeroFilledActiveWalPreallocation()
    {
        using var directory = new TemporaryDirectory();
        await using (var database = await OpenAsync(directory.Path))
        {
            await using var transaction = await database.BeginTransactionAsync(
                database.DefaultColumnFamily,
                PantsTransactionMode.ReadWrite);
            transaction.Put("preallocated"u8.ToArray(), "recovered"u8.ToArray());
            await transaction.CommitAsync(PantsWriteOptions.Sync);
        }

        var walPath = Path.Combine(directory.Path, "wal", "wal.log");
        var validLength = new FileInfo(walPath).Length;
        await using (var wal = new FileStream(walPath, FileMode.Append, FileAccess.Write, FileShare.None))
        {
            await wal.WriteAsync(new byte[16 * 1024]);
        }

        await using var recovered = await OpenAsync(directory.Path);
        await using var reader = await recovered.BeginTransactionAsync(
            recovered.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);

        Assert.Equal(validLength, new FileInfo(walPath).Length);
        Assert.Equal(
            "recovered",
            TestBytes.ToText((await reader.GetAsync("preallocated"u8.ToArray()))!.Value));
    }

    [Theory]
    [InlineData("manifest.journal", "not-a-valid-manifest-journal")]
    [InlineData("intent_log.json", "not-json")]
    public async Task ShouldApplyRecoveryPolicyToCorruptControlMetadata(
        string relativePath,
        string corruptContents)
    {
        using var directory = new TemporaryDirectory();
        await using (var database = await OpenAsync(directory.Path))
        {
        }

        var path = Path.Combine(directory.Path, relativePath);
        await File.WriteAllTextAsync(path, corruptContents);
        var strict = await Assert.ThrowsAnyAsync<PantsException>(() =>
            OpenWithRecoveryAsync(directory.Path, PantsRecoveryPolicy.Strict).AsTask());
        Assert.Equal(PantsErrorCode.RecoveryFailed, strict.Code);

        await using var salvaged = await OpenWithRecoveryAsync(
            directory.Path,
            PantsRecoveryPolicy.Salvage);
        var metrics = await salvaged.GetRuntimeMetricsAsync();

        Assert.Equal(PantsEngineHealth.SalvageMode, metrics.Health);
        Assert.Equal(1, metrics.SalvageModeOpens);
        Assert.NotEmpty(Directory.GetFiles(directory.Path, $"{relativePath}.salvage-retained*"));
    }

    [Fact]
    public async Task ShouldReplayOnlyDurableManifestJournalEdits()
    {
        using var directory = new TemporaryDirectory();
        await using (var database = await OpenAsync(directory.Path))
        {
        }

        var durableEdit = JsonSerializer.SerializeToUtf8Bytes(new
        {
            edit_id = 1,
            edit = new Dictionary<string, object>
            {
                ["CreateColumnFamily"] = new
                {
                    id = 1,
                    name = "durable",
                    created_at = 123UL
                }
            }
        });
        var marker = JsonSerializer.SerializeToUtf8Bytes(new
        {
            last_persisted_sequence = 1UL,
            ts_millis = 123UL
        });
        var uncommittedEdit = JsonSerializer.SerializeToUtf8Bytes(new
        {
            edit_id = 2,
            edit = new Dictionary<string, object>
            {
                ["CreateColumnFamily"] = new
                {
                    id = 2,
                    name = "not-durable",
                    created_at = 124UL
                }
            }
        });
        await using (var journal = new FileStream(
                         Path.Combine(directory.Path, "manifest.journal"),
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None))
        {
            await journal.WriteAsync(EncodeJournalRecord(3, durableEdit));
            await journal.WriteAsync(EncodeJournalRecord(9, marker));
            await journal.WriteAsync(EncodeJournalRecord(3, uncommittedEdit));
            await journal.FlushAsync();
        }

        await using var reopened = await OpenAsync(directory.Path);

        Assert.NotNull(await reopened.GetColumnFamilyAsync("durable"));
        Assert.Null(await reopened.GetColumnFamilyAsync("not-durable"));
        Assert.Equal(0, new FileInfo(Path.Combine(directory.Path, "manifest.journal")).Length);
    }

    [Fact]
    public async Task ShouldClearRecoveredFlushIntentWhenManifestAlreadyOwnsOutput()
    {
        using var directory = new TemporaryDirectory();
        await using (var database = await OpenAsync(directory.Path))
        {
            await using var transaction = await database.BeginTransactionAsync(
                database.DefaultColumnFamily,
                PantsTransactionMode.ReadWrite);
            transaction.Put("key"u8.ToArray(), "value"u8.ToArray());
            await transaction.CommitAsync(PantsWriteOptions.Sync);
            await database.FlushAsync(database.DefaultColumnFamily);
        }

        var fileMetadata = ReadSingleManifestFile(directory.Path);
        await WriteFlushIntentAsync(
            directory.Path,
            CreateIntentFileMetadata(fileMetadata, fileMetadata.GetProperty("name").GetString()!));

        await using var reopened = await OpenAsync(directory.Path);
        await using var reader = await reopened.BeginTransactionAsync(
            reopened.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        var recovery = await reopened.GetRecoveryMetricsAsync();
        var runtime = await reopened.GetRuntimeMetricsAsync();

        Assert.Equal("value", TestBytes.ToText((await reader.GetAsync("key"u8.ToArray()))!.Value));
        Assert.Equal(1, recovery.IntentLogReplayRuns);
        Assert.Equal(1, recovery.IntentLogEntriesReplayed);
        Assert.Equal(recovery.IntentLogReplayRuns, runtime.IntentLogReplayRuns);
        Assert.Equal(recovery.IntentLogEntriesReplayed, runtime.IntentLogEntriesReplayed);
        using var intentLog = JsonDocument.Parse(
            await File.ReadAllBytesAsync(Path.Combine(directory.Path, "intent_log.json")));
        Assert.Empty(intentLog.RootElement.EnumerateArray());
    }

    [Fact]
    public async Task ShouldDeleteProvenFlushOutputLeftBeforeManifestPublication()
    {
        using var directory = new TemporaryDirectory();
        string originalPath;
        await using (var database = await OpenAsync(directory.Path))
        {
            await using var transaction = await database.BeginTransactionAsync(
                database.DefaultColumnFamily,
                PantsTransactionMode.ReadWrite);
            transaction.Put("key"u8.ToArray(), "value"u8.ToArray());
            await transaction.CommitAsync(PantsWriteOptions.Sync);
            await database.FlushAsync(database.DefaultColumnFamily);
            originalPath = Assert.Single(Directory.GetFiles(Path.Combine(directory.Path, "sst"), "*.sst"));
        }

        const string orphanName = "000000_00_00000000000000000999.sst";
        var orphanPath = Path.Combine(directory.Path, "sst", orphanName);
        File.Copy(originalPath, orphanPath);
        var fileMetadata = ReadSingleManifestFile(directory.Path);
        await WriteFlushIntentAsync(
            directory.Path,
            CreateIntentFileMetadata(fileMetadata, orphanName));

        await using var reopened = await OpenAsync(directory.Path);

        Assert.False(File.Exists(orphanPath));
        Assert.Equal(PantsEngineHealth.Healthy, (await reopened.GetRuntimeMetricsAsync()).Health);
    }

    [Fact]
    public async Task ShouldRetainUncertainIntentOutputDuringSalvageRecovery()
    {
        using var directory = new TemporaryDirectory();
        string originalPath;
        await using (var database = await OpenAsync(directory.Path))
        {
            await using var transaction = await database.BeginTransactionAsync(
                database.DefaultColumnFamily,
                PantsTransactionMode.ReadWrite);
            transaction.Put("key"u8.ToArray(), "value"u8.ToArray());
            await transaction.CommitAsync(PantsWriteOptions.Sync);
            await database.FlushAsync(database.DefaultColumnFamily);
            originalPath = Assert.Single(Directory.GetFiles(Path.Combine(directory.Path, "sst"), "*.sst"));
        }

        const string orphanName = "000000_00_00000000000000000999.sst";
        var orphanPath = Path.Combine(directory.Path, "sst", orphanName);
        File.Copy(originalPath, orphanPath);
        var fileMetadata = ReadSingleManifestFile(directory.Path);
        var invalidProof = CreateIntentFileMetadata(fileMetadata, orphanName);
        invalidProof["size_bytes"] = checked((ulong)new FileInfo(orphanPath).Length + 1);
        await WriteFlushIntentAsync(directory.Path, invalidProof);

        var strict = await Assert.ThrowsAnyAsync<PantsException>(() => OpenAsync(directory.Path).AsTask());
        Assert.Equal(PantsErrorCode.RecoveryFailed, strict.Code);

        await using var salvaged = await OpenWithRecoveryAsync(
            directory.Path,
            PantsRecoveryPolicy.Salvage);
        using var intentLog = JsonDocument.Parse(
            await File.ReadAllBytesAsync(Path.Combine(directory.Path, "intent_log.json")));

        Assert.True(File.Exists(orphanPath));
        Assert.NotEmpty(intentLog.RootElement.EnumerateArray());
        Assert.Equal(PantsEngineHealth.SalvageMode, (await salvaged.GetRuntimeMetricsAsync()).Health);
    }

    [Fact]
    public async Task ShouldPreserveUnownedSstGivenCorruptIntentLogDuringSalvage()
    {
        using var directory = new TemporaryDirectory();
        string orphanPath;
        await using (var database = await OpenAsync(directory.Path))
        {
            await using var transaction = await database.BeginTransactionAsync(
                database.DefaultColumnFamily,
                PantsTransactionMode.ReadWrite);
            transaction.Put("key"u8.ToArray(), "value"u8.ToArray());
            await transaction.CommitAsync(PantsWriteOptions.Sync);
            await database.FlushAsync(database.DefaultColumnFamily);
            var ownedPath = Assert.Single(Directory.GetFiles(Path.Combine(directory.Path, "sst"), "*.sst"));
            orphanPath = Path.Combine(directory.Path, "sst", "000000_00_00000000000000000999.sst");
            File.Copy(ownedPath, orphanPath);
        }

        await File.WriteAllTextAsync(Path.Combine(directory.Path, "intent_log.json"), "{corrupt");

        await using var salvaged = await OpenWithRecoveryAsync(directory.Path, PantsRecoveryPolicy.Salvage);

        Assert.True(File.Exists(orphanPath));
        Assert.Equal(PantsEngineHealth.SalvageMode, (await salvaged.GetRuntimeMetricsAsync()).Health);
    }

    [Fact]
    public async Task ShouldReconstructValidSstsGivenTotalManifestLossDuringSalvage()
    {
        using var directory = new TemporaryDirectory();
        string sstPath;
        await using (var database = await OpenAsync(directory.Path))
        {
            await using var transaction = await database.BeginTransactionAsync(
                database.DefaultColumnFamily,
                PantsTransactionMode.ReadWrite);
            transaction.Put("durable-key"u8.ToArray(), "durable-value"u8.ToArray());
            await transaction.CommitAsync(PantsWriteOptions.Sync);
            await database.FlushAsync(database.DefaultColumnFamily);
            sstPath = Assert.Single(Directory.GetFiles(Path.Combine(directory.Path, "sst"), "*.sst"));
        }

        await File.WriteAllTextAsync(Path.Combine(directory.Path, "manifest.snapshot.json"), "{corrupt");
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "manifest.json"), "{corrupt");

        await using var salvaged = await OpenWithRecoveryAsync(directory.Path, PantsRecoveryPolicy.Salvage);
        await using var reader = await salvaged.BeginTransactionAsync(
            salvaged.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);

        Assert.True(File.Exists(sstPath));
        Assert.Equal(
            "durable-value",
            TestBytes.ToText((await reader.GetAsync("durable-key"u8.ToArray()))!.Value));
        var metrics = await salvaged.GetRuntimeMetricsAsync();
        Assert.Equal(PantsEngineHealth.SalvageMode, metrics.Health);
        Assert.Equal(1, metrics.SstCount);
    }

    [Fact]
    public async Task ShouldReopenStrictAndHealthyAfterWalSalvageRepair()
    {
        using var directory = new TemporaryDirectory();
        await using (var database = await OpenAsync(directory.Path))
        {
            await using var transaction = await database.BeginTransactionAsync(
                database.DefaultColumnFamily,
                PantsTransactionMode.ReadWrite);
            transaction.Put("recoverable"u8.ToArray(), "value"u8.ToArray());
            await transaction.CommitAsync(PantsWriteOptions.Sync);
        }

        var walPath = Path.Combine(directory.Path, "wal", "wal.log");
        var wal = await File.ReadAllBytesAsync(walPath);
        wal[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(walPath, wal);

        await using (var salvaged = await OpenWithRecoveryAsync(directory.Path, PantsRecoveryPolicy.Salvage))
        {
            Assert.Equal(PantsEngineHealth.SalvageMode, (await salvaged.GetRuntimeMetricsAsync()).Health);
        }

        var retainedBefore = Directory.GetFiles(Path.Combine(directory.Path, "wal"), "*.salvage-retained*");
        await using var strict = await OpenWithRecoveryAsync(directory.Path, PantsRecoveryPolicy.Strict);

        Assert.Equal(PantsEngineHealth.Healthy, (await strict.GetRuntimeMetricsAsync()).Health);
        Assert.Equal(
            retainedBefore,
            Directory.GetFiles(Path.Combine(directory.Path, "wal"), "*.salvage-retained*"));
    }

    [Fact]
    public async Task ShouldReportConservativelyRetainedOrphanSstAsDegraded()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenAsync(directory.Path);
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "sst", "orphan.sst"), "orphan");

        var metrics = await database.GetRuntimeMetricsAsync();
        var layout = await database.GetStorageLayoutAsync();
        var report = await database.VerifyStorageAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(PantsEngineHealth.Degraded, metrics.Health);
        Assert.Equal(1, metrics.ObsoleteFileBacklog);
        Assert.Equal(PantsEngineHealth.Degraded, layout.Health);
        Assert.Contains("orphan.sst", layout.ObsoleteFiles);
        Assert.Equal(PantsEngineHealth.Degraded, report.Health);
        Assert.Contains(report.Warnings, warning => warning.Contains("orphan.sst", StringComparison.Ordinal));
    }

    static ValueTask<IPantsDatabase> OpenAsync(string path) =>
        PantsDatabase.OpenAsync(PantsOpenOptions.Local(path));

    static ValueTask<IPantsDatabase> OpenWithRecoveryAsync(
        string path,
        PantsRecoveryPolicy recoveryPolicy) =>
        PantsDatabase.OpenAsync(PantsOpenOptions.Local(path).WithRecoveryPolicy(recoveryPolicy));

    static async Task WriteLeaseRecordAsync(
        string path,
        ulong epoch,
        string holderId,
        DateTimeOffset acquiredAt)
    {
        Directory.CreateDirectory(path);
        await File.WriteAllTextAsync(
            Path.Combine(path, ".midge_leader"),
            $"epoch: {epoch}\nholder_id: {holderId}\nacquired_at: {acquiredAt:O}\n");
    }

    static JsonElement ReadSingleManifestFile(string path)
    {
        using var manifest = JsonDocument.Parse(
            File.ReadAllBytes(Path.Combine(path, "manifest.snapshot.json")));
        return Assert.Single(manifest.RootElement.GetProperty("files").EnumerateArray()).Clone();
    }

    static Dictionary<string, object?> CreateIntentFileMetadata(
        JsonElement manifestFile,
        string name) => new(StringComparer.Ordinal)
        {
            ["name"] = name,
            ["level"] = manifestFile.GetProperty("level").Clone(),
            ["size_bytes"] = manifestFile.GetProperty("size_bytes").Clone(),
            ["content_crc32c"] = manifestFile.GetProperty("content_crc32c").Clone(),
            ["cf_id"] = manifestFile.GetProperty("cf_id").Clone(),
            ["smallest_key"] = manifestFile.GetProperty("smallest_key").Clone(),
            ["largest_key"] = manifestFile.GetProperty("largest_key").Clone(),
            ["smallest_seq"] = manifestFile.GetProperty("smallest_seq").Clone(),
            ["largest_seq"] = manifestFile.GetProperty("largest_seq").Clone()
        };

    static Task WriteFlushIntentAsync(
        string path,
        Dictionary<string, object?> fileMetadata)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new object[]
        {
            new Dictionary<string, object?>
            {
                ["FlushPublish"] = new
                {
                    phase = "OutputDurable",
                    cf_id = 0u,
                    sequence = 1ul,
                    file_meta = fileMetadata
                }
            }
        });
        return File.WriteAllBytesAsync(Path.Combine(path, "intent_log.json"), bytes);
    }

    static byte[] EncodeJournalRecord(byte recordType, byte[] payload)
    {
        var record = new byte[checked(1 + sizeof(uint) + payload.Length + sizeof(uint))];
        record[0] = recordType;
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(1), checked((uint)payload.Length));
        payload.CopyTo(record.AsSpan(5));
        BinaryPrimitives.WriteUInt32LittleEndian(
            record.AsSpan(5 + payload.Length),
            Crc32(payload));
        return record;
    }

    static byte ReadFirstBlockAlgorithm(byte[] sst)
    {
        var encodedLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(sst));
        Assert.InRange(encodedLength, 5, sst.Length - sizeof(uint));
        return sst[sizeof(uint) + encodedLength - 5];
    }

    static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) == 0 ? crc >> 1 : (crc >> 1) ^ 0xedb8_8320;
            }
        }

        return ~crc;
    }
}

using System.Globalization;
using System.Text.Json;
using Cntryl.Pants.Observability;
using Cntryl.Pants.Runtime.Internal;
using Cntryl.Pants.Runtime.Internal.Transactions;
using Cntryl.Pants.Storage.Internal;
using Cntryl.Pants.Support.Failpoints;
using Cntryl.Pants.Support.TestDoubles;
using Cntryl.Pants.Transactions.Internal;

namespace Cntryl.Pants.Compatibility;

public sealed class MidgeLocalWalRetentionParityTests
{
    static readonly ColumnFamilyIdentity DefaultFamily = new(
        0,
        "default",
        RuntimeState.DefaultFamilyVersion);

    [Fact]
    public Task ShouldRetireCoveredWalSegmentsWhenAnIdleColumnFamilyPinsTheFloor() =>
        AssertIdleFamilyRetentionBoundAsync(withDeletes: false);

    [Fact]
    public Task ShouldRetireWalSegmentsWithDeletesWhenAnIdleColumnFamilyPinsTheFloor() =>
        AssertIdleFamilyRetentionBoundAsync(withDeletes: true);

    [Fact]
    public Task ShouldNotResurrectDeletedKeyWhenRecoveringACrashImage() =>
        AssertDoesNotResurrectAsync(DeleteKind.Point, crashImage: true);

    [Fact]
    public Task ShouldNotResurrectDeletedKeyWhenReopeningAfterCleanShutdown() =>
        AssertDoesNotResurrectAsync(DeleteKind.Point, crashImage: false);

    [Fact]
    public Task ShouldNotResurrectRangeDeletedKeyWhenRecoveringACrashImage() =>
        AssertDoesNotResurrectAsync(DeleteKind.Range, crashImage: true);

    [Fact]
    public Task ShouldNotResurrectBestEffortDeletedKeyWhenReopeningAfterCleanShutdown() =>
        AssertDoesNotResurrectAsync(DeleteKind.BestEffortPoint, crashImage: false);

    [Fact]
    public Task ShouldNotResurrectBestEffortDeletedKeyWhenRecoveringACrashImage() =>
        AssertDoesNotResurrectAsync(DeleteKind.BestEffortPoint, crashImage: true);

    [Fact]
    public async Task ShouldNotResurrectFlushedDeleteWhenPowerLossDropsItsWalRecord()
    {
        using var directory = new TemporaryDirectory();
        var livePath = Path.Combine(directory.Path, "live");
        var powerLossImagePath = Path.Combine(directory.Path, "power-loss-image");
        var compactedCrashImagePath = Path.Combine(directory.Path, "compacted-crash-image");

        await using (var database = await PantsDatabase.OpenAsync(OpenOptions(livePath)))
        {
            var defaultFamily = database.ColumnFamilies.DefaultFamily;
            var idleFamily = await database.ColumnFamilies.CreateAsync("idle");
            await PutAsync(database, defaultFamily, "k");
            var syncedWalPrefix = File.ReadAllBytes(Path.Combine(livePath, "wal", "wal.log"));

            await using (var transaction = await database.Transactions.BeginAsync(
                             defaultFamily,
                             PantsTransactionMode.ReadWrite))
            {
                transaction.Delete(TestBytes.FromString("k"));
                await transaction.CommitAsync(PantsWriteOptions.Buffered);
            }

            await PutValueAsync(
                database,
                idleFamily,
                "idle-tail",
                TestBytes.FromString("discarded"),
                PantsWriteOptions.Buffered);
            await database.Maintenance.FlushAsync(defaultFamily);
            await AssertKeyAbsentAsync(database, defaultFamily);

            CopyCrashImage(livePath, powerLossImagePath);
            TruncateWalSegmentToSyncedPrefix(powerLossImagePath, syncedWalPrefix);
            ExpireWriterLease(powerLossImagePath);
            await database.ShutdownAsync(TestTimeouts.Expected);
        }

        await using (var afterPowerLoss = await PantsDatabase.OpenAsync(OpenOptions(powerLossImagePath)))
        {
            var family = afterPowerLoss.ColumnFamilies.DefaultFamily;
            await AssertKeyAbsentAsync(afterPowerLoss, family);
            await afterPowerLoss.Maintenance.CompactAllAsync();
            await AssertKeyAbsentAsync(afterPowerLoss, family);

            CopyCrashImage(powerLossImagePath, compactedCrashImagePath);
            ExpireWriterLease(compactedCrashImagePath);
            await afterPowerLoss.ShutdownAsync(TestTimeouts.Expected);
        }

        await using var afterProcessCrash = await PantsDatabase.OpenAsync(
            OpenOptions(compactedCrashImagePath));
        await AssertKeyAbsentAsync(
            afterProcessCrash,
            afterProcessCrash.ColumnFamilies.DefaultFamily);
    }

    [Fact]
    public Task ShouldNotResurrectDeletedKeyWhenOlderFlushedSegmentRemovalFails() =>
        AssertDeletedKeySurvivesFloorRetirementAsync(failOlderRemoval: true);

    [Fact]
    public Task ShouldNotResurrectDeletedKeyWhenFlushedSegmentsRetireWithoutFailure() =>
        AssertDeletedKeySurvivesFloorRetirementAsync(failOlderRemoval: false);

    [Fact]
    public async Task ShouldAllocateWalSegmentIdsAbovePreviousRunsWhenAllSealedSegmentsWerePruned()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "db");
        var firstRunState = NewState();
        ulong nextSegmentIdAfterFirstFlush;
        ulong nextSegmentIdAfterSecondFlush;
        using (var firstRun = LocalDiskStore.Open(path, firstRunState))
        {
            AppendMutation(firstRun, firstRunState, 1, DefaultFamily, CommitOperationKind.Put, "run1-a");
            firstRun.Flush(firstRunState, DefaultFamily);
            nextSegmentIdAfterFirstFlush = firstRun.CurrentWalSegmentId;
            AppendMutation(firstRun, firstRunState, 2, DefaultFamily, CommitOperationKind.Put, "run1-b");
            firstRun.Flush(firstRunState, DefaultFamily);
            nextSegmentIdAfterSecondFlush = firstRun.CurrentWalSegmentId;
        }

        Assert.Empty(SealedWalSegmentIds(path));
        Assert.True(nextSegmentIdAfterSecondFlush > nextSegmentIdAfterFirstFlush);

        var secondRunState = NewStateWithIdleFamily(out var idleFamily);
        ulong[] secondRunSegmentIds;
        using (var secondRun = LocalDiskStore.Open(path, secondRunState))
        {
            secondRun.CreateColumnFamily(idleFamily);
            AppendMutation(secondRun, secondRunState, 3, idleFamily, CommitOperationKind.Put, "idle-key");
            AppendMutation(secondRun, secondRunState, 4, DefaultFamily, CommitOperationKind.Put, "run2-a");
            secondRun.Flush(secondRunState, DefaultFamily);
            secondRunSegmentIds = SealedWalSegmentIds(path);
        }

        Assert.NotEmpty(secondRunSegmentIds);
        Assert.All(secondRunSegmentIds, segmentId => Assert.True(
            segmentId >= nextSegmentIdAfterSecondFlush,
            $"Run 2 reused WAL segment identity {segmentId}; the durable next ID after run 1 was {nextSegmentIdAfterSecondFlush}."));
    }

    [Fact]
    public async Task ShouldKeepRequiredWritesAfterWalRetirementAndPowerLoss()
    {
        using var directory = new TemporaryDirectory();
        var livePath = Path.Combine(directory.Path, "live");
        var firstImagePath = Path.Combine(directory.Path, "first-crash-image");
        var secondImagePath = Path.Combine(directory.Path, "second-crash-image");
        var expected = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        await using (var firstRun = await PantsDatabase.OpenAsync(OpenOptions(livePath)))
        {
            var family = firstRun.ColumnFamilies.DefaultFamily;
            await PutAsync(firstRun, family, "deleted", incompressibleValue: true);
            await firstRun.Maintenance.FlushAsync(family);
            await DeletePointAsync(firstRun, family, "deleted");
            await firstRun.Maintenance.FlushAsync(family);
            await ApplyRetentionRoundsAsync(firstRun, family, "first", expected);
            await firstRun.Maintenance.CompactAllAsync();
            Assert.Empty(SealedWalSegmentIds(livePath));

            CopyCrashImage(livePath, firstImagePath);
            ExpireWriterLease(firstImagePath);
        }

        await using (var firstRecovery = await PantsDatabase.OpenAsync(OpenOptions(firstImagePath)))
        {
            Assert.Equal(0, (await firstRecovery.Diagnostics.GetRecoveryMetricsAsync()).WalRecordsReplayed);
            await AssertRetainedValuesAsync(firstRecovery, expected);
            var family = firstRecovery.ColumnFamilies.DefaultFamily;
            await ApplyRetentionRoundsAsync(firstRecovery, family, "second", expected);
            await firstRecovery.Maintenance.CompactAllAsync();
            Assert.Empty(SealedWalSegmentIds(firstImagePath));

            CopyCrashImage(firstImagePath, secondImagePath);
            ExpireWriterLease(secondImagePath);
        }

        await using var secondRecovery = await PantsDatabase.OpenAsync(OpenOptions(secondImagePath));
        Assert.Equal(0, (await secondRecovery.Diagnostics.GetRecoveryMetricsAsync()).WalRecordsReplayed);
        await AssertRetainedValuesAsync(secondRecovery, expected);
    }

    static async Task AssertIdleFamilyRetentionBoundAsync(bool withDeletes)
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "db");
        var options = RetentionOpenOptions(path);
        await using var database = await PantsDatabase.OpenAsync(options);
        var defaultFamily = database.ColumnFamilies.DefaultFamily;
        var idleFamily = await database.ColumnFamilies.CreateAsync("idle");
        await PutAsync(database, idleFamily, "idle-key", incompressibleValue: true);

        for (var round = 0; round < 20; round++)
        {
            var key = $"busy-{round:000}";
            for (var index = 0; index < 5; index++)
            {
                await PutAsync(
                    database,
                    defaultFamily,
                    withDeletes ? key : $"{key}-{index}",
                    incompressibleValue: true);
            }

            if (withDeletes)
            {
                await DeletePointAsync(database, defaultFamily, key);
            }

            await database.Maintenance.FlushAsync(defaultFamily);
        }

        var retained = CountSealedWalSegments(path);
        Assert.True(
            retained <= 10,
            $"{retained} sealed WAL segments remained after 20 flushed rounds.");

        // Pants' explicit CompactAllAsync flushes every active family, including the idle one.
        // Measure the retention bound first, then compact to verify the final deleted value.
        await database.Maintenance.CompactAllAsync();
        await database.ShutdownAsync(TestTimeouts.Expected);
        await database.DisposeAsync();
        await using var reopened = await PantsDatabase.OpenAsync(RetentionOpenOptions(path));
        await AssertKeyPresentAsync(reopened, "idle", "idle-key");
        if (withDeletes)
        {
            await AssertKeyAbsentAsync(reopened, reopened.ColumnFamilies.DefaultFamily, "busy-019");
        }
    }

    static async Task AssertDeletedKeySurvivesFloorRetirementAsync(bool failOlderRemoval)
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "db");
        var failpoints = new ArmableFailpointHandler();
        var state = NewStateWithIdleFamily(out var idleFamily);
        using (var store = LocalDiskStore.Open(path, state, failpoints: failpoints))
        {
            store.CreateColumnFamily(idleFamily);
            AppendMutation(store, state, 1, idleFamily, CommitOperationKind.Put, "idle-key");
            AppendMutation(store, state, 2, DefaultFamily, CommitOperationKind.Put, "k");
            store.Flush(state, DefaultFamily);
            AppendMutation(store, state, 3, DefaultFamily, CommitOperationKind.Delete, "k");
            store.Flush(state, DefaultFamily);
            _ = store.Compact(state, force: true);

            var retainedBeforeIdleFlush = SealedWalSegmentIds(path);
            Assert.Equal(2, retainedBeforeIdleFlush.Length);
            Assert.True(ReadWalHistoryStart(path) <= retainedBeforeIdleFlush[0]);

            if (failOlderRemoval)
            {
                failpoints.Arm(Failpoint.BeforeLocalWalRetirementDelete);
                _ = Assert.Throws<IOException>(() => store.Flush(state, idleFamily));
                Assert.Equal(retainedBeforeIdleFlush, SealedWalSegmentIds(path));
                Assert.True(ReadWalHistoryStart(path) > retainedBeforeIdleFlush[^1]);
            }
            else
            {
                store.Flush(state, idleFamily);
                Assert.Empty(SealedWalSegmentIds(path));
            }
        }

        await using var reopened = await PantsDatabase.OpenAsync(OpenOptions(path));
        await AssertKeyAbsentAsync(reopened, reopened.ColumnFamilies.DefaultFamily);
    }

    static RuntimeState NewState() => new(
        new ManualClock(DateTimeOffset.UnixEpoch),
        new RuntimeTelemetry());

    static RuntimeState NewStateWithIdleFamily(out ColumnFamilyIdentity idleFamily)
    {
        var state = NewState();
        idleFamily = new ColumnFamilyIdentity(1, "idle", 0);
        state.FamilyGeneration[idleFamily.Name] = idleFamily.Generation;
        state.ActiveFamilyVersions[idleFamily.Name] = idleFamily.Generation;
        state.FamilyData[idleFamily] = RuntimeState.EmptyFamily;
        state.RangeTombstones[idleFamily] = [];
        state.ActiveMemtableBytes[idleFamily] = 0;
        state.NextColumnFamilyId = 2;
        return state;
    }

    static void AppendMutation(
        LocalDiskStore store,
        RuntimeState state,
        long transactionId,
        ColumnFamilyIdentity family,
        CommitOperationKind operationKind,
        string key)
    {
        var operation = new TransactionIntentOperation(
            0,
            operationKind,
            family,
            TestBytes.FromString(key),
            null,
            operationKind == CommitOperationKind.Put ? TestBytes.FromString($"{key}-value") : null,
            null,
            null,
            false);
        var source = new TransactionOperationSource(
            null,
            [operation],
            1,
            DateTimeOffset.UnixEpoch);
        using var payload = new CommitPayload(
            transactionId,
            PantsTransactionMode.ReadWrite,
            PantsConflictPolicy.LastWriteWins,
            DateTimeOffset.UnixEpoch,
            state.CreateVersion(),
            source,
            []);
        _ = store.AppendCommit(payload, state, PantsDurability.Sync);
    }

    static ulong[] SealedWalSegmentIds(string path) =>
        Directory.EnumerateFiles(Path.Combine(path, "wal"), "*.wal")
            .Select(file => ulong.Parse(
                Path.GetFileNameWithoutExtension(file),
                CultureInfo.InvariantCulture))
            .Order()
            .ToArray();

    static ulong ReadWalHistoryStart(string path)
    {
        using var manifest = JsonDocument.Parse(
            File.ReadAllBytes(Path.Combine(path, "manifest.snapshot.json")));
        return manifest.RootElement.GetProperty("wal_history_start").GetUInt64();
    }

    static async Task AssertDoesNotResurrectAsync(DeleteKind deleteKind, bool crashImage)
    {
        using var directory = new TemporaryDirectory();
        var livePath = Path.Combine(directory.Path, "live");
        var options = OpenOptions(livePath);
        await using var database = await PantsDatabase.OpenAsync(options);
        var defaultFamily = database.ColumnFamilies.DefaultFamily;
        var idleFamily = await database.ColumnFamilies.CreateAsync("idle");
        await PutAsync(database, idleFamily, "idle-key");
        await PutAsync(database, defaultFamily, "k");
        await database.Maintenance.FlushAsync(defaultFamily);
        await DeleteAsync(database, defaultFamily, deleteKind);
        await database.Maintenance.FlushAsync(defaultFamily);
        await database.Maintenance.CompactAllAsync();
        await AssertKeyAbsentAsync(database, defaultFamily);

        if (crashImage)
        {
            var imagePath = Path.Combine(directory.Path, "image");
            CopyCrashImage(livePath, imagePath);
            ExpireWriterLease(imagePath);
            await using var recovered = await PantsDatabase.OpenAsync(OpenOptions(imagePath));
            await AssertKeyAbsentAsync(recovered, recovered.ColumnFamilies.DefaultFamily);
            return;
        }

        await database.ShutdownAsync(TestTimeouts.Expected);
        await database.DisposeAsync();
        await using var reopened = await PantsDatabase.OpenAsync(OpenOptions(livePath));
        await AssertKeyAbsentAsync(reopened, reopened.ColumnFamilies.DefaultFamily);
    }

    static PantsOpenOptions OpenOptions(string path) =>
        PantsOpenOptions.Local(path)
            .WithBackgroundCompaction(false)
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(256L * 1024 * 1024))
            .WithMemtableLimits(64L * 1024 * 1024, 8 * 1024);

    static PantsOpenOptions RetentionOpenOptions(string path) =>
        OpenOptions(path).WithBackgroundCompaction(true);

    static async Task PutAsync(
        IPantsDatabase database,
        IPantsColumnFamily family,
        string key,
        bool incompressibleValue = false)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            family,
            PantsTransactionMode.ReadWrite);
        transaction.Put(
            TestBytes.FromString(key),
            incompressibleValue
                ? CreateIncompressibleValue(key)
                : Enumerable.Repeat((byte)'v', 1024).ToArray());
        await transaction.CommitAsync(PantsWriteOptions.Sync);
    }

    static async Task ApplyRetentionRoundsAsync(
        IPantsDatabase database,
        IPantsColumnFamily family,
        string prefix,
        Dictionary<string, byte[]> expected)
    {
        for (var round = 0; round < 6; round++)
        {
            for (var index = 0; index < 5; index++)
            {
                var key = $"{prefix}-{round:00}-{index}";
                var value = CreateIncompressibleValue(key);
                await PutValueAsync(
                    database,
                    family,
                    key,
                    value,
                    (round + index) % 2 == 0 ? PantsWriteOptions.Sync : PantsWriteOptions.Buffered);
                expected.Add(key, value);
            }

            await database.Maintenance.FlushAsync(family);
        }
    }

    static async Task PutValueAsync(
        IPantsDatabase database,
        IPantsColumnFamily family,
        string key,
        byte[] value,
        PantsWriteOptions writeOptions)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            family,
            PantsTransactionMode.ReadWrite);
        transaction.Put(TestBytes.FromString(key), value);
        await transaction.CommitAsync(writeOptions);
    }

    static async Task AssertRetainedValuesAsync(
        IPantsDatabase database,
        IReadOnlyDictionary<string, byte[]> expected)
    {
        var family = database.ColumnFamilies.DefaultFamily;
        await using var reader = await database.Transactions.BeginAsync(
            family,
            PantsTransactionMode.ReadOnly);
        foreach (var (key, expectedValue) in expected)
        {
            var actual = await reader.GetAsync(TestBytes.FromString(key));
            Assert.NotNull(actual);
            Assert.Equal(expectedValue, actual.Value.ToArray());
        }

        Assert.Null(await reader.GetAsync(TestBytes.FromString("deleted")));
    }

    static byte[] CreateIncompressibleValue(string key)
    {
        // Repeated Midge values compress in Pants' LZ4 WAL frames and do not create equivalent
        // retention pressure, so keep the 1 KiB fixture size with deterministic incompressible data.
        var value = new byte[1024];
        var state = 2166136261u;
        foreach (var character in key)
        {
            state = unchecked((state ^ character) * 16777619u);
        }

        if (state == 0)
        {
            state = 1;
        }

        for (var index = 0; index < value.Length; index++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            value[index] = (byte)(state >> 24);
        }

        return value;
    }

    static async Task DeletePointAsync(
        IPantsDatabase database,
        IPantsColumnFamily family,
        string key)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            family,
            PantsTransactionMode.ReadWrite);
        transaction.Delete(TestBytes.FromString(key));
        await transaction.CommitAsync(PantsWriteOptions.Sync);
    }

    static async Task DeleteAsync(
        IPantsDatabase database,
        IPantsColumnFamily family,
        DeleteKind deleteKind)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            family,
            PantsTransactionMode.ReadWrite);
        switch (deleteKind)
        {
            case DeleteKind.Point:
                transaction.Delete(TestBytes.FromString("k"));
                break;
            case DeleteKind.Range:
                transaction.DeleteRange(TestBytes.FromString("a"), TestBytes.FromString("z"));
                break;
            case DeleteKind.BestEffortPoint:
                transaction.Delete(TestBytes.FromString("k"));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(deleteKind), deleteKind, null);
        }

        await transaction.CommitAsync(
            deleteKind == DeleteKind.BestEffortPoint
                ? PantsWriteOptions.BestEffort
                : PantsWriteOptions.Sync);
    }

    static async Task AssertKeyAbsentAsync(
        IPantsDatabase database,
        IPantsColumnFamily family)
    {
        await using var reader = await database.Transactions.BeginAsync(
            family,
            PantsTransactionMode.ReadOnly);
        Assert.Null(await reader.GetAsync(TestBytes.FromString("k")));
    }

    static async Task AssertKeyAbsentAsync(
        IPantsDatabase database,
        IPantsColumnFamily family,
        string key)
    {
        await using var reader = await database.Transactions.BeginAsync(
            family,
            PantsTransactionMode.ReadOnly);
        Assert.Null(await reader.GetAsync(TestBytes.FromString(key)));
    }

    static async Task AssertKeyPresentAsync(
        IPantsDatabase database,
        string familyName,
        string key)
    {
        var family = (await database.ColumnFamilies.GetAsync(familyName))!;
        await using var reader = await database.Transactions.BeginAsync(
            family,
            PantsTransactionMode.ReadOnly);
        Assert.NotNull(await reader.GetAsync(TestBytes.FromString(key)));
    }

    static int CountSealedWalSegments(string path) =>
        Directory.EnumerateFiles(Path.Combine(path, "wal"), "*.wal")
            .Count();

    static void CopyCrashImage(string source, string destination)
    {
        Directory.CreateDirectory(destination);
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

    static void TruncateWalSegmentToSyncedPrefix(string databasePath, byte[] syncedPrefix)
    {
        var walPath = Assert.Single(Directory.EnumerateFiles(
            Path.Combine(databasePath, "wal"),
            "*.wal"));
        var currentBytes = File.ReadAllBytes(walPath);
        Assert.True(
            currentBytes.Length > syncedPrefix.Length,
            $"The sealed WAL must contain an unsynced tail beyond {syncedPrefix.Length} bytes.");
        Assert.Equal(syncedPrefix, currentBytes.AsSpan(0, syncedPrefix.Length).ToArray());
        using var wal = new FileStream(
            walPath,
            FileMode.Open,
            FileAccess.Write,
            FileShare.Read);
        wal.SetLength(syncedPrefix.Length);
        wal.Flush(true);
    }

    static void ExpireWriterLease(string path)
    {
        var leasePath = Path.Combine(path, ".midge_leader");
        var lines = File.ReadAllLines(leasePath)
            .Where(static line => !line.StartsWith("checksum: ", StringComparison.Ordinal))
            .Select(static line => line.StartsWith("acquired_at: ", StringComparison.Ordinal)
                ? "acquired_at: 1970-01-01T00:00:00Z"
                : line)
            .ToArray();
        File.WriteAllLines(leasePath, lines);
    }

    enum DeleteKind
    {
        Point,
        Range,
        BestEffortPoint
    }
}

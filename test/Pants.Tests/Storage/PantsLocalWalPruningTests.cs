using System.Text.Json;
using Cntryl.Pants.Exceptions;
using Cntryl.Pants.Observability;
using Cntryl.Pants.Runtime.Internal;
using Cntryl.Pants.Runtime.Internal.Transactions;
using Cntryl.Pants.Storage.Internal;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage;

public sealed class PantsLocalWalPruningTests
{
    const int ValueBytes = 64 * 1024;
    static readonly ColumnFamilyIdentity DefaultFamily = new(
        0,
        "default",
        RuntimeState.DefaultFamilyVersion);

    [Fact]
    public void ShouldBlockBeforeWalHistoryExceedsItsByteLimitAndExposeTheCause()
    {
        using var directory = new TemporaryDirectory();
        var state = new RuntimeState(new ManualClock(DateTimeOffset.UnixEpoch), new RuntimeTelemetry());
        using var store = LocalDiskStore.Open(
            directory.Path,
            state,
            maxWalRetentionBytes: 256,
            maxWalRetentionSegments: 8);
        Assert.Equal(256, store.WalRetentionByteLimitBytes);

        using (var first = CreateCommitPayload(state, 1, DefaultFamily, "first"))
        {
            _ = store.AppendCommit(first, state, PantsDurability.Sync);
        }

        var firstSequence = state.Sequence;
        var retainedBytes = store.WalRetainedBytes;
        Assert.InRange(retainedBytes, 1, 256);

        using var next = CreateCommitPayload(state, 2, DefaultFamily, "second");
        _ = Assert.Throws<PantsNoSpaceException>(() =>
            store.AppendCommit(next, state, PantsDurability.Sync));

        Assert.Equal(firstSequence, state.Sequence);
        Assert.Equal(retainedBytes, store.WalRetainedBytes);
        Assert.Equal(PantsWalRetirementBlockedCause.RetainedWalByteLimit,
            store.WalRetirementBlockedCause);
    }

    [Fact]
    public void ShouldBlockBeforeRetainingTooManySegmentsAndExposeTheCause()
    {
        using var directory = new TemporaryDirectory();
        var state = new RuntimeState(new ManualClock(DateTimeOffset.UnixEpoch), new RuntimeTelemetry());
        using var store = LocalDiskStore.Open(
            directory.Path,
            state,
            maxWalRetentionBytes: 4096,
            maxWalRetentionSegments: 1);
        Assert.Equal(1, store.WalRetentionSegmentLimit);

        using (var first = CreateCommitPayload(state, 1, DefaultFamily, "first"))
        {
            _ = store.AppendCommit(first, state, PantsDurability.Sync);
        }

        var firstSequence = state.Sequence;
        _ = store.RotateActiveLocalWal();
        Assert.Equal(0, store.ActiveWalBytes);
        Assert.Equal(1, store.WalRetainedSegments);

        using var next = CreateCommitPayload(state, 2, DefaultFamily, "second");
        _ = Assert.Throws<PantsNoSpaceException>(() =>
            store.AppendCommit(next, state, PantsDurability.Sync));

        Assert.Equal(firstSequence, state.Sequence);
        Assert.Equal(1, store.WalRetainedSegments);
        Assert.Equal(PantsWalRetirementBlockedCause.RetainedWalSegmentLimit,
            store.WalRetirementBlockedCause);
    }

    [Fact]
    public async Task ShouldFlushFamilyThroughItsOwnCommitBoundaryWhenAnotherFamilyIsPending()
    {
        using var directory = new TemporaryDirectory();
        var state = new RuntimeState(new ManualClock(DateTimeOffset.UnixEpoch), new RuntimeTelemetry());
        var defaultFamily = new ColumnFamilyIdentity(0, "default", RuntimeState.DefaultFamilyVersion);
        var cold = new ColumnFamilyIdentity(1, "frontier-cold", 0);
        state.FamilyGeneration[cold.Name] = cold.Generation;
        state.ActiveFamilyVersions[cold.Name] = cold.Generation;
        state.FamilyData[cold] = RuntimeState.EmptyFamily;
        state.RangeTombstones[cold] = [];
        state.ActiveMemtableBytes[cold] = 0;
        state.NextColumnFamilyId = 2;

        using (var store = LocalDiskStore.Open(directory.Path, state))
        {
            store.CreateColumnFamily(cold);
            using (var hot = CreateCommitPayload(state, 1, defaultFamily, "hot-key"))
            {
                _ = store.AppendCommit(hot, state, PantsDurability.Sync);
            }

            var hotCommit = checked((ulong)state.Sequence);
            using (var pending = CreateCommitPayload(state, 2, cold, "cold-key"))
            {
                _ = store.AppendCommit(pending, state, PantsDurability.Sync);
            }

            var coldCommit = checked((ulong)state.Sequence);

            Assert.True(coldCommit > hotCommit);
            store.Flush(state, defaultFamily);

            Assert.Equal(hotCommit, ReadFlushFrontier(directory.Path, defaultFamily.Id));
        }

        var recoveredState = new RuntimeState(
            new ManualClock(DateTimeOffset.UnixEpoch),
            new RuntimeTelemetry());
        using (LocalDiskStore.Open(directory.Path, recoveredState))
        {
            var recoveredCold = recoveredState.FamilyData.Keys.Single(
                family => family.Name == "frontier-cold");
            Assert.True(recoveredState.FamilyData[recoveredCold]
                .ContainsKey(TestBytes.FromString("cold-key")));
        }
    }

    [Fact]
    public async Task ShouldRetainWalHistoryUntilTheOldestFamilyFrontierAdvances()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenAsync(directory.Path);
        var cold = await database.ColumnFamilies.CreateAsync("cold");
        await PutAsync(database, cold, "cold-key", 16);

        for (var cycle = 0; cycle < 6; cycle++)
        {
            await PutAsync(database, database.ColumnFamilies.DefaultFamily, $"hot-{cycle}", ValueBytes);
            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        }

        Assert.True(
            WalBytes(directory.Path) > 2L * ValueBytes,
            "Later covered segments must remain while an older family blocks prefix retirement.");

        await database.Maintenance.FlushAsync(cold);

        Assert.True(
            WalBytes(directory.Path) < 2L * ValueBytes,
            "The covered prefix should retire after the cold family frontier advances.");
    }

    [Fact]
    public async Task ShouldIgnoreRestoredWalBelowDurableHistoryStartAfterRetirement()
    {
        using var directory = new TemporaryDirectory();
        await using (var database = await OpenAsync(directory.Path))
        {
            await PutAsync(database, database.ColumnFamilies.DefaultFamily, "persisted", 16);
            var retiredBytes = await File.ReadAllBytesAsync(
                Path.Combine(directory.Path, "wal", "wal.log"));
            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);

            using var manifest = System.Text.Json.JsonDocument.Parse(
                await File.ReadAllBytesAsync(Path.Combine(directory.Path, "manifest.snapshot.json")));
            Assert.Equal(
                2UL,
                manifest.RootElement.GetProperty("wal_history_start").GetUInt64());

            await File.WriteAllBytesAsync(
                Path.Combine(directory.Path, "wal", "00000000000000000001.wal"),
                retiredBytes);
        }

        await using var reopened = await OpenAsync(directory.Path);
        Assert.Equal(0, (await reopened.Diagnostics.GetRecoveryMetricsAsync()).WalRecordsReplayed);
        await using var reader = await reopened.Transactions.BeginAsync(
            reopened.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        Assert.NotNull(await reader.GetAsync(TestBytes.FromString("persisted")));
    }

    [Fact]
    public async Task ShouldKeepDroppedFamilyFrontierTerminalAcrossReopen()
    {
        using var directory = new TemporaryDirectory();
        uint familyId;
        await using (var database = await OpenAsync(directory.Path))
        {
            var family = await database.ColumnFamilies.CreateAsync("terminal-frontier");
            familyId = family.Id;
            await PutAsync(database, family, "retained", 16);
            await database.Maintenance.FlushAsync(family);
            Assert.True(ReadFlushFrontier(directory.Path, familyId) > 0);

            await database.ColumnFamilies.DropAsync(family);
            Assert.Equal(ulong.MaxValue, ReadFlushFrontier(directory.Path, familyId));
        }

        await using var reopened = await OpenAsync(directory.Path);
        Assert.Null(await reopened.ColumnFamilies.GetAsync("terminal-frontier"));
        Assert.Equal(ulong.MaxValue, ReadFlushFrontier(directory.Path, familyId));
    }

    [Fact]
    public async Task ShouldRecoverUncoveredWritesAfterReopenWhenCoveredSegmentsWerePruned()
    {
        using var directory = new TemporaryDirectory();
        await using (var database = await OpenAsync(directory.Path))
        {
            var cold = await database.ColumnFamilies.CreateAsync("cold");
            await PutAsync(database, cold, "gone", 16);
            await using (var transaction = await database.Transactions.BeginAsync(
                             cold,
                             PantsTransactionMode.ReadWrite))
            {
                transaction.Delete(TestBytes.FromString("gone"));
                transaction.Put(TestBytes.FromString("kept"), TestBytes.FromString("cold-value"));
                await transaction.CommitAsync(PantsWriteOptions.Sync);
            }

            for (var cycle = 0; cycle < 3; cycle++)
            {
                await PutAsync(database, database.ColumnFamilies.DefaultFamily, $"hot-{cycle}", 1024);
                await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
            }
        }

        await using var reopened = await OpenAsync(directory.Path);
        var coldReopened = (await reopened.ColumnFamilies.GetAsync("cold"))!;
        await using var reader = await reopened.Transactions.BeginAsync(
            coldReopened,
            PantsTransactionMode.ReadOnly);
        Assert.Null(await reader.GetAsync(TestBytes.FromString("gone")));
        Assert.Equal(
            "cold-value",
            TestBytes.ToText((await reader.GetAsync(TestBytes.FromString("kept")))!.Value));
        await using var hot = await reopened.Transactions.BeginAsync(
            reopened.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        Assert.NotNull(await hot.GetAsync(TestBytes.FromString("hot-0")));
        Assert.NotNull(await hot.GetAsync(TestBytes.FromString("hot-2")));
    }

    static long WalBytes(string root) =>
        Directory.EnumerateFiles(Path.Combine(root, "wal"))
            .Sum(static path => new FileInfo(path).Length);

    static ulong ReadFlushFrontier(string root, uint familyId)
    {
        using var manifest = JsonDocument.Parse(
            File.ReadAllBytes(Path.Combine(root, "manifest.snapshot.json")));
        return manifest.RootElement.GetProperty("flushed_through")
            .EnumerateArray()
            .Single(row => row.GetProperty("cf_id").GetUInt32() == familyId)
            .GetProperty("through_sequence")
            .GetUInt64();
    }

    static CommitPayload CreateCommitPayload(
        RuntimeState state,
        long transactionId,
        ColumnFamilyIdentity family,
        string key)
    {
        var operation = new TransactionIntentOperation(
            0,
            CommitOperationKind.Put,
            family,
            TestBytes.FromString(key),
            null,
            TestBytes.FromString($"{key}-value"),
            null,
            null,
            false);
        var source = new TransactionOperationSource(
            null,
            [operation],
            1,
            DateTimeOffset.UnixEpoch);
        return new CommitPayload(
            transactionId,
            PantsTransactionMode.ReadWrite,
            PantsConflictPolicy.LastWriteWins,
            DateTimeOffset.UnixEpoch,
            state.CreateVersion(),
            source,
            []);
    }

    static async Task PutAsync(
        IPantsDatabase database,
        IPantsColumnFamily family,
        string key,
        int valueBytes)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            family,
            PantsTransactionMode.ReadWrite);
        transaction.Put(TestBytes.FromString(key), System.Security.Cryptography.RandomNumberGenerator.GetBytes(valueBytes));
        await transaction.CommitAsync(PantsWriteOptions.Sync);
    }

    static ValueTask<IPantsDatabase> OpenAsync(string path) =>
        PantsDatabase.OpenAsync(PantsOpenOptions.Local(path).WithBackgroundCompaction(false));
}

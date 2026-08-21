namespace Pants.Tests;

public sealed class PantsTelemetryContractTests
{
    [Fact]
    public async Task ShouldReportPerDatabaseReadPathActivity()
    {
        await using IPantsDatabase first = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await using IPantsDatabase second = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());

        await using (IPantsTransaction transaction = await first.BeginTransactionAsync(
                         first.DefaultColumnFamily,
                         PantsTransactionMode.ReadOnly))
        {
            Assert.Null(await transaction.GetAsync("missing"u8.ToArray()));
            await transaction.CommitAsync(PantsWriteOptions.Sync);
        }

        PantsReadPathDiagnostics firstDiagnostics = await first.GetReadPathDiagnosticsAsync();
        PantsReadPathDiagnostics secondDiagnostics = await second.GetReadPathDiagnosticsAsync();

        Assert.Equal(1, firstDiagnostics.ReadOnlyTransactionsBegun);
        Assert.Equal(1, firstDiagnostics.ReadOnlySnapshotCacheHits);
        Assert.Equal(1, firstDiagnostics.SnapshotsRegistered);
        Assert.Equal(1, firstDiagnostics.SnapshotsUnregistered);
        Assert.Equal(1, (await first.GetReadAmplificationMetricsAsync()).ReadsTotal);
        Assert.Equal(new PantsReadPathDiagnostics(), secondDiagnostics);
    }

    [Fact]
    public async Task ShouldDistinguishPointAndRangeWriteConflicts()
    {
        await using IPantsDatabase database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await using IPantsTransaction pointWriter = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        pointWriter.SetConflictPolicy(PantsConflictPolicy.AbortOnWriteConflict);
        pointWriter.Put("key"u8.ToArray(), "first"u8.ToArray());

        await using (IPantsTransaction rangeWriter = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            rangeWriter.DeleteRange("a"u8.ToArray(), "z"u8.ToArray());
            await rangeWriter.CommitAsync(PantsWriteOptions.Buffered);
        }

        await Assert.ThrowsAsync<PantsWriteConflictException>(() =>
            pointWriter.CommitAsync(PantsWriteOptions.Buffered).AsTask());

        PantsRuntimeMetrics metrics = await database.GetRuntimeMetricsAsync();
        Assert.Equal(1, metrics.WriteConflictsTotal);
        Assert.Equal(0, metrics.WriteConflictsPointTotal);
        Assert.Equal(1, metrics.WriteConflictsRangeTotal);
    }

    [Fact]
    public async Task ShouldReportBloomRejectionsAndDataBlockReads()
    {
        using var directory = new TemporaryDirectory();
        await using IPantsDatabase database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path));
        await using (IPantsTransaction writer = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            for (int index = 0; index < 64; index++)
            {
                writer.Put(TestBytes.FromString($"key-{index:D4}"), new byte[1024]);
            }

            await writer.CommitAsync(PantsWriteOptions.Buffered);
        }

        await database.FlushAsync(database.DefaultColumnFamily);
        await using IPantsTransaction reader = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        Assert.NotNull(await reader.GetAsync("key-0001"u8.ToArray()));
        Assert.NotNull(await reader.GetAsync("key-0001"u8.ToArray()));
        for (int index = 0; index < 32; index++)
        {
            Assert.Null(await reader.GetAsync(TestBytes.FromString($"key-{index:D4}-absent")));
        }

        PantsReadPathDiagnostics diagnostics = await database.GetReadPathDiagnosticsAsync();
        PantsReadAmplificationMetrics amplification = await database.GetReadAmplificationMetricsAsync();
        PantsRuntimeMetrics runtime = await database.GetRuntimeMetricsAsync();
        Assert.True(diagnostics.BloomChecks >= 33);
        Assert.True(diagnostics.BloomRejects > 0);
        Assert.True(diagnostics.DataBlocksRead < diagnostics.BloomChecks);
        Assert.True(diagnostics.SstBlockCacheHits > 0);
        Assert.True(diagnostics.SstBlockCacheMisses > 0);
        Assert.Equal(diagnostics.SstBlockCacheHits, runtime.CacheHits);
        Assert.Equal(diagnostics.SstBlockCacheMisses, runtime.CacheMisses);
        Assert.Equal(diagnostics.BloomChecks, runtime.SstBloomChecksTotal);
        Assert.Equal(diagnostics.BloomRejects, runtime.SstBloomRejectsTotal);
        Assert.Equal(diagnostics.DataBlocksRead, runtime.SstDataBlocksReadTotal);
        Assert.Equal(34, amplification.ReadsTotal);
        Assert.True(amplification.BlocksReadTotal > diagnostics.DataBlocksRead);
    }

    [Fact]
    public async Task ShouldReportPhysicalSstWorkGivenFlushedRangeScan()
    {
        using var directory = new TemporaryDirectory();
        await using IPantsDatabase database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path));
        await using (IPantsTransaction writer = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            writer.Put("a"u8.ToArray(), "one"u8.ToArray());
            writer.Put("b"u8.ToArray(), "two"u8.ToArray());
            writer.Put("c"u8.ToArray(), "three"u8.ToArray());
            writer.DeleteRange("b"u8.ToArray(), "c"u8.ToArray());
            await writer.CommitAsync(PantsWriteOptions.Buffered);
        }

        await database.FlushAsync(database.DefaultColumnFamily);
        PantsReadPathDiagnostics before = await database.GetReadPathDiagnosticsAsync();
        await using IPantsTransaction reader = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        await using IPantsScan scan = await reader.ScanAsync(new PantsScanQuery());
        var entries = new List<PantsEntry>();
        await foreach (PantsEntry entry in scan)
        {
            entries.Add(entry);
        }

        PantsReadPathDiagnostics after = await database.GetReadPathDiagnosticsAsync();
        Assert.Equal(2, entries.Count);
        Assert.True(after.CandidateSstFilesChecked > before.CandidateSstFilesChecked);
        Assert.True(after.CandidateBlocksChecked > before.CandidateBlocksChecked);
        Assert.True(after.DataBlocksRead > before.DataBlocksRead);
        Assert.True(after.RangeTombstoneScans > before.RangeTombstoneScans);
    }
}

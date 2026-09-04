using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Cntryl.Pants.Observability;
using Cntryl.Pants.Scan;
using Cntryl.Pants.Storage;
using Cntryl.Pants.Transactions;

namespace Cntryl.Pants.Tier4;

/// <summary>
///     Slice 6e (issue #219): a scale-ladder qualification run — ingests a base record count into a
///     local, non-hybrid database, then reports ingest throughput, database size, startup/steady-state
///     RSS, block-cache-cold/warm point and prefix-scan latency percentiles, physical write/read
///     amplification, compaction debt and peak RSS, a clean reopen, and a
///     crash/WAL-replay-recovery check. Deliberately outside
///     BenchmarkDotNet's iteration/warmup machinery (which would multiply run time many-fold for a
///     multi-million-record ingest) — this runs each tier exactly once, like a real qualification
///     pass, not a micro-benchmark.
/// </summary>
static class ScaleLadderRunner
{
    const int ValueSizeBytes = 150; // representative of a small address record.
    const int SecondaryIndexCount = 3;
    const int BatchSize = 500;
    const int FlushEveryRecords = 100_000;
    const int LatencySampleCount = 500;
    const int PrefixSampleCount = 100;
    const int GroupSize = 1_000; // records sharing a common key prefix, for prefix-scan sampling.

    internal const int AddressIndexEntryMultiplier = 1 + SecondaryIndexCount;
    internal const string ColdCacheQualifier = "Block-cache-cold (OS page cache not reset)";

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length is 0 or > 2 ||
            !long.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var recordCount) ||
            recordCount <= 0)
        {
            Console.Error.WriteLine("Usage: scaleladder <record-count> [output-markdown-path]");
            return 2;
        }

        var outputPath = args.Length > 1
            ? args[1]
            : Path.Combine("docs", "performance", $"scale-ladder-{recordCount}.md");
        using var directory = new TemporaryDirectoryHandle();
        var report = await RunTierAsync(recordCount, directory.Path);

        Console.WriteLine("Running crash/WAL-replay-recovery check...");
        var (crashCheckPassed, crashCheckDetail) = await ScaleLadderCrashCheck.RunAsync();
        Console.WriteLine($"  {(crashCheckPassed ? "PASS" : "FAIL")}: {crashCheckDetail}");
        report = report with { CrashRecoveryPassed = crashCheckPassed, CrashRecoveryDetail = crashCheckDetail };

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) is { Length: > 0 } dir ? dir : ".");
        await File.WriteAllTextAsync(outputPath, report.ToMarkdown());
        Console.WriteLine($"Wrote {outputPath}");
        Console.WriteLine(report.ToMarkdown());

        // A qualification run that silently "succeeds" despite a failed correctness check
        // (reopen spot-check or crash recovery) is worse than no report at all — automation
        // consuming this exit code must see the failure.
        return ExitCodeFor(report.ReopenSpotCheckCorrect, crashCheckPassed);
    }

    internal static int ExitCodeFor(bool reopenSpotCheckCorrect, bool crashRecoveryPassed) =>
        reopenSpotCheckCorrect && crashRecoveryPassed ? 0 : 1;

    static async Task<TierReport> RunTierAsync(long recordCount, string databasePath)
    {
        var budgetBytes = 256L * 1024 * 1024;
        var options = PantsOpenOptions.Local(databasePath)
            .WithBackgroundCompaction(true)
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(budgetBytes));

        var emptyOpenStopwatch = Stopwatch.StartNew();
        var database = await PantsDatabase.OpenAsync(options);
        emptyOpenStopwatch.Stop();
        var emptyOpenRss = MeasureRssBytes();

        using var currentProcess = Process.GetCurrentProcess();
        await using var ingestAndCompactionPeakMonitor = new PeakWorkingSetMonitor(currentProcess);
        long logicalBytesIngested = 0;
        var ingestStopwatch = Stopwatch.StartNew();
        for (long batchStart = 0; batchStart < recordCount; batchStart += BatchSize)
        {
            var batchCount = (int)Math.Min(BatchSize, recordCount - batchStart);
            await using (var transaction = await database.Transactions.BeginAsync(
                             database.ColumnFamilies.DefaultFamily,
                             PantsTransactionMode.ReadWrite))
            {
                for (var offset = 0; offset < batchCount; offset++)
                {
                    var index = batchStart + offset;
                    foreach (var mutation in CreateMutationsForRecord(index))
                    {
                        transaction.Put(mutation.Key, mutation.Value);
                        logicalBytesIngested = checked(
                            logicalBytesIngested + mutation.Key.Length + mutation.Value.Length);
                    }
                }

                await transaction.CommitAsync(PantsWriteOptions.Buffered);
            }

            var ingested = batchStart + batchCount;
            if (ingested % FlushEveryRecords < BatchSize)
            {
                await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
            }

            if (ingested % 1_000_000 < BatchSize)
            {
                Console.WriteLine(
                    $"  ingested {ingested:N0}/{recordCount:N0} " +
                    $"({ingestStopwatch.Elapsed.TotalSeconds:F1}s elapsed, " +
                    $"{ingested / Math.Max(0.001, ingestStopwatch.Elapsed.TotalSeconds):N0} rec/s)");
            }
        }

        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        await database.Maintenance.CompactAllAsync();
        ingestStopwatch.Stop();
        await ingestAndCompactionPeakMonitor.StopAsync();
        var ingestAndCompactionPeakRss = ingestAndCompactionPeakMonitor.PeakBytes;

        var steadyStateRss = MeasureRssBytes();
        var databaseSizeBytes = DirectorySize(databasePath);
        var runtimeAfterIngest = await database.Diagnostics.GetRuntimeMetricsAsync();

        // Fixed sample sets, replayed identically for both the cold and warm passes — see
        // MeasurePointLatenciesAsync's doc comment for why a fresh random draw per pass would
        // make "warm" meaningless.
        var random = new Random(12345);
        var pointSampleIndices = BuildPointSampleIndices(recordCount, random);
        var prefixSampleGroups = BuildPrefixSampleGroups(recordCount, random);
        var coldPoint = await MeasurePointLatenciesAsync(database, pointSampleIndices);
        var coldPrefix = await MeasurePrefixLatenciesAsync(database, recordCount, prefixSampleGroups);
        var warmPoint = await MeasurePointLatenciesAsync(database, pointSampleIndices);
        var warmPrefix = await MeasurePrefixLatenciesAsync(database, recordCount, prefixSampleGroups);

        var amplification = await database.Diagnostics.GetReadAmplificationMetricsAsync();
        var runtimeFinal = await database.Diagnostics.GetRuntimeMetricsAsync();

        await database.DisposeAsync();

        var reopenStopwatch = Stopwatch.StartNew();
        var reopened = await PantsDatabase.OpenAsync(options);
        reopenStopwatch.Stop();
        var reopenRss = MeasureRssBytes();
        var reopenSpotCheck = await reopened.Transactions.BeginAsync(
            reopened.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        var reopenValue = await reopenSpotCheck.GetAsync(KeyFor(recordCount / 2));
        await reopenSpotCheck.DisposeAsync();
        var reopenCorrect = reopenValue is { } value &&
                            value.Span.SequenceEqual(ValueFor(recordCount / 2));

        // Must fully release this process's own handle (including its write lease) before a
        // separate process can open the same database path below — Pants is single-writer.
        await reopened.DisposeAsync();

        // A genuinely separate process's peak RSS opening (and verifying) the now-populated
        // corpus — uncontaminated by this process's own ingest/compaction memory history. This
        // is the "startup RSS for the populated corpus" figure; emptyOpenRss above is only the
        // baseline before any data existed, and is reported separately for context.
        Console.WriteLine("  measuring populated-corpus reopen peak RSS in a separate process...");
        var (
            reopenProbeSucceeded,
            populatedReopenPeakRssBytes,
            reopenProbeBudgetBytes,
            reopenProbeDetail) = await ScaleLadderReopenProbe.RunAsync(
            databasePath,
            recordCount,
            budgetBytes);
        if (!reopenProbeSucceeded)
        {
            Console.Error.WriteLine($"  reopen probe FAILED: {reopenProbeDetail}");
        }

        return new TierReport(
            recordCount,
            ValueSizeBytes,
            AddressIndexEntryMultiplier,
            budgetBytes,
            emptyOpenStopwatch.Elapsed,
            emptyOpenRss,
            ingestStopwatch.Elapsed,
            recordCount / Math.Max(0.001, ingestStopwatch.Elapsed.TotalSeconds),
            steadyStateRss,
            ingestAndCompactionPeakRss,
            databaseSizeBytes,
            logicalBytesIngested,
            runtimeAfterIngest.SstBytesWrittenTotal,
            runtimeAfterIngest.WalBytesWrittenTotal,
            coldPoint,
            coldPrefix,
            warmPoint,
            warmPrefix,
            amplification,
            runtimeAfterIngest,
            runtimeFinal,
            reopenStopwatch.Elapsed,
            reopenRss,
            reopenCorrect && reopenProbeSucceeded,
            populatedReopenPeakRssBytes,
            reopenProbeBudgetBytes,
            false,
            "(not yet run)");
    }

    static long[] BuildPointSampleIndices(long recordCount, Random random) =>
        Enumerable.Range(0, LatencySampleCount)
            .Select(_ => (long)(random.NextDouble() * recordCount))
            .ToArray();

    static long[] BuildPrefixSampleGroups(long recordCount, Random random)
    {
        var groupCount = Math.Max(1, recordCount / GroupSize);
        return Enumerable.Range(0, PrefixSampleCount)
            .Select(_ => (long)(random.NextDouble() * groupCount))
            .ToArray();
    }

    /// <summary>
    ///     Replays the same fixed <paramref name="sampleIndices" /> for both the cold and warm
    ///     passes — reusing a fresh random draw for "warm" would query different keys/blocks than
    ///     "cold" touched, at which point neither the SST reader/block caches nor the OS page cache
    ///     have anything warm to serve from, making the two passes indistinguishable.
    /// </summary>
    static async Task<LatencySummary> MeasurePointLatenciesAsync(
        IPantsDatabase database,
        long[] sampleIndices)
    {
        var samples = new double[sampleIndices.Length];
        for (var i = 0; i < sampleIndices.Length; i++)
        {
            var index = sampleIndices[i];
            await using var transaction = await database.Transactions.BeginAsync(
                database.ColumnFamilies.DefaultFamily,
                PantsTransactionMode.ReadOnly);
            var stopwatch = Stopwatch.StartNew();
            var value = await transaction.GetAsync(KeyFor(index));
            samples[i] = stopwatch.Elapsed.TotalMicroseconds;

            if (value is null || !value.Value.Span.SequenceEqual(ValueFor(index)))
            {
                throw new InvalidOperationException(
                    $"Point-read latency sample for index {index} returned a missing or " +
                    "incorrect value — a latency number over a wrong answer is worthless.");
            }
        }

        return LatencySummary.From(samples);
    }

    static async Task<LatencySummary> MeasurePrefixLatenciesAsync(
        IPantsDatabase database,
        long recordCount,
        long[] sampleGroups)
    {
        var samples = new double[sampleGroups.Length];
        for (var i = 0; i < sampleGroups.Length; i++)
        {
            var group = sampleGroups[i];
            var expectedCount = (int)Math.Min(GroupSize, Math.Max(0, recordCount - group * GroupSize));
            await using var transaction = await database.Transactions.BeginAsync(
                database.ColumnFamilies.DefaultFamily,
                PantsTransactionMode.ReadOnly);
            var stopwatch = Stopwatch.StartNew();
            await using var scan = await transaction.ScanAsync(CreatePrefixQueryForGroup(group));
            var count = 0;
            await foreach (var entry in scan)
            {
                var expectedIndex = group * GroupSize + count;
                if (!entry.Key.Span.SequenceEqual(PostalIndexKeyFor(expectedIndex)) ||
                    !entry.Value.Span.SequenceEqual(IndexValueFor(expectedIndex)))
                {
                    throw new InvalidOperationException(
                        $"Prefix-scan latency sample for group {group} returned an unexpected " +
                        $"key/value at position {count} — a latency number over wrong results " +
                        "is worthless.");
                }

                count++;
            }

            samples[i] = stopwatch.Elapsed.TotalMicroseconds;

            if (count != expectedCount)
            {
                throw new InvalidOperationException(
                    $"Prefix-scan latency sample for group {group} returned {count} entries, " +
                    $"expected {expectedCount}.");
            }
        }

        return LatencySummary.From(samples);
    }

    internal static byte[] KeyFor(long index) =>
        Encoding.UTF8.GetBytes($"addr-id-{index:D12}");

    internal static IReadOnlyList<KeyValuePair<byte[], byte[]>> CreateMutationsForRecord(long index) =>
    [
        new(KeyFor(index), ValueFor(index)),
        new(PostalIndexKeyFor(index), IndexValueFor(index)),
        new(StreetIndexKeyFor(index), IndexValueFor(index)),
        new(LocalityIndexKeyFor(index), IndexValueFor(index))
    ];

    static byte[] GroupPrefixFor(long group) =>
        Encoding.UTF8.GetBytes($"idx-postal-{group:D9}-");

    internal static PantsScanQuery CreatePrefixQueryForGroup(long group) => new()
    {
        Prefix = GroupPrefixFor(group)
    };

    static byte[] PostalIndexKeyFor(long index) =>
        Encoding.UTF8.GetBytes(
            $"idx-postal-{index / GroupSize:D9}-{index % GroupSize:D4}");

    static byte[] StreetIndexKeyFor(long index) =>
        Encoding.UTF8.GetBytes(
            $"idx-street-{index % 10_000:D5}-{index:D12}");

    static byte[] LocalityIndexKeyFor(long index) =>
        Encoding.UTF8.GetBytes(
            $"idx-locality-{index % 1_000:D4}-{index:D12}");

    static byte[] IndexValueFor(long index)
    {
        var value = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(value, index);
        return value;
    }

    internal static byte[] ValueFor(long index)
    {
        // Deterministic but non-trivially-compressible content (an address-like record has
        // real entropy, unlike a single repeated byte) — otherwise SST block compression makes
        // the reported database size and write amplification meaningless.
        var value = GC.AllocateUninitializedArray<byte>(ValueSizeBytes);
        var state = unchecked((ulong)index * 6364136223846793005UL + 1442695040888963407UL);
        for (var i = 0; i < value.Length; i++)
        {
            state = unchecked(state * 6364136223846793005UL + 1442695040888963407UL);
            value[i] = (byte)(state >> 33);
        }

        return value;
    }

    internal static string PrefixScanMetricName(bool warm) =>
        $"{(warm ? "Warm" : ColdCacheQualifier)} prefix scan " +
        $"({GroupSize.ToString("N0", CultureInfo.InvariantCulture)}-key group) p50/p95/p99";

    internal static double CalculateWriteAmplification(
        long logicalBytes,
        long sstBytesWritten,
        long walBytesWritten) =>
        logicalBytes == 0
            ? 0
            : (double)checked(sstBytesWritten + walBytesWritten) / logicalBytes;

    static long MeasureRssBytes()
    {
        GC.Collect(2, GCCollectionMode.Forced, true);
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return process.WorkingSet64;
    }

    static long DirectorySize(string path) =>
        Directory.Exists(path)
            ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Sum(static file => new FileInfo(file).Length)
            : 0;

    sealed class TemporaryDirectoryHandle : IDisposable
    {
        public TemporaryDirectoryHandle()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"pants-scale-ladder-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, true);
            }
            catch (IOException)
            {
                // Best effort; a leaked temp directory is not test-critical.
            }
        }
    }

    sealed record LatencySummary(double P50Microseconds, double P95Microseconds, double P99Microseconds)
    {
        public static LatencySummary From(double[] samples)
        {
            var sorted = samples.Order().ToArray();
            return new LatencySummary(
                Percentile(sorted, 0.50),
                Percentile(sorted, 0.95),
                Percentile(sorted, 0.99));
        }

        static double Percentile(double[] sorted, double percentile)
        {
            if (sorted.Length == 0)
            {
                return 0;
            }

            var rank = (int)Math.Ceiling(percentile * sorted.Length) - 1;
            return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
        }
    }

    sealed record TierReport(
        long RecordCount,
        int ValueSizeBytes,
        int AddressIndexEntryMultiplier,
        long BudgetBytes,
        TimeSpan EmptyOpenElapsed,
        long EmptyOpenRssBytes,
        TimeSpan IngestElapsed,
        double IngestRecordsPerSecond,
        long SteadyStateRssBytes,
        long IngestAndCompactionPeakRssBytes,
        long DatabaseSizeBytes,
        long LogicalBytesIngested,
        long SstBytesWritten,
        long WalBytesWritten,
        LatencySummary ColdPointLatency,
        LatencySummary ColdPrefixLatency,
        LatencySummary WarmPointLatency,
        LatencySummary WarmPrefixLatency,
        PantsReadAmplificationMetrics Amplification,
        PantsRuntimeMetrics RuntimeAfterIngest,
        PantsRuntimeMetrics RuntimeFinal,
        TimeSpan ReopenElapsed,
        long ReopenRssBytes,
        bool ReopenSpotCheckCorrect,
        long PopulatedReopenPeakRssBytes,
        long ReopenProbeBudgetBytes,
        bool CrashRecoveryPassed,
        string CrashRecoveryDetail)
    {
        public string ToMarkdown()
        {
            var builder = new StringBuilder();
            builder.AppendLine(
                CultureInfo.InvariantCulture,
                $"## Tier: {RecordCount:N0} base records " +
                $"({AddressIndexEntryMultiplier} entries/record, {ValueSizeBytes} bytes/primary value, " +
                $"{BudgetBytes / 1024 / 1024} MiB memory budget)");
            builder.AppendLine();
            builder.AppendLine("| Metric | Value |");
            builder.AppendLine("| --- | --- |");
            Row(builder, "Ingest throughput", $"{IngestRecordsPerSecond:N0} records/sec");
            Row(builder, "Address entry multiplier", $"{AddressIndexEntryMultiplier} entries/base record");
            Row(builder, "Ingest wall time", $"{IngestElapsed.TotalSeconds:F1} s");
            Row(builder, "Database size on disk", $"{DatabaseSizeBytes / 1024.0 / 1024:F1} MiB");
            Row(builder, "Startup time (empty database, baseline)", $"{EmptyOpenElapsed.TotalMilliseconds:F0} ms");
            Row(builder, "RSS after opening an empty database (baseline)", $"{EmptyOpenRssBytes / 1024 / 1024:N0} MiB");
            Row(builder, "Steady-state RSS after ingest+compact", $"{SteadyStateRssBytes / 1024 / 1024:N0} MiB");
            Row(
                builder,
                "Peak RSS during ingest and background/final compaction",
                $"{IngestAndCompactionPeakRssBytes / 1024 / 1024:N0} MiB");
            Row(
                builder,
                "Peak RSS opening the populated corpus (separate process)",
                $"{PopulatedReopenPeakRssBytes / 1024 / 1024:N0} MiB");
            Row(
                builder,
                "Populated reopen configured memory budget",
                $"{ReopenProbeBudgetBytes / 1024 / 1024:N0} MiB");
            Row(builder, "Clean reopen time (same process)", $"{ReopenElapsed.TotalMilliseconds:F0} ms");
            Row(builder, "Clean reopen RSS (same process)", $"{ReopenRssBytes / 1024 / 1024:N0} MiB");
            Row(builder, "Clean reopen spot-check correct", ReopenSpotCheckCorrect ? "yes" : "NO — FAILED");
            Row(
                builder,
                "Crash/WAL-replay recovery check",
                CrashRecoveryPassed ? $"PASS — {CrashRecoveryDetail}" : $"FAIL — {CrashRecoveryDetail}");
            Row(
                builder,
                $"{ColdCacheQualifier} point read p50/p95/p99",
                $"{ColdPointLatency.P50Microseconds:F0} / {ColdPointLatency.P95Microseconds:F0} / " +
                $"{ColdPointLatency.P99Microseconds:F0} μs");
            Row(
                builder,
                "Warm point read p50/p95/p99",
                $"{WarmPointLatency.P50Microseconds:F0} / {WarmPointLatency.P95Microseconds:F0} / " +
                $"{WarmPointLatency.P99Microseconds:F0} μs");
            Row(
                builder,
                PrefixScanMetricName(false),
                $"{ColdPrefixLatency.P50Microseconds:F0} / {ColdPrefixLatency.P95Microseconds:F0} / " +
                $"{ColdPrefixLatency.P99Microseconds:F0} μs");
            Row(
                builder,
                PrefixScanMetricName(true),
                $"{WarmPrefixLatency.P50Microseconds:F0} / {WarmPrefixLatency.P95Microseconds:F0} / " +
                $"{WarmPrefixLatency.P99Microseconds:F0} μs");
            Row(
                builder,
                "Physical WAL/SST bytes written",
                $"{WalBytesWritten / 1024.0 / 1024:F1} / {SstBytesWritten / 1024.0 / 1024:F1} MiB");
            Row(
                builder,
                "Write amplification ((WAL + SST bytes) / logical key+value bytes)",
                $"{WriteAmplification():F2}x");
            Row(builder, "Read amplification (avg SSTs/read)", $"{Amplification.AverageSstsPerRead:F2}");
            Row(builder, "Read amplification (avg blocks/read)", $"{Amplification.AverageBlocksPerRead:F2}");
            Row(builder, "L0 overlap rate", $"{Amplification.L0OverlapRate:P1}");
            Row(builder, "Compactions run", $"{RuntimeFinal.CompactionsRun:N0}");
            Row(builder, "Compaction failures", $"{RuntimeFinal.CompactionFailures:N0}");
            Row(builder, "Pending compactions at end", $"{RuntimeFinal.PendingCompactions:N0}");
            Row(builder, "Obsolete file backlog at end", $"{RuntimeFinal.ObsoleteFileBacklog:N0}");
            Row(builder, "Write stalls (compaction) total", $"{RuntimeFinal.WriteStallsCompactionTotal:N0}");
            Row(builder, "Write stalls (memory) total", $"{RuntimeFinal.WriteStallsMemoryTotal:N0}");
            Row(
                builder,
                "Active/immutable memtable bytes at ingest end",
                $"{RuntimeAfterIngest.ActiveMemtableBytes / 1024:N0} KiB / " +
                $"{RuntimeAfterIngest.ImmutableMemtableBytes / 1024:N0} KiB");
            Row(
                builder,
                "Block cache used/capacity",
                $"{RuntimeFinal.BlockCacheUsedBytes / 1024 / 1024:F1} / " +
                $"{RuntimeFinal.BlockCacheCapacityBytes / 1024 / 1024:F1} MiB");
            Row(
                builder,
                "Compaction buffer peak/capacity",
                $"{RuntimeFinal.CompactionBufferPeakBytes / 1024 / 1024:F1} / " +
                $"{RuntimeFinal.CompactionBufferCapacityBytes / 1024 / 1024:F1} MiB");
            Row(
                builder,
                "Scan buffer peak/capacity",
                $"{RuntimeFinal.ScanBufferPeakBytes / 1024 / 1024:F1} / " +
                $"{RuntimeFinal.ScanBufferCapacityBytes / 1024 / 1024:F1} MiB");
            builder.AppendLine();
            return builder.ToString();
        }

        double WriteAmplification()
        {
            return CalculateWriteAmplification(
                LogicalBytesIngested,
                SstBytesWritten,
                WalBytesWritten);
        }

        static void Row(StringBuilder builder, string metric, string value) =>
            builder.AppendLine(CultureInfo.InvariantCulture, $"| {metric} | {value} |");
    }
}

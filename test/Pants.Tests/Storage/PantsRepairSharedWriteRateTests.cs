using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage;

public sealed class PantsRepairSharedWriteRateTests
{
    [Fact]
    public async Task ShouldShareOneBoundedScheduleAcrossActualRepairScratchFlushAndFinalOutputs()
    {
        using var directory = new TemporaryDirectory();
        var sstDirectory = Directory.CreateDirectory(Path.Combine(directory.Path, "sst"));
        var files = new List<FileMeta>();
        for (var index = 0; index < 9; index++)
        {
            var sequence = checked((ulong)(index * 4 + 1));
            var entries = new[]
            {
                Entry("a", "anchor", sequence), Entry($"key-{index}", $"value-{index}", sequence + 1),
                Entry("m", index == 8 ? null : "old", sequence + 2, index == 8), Entry("z", "edge", sequence + 3)
            };
            var name = $"000000_01_{index + 1:00000000000000000000}.sst";
            var bytes = SstCodec.Encode(entries, [], PantsPerformanceGoal.Latency);
            await File.WriteAllBytesAsync(Path.Combine(sstDirectory.FullName, name), bytes);
            files.Add(new FileMeta
            {
                Name = name,
                ColumnFamilyId = 0,
                Level = 1,
                SstSequence = checked((ulong)index + 1),
                SizeBytes = checked((ulong)bytes.Length),
                ContentCrc32C = DiskFormat.Crc32C(bytes),
                SmallestKey = "a"u8.ToArray(),
                LargestKey = "z"u8.ToArray(),
                KeyBoundsComplete = true,
                SmallestSequence = sequence,
                LargestSequence = sequence + 3
            });
        }
        var manifest = new ManifestState
        {
            LastPersistedSequence = 36,
            NextSstSeqs = new Dictionary<uint, ulong> { [0] = 10 },
            FlushedThrough = new Dictionary<uint, ulong> { [0] = 36 },
            Files = files
        };
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "FORMAT"), "midge-format-version=9\n");
        await File.WriteAllBytesAsync(Path.Combine(directory.Path, "manifest.snapshot.json"),
            ManifestCodec.Encode(manifest, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));
        var flushValue = new byte[200 * 1024];
        new Random(20261104).NextBytes(flushValue);
        var clock = new ManualSchedulingTimeProvider();
        var probe = new RateProbe(Path.Combine(sstDirectory.FullName, ".flush-staging"), clock);
        var options = PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false)
            .WithLeaseTimeToLive(TimeSpan.FromDays(7)).WithRuntimeResponseTimeout(TimeSpan.FromDays(1))
            .WithCompaction(new PantsCompactionConfiguration(L0FileCountTrigger: 2, L1TargetSizeBytes: 1024 * 1024,
                MaximumLevels: 3, TargetSstSizeBytes: 256, MaximumInputFiles: 2, BackgroundEnabled: false))
            .WithWriteBackpressure(new PantsWriteBackpressureConfiguration(BackgroundWriteBytesPerSecond: 1024 * 1024));
        await using (var database = await PantsDatabase.OpenForTestingAsync(options,
                         new RuntimeDependencies(probe, runtimeTimeProvider: clock)))
        {
            var concurrent = await database.ColumnFamilies.CreateAsync("concurrent");
            var repair = database.Maintenance.CompactAllAsync().AsTask();
            var scratch = await probe.NextAsync();
            Assert.True(scratch.ScratchPresent);
            Assert.Equal(0, scratch.StartedAt);
            Assert.True(scratch.Until > 0);
            await using (var writer = await database.Transactions.BeginAsync(concurrent, PantsTransactionMode.ReadWrite))
            {
                writer.Put("flush-key"u8.ToArray(), flushValue);
                await writer.CommitAsync(PantsWriteOptions.Sync);
            }
            var flush = database.Maintenance.FlushAsync(concurrent).AsTask();
            try
            {
                var concurrentFlush = await probe.NextAsync();
                Assert.True(concurrentFlush.FlushPresent);
                Assert.Equal(scratch.Until, concurrentFlush.StartedAt);
                Assert.NotEqual(scratch.ThreadId, concurrentFlush.ThreadId);
                Assert.False(repair.IsCompleted);
                Assert.False(flush.IsCompleted);
                clock.Advance(TimeSpan.FromTicks(scratch.Until));
                var nextScratch = await probe.NextAsync();
                Assert.Equal(scratch.ThreadId, nextScratch.ThreadId);
                Assert.Equal(concurrentFlush.Until, nextScratch.StartedAt);
                Assert.False(flush.IsCompleted);
                Assert.Equal(checked((ulong)scratch.Bytes),
                    (await database.Diagnostics.GetRuntimeMetricsAsync()).BackgroundRateLimitedBytes);
                await using var retained = await database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily,
                    PantsTransactionMode.ReadOnly);
                Assert.Equal("value-8"u8.ToArray(), (await retained.GetAsync("key-8"u8.ToArray()))?.ToArray());
            }
            finally
            {
                probe.Continue();
            }
            await Task.WhenAll(repair, flush).WaitAsync(TestTimeouts.Expected);
            var reports = probe.Reservations.OrderBy(static item => item.StartedAt).ThenBy(static item => item.Until).ToArray();
            Assert.All(reports, item => Assert.InRange(item.Bytes, 1, 64 * 1024));
            Assert.Contains(reports, static item => item.Bytes == 64 * 1024);
            for (var index = 1; index < reports.Length; index++) { Assert.True(reports[index].StartedAt >= reports[index - 1].Until); }
            Assert.True(probe.FinalCompactionWaitObserved);
            var metrics = await database.Diagnostics.GetRuntimeMetricsAsync();
            Assert.Equal(checked((ulong)reports.Sum(static item => (long)item.Bytes)), metrics.BackgroundRateLimitedBytes);
            Assert.True(metrics.BackgroundRateLimitedBytes > checked((ulong)metrics.SstBytesWrittenTotal));
            Assert.True(metrics.SstBytesWrittenTotal > 0);
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(sstDirectory.FullName, ".flush-staging")));
        }
        await using var reopened = await PantsDatabase.OpenAsync(options);
        await using var reader = await reopened.Transactions.BeginAsync(reopened.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        for (var index = 0; index < 9; index++)
        {
            Assert.Equal(TestBytes.FromString($"value-{index}"), (await reader.GetAsync(TestBytes.FromString($"key-{index}")))?.ToArray());
        }
        Assert.Null(await reader.GetAsync("m"u8.ToArray()));
        var reopenedConcurrent = Assert.IsAssignableFrom<IPantsColumnFamily>(await reopened.ColumnFamilies.GetAsync("concurrent"));
        await using var flushReader = await reopened.Transactions.BeginAsync(reopenedConcurrent, PantsTransactionMode.ReadOnly);
        Assert.Equal(flushValue, (await flushReader.GetAsync("flush-key"u8.ToArray()))?.ToArray());
    }

    static SstEntry Entry(string key, string? value, ulong sequence, bool delete = false) =>
        new(TestBytes.FromString(key), value is null ? null : TestBytes.FromString(value), sequence, null, delete);

    sealed record Reservation(int Bytes, long StartedAt, long Until, int ThreadId, bool ScratchPresent, bool FlushPresent);

    sealed class RateProbe(string staging, ManualSchedulingTimeProvider clock) : IFailpointHandler
    {
        readonly Channel<Reservation> _events = Channel.CreateUnbounded<Reservation>();
        readonly ConcurrentQueue<Reservation> _reservations = new();
        int _continuing;
        int _compactionThread;
        int _finalCompactionWaitObserved;
        public IEnumerable<Reservation> Reservations => _reservations;
        public bool FinalCompactionWaitObserved => Volatile.Read(ref _finalCompactionWaitObserved) != 0;
        public async Task<Reservation> NextAsync() => await _events.Reader.ReadAsync().AsTask().WaitAsync(TestTimeouts.Expected);
        public void Hit(Failpoint failpoint) { }
        public void Continue()
        {
            Volatile.Write(ref _continuing, 1);
            clock.Advance(TimeSpan.FromSeconds(1));
        }
        public void BackgroundWriteRateWait(int bytes, long startedAt, long until)
        {
            var thread = Environment.CurrentManagedThreadId;
            Interlocked.CompareExchange(ref _compactionThread, thread, 0);
            var paths = Directory.EnumerateFiles(staging).Select(Path.GetFileName).ToArray();
            if (thread == Volatile.Read(ref _compactionThread) && paths.Any(name => name!.Contains("000000_", StringComparison.Ordinal)))
            {
                Volatile.Write(ref _finalCompactionWaitObserved, 1);
            }
            var report = new Reservation(bytes, startedAt, until, thread,
                paths.Any(name => name!.StartsWith("repair.", StringComparison.Ordinal)),
                paths.Any(name => name!.Contains("000001_", StringComparison.Ordinal)));
            _reservations.Enqueue(report);
            _events.Writer.TryWrite(report);
            if (Volatile.Read(ref _continuing) != 0) { clock.Advance(TimeSpan.FromSeconds(1)); }
        }
    }
}

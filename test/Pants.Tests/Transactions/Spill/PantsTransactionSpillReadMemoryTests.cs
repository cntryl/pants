using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Transactions.Spill;

public sealed class PantsTransactionSpillReadMemoryTests
{
    const int LargeValueBytes = 64 * 1_024;
    const int LargeValueCount = 256;
    const int CursorLargeValueBytes = 512 * 1_024;
    const int CursorValueCount = 40;
    const long MaximumCursorMoveNextAllocationBytes = 256 * 1_024;
    const long MaximumSynchronousReadAllocationBytes = 4 * 1_024 * 1_024;

    static readonly ColumnFamilyIdentity Family = new(0, "default", 0);

    [Fact]
    public async Task ShouldBoundPointAndScanReadsGivenManyLargeSpilledValues()
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.Local(directory.Path)
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(4 * 1_024 * 1_024))
            .WithMemtableLimits(1_024 * 1_024)
            .WithTransactionMemoryPool(1_024 * 1_024)
            .WithBackgroundCompaction(false);
        await using var database = await PantsDatabase.OpenAsync(options);
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put("scan:000"u8.ToArray(), "before-range"u8.ToArray());
        transaction.Put("scan:010"u8.ToArray(), "deleted-by-range"u8.ToArray());
        for (var index = 0; index < LargeValueCount; index++)
        {
            transaction.Put(
                TestBytes.FromString($"scan:{index + 100:000}"),
                new byte[LargeValueBytes]);
        }

        transaction.DeleteRange("scan:000"u8.ToArray(), "scan:020"u8.ToArray());
        transaction.Put("scan:000"u8.ToArray(), "after-range"u8.ToArray());
        transaction.Put("scan:020"u8.ToArray(), "outside-range"u8.ToArray());
        Assert.NotEmpty(TransactionSpillHardeningTestHarness.FindArtifacts(directory.Path));

        var pointAllocationStart = GC.GetAllocatedBytesForCurrentThread();
        var updatedRequest = transaction.GetAsync("scan:000"u8.ToArray());
        var updatedLookupAllocated = GC.GetAllocatedBytesForCurrentThread() - pointAllocationStart;
        var updated = await updatedRequest;
        pointAllocationStart = GC.GetAllocatedBytesForCurrentThread();
        var deletedRequest = transaction.GetAsync("scan:010"u8.ToArray());
        var deletedLookupAllocated = GC.GetAllocatedBytesForCurrentThread() - pointAllocationStart;
        var deleted = await deletedRequest;

        Assert.Equal("after-range", TestBytes.ToText(updated!.Value));
        Assert.Null(deleted);
        Assert.True(
            updatedLookupAllocated < MaximumSynchronousReadAllocationBytes,
            $"Updated-key lookup allocated {updatedLookupAllocated:N0} bytes before its first await.");
        Assert.True(
            deletedLookupAllocated < MaximumSynchronousReadAllocationBytes,
            $"Range-deleted lookup allocated {deletedLookupAllocated:N0} bytes before its first await.");

        var scanAllocationStart = GC.GetAllocatedBytesForCurrentThread();
        var scanRequest = transaction.ScanAsync(new PantsScanQuery
        {
            StartInclusive = "scan:000"u8.ToArray(),
            EndExclusive = "scan:030"u8.ToArray(),
            Limit = 2
        });
        var scanCreationAllocated = GC.GetAllocatedBytesForCurrentThread() - scanAllocationStart;
        await using var scan = await scanRequest;
        var rows = new List<PantsEntry>();
        await foreach (var row in scan)
        {
            rows.Add(row);
        }

        Assert.Equal(["scan:000", "scan:020"], rows.Select(row => TestBytes.ToText(row.Key)));
        Assert.Equal("after-range", TestBytes.ToText(rows[0].Value));
        Assert.Equal("outside-range", TestBytes.ToText(rows[1].Value));
        Assert.True(
            scanCreationAllocated < MaximumSynchronousReadAllocationBytes,
            $"Scan creation allocated {scanCreationAllocated:N0} bytes before its first await.");
    }

    [Fact]
    public void ShouldBoundOneRowForwardAndReverseScanEnumerationGivenLargeSpilledValues()
    {
        using var directory = new TemporaryDirectory();
        using var store = new TransactionSpillStore(directory.Path, 1, Family);
        var value = new byte[CursorLargeValueBytes];
        store.WriteRun(Enumerable.Range(0, CursorValueCount)
            .Select(index => new TransactionIntentOperation(
                checked((ulong)index),
                CommitOperationKind.Put,
                Family,
                TestBytes.FromString($"key-{index:00}"),
                null,
                value,
                null,
                null,
                false))
            .ToArray());
        using var view = store.CreateReadView([], new TransactionMemoryPool(4_096));
        using var forward = view.CreateKeyScan(null, null, PantsScanDirection.Forward);
        using var reverse = view.CreateKeyScan(null, null, PantsScanDirection.Reverse);

        var allocationStart = GC.GetAllocatedBytesForCurrentThread();
        var movedForward = forward.MoveNext();
        var forwardAllocated = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
        allocationStart = GC.GetAllocatedBytesForCurrentThread();
        var movedReverse = reverse.MoveNext();
        var reverseAllocated = GC.GetAllocatedBytesForCurrentThread() - allocationStart;

        Assert.True(movedForward);
        Assert.Equal("key-00", TestBytes.ToText(forward.Current));
        Assert.True(movedReverse);
        Assert.Equal("key-39", TestBytes.ToText(reverse.Current));
        Assert.True(
            forwardAllocated < MaximumCursorMoveNextAllocationBytes,
            $"One-row forward scan allocated {forwardAllocated:N0} bytes while enumerating.");
        Assert.True(
            reverseAllocated < MaximumCursorMoveNextAllocationBytes,
            $"One-row reverse scan allocated {reverseAllocated:N0} bytes while enumerating.");
    }

    [Fact]
    public void ShouldReleaseSpillReadViewWhenItsMemoryReservationIsRejected()
    {
        using var directory = new TemporaryDirectory();
        using var store = new TransactionSpillStore(directory.Path, 1, Family);
        store.WriteRun([
            new TransactionIntentOperation(
                0,
                CommitOperationKind.Put,
                Family,
                "key"u8.ToArray(),
                null,
                "value"u8.ToArray(),
                null,
                null,
                false)
        ]);

        Assert.Throws<PantsResourceLimitException>(() =>
            store.CreateReadView([], new TransactionMemoryPool(1)));

        store.Dispose();
        Assert.False(Directory.Exists(Path.Combine(directory.Path, "txn")));
    }

    [Fact]
    public void ShouldReserveSpillReaderStateBeforeOpeningRunIndexes()
    {
        using var directory = new TemporaryDirectory();
        var readers = new CountingRunReaders();
        using var store = CreateManyRunStore(directory.Path, 8, readers);
        var memoryPool = new TransactionMemoryPool(4_096);
        using var view = store.CreateReadView([], memoryPool);
        var viewBytes = memoryPool.Used;

        using (view.CreateKeyScan(null, null, PantsScanDirection.Reverse))
        {
            Assert.True(memoryPool.Used > viewBytes);
            Assert.Equal(1, readers.PeakActive);
        }

        Assert.Equal(viewBytes, memoryPool.Used);
        Assert.Equal(0, readers.Active);
    }

    [Fact]
    public void ShouldRejectScanBeforeOpeningSpillRunIndexesWhenReservationDoesNotFit()
    {
        using var directory = new TemporaryDirectory();
        var readers = new CountingRunReaders();
        using var store = CreateManyRunStore(directory.Path, 8, readers);
        var memoryPool = new TransactionMemoryPool(IntPtr.Size * (8 + 4));
        using var view = store.CreateReadView([], memoryPool);
        var viewBytes = memoryPool.Used;

        Assert.Throws<PantsResourceLimitException>(() =>
            view.CreateKeyScan(null, null, PantsScanDirection.Reverse));

        Assert.Equal(0, readers.PeakActive);
        Assert.Equal(0, readers.Active);
        Assert.Equal(viewBytes, memoryPool.Used);
    }

    [Fact]
    public void ShouldReleaseSpillReaderReservationWhenRunCursorConstructionFails()
    {
        using var directory = new TemporaryDirectory();
        var readers = new CountingRunReaders(failOnOpen: 2);
        using var store = CreateManyRunStore(directory.Path, 8, readers);
        var memoryPool = new TransactionMemoryPool(4_096);
        using var view = store.CreateReadView([], memoryPool);
        var viewBytes = memoryPool.Used;

        Assert.Throws<PantsIOException>(() =>
            view.CreateKeyScan(null, null, PantsScanDirection.Reverse));

        Assert.Equal(2, readers.OpenCalls);
        Assert.Equal(1, readers.PeakActive);
        Assert.Equal(0, readers.Active);
        Assert.Equal(viewBytes, memoryPool.Used);
    }

    [Theory]
    [InlineData(PantsScanDirection.Forward)]
    [InlineData(PantsScanDirection.Reverse)]
    public void ShouldKeepAtMostOneSpillRunReaderOpenPerScan(PantsScanDirection direction)
    {
        using var directory = new TemporaryDirectory();
        var readers = new CountingRunReaders();
        using var store = CreateManyRunStore(directory.Path, 8, readers);
        using var view = store.CreateReadView([], new TransactionMemoryPool(4_096));
        using var scan = view.CreateKeyScan(null, null, direction);
        var keys = new List<string>();
        while (scan.MoveNext())
        {
            keys.Add(TestBytes.ToText(scan.Current));
        }

        var expected = Enumerable.Range(0, 8)
            .Select(index => $"key-{index:00}")
            .ToArray();
        if (direction == PantsScanDirection.Reverse)
        {
            Array.Reverse(expected);
        }

        Assert.Equal(expected, keys);
        Assert.Equal(1, readers.PeakActive);
        Assert.Equal(0, readers.Active);
    }

    [Fact]
    public async Task ShouldRetainSpilledScanReadViewUntilEnumerationCompletesAfterRollback()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await TransactionSpillHardeningTestHarness.OpenLocalAsync(directory.Path);
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put("retained:a"u8.ToArray(), "alpha"u8.ToArray());
        TransactionSpillHardeningTestHarness.Fill(transaction, "outside", 4);
        transaction.Put("retained:b"u8.ToArray(), "bravo"u8.ToArray());
        await using var scan = await transaction.ScanAsync(new PantsScanQuery
        {
            Prefix = "retained:"u8.ToArray()
        });
        Assert.NotEmpty(TransactionSpillHardeningTestHarness.FindArtifacts(directory.Path));

        await transaction.RollbackAsync();
        Assert.NotEmpty(TransactionSpillHardeningTestHarness.FindArtifacts(directory.Path));
        var rows = new List<PantsEntry>();
        await foreach (var row in scan)
        {
            rows.Add(row);
        }

        await scan.DisposeAsync();
        Assert.Equal(["retained:a", "retained:b"], rows.Select(row => TestBytes.ToText(row.Key)));
        Assert.Equal(["alpha", "bravo"], rows.Select(row => TestBytes.ToText(row.Value)));
        Assert.Empty(TransactionSpillHardeningTestHarness.FindArtifacts(directory.Path));
    }

    static TransactionSpillStore CreateManyRunStore(
        string path,
        int runCount,
        CountingRunReaders readers)
    {
        var store = new TransactionSpillStore(
            path,
            1,
            Family,
            readStreamFactory: readers.Open);
        for (var index = 0; index < runCount; index++)
        {
            store.WriteRun([
                new TransactionIntentOperation(
                    checked((ulong)index),
                    CommitOperationKind.Put,
                    Family,
                    TestBytes.FromString($"key-{index:00}"),
                    null,
                    TestBytes.FromString($"value-{index:00}"),
                    null,
                    null,
                    false)
            ]);
        }

        return store;
    }

    sealed class CountingRunReaders
    {
        readonly Lock _gate = new();
        readonly int? _failOnOpen;

        public CountingRunReaders(int? failOnOpen = null)
        {
            _failOnOpen = failOnOpen;
        }

        public int OpenCalls { get; private set; }

        public int Active { get; private set; }

        public int PeakActive { get; private set; }

        public TrackingReadStream Open(string path)
        {
            lock (_gate)
            {
                OpenCalls++;
                if (OpenCalls == _failOnOpen)
                {
                    throw new IOException("Injected spill reader open failure.");
                }

                Active++;
                PeakActive = Math.Max(PeakActive, Active);
            }

            return new TrackingReadStream(
                new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read),
                this);
        }

        public void Closed()
        {
            lock (_gate)
            {
                Active--;
            }
        }
    }

    sealed class TrackingReadStream(Stream inner, CountingRunReaders readers) : Stream
    {
        bool _disposed;

        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) =>
            inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => inner.Read(buffer);

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                inner.Dispose();
                readers.Closed();
            }

            base.Dispose(disposing);
        }
    }
}

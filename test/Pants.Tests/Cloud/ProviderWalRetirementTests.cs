using System.Text;
using System.Text.Json;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

public sealed class ProviderWalRetirementTests
{
    const int PageBytes = 4 * 1024;

    [Fact]
    public async Task ShouldRetireCoveredWalWithoutTouchingUnrelatedSsts()
    {
        await using var harness = await RetirementHarness.CreateAsync();
        var covering = harness.PublishSst("covering.sst", 0, "a-0", "a-9");
        var unrelated = Enumerable.Range(0, 24)
            .Select(index => index % 2 == 0
                ? harness.PublishSst($"unrelated-{index}.sst", 0, $"z-{index:D2}-0", $"z-{index:D2}-9")
                : harness.PublishSst($"other-family-{index}.sst", 1, "a-0", "a-9"))
            .ToArray();
        harness.PublishManifest([covering, .. unrelated]);
        var segment = await harness.PublishSegmentAsync(1, RetirementHarness.Records("a-", 5));
        harness.ResetRequests();

        var result = await harness.Persistence.RetireCoveredWalAsync(CancellationToken.None);

        Assert.Equal(new WalRetirementResult(WalRetirementOutcome.Settled, 1), result);
        Assert.Empty(harness.CatalogSegmentIds());
        Assert.False(harness.Wal.Contains(segment.ObjectKey));
        foreach (var file in unrelated)
        {
            Assert.Empty(harness.Sst.RequestsFor(PantsCloudObjectLayout.SstPrefix + file.Name));
        }

        Assert.All(
            harness.Sst.RequestsFor(PantsCloudObjectLayout.SstPrefix + covering.Name),
            request => Assert.Equal(StoreOperation.Head, request.Operation));
        Assert.False(harness.Persistence.HasPersistenceAnomaly);
    }

    [Fact]
    public async Task ShouldBoundRetirementMemoryByMaintenancePoolIndependentOfSegmentSize()
    {
        const int largestFrame = 3 * PageBytes;
        await using var harness = await RetirementHarness.CreateAsync(budgetBytes: 4L * largestFrame + 1024);
        harness.PublishManifest([harness.PublishSst("covering.sst", 0, "a-", "a-~")]);
        var records = Enumerable.Range(0, 256)
            .Select(index => new WalTestRecord(
                0,
                $"a-{index:D4}",
                index % 16 == 0 ? largestFrame - 256 : 900))
            .ToArray();
        var segment = await harness.PublishSegmentAsync(1, records);
        Assert.True(segment.SizeBytes > 64UL * PageBytes, "The segment should dwarf the pool.");
        harness.ResetRequests();

        var result = await harness.Persistence.RetireCoveredWalAsync(CancellationToken.None);

        Assert.Equal(1, result.RetiredSegments);
        Assert.DoesNotContain(
            harness.Wal.RequestsFor(segment.ObjectKey),
            request => request.Operation == StoreOperation.Get);
        Assert.True(harness.Budget.Peak <= harness.Budget.Limit);
        Assert.True(
            harness.Wal.RequestsFor(segment.ObjectKey).Max(static request => request.Length) <= largestFrame,
            "Each ranged read is bounded by a page or one frame.");
        Assert.Equal(0, harness.Budget.Current);
    }

    [Fact]
    public async Task ShouldRetainWalAuthorityWithoutAnomalyWhenFrameExceedsMaintenancePool()
    {
        await using var harness = await RetirementHarness.CreateAsync(budgetBytes: 4L * PageBytes);
        harness.PublishManifest([harness.PublishSst("covering.sst", 0, "a-", "a-~")]);
        var segment = await harness.PublishSegmentAsync(1, [new WalTestRecord(0, "a-big", 2 * PageBytes)]);

        var result = await harness.Persistence.RetireCoveredWalAsync(CancellationToken.None);

        Assert.Equal(new WalRetirementResult(WalRetirementOutcome.Deferred, 0), result);
        Assert.Equal([segment.SegmentId], harness.CatalogSegmentIds());
        Assert.True(harness.Wal.Contains(segment.ObjectKey));
        Assert.False(harness.Persistence.HasPersistenceAnomaly);
        Assert.Equal(0, harness.Budget.Current);
    }

    [Fact]
    public async Task ShouldFinishOldestWalProofAcrossShortAttemptQuantaWithoutRereading()
    {
        await using var harness = await RetirementHarness.CreateAsync(quantum: TimeSpan.FromMilliseconds(1));
        harness.PublishManifest([harness.PublishSst("covering.sst", 0, "a-", "a-~")]);
        var segment = await harness.PublishSegmentAsync(1, RetirementHarness.Records("a-", 64, 900));
        harness.ResetRequests();
        harness.Wal.BeforeRangeRead = (_, _) =>
        {
            harness.Clock.Advance(TimeSpan.FromMilliseconds(10));
            return ValueTask.CompletedTask;
        };

        var attempts = await harness.RetireUntilRetiredAsync(result =>
        {
            Assert.Equal(WalRetirementOutcome.Yielded, result.Outcome);
            Assert.Equal([segment.SegmentId], harness.CatalogSegmentIds());
        });

        Assert.True(attempts > 4, $"Retirement took {attempts} attempts; it should have yielded.");
        AssertResumedWithoutRereading(harness.Wal.RangeOffsets(segment.ObjectKey));
        Assert.Empty(harness.CatalogSegmentIds());
        Assert.False(harness.Persistence.HasPersistenceAnomaly);
    }

    [Fact]
    public async Task ShouldFinishOldestWalProofAcrossRepeatedProviderTimeouts()
    {
        await using var harness = await RetirementHarness.CreateAsync();
        harness.PublishManifest([harness.PublishSst("covering.sst", 0, "a-", "a-~")]);
        var segment = await harness.PublishSegmentAsync(1, RetirementHarness.Records("a-", 64, 900));
        harness.ResetRequests();
        var reads = 0;
        harness.Wal.BeforeRangeRead = (_, _) => ++reads % 3 == 0
            ? ValueTask.FromException(new PantsTimeoutException("The provider timed out."))
            : ValueTask.CompletedTask;

        var attempts = await harness.RetireUntilRetiredAsync(result =>
        {
            Assert.Equal(WalRetirementOutcome.Deferred, result.Outcome);
            Assert.Equal([segment.SegmentId], harness.CatalogSegmentIds());
            Assert.False(harness.Persistence.HasPersistenceAnomaly);
        });

        Assert.True(attempts > 2);
        AssertResumedWithoutRereading(harness.Wal.RangeOffsets(segment.ObjectKey));
        Assert.False(harness.Persistence.HasPersistenceAnomaly);
    }

    [Fact]
    public async Task ShouldRestartCrcProgressWhenWalObjectIdentityChanges()
    {
        await using var harness = await RetirementHarness.CreateAsync(quantum: TimeSpan.FromMilliseconds(1));
        harness.PublishManifest([harness.PublishSst("covering.sst", 0, "a-", "a-~")]);
        var segment = await harness.PublishSegmentAsync(1, RetirementHarness.Records("a-", 32, 900));
        harness.AdvanceClockOnEveryRangeRead();
        harness.ResetRequests();

        var first = await harness.Persistence.RetireCoveredWalAsync(CancellationToken.None);
        Assert.Equal(WalRetirementOutcome.Yielded, first.Outcome);
        harness.Wal.Write(segment.ObjectKey, harness.Wal.Read(segment.ObjectKey));

        await harness.RetireUntilRetiredAsync();

        AssertRestartedOnce(harness.Wal.RangeOffsets(segment.ObjectKey));
    }

    [Fact]
    public async Task ShouldPreserveOldestProofProgressWhileNewerSstsAreAppended()
    {
        await using var harness = await RetirementHarness.CreateAsync(quantum: TimeSpan.FromMilliseconds(1));
        var covering = harness.PublishSst("covering.sst", 0, "a-", "a-~");
        harness.PublishManifest([covering]);
        var segment = await harness.PublishSegmentAsync(1, RetirementHarness.Records("a-", 32, 900));
        harness.AdvanceClockOnEveryRangeRead();
        harness.ResetRequests();

        Assert.Equal(
            WalRetirementOutcome.Yielded,
            (await harness.Persistence.RetireCoveredWalAsync(CancellationToken.None)).Outcome);
        harness.PublishManifest([covering, harness.PublishSst("appended.sst", 0, "b-0", "b-9")]);

        await harness.RetireUntilRetiredAsync();

        AssertResumedWithoutRereading(harness.Wal.RangeOffsets(segment.ObjectKey));
        Assert.Empty(harness.Sst.RequestsFor(PantsCloudObjectLayout.SstPrefix + "appended.sst"));
    }

    [Fact]
    public async Task ShouldRestartSemanticProgressWhenCoveringSstIsReplaced()
    {
        await using var harness = await RetirementHarness.CreateAsync(quantum: TimeSpan.FromMilliseconds(1));
        harness.PublishManifest([harness.PublishSst("compacted-away.sst", 0, "a-", "a-~")]);
        var segment = await harness.PublishSegmentAsync(1, RetirementHarness.Records("a-", 32, 900));
        harness.AdvanceClockOnEveryRangeRead();
        harness.ResetRequests();

        Assert.Equal(
            WalRetirementOutcome.Yielded,
            (await harness.Persistence.RetireCoveredWalAsync(CancellationToken.None)).Outcome);
        harness.PublishManifest([harness.PublishSst("compaction-output.sst", 0, "a-", "a-~")]);

        await harness.RetireUntilRetiredAsync();

        AssertRestartedOnce(harness.Wal.RangeOffsets(segment.ObjectKey));
        Assert.Empty(harness.Sst.RequestsFor(PantsCloudObjectLayout.SstPrefix + "compacted-away.sst"));
    }

    [Fact]
    public async Task ShouldRetireOnlyContiguousOldestPrefixOfCoveredSegments()
    {
        await using var harness = await RetirementHarness.CreateAsync();
        harness.PublishManifest([harness.PublishSst("covering.sst", 0, "a-", "a-~")]);
        await harness.PublishSegmentAsync(1, RetirementHarness.Records("a-", 2));
        await harness.PublishSegmentAsync(2, RetirementHarness.Records("uncovered-", 2));
        var third = await harness.PublishSegmentAsync(3, RetirementHarness.Records("a-", 2));
        harness.ResetRequests();

        var result = await harness.Persistence.RetireCoveredWalAsync(CancellationToken.None);

        Assert.Equal(new WalRetirementResult(WalRetirementOutcome.Settled, 1), result);
        Assert.Equal([2UL, 3UL], harness.CatalogSegmentIds());
        Assert.Empty(harness.Wal.RequestsFor(third.ObjectKey));
    }

    [Fact]
    public async Task ShouldRetainEverySegmentWhenOldestSegmentIsNotCovered()
    {
        await using var harness = await RetirementHarness.CreateAsync();
        harness.PublishManifest([harness.PublishSst("covering.sst", 0, "a-", "a-~")]);
        await harness.PublishSegmentAsync(1, RetirementHarness.Records("uncovered-", 2));
        await harness.PublishSegmentAsync(2, RetirementHarness.Records("a-", 2));

        var first = await harness.Persistence.RetireCoveredWalAsync(CancellationToken.None);
        harness.ResetRequests();
        var second = await harness.Persistence.RetireCoveredWalAsync(CancellationToken.None);

        Assert.Equal(WalRetirementResult.Settled, first);
        Assert.Equal(WalRetirementResult.Settled, second);
        Assert.Equal([1UL, 2UL], harness.CatalogSegmentIds());
        Assert.DoesNotContain(
            harness.Wal.Requests,
            static request => request.Operation == StoreOperation.GetRange);
    }

    [Fact]
    public async Task ShouldRetireProvenPrefixWhenNewerSegmentProofIsDeferred()
    {
        await using var harness = await RetirementHarness.CreateAsync();
        harness.PublishManifest([harness.PublishSst("covering.sst", 0, "a-", "a-~")]);
        await harness.PublishSegmentAsync(1, RetirementHarness.Records("a-", 2));
        var second = await harness.PublishSegmentAsync(2, RetirementHarness.Records("a-", 2));
        harness.Wal.BeforeRangeRead = (key, _) => key == second.ObjectKey
            ? ValueTask.FromException(new PantsBusyException("The provider is busy."))
            : ValueTask.CompletedTask;

        var result = await harness.Persistence.RetireCoveredWalAsync(CancellationToken.None);

        Assert.Equal(new WalRetirementResult(WalRetirementOutcome.Deferred, 1), result);
        Assert.Equal([2UL], harness.CatalogSegmentIds());
        Assert.False(harness.Persistence.HasPersistenceAnomaly);
    }

    [Fact]
    public async Task ShouldLeaveCatalogUnchangedWhenPublishedManifestChangesBeforeCommit()
    {
        await using var harness = await RetirementHarness.CreateAsync();
        var covering = harness.PublishSst("covering.sst", 0, "a-", "a-~");
        harness.PublishManifest([covering]);
        var segment = await harness.PublishSegmentAsync(1, RetirementHarness.Records("a-", 2));
        harness.Wal.BeforeRangeRead = (_, _) =>
        {
            harness.PublishManifest([covering, harness.PublishSst("flushed.sst", 0, "b-0", "b-9")]);
            harness.Wal.BeforeRangeRead = null;
            return ValueTask.CompletedTask;
        };

        var first = await harness.Persistence.RetireCoveredWalAsync(CancellationToken.None);
        Assert.Equal(new WalRetirementResult(WalRetirementOutcome.Yielded, 0), first);
        Assert.Equal([segment.SegmentId], harness.CatalogSegmentIds());
        harness.ResetRequests();

        var second = await harness.Persistence.RetireCoveredWalAsync(CancellationToken.None);

        Assert.Equal(1, second.RetiredSegments);
        Assert.Empty(harness.CatalogSegmentIds());
        Assert.DoesNotContain(
            harness.Wal.RequestsFor(segment.ObjectKey),
            static request => request.Operation == StoreOperation.GetRange);
    }

    [Fact]
    public async Task ShouldRevalidateFromStartAfterRestartAndKeepCatalogRecoverable()
    {
        await using var harness = await RetirementHarness.CreateAsync(quantum: TimeSpan.FromMilliseconds(1));
        harness.PublishManifest([harness.PublishSst("covering.sst", 0, "a-", "a-~")]);
        var segment = await harness.PublishSegmentAsync(1, RetirementHarness.Records("a-", 32, 900));
        harness.AdvanceClockOnEveryRangeRead();
        harness.ResetRequests();
        Assert.Equal(
            WalRetirementOutcome.Yielded,
            (await harness.Persistence.RetireCoveredWalAsync(CancellationToken.None)).Outcome);

        var hydrated = await ProviderCloudPersistence.HydrateLocalCacheAsync(
            harness.Cache.Path,
            harness.Wal,
            harness.Sst,
            harness.Control,
            PantsRecoveryPolicy.Strict,
            CancellationToken.None);
        Assert.Equal([segment.SegmentId], hydrated.PublishedWalSegments.Keys);

        harness.ResetRequests();
        var restarted = harness.CreatePersistence(quantum: TimeSpan.FromHours(1));
        var result = await restarted.RetireCoveredWalAsync(CancellationToken.None);

        Assert.Equal(1, result.RetiredSegments);
        // Proof progress is process-local: the new process revalidates from the first byte.
        AssertResumedWithoutRereading(harness.Wal.RangeOffsets(segment.ObjectKey));
        Assert.Empty(harness.CatalogSegmentIds());
    }

    [Fact]
    public async Task ShouldProveWalThroughWholeObjectReadWhenStoreHasNoRanges()
    {
        await using var harness = await RetirementHarness.CreateAsync(supportsRanges: false);
        harness.PublishManifest([harness.PublishSst("covering.sst", 0, "a-", "a-~")]);
        await harness.PublishSegmentAsync(1, RetirementHarness.Records("a-", 4));

        var result = await harness.Persistence.RetireCoveredWalAsync(CancellationToken.None);

        Assert.Equal(1, result.RetiredSegments);
        Assert.Empty(harness.CatalogSegmentIds());
    }

    /// <summary>Every ranged read starts past the previous one: no acknowledged frame is read again.</summary>
    static void AssertResumedWithoutRereading(ulong[] offsets)
    {
        Assert.Equal(0UL, offsets[0]);
        for (var index = 1; index < offsets.Length; index++)
        {
            Assert.True(
                offsets[index] > offsets[index - 1],
                $"Read {index} restarted at {offsets[index]} after {offsets[index - 1]}.");
        }
    }

    /// <summary>The proof restarted from the segment's first byte exactly once.</summary>
    static void AssertRestartedOnce(ulong[] offsets) =>
        Assert.Equal(2, offsets.Count(static offset => offset == 0));

    sealed class RetirementHarness : IAsyncDisposable
    {
        static readonly JsonSerializerOptions ManifestJsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        };

        readonly long _budgetBytes;
        readonly CloudLeaseCoordinator _lease;
        ulong _lastPersistedSequence;

        RetirementHarness(
            long budgetBytes,
            CloudLeaseCoordinator lease,
            ulong epoch,
            bool supportsRanges)
        {
            _budgetBytes = budgetBytes;
            _lease = lease;
            Epoch = epoch;
            Wal = new ScriptedCloudObjectStore { SupportsRanges = supportsRanges };
        }

        public TemporaryDirectory Cache { get; } = new();

        public ManualTimeProvider Clock { get; } = new();

        public ScriptedCloudObjectStore Wal { get; }

        public ScriptedCloudObjectStore Sst { get; } = new();

        public ScriptedCloudObjectStore Control { get; } = new();

        public ResourceBudget Budget { get; private set; } = null!;

        public ProviderCloudPersistence Persistence { get; private set; } = null!;

        public ulong Epoch { get; }

        public static async Task<RetirementHarness> CreateAsync(
            long budgetBytes = 64L * 1024 * 1024,
            TimeSpan? quantum = null,
            bool supportsRanges = true)
        {
            var lease = new CloudLeaseCoordinator(
                new TestCloudLeaseStore(),
                new ManualClock(DateTimeOffset.UnixEpoch),
                "writer",
                TimeSpan.FromSeconds(10),
                TimeSpan.Zero);
            var epoch = await lease.AcquireAsync(CancellationToken.None);
            var harness = new RetirementHarness(budgetBytes, lease, epoch, supportsRanges);
            harness.Persistence = harness.CreatePersistence(quantum ?? TimeSpan.FromHours(1));
            await harness.Persistence.FenceWalCatalogAsync(CancellationToken.None);
            return harness;
        }

        public ProviderCloudPersistence CreatePersistence(TimeSpan quantum)
        {
            Budget = new ResourceBudget(_budgetBytes);
            return new ProviderCloudPersistence(
                Cache.Path,
                Wal,
                Sst,
                Control,
                _lease,
                walRetirementSettings: new WalRetirementSettings(
                    Budget,
                    new MaintenanceMemoryGate(),
                    PageBytes,
                    quantum,
                    Clock));
        }

        public static WalTestRecord[] Records(string prefix, int count, int valueBytes = 16) =>
            Enumerable.Range(0, count)
                .Select(index => new WalTestRecord(0, $"{prefix}{index:D4}", valueBytes))
                .ToArray();

        public void ResetRequests()
        {
            Wal.ResetRequests();
            Sst.ResetRequests();
            Control.ResetRequests();
        }

        public void AdvanceClockOnEveryRangeRead() =>
            Wal.BeforeRangeRead = (_, _) =>
            {
                Clock.Advance(TimeSpan.FromMilliseconds(10));
                return ValueTask.CompletedTask;
            };

        public FileMeta PublishSst(string name, uint family, string smallestKey, string largestKey)
        {
            var file = new FileMeta
            {
                Name = name,
                ColumnFamilyId = family,
                SizeBytes = 128,
                SmallestKey = Encoding.UTF8.GetBytes(smallestKey),
                LargestKey = Encoding.UTF8.GetBytes(largestKey),
                KeyBoundsComplete = true,
                SmallestSequence = 1,
                LargestSequence = 1_000_000
            };
            Sst.Write(PantsCloudObjectLayout.SstPrefix + name, new byte[file.SizeBytes]);
            return file;
        }

        public void PublishManifest(FileMeta[] files)
        {
            _lastPersistedSequence = Math.Max(_lastPersistedSequence, 1_000_000);
            var manifest = new ManifestState
            {
                LastPersistedSequence = _lastPersistedSequence,
                Files = [.. files]
            };
            Control.Write(
                PantsCloudObjectLayout.MetadataPrefix + "manifest.json",
                JsonSerializer.SerializeToUtf8Bytes(manifest, ManifestJsonOptions));
        }

        public async Task<ProviderPublishedWalSegment> PublishSegmentAsync(
            ulong segmentId,
            WalTestRecord[] records)
        {
            using var stream = new MemoryStream();
            var maximumSequence = 0UL;
            for (var index = 0; index < records.Length; index++)
            {
                var record = records[index];
                var beginSequence = checked((segmentId * 10_000) + ((ulong)index * 10));
                var payload = WalCodec.EncodeTransactionBatch(
                    checked(beginSequence + 1),
                    beginSequence,
                    Epoch,
                    [
                        new WalMutation(
                            record.Family,
                            WalOperation.Put,
                            Encoding.UTF8.GetBytes(record.Key),
                            RandomValue(record.ValueBytes, index),
                            0,
                            null,
                            null)
                    ]);
                DiskFormat.WriteUInt32(stream, checked((uint)payload.Length));
                DiskFormat.WriteUInt32(stream, DiskFormat.Crc32C(payload));
                stream.Write(payload);
                maximumSequence = WalCodec.DecodeRecord(payload).Sequence;
            }

            await Persistence.PublishWalBatchAsync(
                [new SealedWalSegment(segmentId, Epoch, maximumSequence, $"{segmentId}.wal", stream.ToArray())],
                CancellationToken.None);
            return ReadCatalog().Single(segment => segment.SegmentId == segmentId);
        }

        static byte[] RandomValue(int length, int seed)
        {
            // Incompressible, so the encoded WAL is as large as the values.
            var value = new byte[length];
            new Random(seed).NextBytes(value);
            return value;
        }

        public ulong[] CatalogSegmentIds() => ReadCatalog()
            .Select(static segment => segment.SegmentId)
            .ToArray();

        public async Task<int> RetireUntilRetiredAsync(Action<WalRetirementResult>? beforeRetired = null)
        {
            for (var attempt = 1; attempt <= 1_000; attempt++)
            {
                var result = await Persistence.RetireCoveredWalAsync(CancellationToken.None);
                if (result.RetiredSegments > 0)
                {
                    return attempt;
                }

                beforeRetired?.Invoke(result);
            }

            throw new InvalidOperationException("WAL retirement did not finish.");
        }

        public ValueTask DisposeAsync()
        {
            _lease.Dispose();
            Cache.Dispose();
            return ValueTask.CompletedTask;
        }

        SortedDictionary<ulong, ProviderPublishedWalSegment>.ValueCollection ReadCatalog() =>
            ProviderCloudPersistence.DecodeCatalogForFloor(
                    Wal.Read(PantsCloudObjectLayout.WalCatalogObjectKey))
                .Segments.Values;
    }
}

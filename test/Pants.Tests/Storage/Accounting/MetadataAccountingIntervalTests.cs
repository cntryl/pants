using Cntryl.Pants.Storage.Internal.Accounting;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage.Accounting;

public sealed class MetadataAccountingIntervalTests
{
    [Fact]
    public void ShouldSubtractObservedHistogramBinsAndRejectIncompleteOrDecreasingIntervals()
    {
        using var directory = new TemporaryDirectory();
        var time = new ManualTimeProvider();
        var owner = new MetadataAccountingOwner(time);
        Checkpoint(TimeSpan.FromMilliseconds(1));
        var before = owner.Handle.Snapshot();
        Checkpoint(TimeSpan.FromMilliseconds(30));
        var after = owner.Handle.Snapshot();
        var delta = after.Delta(before);
        var bucket = delta.Bucket(MetadataOrigin.Administration, MetadataMedium.Persistent);
        Assert.Equal(1UL, bucket[MetadataCounter.CheckpointAttempts]);
        Assert.Equal(30_000_000UL, bucket[MetadataCounter.CheckpointNanoseconds]);
        Assert.Equal(1UL, bucket.CheckpointLatency.Aggregate(0UL, static (total, count) => total + count));
        Assert.Equal(1UL, bucket.CheckpointLatency[MetadataAccountingArithmetic.LatencyBin(30_000_000)]);

        var index = (int)MetadataOrigin.Administration * 2;
        var observed = after.Buckets[index];
        var missingHistogram = after with
        {
            Buckets = after.Buckets.SetItem(index, observed with { CheckpointLatency = before.Buckets[index].CheckpointLatency })
        };
        Assert.Throws<InvalidOperationException>(() => missingHistogram.Delta(before));
        var decreasing = after with
        {
            Buckets = after.Buckets.SetItem(index, observed with
            {
                Counters = observed.Counters.SetItem((int)MetadataCounter.SnapshotIssuedBytes, 0)
            })
        };
        Assert.Throws<InvalidOperationException>(() => decreasing.Delta(before));

        Checkpoint(TimeSpan.FromMinutes(20));
        var exceeded = owner.Handle.Snapshot();
        Assert.True(exceeded.Overflow);
        Assert.True(exceeded.Bucket(MetadataOrigin.Administration, MetadataMedium.Persistent).CheckpointLatency[^1] > 0);
        Assert.Throws<InvalidOperationException>(() => exceeded.Delta(after));

        void Checkpoint(TimeSpan duration)
        {
            using var operation = owner.Begin(MetadataOperationKind.Checkpoint, MetadataOrigin.Administration, MetadataMedium.Persistent);
            var bytes = "persisted checkpoint"u8.ToArray();
            AtomicStagedFile.Write(Path.Combine(directory.Path, "manifest.snapshot.json"), bytes,
                observation: operation.Observe(MetadataPayload.Snapshot));
            time.Advance(duration);
            operation.SnapshotDurable((ulong)bytes.Length);
            operation.CheckpointComplete();
            operation.Finish(true);
        }
    }

    [Fact]
    public void ShouldPreserveFirstReceiptClockAndCreditOnlyOneMatchingPublication()
    {
        var time = new ManualTimeProvider();
        var owner = new MetadataAccountingOwner(time);
        var receipt = new MetadataPublicationReceipt("000000_00_00000000000000000001.sst", 0, 1, 128, 7,
            MetadataOrigin.CloudFlush, MetadataMedium.Persistent, time.GetTimestamp());
        owner.RetainPublication(receipt);
        time.Advance(TimeSpan.FromSeconds(1));
        owner.RetainPublication(receipt with { StartedTimestamp = time.GetTimestamp() });
        time.Advance(TimeSpan.FromSeconds(1));
        var file = new FileMeta { Name = receipt.Name, ColumnFamilyId = 0, SstSequence = 1, SizeBytes = 128, ContentCrc32C = 7 };
        owner.CompletePublications([file]);
        owner.CompletePublications([file]);
        var bucket = owner.Handle.Snapshot().Bucket(MetadataOrigin.CloudFlush, MetadataMedium.Persistent);
        Assert.Equal(1UL, bucket[MetadataCounter.FlushCommittedCount]);
        Assert.Equal(128UL, bucket[MetadataCounter.FlushCommittedSstBytes]);
        Assert.Equal(2_000_000_000UL, bucket[MetadataCounter.FullPublicationNanoseconds]);
    }

    [Fact]
    public void ShouldInvalidateMismatchedAndUnsettledPublicationInsteadOfInventingSuccess()
    {
        var owner = new MetadataAccountingOwner();
        var before = owner.Handle.Snapshot();
        owner.RetainPublication(new MetadataPublicationReceipt("output.sst", 0, 1, 128, 7,
            MetadataOrigin.Recovery, MetadataMedium.Persistent, owner.Timestamp()));
        owner.CompletePublications([new FileMeta { Name = "output.sst", ColumnFamilyId = 0, SstSequence = 1, SizeBytes = 128, ContentCrc32C = 9 }]);
        var after = owner.Handle.Snapshot();
        Assert.Equal(0UL, after.Bucket(MetadataOrigin.Recovery, MetadataMedium.Persistent)[MetadataCounter.FlushCommittedCount]);
        Assert.True(after.IncompleteObservations > 0);
        Assert.Throws<InvalidOperationException>(() => after.Delta(before));
        owner.SealPublications();
        owner.RetainPublication(new MetadataPublicationReceipt("late.sst", 0, 2, 128, 8,
            MetadataOrigin.Recovery, MetadataMedium.Persistent, owner.Timestamp()));
        Assert.True(owner.Handle.Snapshot().IncompleteObservations > after.IncompleteObservations);
    }

    [Fact]
    public void ShouldInvalidatePublicationCapacityExhaustionAndAbandonedOperation()
    {
        var owner = new MetadataAccountingOwner();
        var before = owner.Handle.Snapshot();
        for (var index = 0; index < 1100; index++)
        {
            owner.RetainPublication(new MetadataPublicationReceipt($"output-{index}.sst", 0, (ulong)index, 128, 7,
                MetadataOrigin.CloudFlush, MetadataMedium.Persistent, owner.Timestamp()));
        }

        using (owner.Begin(MetadataOperationKind.Checkpoint, MetadataOrigin.Administration, MetadataMedium.Persistent))
        {
            // Leaving an operation without its terminal result must remain observable.
        }

        var after = owner.Handle.Snapshot();
        var bucket = after.Bucket(MetadataOrigin.Administration, MetadataMedium.Persistent);
        Assert.Equal(1UL, bucket[MetadataCounter.AbandonedOperations]);
        Assert.Equal(1UL, bucket[MetadataCounter.OperationFailures]);
        Assert.Equal(0UL, bucket.ActiveOperations);
        Assert.True(after.IncompleteObservations > 0);
        Assert.Throws<InvalidOperationException>(() => after.Delta(before));
        owner.SealPublications();
        Assert.True(owner.Handle.Snapshot().IncompleteObservations > after.IncompleteObservations);
    }
}

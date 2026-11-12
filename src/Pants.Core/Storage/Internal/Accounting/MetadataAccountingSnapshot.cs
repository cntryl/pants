using System.Collections.Immutable;

namespace Cntryl.Pants.Storage.Internal.Accounting;

sealed record MetadataAccountingSnapshot(
    ulong OwnerId,
    ImmutableArray<MetadataAccountingBucket> Buckets,
    bool Overflow,
    ulong LateMutations,
    ulong IncompleteObservations)
{
    public MetadataAccountingBucket Bucket(MetadataOrigin origin, MetadataMedium medium) =>
        Buckets[(int)origin * 2 + (int)medium];

    public MetadataAccountingSnapshot Delta(MetadataAccountingSnapshot before)
    {
        if (OwnerId == 0 || OwnerId != before.OwnerId ||
            Buckets.Length != MetadataAccountingArithmetic.BucketCount ||
            before.Buckets.Length != MetadataAccountingArithmetic.BucketCount ||
            Overflow || before.Overflow || LateMutations != 0 || before.LateMutations != 0 ||
            IncompleteObservations != 0 || before.IncompleteObservations != 0)
        {
            throw new InvalidOperationException("Metadata accounting identity or integrity is invalid.");
        }

        var buckets = ImmutableArray.CreateBuilder<MetadataAccountingBucket>(Buckets.Length);
        for (var index = 0; index < Buckets.Length; index++)
        {
            var after = Buckets[index];
            var prior = before.Buckets[index];
            if (after.Origin != prior.Origin || after.Medium != prior.Medium ||
                (int)after.Origin * 2 + (int)after.Medium != index)
            {
                throw new InvalidOperationException("Metadata accounting bucket identity differs.");
            }

            ValidateCoverage(after);
            ValidateCoverage(prior);

            var delta = after with
            {
                Counters = Subtract(after.Counters, prior.Counters, MetadataAccountingArithmetic.CounterCount),
                CheckpointLatency = Subtract(after.CheckpointLatency, prior.CheckpointLatency, MetadataAccountingArithmetic.LatencyBins),
                PublicationLatency = Subtract(after.PublicationLatency, prior.PublicationLatency, MetadataAccountingArithmetic.LatencyBins),
                CommittedSstSizes = Subtract(after.CommittedSstSizes, prior.CommittedSstSizes, MetadataAccountingArithmetic.SizeBins)
            };
            ValidateCoverage(delta);
            buckets.Add(delta);
        }

        return this with { Buckets = buckets.MoveToImmutable() };
    }

    static void ValidateCoverage(MetadataAccountingBucket bucket)
    {
        if (bucket.Counters.Length != MetadataAccountingArithmetic.CounterCount ||
            bucket.CheckpointLatency.Length != MetadataAccountingArithmetic.LatencyBins ||
            bucket.PublicationLatency.Length != MetadataAccountingArithmetic.LatencyBins ||
            bucket.CommittedSstSizes.Length != MetadataAccountingArithmetic.SizeBins)
        {
            throw new InvalidOperationException("Metadata accounting histogram or counter layout is invalid.");
        }

        static ulong Count(ImmutableArray<ulong> counts)
        {
            var total = 0UL;
            foreach (var count in counts)
            {
                if (!MetadataAccountingArithmetic.Add(ref total, count))
                {
                    throw new InvalidOperationException("Metadata accounting histogram overflowed.");
                }
            }

            return total;
        }

        if (Count(bucket.CheckpointLatency) != bucket[MetadataCounter.CheckpointAttempts] ||
            Count(bucket.PublicationLatency) != bucket[MetadataCounter.FlushCommittedCount] ||
            Count(bucket.CommittedSstSizes) != bucket[MetadataCounter.FlushCommittedCount])
        {
            throw new InvalidOperationException("Metadata accounting histogram does not cover every operation.");
        }
    }

    static ImmutableArray<ulong> Subtract(ImmutableArray<ulong> after, ImmutableArray<ulong> before, int length)
    {
        if (after.Length != length || before.Length != length)
        {
            throw new InvalidOperationException("Metadata accounting histogram or counter layout is invalid.");
        }

        var result = ImmutableArray.CreateBuilder<ulong>(length);
        for (var index = 0; index < length; index++)
        {
            if (after[index] < before[index])
            {
                throw new InvalidOperationException("Metadata accounting observation decreased.");
            }

            result.Add(after[index] - before[index]);
        }

        return result.MoveToImmutable();
    }
}

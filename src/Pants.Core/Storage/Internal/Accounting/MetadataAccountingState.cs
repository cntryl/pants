using System.Collections.Immutable;

namespace Cntryl.Pants.Storage.Internal.Accounting;

// This object contains counters only. A retained handle never retains storage or a runtime.
sealed class MetadataAccountingState(ulong ownerId)
{
    public ulong OwnerId { get; } = ownerId;

    readonly Lock _gate = new();
    readonly ulong[][] _counters = Create(MetadataAccountingArithmetic.CounterCount);
    readonly ulong[][] _checkpointLatency = Create(MetadataAccountingArithmetic.LatencyBins);
    readonly ulong[][] _publicationLatency = Create(MetadataAccountingArithmetic.LatencyBins);
    readonly ulong[][] _sstSizes = Create(MetadataAccountingArithmetic.SizeBins);
    readonly ulong[] _active = new ulong[MetadataAccountingArithmetic.BucketCount];
    bool _overflow = ownerId == 0;
    ulong _late;
    ulong _incomplete;

    public void Invalidate(bool late = false)
    {
        lock (_gate)
        {
            if (late)
            {
                _overflow |= !MetadataAccountingArithmetic.Add(ref _late, 1);
            }
            else
            {
                _overflow |= !MetadataAccountingArithmetic.Add(ref _incomplete, 1);
            }
        }
    }

    public void Begin(int bucket)
    {
        lock (_gate)
        {
            _overflow |= !MetadataAccountingArithmetic.Add(ref _active[bucket], 1);
        }
    }

    public void Fold(int bucket, ulong[] counters, bool overflow, bool finishOperation = false)
    {
        lock (_gate)
        {
            _overflow |= overflow;
            for (var index = 0; index < counters.Length; index++)
            {
                _overflow |= !MetadataAccountingArithmetic.Add(ref _counters[bucket][index], counters[index]);
            }

            if (finishOperation)
            {
                if (_active[bucket] == 0)
                {
                    _overflow = true;
                }
                else
                {
                    _active[bucket]--;
                }
            }

            Record(_checkpointLatency[bucket], counters, MetadataCounter.CheckpointAttempts, MetadataCounter.CheckpointNanoseconds);
            Record(_publicationLatency[bucket], counters, MetadataCounter.FlushCommittedCount, MetadataCounter.FullPublicationNanoseconds);
            if (counters[(int)MetadataCounter.FlushCommittedCount] != 0)
            {
                var bytes = counters[(int)MetadataCounter.FlushCommittedSstBytes];
                _overflow |= bytes == 0;
                var bin = bytes == 0 ? 0 : System.Numerics.BitOperations.Log2(bytes);
                _overflow |= !MetadataAccountingArithmetic.Add(ref _sstSizes[bucket][bin], 1);
            }
        }
    }

    void Record(ulong[] bins, ulong[] counters, MetadataCounter count, MetadataCounter elapsed)
    {
        if (counters[(int)count] != 0)
        {
            _overflow |= counters[(int)elapsed] >= (16_000_000UL << 16);
            _overflow |= !MetadataAccountingArithmetic.Add(
                ref bins[MetadataAccountingArithmetic.LatencyBin(counters[(int)elapsed])], 1);
        }
    }

    public MetadataAccountingSnapshot Snapshot()
    {
        lock (_gate)
        {
            var buckets = ImmutableArray.CreateBuilder<MetadataAccountingBucket>(MetadataAccountingArithmetic.BucketCount);
            for (var index = 0; index < MetadataAccountingArithmetic.BucketCount; index++)
            {
                buckets.Add(new MetadataAccountingBucket(
                    (MetadataOrigin)(index / 2), (MetadataMedium)(index % 2),
                    [.. _counters[index]], [.. _checkpointLatency[index]],
                    [.. _publicationLatency[index]], [.. _sstSizes[index]], _active[index]));
            }

            return new MetadataAccountingSnapshot(OwnerId, buckets.MoveToImmutable(), _overflow, _late, _incomplete);
        }
    }

    static ulong[][] Create(int size) => Enumerable.Range(0, MetadataAccountingArithmetic.BucketCount)
        .Select(_ => new ulong[size]).ToArray();
}

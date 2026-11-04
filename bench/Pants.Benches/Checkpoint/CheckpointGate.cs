using System.Collections.Immutable;
using Cntryl.Pants.Storage.Internal.Accounting;

namespace Cntryl.Pants.Checkpoint;

static class CheckpointGate
{
    public static Verdict Evaluate(MetadataAccountingSnapshot delta, ulong expectedFlushes, ulong completedCompactions)
    {
        var reasons = Integrity(delta, false).ToList();
        var ordinary = delta.Bucket(MetadataOrigin.OrdinaryLocalFlush, MetadataMedium.Persistent);
        if (ordinary[MetadataCounter.FlushCommittedCount] != expectedFlushes)
        {
            reasons.Add("Flush count differs from fixed construction.");
        }
        if (ordinary[MetadataCounter.FlushCommittedSstBytes] == 0 || ordinary[MetadataCounter.FullPublicationNanoseconds] == 0)
        {
            reasons.Add("Zero ordinary-local Persistent denominator.");
        }
        if (completedCompactions == 0)
        {
            reasons.Add("No genuine compaction completed in measured ingestion.");
        }
        if (delta.Buckets.Any(static bucket => bucket.Origin == MetadataOrigin.Unclassified &&
                (bucket[MetadataCounter.OperationAttempts] > 0 || bucket[MetadataCounter.FlushCommittedCount] > 0)))
        {
            reasons.Add("Unclassified measured metadata cost.");
        }
        var p95 = P95(ordinary.CheckpointLatency);
        var snapshotMiss = (UInt128)ordinary[MetadataCounter.SnapshotIssuedBytes] * 100 >=
                           (UInt128)ordinary[MetadataCounter.FlushCommittedSstBytes] * 5;
        var checkpointMiss = (UInt128)ordinary[MetadataCounter.CheckpointNanoseconds] * 100 >=
                             (UInt128)ordinary[MetadataCounter.FullPublicationNanoseconds] * 20 &&
                             p95 is { Lower: >= 5_000_000 };
        return new Verdict(reasons.Count == 0, [.. reasons], snapshotMiss, checkpointMiss,
            reasons.Count == 0 ? snapshotMiss || checkpointMiss : null, p95?.Lower, p95?.Upper);
    }

    public static Verdict Settle(Verdict measured, ulong originalOwner, MetadataAccountingSnapshot original,
        ulong reopenedOwner, MetadataAccountingSnapshot reopened)
    {
        var reasons = measured.InvalidReasons.ToBuilder();
        if (original.OwnerId != originalOwner || reopened.OwnerId != reopenedOwner ||
            originalOwner == 0 || reopenedOwner == 0 || originalOwner == reopenedOwner)
        {
            reasons.Add("Original/reopened settlement owner identity is missing, changed or reused.");
        }
        reasons.AddRange(Integrity(original, true));
        reasons.AddRange(Integrity(reopened, true));
        return measured with
        {
            Valid = reasons.Count == 0,
            InvalidReasons = reasons.ToImmutable(),
            Miss = reasons.Count == 0 ? measured.SnapshotMiss || measured.CheckpointMiss : null
        };
    }

    public static ImmutableArray<string> Integrity(MetadataAccountingSnapshot snapshot, bool settled)
    {
        var reasons = ImmutableArray.CreateBuilder<string>();
        if (snapshot.OwnerId == 0 || snapshot.Overflow || snapshot.LateMutations != 0 || snapshot.IncompleteObservations != 0)
        {
            reasons.Add("Accounting owner contains overflow/escaped/incomplete evidence.");
        }
        try
        {
            _ = snapshot.Delta(snapshot);
        }
        catch (InvalidOperationException exception)
        {
            reasons.Add(exception.Message);
            return reasons.ToImmutable();
        }
        foreach (var bucket in snapshot.Buckets)
        {
            if (settled && bucket.ActiveOperations != 0 || bucket.Medium == MetadataMedium.Persistent && bucket.ActiveOperations != 0)
            {
                reasons.Add($"{bucket.Origin}: metadata operation crosses the boundary.");
            }
            if (bucket.Medium != MetadataMedium.Persistent)
            {
                continue;
            }
            if (bucket[MetadataCounter.OperationFailures] != 0 || bucket[MetadataCounter.AbandonedOperations] != 0)
            {
                reasons.Add($"{bucket.Origin}: persistent metadata failed or was abandoned.");
            }
            if (bucket[MetadataCounter.CheckpointAttempts] != bucket[MetadataCounter.CheckpointCompleteCount] ||
                bucket[MetadataCounter.SnapshotDurableCount] != bucket[MetadataCounter.CheckpointCompleteCount])
            {
                reasons.Add($"{bucket.Origin}: checkpoint durability/completion differs.");
            }
        }
        return reasons.ToImmutable();
    }

    public static (ulong Lower, ulong Upper)? P95(ImmutableArray<ulong> bins)
    {
        var count = bins.Aggregate((UInt128)0, static (total, bin) => total + bin);
        if (count == 0)
        {
            return null;
        }
        var rank = (count * 95 + 99) / 100;
        var seen = (UInt128)0;
        for (var index = 0; index < bins.Length; index++)
        {
            seen += bins[index];
            if (seen < rank)
            {
                continue;
            }
            var lower = index < 64 ? (ulong)index * 250_000 : 16_000_000UL << (index - 64);
            var upper = index < 64 ? lower + 249_999 : (16_000_000UL << (index - 63)) - 1;
            return (lower, upper);
        }
        throw new InvalidOperationException("Checkpoint histogram rank is impossible.");
    }

    public sealed record Verdict(bool Valid, ImmutableArray<string> InvalidReasons, bool SnapshotMiss,
        bool CheckpointMiss, bool? Miss, ulong? CheckpointP95LowerNanoseconds, ulong? CheckpointP95UpperNanoseconds);
}

using System.Collections.Immutable;

namespace Cntryl.Pants.Storage.Internal;

interface IStorageReadStore
{
    bool IsWithinFileRange(FileMeta file, ReadOnlySpan<byte> key);

    IReadOnlyDictionary<uint, ImmutableArray<FileMeta>> GetVisibleFilesSnapshot();

    bool IsSstAvailable(FileMeta file);

    ValueTask<SstEntry?> TryReadPointValueAsync(
        IReadOnlyList<FileMeta> candidatesNewestFirst,
        ReadOnlyMemory<byte> key,
        CancellationToken cancellationToken);

    ValueTask<ulong?> GetLatestMutationSequenceAsync(
        IReadOnlyList<FileMeta> candidatesNewestFirst,
        ReadOnlyMemory<byte> key,
        CancellationToken cancellationToken);

    ValueTask<bool> HasMutationInRangeAsync(
        IReadOnlyList<FileMeta> candidates,
        ReadOnlyMemory<byte> startInclusive,
        ReadOnlyMemory<byte> endExclusive,
        ulong afterSequence,
        ResourceBudget? resourceBudget,
        CancellationToken cancellationToken);
}

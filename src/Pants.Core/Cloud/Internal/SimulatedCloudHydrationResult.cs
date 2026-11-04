using Cntryl.Pants.Storage.Internal.Wal;

namespace Cntryl.Pants.Cloud.Internal;

sealed record SimulatedCloudHydrationResult(
    ulong MinimumWriterEpoch,
    bool RequiresSalvage,
    ulong NextWalSegmentId = 1,
    WalChainLink? WalHead = null,
    ulong SequenceFloor = 0,
    IReadOnlyList<IRemoteWalSegment>? RemoteWalSegments = null,
    ulong CloudDurableSequence = 0);

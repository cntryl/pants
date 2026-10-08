namespace Cntryl.Pants.Cloud.Internal;

interface ICloudPersistence : ICloudDdlAuthority, IAsyncDisposable
{
    bool HasPersistenceAnomaly { get; }

    ValueTask PublishWalBatchAsync(
        IReadOnlyList<SealedWalSegment> segments,
        CancellationToken cancellationToken);

    ValueTask MirrorMetadataAndSstsAsync(CancellationToken cancellationToken);

    ValueTask CollectObsoleteSstsAsync(CancellationToken cancellationToken);

    /// <summary>
    ///     One bounded background turn of retiring published WAL that the published manifest
    ///     covers. Running out of time or memory is an outcome, not an error.
    /// </summary>
    ValueTask<WalRetirementResult> RetireCoveredWalAsync(CancellationToken cancellationToken);

    ValueTask ValidateWriteAuthorityAsync(CancellationToken cancellationToken);
}

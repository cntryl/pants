using System.Diagnostics;

namespace Cntryl.Pants.Cloud.Internal;

sealed class ProviderCloudSstSourceFactory(
    ICloudObjectStore store,
    RuntimeTelemetry telemetry,
    ProviderSstIdentityRegistry? identityRegistry = null) : IAsyncSstSourceFactory, IAsyncSstEvictionVerifier
{
    readonly ICloudObjectStore _store = store ?? throw new ArgumentNullException(nameof(store));
    readonly ProviderSstIdentityRegistry _identityRegistry = identityRegistry ?? new();

    public async ValueTask<IAsyncSstSource?> OpenAsync(
        FileMeta file,
        CancellationToken cancellationToken)
    {
        var objectKey = PantsCloudObjectLayout.SstPrefix + file.Name;
        var metadata = await ReadMetadataAsync(objectKey, cancellationToken).ConfigureAwait(false);

        if (metadata is null)
        {
            return null;
        }

        if (file.SizeBytes != 0 && metadata.SizeBytes != file.SizeBytes)
        {
            throw new PantsCorruptionException(
                $"Cloud SST '{file.Name}' length differs from its manifest.");
        }

        return new ProviderCloudSstSource(
            _store,
            objectKey,
            metadata,
            telemetry);
    }

    public async ValueTask VerifyRemoteSstForEvictionAsync(
        FileMeta file,
        long localLength,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_identityRegistry.TryGet(file.Name, out var identity))
        {
            throw new PantsRecoveryFailedException(
                $"Cloud SST '{file.Name}' has no retained publication identity for safe eviction.");
        }

        if (localLength < 0 || checked((ulong)localLength) != identity.SizeBytes)
        {
            throw new PantsRecoveryFailedException(
                $"Local SST '{file.Name}' differs from its verified cloud publication.");
        }

        if (file.SizeBytes != 0 && file.SizeBytes != identity.SizeBytes)
        {
            throw new PantsCorruptionException(
                $"Cloud SST '{file.Name}' length differs from its manifest.");
        }

        var objectKey = PantsCloudObjectLayout.SstPrefix + file.Name;
        var metadata = await ReadMetadataAsync(objectKey, cancellationToken).ConfigureAwait(false);
        if (metadata is null)
        {
            throw new PantsRecoveryFailedException(
                $"Manifest cloud SST '{file.Name}' is unavailable for eviction.");
        }

        if (metadata.SizeBytes != identity.SizeBytes)
        {
            throw new PantsRecoveryFailedException(
                $"Cloud SST '{file.Name}' differs from its verified publication size.");
        }

        if (!StringComparer.Ordinal.Equals(metadata.Version, identity.Version))
        {
            throw new PantsFencedException(
                $"Immutable cloud SST '{objectKey}' changed identity after publication.");
        }
    }

    async ValueTask<CloudObjectMetadata?> ReadMetadataAsync(
        string objectKey,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _store.HeadAsync(objectKey, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (
            cancellationToken.IsCancellationRequested &&
            exception.CancellationToken != cancellationToken)
        {
            throw new OperationCanceledException(
                exception.Message,
                exception,
                cancellationToken);
        }
    }

    sealed class ProviderCloudSstSource(
        ICloudObjectStore store,
        string objectKey,
        CloudObjectMetadata metadata,
        RuntimeTelemetry telemetry) : IAsyncSstSource
    {
        int _disposed;

        public long Length { get; } = checked((long)metadata.SizeBytes);

        public async ValueTask<byte[]> ReadExactlyAsync(
            long offset,
            int rangeLength,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfNegative(rangeLength);
            if (offset > Length || rangeLength > Length - offset)
            {
                throw new EndOfStreamException("The cloud SST range is outside the object.");
            }

            var startedAt = Stopwatch.GetTimestamp();
            telemetry.RecordRemoteRangeStarted();
            var returnedBytes = 0L;
            var failed = true;
            try
            {
                CloudObject? value;
                try
                {
                    value = store is CloudObjectStore builtInStore
                        ? await builtInStore.GetRangeAsync(
                                objectKey,
                                checked((ulong)offset),
                                rangeLength,
                                metadata,
                                cancellationToken)
                            .ConfigureAwait(false)
                        : await store.GetRangeAsync(
                                objectKey,
                                checked((ulong)offset),
                                rangeLength,
                                cancellationToken)
                            .ConfigureAwait(false);
                }
                catch (OperationCanceledException exception) when (
                    cancellationToken.IsCancellationRequested &&
                    exception.CancellationToken != cancellationToken)
                {
                    throw new OperationCanceledException(
                        exception.Message,
                        exception,
                        cancellationToken);
                }

                returnedBytes = value?.Data.Length ?? 0;
                if (value is null)
                {
                    throw new PantsRecoveryFailedException(
                        $"Manifest-owned cloud SST '{objectKey}' is missing during a ranged read.");
                }

                if (!StringComparer.Ordinal.Equals(value.Version, metadata.Version))
                {
                    throw new PantsBusyException(
                        $"Immutable cloud SST '{objectKey}' was replaced during a ranged read.");
                }

                if (value.Data.Length != rangeLength)
                {
                    throw new PantsCorruptionException(
                        $"Cloud SST '{objectKey}' returned a truncated range.");
                }

                var bytes = value.Data.ToArray();
                failed = false;
                return bytes;
            }
            finally
            {
                telemetry.RecordRemoteRangeCompleted(
                    returnedBytes,
                    Stopwatch.GetElapsedTime(startedAt),
                    failed);
            }
        }

        public ValueTask DisposeAsync()
        {
            Volatile.Write(ref _disposed, 1);
            return ValueTask.CompletedTask;
        }
    }
}

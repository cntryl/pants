namespace Cntryl.Pants.Cloud.Internal;

sealed class ProviderCloudSstSourceFactory(ICloudObjectStore store) : IAsyncSstSourceFactory
{
    readonly ICloudObjectStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public async ValueTask<IAsyncSstSource?> OpenAsync(
        FileMeta file,
        CancellationToken cancellationToken)
    {
        var objectKey = PantsCloudObjectLayout.SstPrefix + file.Name;
        CloudObjectMetadata? metadata;
        try
        {
            metadata = await _store.HeadAsync(objectKey, cancellationToken).ConfigureAwait(false);
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
            checked((long)metadata.SizeBytes),
            metadata.Version);
    }

    sealed class ProviderCloudSstSource(
        ICloudObjectStore store,
        string objectKey,
        long length,
        string version) : IAsyncSstSource
    {
        int _disposed;

        public long Length { get; } = length;

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

            CloudObject? value;
            try
            {
                value = await store.GetRangeAsync(
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

            if (value is null)
            {
                throw new PantsRecoveryFailedException(
                    $"Manifest-owned cloud SST '{objectKey}' is missing during a ranged read.");
            }
            if (!StringComparer.Ordinal.Equals(value.Version, version))
            {
                throw new PantsCorruptionException(
                    $"Immutable cloud SST '{objectKey}' was replaced during a ranged read.");
            }

            if (value.Data.Length != rangeLength)
            {
                throw new PantsCorruptionException(
                    $"Cloud SST '{objectKey}' returned a truncated range.");
            }

            return value.Data.ToArray();
        }

        public ValueTask DisposeAsync()
        {
            Volatile.Write(ref _disposed, 1);
            return ValueTask.CompletedTask;
        }
    }
}

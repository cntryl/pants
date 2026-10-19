using System.Buffers;
using System.Text.Json;

namespace Cntryl.Pants.Cloud.Internal;

/// <summary>
///     Counts JSON into a fixed workspace, then reserves the serialized publication envelope
///     before allocating its byte array. The reservation is shared with other maintenance work.
/// </summary>
sealed class CloudMetadataWriteAdmission(ResourceBudget budget)
{
    public CloudMetadataSerializedBytes Serialize<T>(
        T value,
        JsonSerializerOptions options,
        string resourceName = "Cloud WAL catalog")
    {
        var fixedWorkspaceBytes = FixedWorkspaceBytes;
        var maximumSerializedBytes = (budget.Limit - fixedWorkspaceBytes) /
                                     ImmutablePublicationEnvelope.CopyFactor;
        if (maximumSerializedBytes <= 0)
        {
            throw new PantsResourceLimitException(
                $"{resourceName} publication needs at least " +
                $"{fixedWorkspaceBytes + ImmutablePublicationEnvelope.CopyFactor} " +
                $"bytes of maintenance memory; the pool holds {budget.Limit}.");
        }

        var countingBufferBytes = checked((int)Math.Min(
            ProviderWalCatalogReadAdmission.ReadRangeBytes,
            Math.Max(256, budget.Limit / 4)));
        long serializedBytes;
        using (budget.Reserve(countingBufferBytes))
        {
            var counter = new JsonCountingBufferWriter(
                countingBufferBytes,
                maximumSerializedBytes,
                resourceName);
            using (var jsonWriter = new Utf8JsonWriter(
                       counter,
                       new JsonWriterOptions
                       {
                           Encoder = options.Encoder,
                           Indented = options.WriteIndented,
                           MaxDepth = options.MaxDepth
                       }))
            {
                JsonSerializer.Serialize(jsonWriter, value, options);
                jsonWriter.Flush();
            }

            serializedBytes = counter.BytesWritten;
        }

        var workspaceBytes = PublicationWorkspaceBytes(serializedBytes, fixedWorkspaceBytes);
        if (workspaceBytes > budget.Limit)
        {
            throw new PantsResourceLimitException(
                $"{resourceName} publication needs {workspaceBytes} bytes of maintenance " +
                $"memory; the pool holds {budget.Limit}.");
        }

        var reservation = budget.Reserve(workspaceBytes);
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, options);
            if (bytes.LongLength != serializedBytes)
            {
                throw new PantsCorruptionException(
                    $"{resourceName} serialization changed length between admission and publication.");
            }

            return new CloudMetadataSerializedBytes(bytes, reservation);
        }
        catch
        {
            reservation.Dispose();
            throw;
        }
    }

    public IDisposable AdmitExisting(
        long serializedBytes,
        string resourceName = "Cloud WAL catalog")
    {
        ArgumentOutOfRangeException.ThrowIfNegative(serializedBytes);
        var workspaceBytes = PublicationWorkspaceBytes(serializedBytes, FixedWorkspaceBytes);
        if (workspaceBytes > budget.Limit)
        {
            throw new PantsResourceLimitException(
                $"{resourceName} repair needs {workspaceBytes} bytes of maintenance memory; " +
                $"the pool holds {budget.Limit}.");
        }

        return budget.Reserve(workspaceBytes);
    }

    long FixedWorkspaceBytes => Math.Min(
        ImmutablePublicationEnvelope.FixedWorkspaceBytes,
        budget.Limit / 2);

    static long PublicationWorkspaceBytes(long serializedBytes, long fixedWorkspaceBytes) =>
        serializedBytes > (long.MaxValue - fixedWorkspaceBytes) / ImmutablePublicationEnvelope.CopyFactor
            ? long.MaxValue
            : serializedBytes * ImmutablePublicationEnvelope.CopyFactor + fixedWorkspaceBytes;

    sealed class JsonCountingBufferWriter(int bufferBytes, long maximumBytes, string resourceName) : IBufferWriter<byte>
    {
        readonly byte[] _buffer = GC.AllocateUninitializedArray<byte>(bufferBytes);
        long _bytesWritten;

        public long BytesWritten => _bytesWritten;

        public void Advance(int count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (count > _buffer.Length)
            {
                throw new PantsResourceLimitException(
                    $"{resourceName} JSON requested more counting workspace than the maintenance budget allows.");
            }

            try
            {
                _bytesWritten = checked(_bytesWritten + count);
            }
            catch (OverflowException exception)
            {
                throw new PantsResourceLimitException(
                    $"{resourceName} serialization exceeds the memory accounting range.",
                    exception);
            }

            if (_bytesWritten > maximumBytes)
            {
                throw new PantsResourceLimitException(
                    $"Serialized {resourceName} exceeds the {maximumBytes}-byte publication envelope.");
            }
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            ValidateSizeHint(sizeHint);
            return _buffer;
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            ValidateSizeHint(sizeHint);
            return _buffer;
        }

        void ValidateSizeHint(int sizeHint)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
            if (Math.Max(1, sizeHint) > _buffer.Length)
            {
                throw new PantsResourceLimitException(
                    $"{resourceName} JSON needs a {sizeHint}-byte token workspace; " +
                    $"the bounded workspace is {_buffer.Length} bytes.");
            }
        }
    }
}

sealed class CloudMetadataSerializedBytes(byte[] bytes, IDisposable reservation) : IDisposable
{
    IDisposable? _reservation = reservation;

    public ReadOnlyMemory<byte> Bytes { get; } = bytes;

    public void Dispose() => Interlocked.Exchange(ref _reservation, null)?.Dispose();
}

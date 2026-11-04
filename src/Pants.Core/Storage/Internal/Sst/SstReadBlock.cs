using System.Buffers.Binary;
using Cntryl.Pants.Storage.Internal.IO;
using Microsoft.Win32.SafeHandles;

namespace Cntryl.Pants.Storage.Internal.Sst;

sealed class SstReadBlock(byte[] content, IDisposable? reservation) : IDisposable
{
    IDisposable? _reservation = reservation;
    public byte[] Content { get; } = content;
    public void Dispose() => Interlocked.Exchange(ref _reservation, null)?.Dispose();

    public static SstReadBlock Read(SafeFileHandle file, long length, SstBlockHandle handle, ResourceBudget? budget)
    {
        ValidateHandle(length, handle);
        using var encodedClaim = budget?.Reserve(checked((long)handle.Size));
        var encoded = PositionalFile.ReadExactly(file, checked((long)handle.Offset), checked((int)handle.Size));
        return Decode(encoded, budget);
    }

    public static async ValueTask<SstReadBlock> ReadAsync(
        IAsyncSstSource source, long length, SstBlockHandle handle, ResourceBudget? budget, CancellationToken cancellationToken)
    {
        ValidateHandle(length, handle);
        using var encodedClaim = budget?.Reserve(checked((long)handle.Size));
        var encoded = await source.ReadExactlyAsync(
            checked((long)handle.Offset), checked((int)handle.Size), cancellationToken).ConfigureAwait(false);
        return Decode(encoded, budget);
    }

    static SstReadBlock Decode(byte[] encoded, ResourceBudget? budget)
    {
        if (budget is null)
        {
            return new SstReadBlock(
                SstCodec.ReadBlock(encoded, new SstBlockHandle(0, checked((ulong)encoded.Length))), null);
        }
        if (BinaryPrimitives.ReadUInt32LittleEndian(encoded) != encoded.Length - 4)
        {
            throw new PantsCorruptionException("SST block length does not match its handle.");
        }
        var decodedSize = SstBlockCodec.GetDecodedSize(encoded.AsSpan(4));
        var decodedClaim = budget?.Reserve(decodedSize);
        try
        {
            return new SstReadBlock(
                SstCodec.ReadBlock(encoded, new SstBlockHandle(0, checked((ulong)encoded.Length))), decodedClaim);
        }
        catch
        {
            decodedClaim?.Dispose();
            throw;
        }
    }

    static void ValidateHandle(long length, SstBlockHandle handle)
    {
        if (handle.Offset > (ulong)length || handle.Size < 9 || handle.Size > int.MaxValue ||
            handle.Size > (ulong)length - handle.Offset)
        {
            throw new PantsCorruptionException("SST block handle is outside the file.");
        }
    }
}

using System.Buffers.Binary;
using Cntryl.Pants.Storage.Internal.IO;
using Microsoft.Win32.SafeHandles;

namespace Cntryl.Pants.Storage.Internal.Sst;

/// <summary>
///     Admission precedes native reads, decompression and parsing. Retained claims follow the
///     physical reader, including escaped leases, rather than cache membership.
/// </summary>
sealed class SstMetadataAllocation : IDisposable
{
    readonly ResourceBudget _budget;
    readonly List<(ResourceBudget.Reservation Claim, long Bytes)> _claims = [];
    long _reserved;

    public SstMetadataAllocation(ResourceBudget budget, string name)
    {
        _budget = budget;
        Reserve(checked(512 + name.Length * 2L));
    }

    public void Reserve(long bytes)
    {
        var claim = _budget.ReserveOwned(bytes);
        _claims.Add((claim, bytes));
        _reserved = checked(_reserved + bytes);
    }

    public void Retain(long bytes)
    {
        if (bytes > _reserved)
        {
            Reserve(bytes - _reserved);
        }

        var retainedTotal = bytes;
        for (var index = 0; index < _claims.Count; index++)
        {
            var (claim, size) = _claims[index];
            var retained = Math.Min(bytes, size);
            claim.ShrinkTo(retained);
            _claims[index] = (claim, retained);
            bytes -= retained;
        }
        _reserved = retainedTotal;
    }

    public byte[] ReadBlock(SafeFileHandle file, long fileLength, SstBlockHandle handle)
    {
        ValidateHandle(fileLength, handle);
        using var encodedClaim = _budget.Reserve(checked((long)handle.Size));
        var encoded = PositionalFile.ReadExactly(file, checked((long)handle.Offset), checked((int)handle.Size));
        return Decode(encoded);
    }

    public async ValueTask<byte[]> ReadBlockAsync(
        IAsyncSstSource source, long fileLength, SstBlockHandle handle, CancellationToken cancellationToken)
    {
        ValidateHandle(fileLength, handle);
        using var encodedClaim = _budget.Reserve(checked((long)handle.Size));
        var encoded = await source.ReadExactlyAsync(
            checked((long)handle.Offset), checked((int)handle.Size), cancellationToken).ConfigureAwait(false);
        return Decode(encoded);
    }

    byte[] Decode(byte[] encoded)
    {
        var size = BinaryPrimitives.ReadUInt32LittleEndian(encoded);
        if (size != encoded.Length - 4)
        {
            throw new PantsCorruptionException("SST block length does not match its handle.");
        }

        var decodedSize = SstBlockCodec.GetDecodedSize(encoded.AsSpan(4));
        // Index/list/key and range/list/key allocations are bounded by this conservative parse
        // workspace. Trie traversal additionally admits each reconstructed prefix before allocation.
        Reserve(checked(decodedSize * 8L + 512));
        return SstCodec.ReadBlock(encoded, new SstBlockHandle(0, checked((ulong)encoded.Length)));
    }

    static void ValidateHandle(long fileLength, SstBlockHandle handle)
    {
        if (handle.Offset > (ulong)fileLength || handle.Size < 9 || handle.Size > int.MaxValue ||
            handle.Size > (ulong)fileLength - handle.Offset)
        {
            throw new PantsCorruptionException("SST block handle is outside the file.");
        }
    }

    public void Dispose()
    {
        foreach (var (claim, _) in _claims)
        {
            claim.Dispose();
        }
        _claims.Clear();
    }
}

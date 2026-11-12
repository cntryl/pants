namespace Cntryl.Pants.Exceptions;

/// <summary>A safe family drop found committed data in the active memtable.</summary>
public sealed class PantsUnflushedDataPresentException : PantsException
{
    public PantsUnflushedDataPresentException(uint columnFamilyId, long unflushedBytes)
        : base(PantsErrorCode.UnflushedDataPresent,
            $"Column family {columnFamilyId} has {unflushedBytes} committed bytes that have not been flushed.")
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(unflushedBytes);
        ColumnFamilyId = columnFamilyId;
        UnflushedBytes = unflushedBytes;
    }

    public uint ColumnFamilyId { get; }

    public long UnflushedBytes { get; }
}

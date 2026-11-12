namespace Cntryl.Pants.Storage.Internal.Sst;

sealed record SstEntry(
    byte[] Key,
    byte[]? Value,
    ulong Sequence,
    ulong? Expiration,
    bool IsDelete)
{
    readonly byte _entryType;

    public byte EntryType
    {
        get => IsDelete ? (byte)2 : _entryType;
        init => _entryType = value;
    }
}

namespace Cntryl.Pants.Storage.Internal.Recovery;

sealed record IntentLogState(List<IntentEntry> Entries, ulong Sequence, uint Checksum, byte[] Bytes, int ValidLength)
{
    public int FileLength => Bytes.Length;

    public ReadOnlySpan<byte> VerifiedPrefix => Bytes.AsSpan(0, ValidLength);
}

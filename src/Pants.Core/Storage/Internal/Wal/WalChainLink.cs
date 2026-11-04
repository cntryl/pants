namespace Cntryl.Pants.Storage.Internal.Wal;

sealed record WalChainLink(ulong Id, ulong LastSeq, byte[] Digest)
{
    public static WalChainLink Genesis => new(0, 0, new byte[16]);

    public bool Matches(WalChainLink other) =>
        Id == other.Id && LastSeq == other.LastSeq && Digest.AsSpan().SequenceEqual(other.Digest);
}

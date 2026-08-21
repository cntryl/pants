namespace Pants;

internal sealed class DeleteRange
{
    public DeleteRange(byte[] start, byte[] endExclusive)
    {
        Start = start;
        EndExclusive = endExclusive;
    }

    public byte[] Start { get; }

    public byte[] EndExclusive { get; }
}

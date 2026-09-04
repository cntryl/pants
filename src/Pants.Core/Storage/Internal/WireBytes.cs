namespace Cntryl.Pants.Storage.Internal;

static class WireBytes
{
    public static byte[] Copy(ReadOnlyMemory<byte> source) => source.ToArray();
}

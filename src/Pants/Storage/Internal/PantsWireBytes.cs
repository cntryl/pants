namespace Cntryl.Pants.Storage.Internal;

static class PantsWireBytes
{
    public static byte[] Copy(ReadOnlyMemory<byte> source) => source.ToArray();
}

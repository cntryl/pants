namespace Pants;

internal static class PantsWireBytes
{
    public static byte[] Copy(ReadOnlyMemory<byte> source) => source.ToArray();
}

namespace Cntryl.Pants;

internal sealed class SstBlockCacheEntry(byte[] content)
{
    private readonly byte[] _content = content;

    public int SizeBytes => _content.Length;

    public ReadOnlyMemory<byte> Content => _content;

    public bool ContainsKey(ReadOnlySpan<byte> key) =>
        MidgeSstCodec.DataBlockContainsKey(_content, key);
}

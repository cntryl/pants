namespace Pants;

internal sealed class SstBlockCacheEntry(byte[] content)
{
    private readonly byte[] _content = content;

    public int SizeBytes => _content.Length;

    public ReadOnlyMemory<byte> Content => _content;
}

namespace Cntryl.Pants.Storage.Internal.Cache;

sealed class SstBlockCacheEntry(byte[] content)
{
    readonly byte[] _content = content;

    public int SizeBytes => _content.Length;

    public ReadOnlyMemory<byte> Content => _content;

    public bool ContainsKey(ReadOnlySpan<byte> key) =>
        SstCodec.DataBlockContainsKey(_content, key);
}

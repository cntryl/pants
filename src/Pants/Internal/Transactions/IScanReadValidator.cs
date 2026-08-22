namespace Pants;

internal interface IScanReadValidator : IDisposable
{
    void ValidateKey(ReadOnlySpan<byte> key);

    void Complete();
}

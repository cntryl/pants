namespace Cntryl.Pants.Transactions.Internal;

interface IScanReadValidator : IDisposable
{
    void ValidateKey(ReadOnlySpan<byte> key);

    void Complete();
}

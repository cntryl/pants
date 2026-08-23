using System.Text;

namespace Cntryl.Pants.Benches.Tier1;

static class BenchmarkData
{
    public static byte[] Key(int index) => Encoding.UTF8.GetBytes($"user:{index:D8}:profile");

    public static byte[] Value(int size, byte seed = 0x5a)
    {
        var value = GC.AllocateUninitializedArray<byte>(size);
        value.AsSpan().Fill(seed);
        return value;
    }
}

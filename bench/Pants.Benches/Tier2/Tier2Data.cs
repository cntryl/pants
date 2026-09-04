using System.Text;

namespace Cntryl.Pants.Tier2;

static class Tier2Data
{
    public static byte[] Key(int index) => Encoding.UTF8.GetBytes($"key:{index:D10}");

    public static byte[] Value(int size, int seed = 0)
    {
        var value = GC.AllocateUninitializedArray<byte>(size);
        value.AsSpan().Fill(checked((byte)(seed % 251)));
        return value;
    }
}

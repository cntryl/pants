using System.Buffers.Binary;

namespace Cntryl.Pants.Benches.Tier4;

static class Tier4Data
{
    public static byte[] Key(int index)
    {
        var key = new byte[16];
        BinaryPrimitives.WriteInt64BigEndian(key.AsSpan(8), index);
        return key;
    }

    public static byte[] Value(int size, int seed = 0)
    {
        var value = GC.AllocateUninitializedArray<byte>(size);
        value.AsSpan().Fill(checked((byte)(seed % 251)));
        return value;
    }
}

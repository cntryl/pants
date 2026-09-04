using System.Buffers.Binary;

namespace Cntryl.Pants.Tier3;

static class Tier3Data
{
    public static byte[] Key(int index, byte prefix = 0x7a)
    {
        var key = new byte[16];
        key[0] = prefix;
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

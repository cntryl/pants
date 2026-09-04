using System.Text;

namespace Cntryl.Pants.Support.TestDoubles;

static class TestBytes
{
    public static byte[] FromString(string value) => Encoding.UTF8.GetBytes(value);

    public static string ToText(ReadOnlyMemory<byte> value) => Encoding.UTF8.GetString(value.Span);
}

namespace Cntryl.Pants;

internal sealed class ByteArrayComparer : IComparer<byte[]>, IEqualityComparer<byte[]>
{
    public static ByteArrayComparer Instance { get; } = new();

    private ByteArrayComparer()
    {
    }

    public int Compare(byte[]? x, byte[]? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return y is null ? 0 : -1;
        }

        if (y is null)
        {
            return 1;
        }

        int length = Math.Min(x.Length, y.Length);
        for (int index = 0; index < length; index++)
        {
            int comparison = x[index].CompareTo(y[index]);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return x.Length.CompareTo(y.Length);
    }

    public bool Equals(byte[]? x, byte[]? y) => Compare(x, y) == 0;

    public int GetHashCode(byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var hash = new HashCode();
        foreach (byte item in value)
        {
            hash.Add(item);
        }

        return hash.ToHashCode();
    }
}

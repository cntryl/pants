namespace Cntryl.Pants;

internal sealed class KeyStructureProfiler
{
    private const int TrackedPrefixLength = 4;
    private const int MaximumTrackedPrefixes = 4096;
    private readonly Dictionary<byte[], int> _prefixFrequencies = new(ByteArrayComparer.Instance);
    private readonly long[] _byteFrequencies = new long[256];
    private byte[]? _firstKey;
    private byte[]? _previousKey;
    private long _sharedPrefixTotal;
    private int _maximumSharedPrefix;
    private long _totalBytes;
    private long _totalKeyBytes;
    private UInt128 _totalKeyBytesSquared;
    private int _commonPrefixLength;
    private int _keyCount;

    public void Add(ReadOnlySpan<byte> key)
    {
        if (key.IsEmpty)
        {
            return;
        }

        if (_previousKey is not null)
        {
            int shared = CommonPrefixLength(_previousKey, key);
            _sharedPrefixTotal = checked(_sharedPrefixTotal + shared);
            _maximumSharedPrefix = Math.Max(_maximumSharedPrefix, shared);
        }

        if (_firstKey is null)
        {
            _firstKey = key.ToArray();
            _commonPrefixLength = key.Length;
        }
        else
        {
            _commonPrefixLength = Math.Min(
                _commonPrefixLength,
                CommonPrefixLength(_firstKey, key));
        }

        byte[] prefix = key[..Math.Min(TrackedPrefixLength, key.Length)].ToArray();
        if (_prefixFrequencies.TryGetValue(prefix, out int frequency))
        {
            _prefixFrequencies[prefix] = checked(frequency + 1);
        }
        else if (_prefixFrequencies.Count < MaximumTrackedPrefixes)
        {
            _prefixFrequencies.Add(prefix, 1);
        }

        foreach (byte value in key)
        {
            _byteFrequencies[value] = checked(_byteFrequencies[value] + 1);
            _totalBytes = checked(_totalBytes + 1);
        }

        _keyCount = checked(_keyCount + 1);
        _totalKeyBytes = checked(_totalKeyBytes + key.Length);
        _totalKeyBytesSquared = checked(
            _totalKeyBytesSquared + ((UInt128)(uint)key.Length * (uint)key.Length));
        _previousKey = key.ToArray();
    }

    public KeyStructureProfile Finish()
    {
        if (_keyCount == 0)
        {
            return new KeyStructureProfile(0, 0, 0, 0, 0, 0, [], 0);
        }

        float averageSharedPrefix = _keyCount > 1
            ? (float)_sharedPrefixTotal / (_keyCount - 1)
            : 0;
        float meanLength = (float)_totalKeyBytes / _keyCount;
        float meanLengthSquared = (float)_totalKeyBytesSquared / _keyCount;
        float standardDeviation = MathF.Sqrt(MathF.Max(
            0F,
            meanLengthSquared - (meanLength * meanLength)));
        return new KeyStructureProfile(
            averageSharedPrefix,
            _maximumSharedPrefix,
            _prefixFrequencies.Count,
            CalculateEntropy(),
            _commonPrefixLength,
            standardDeviation,
            _prefixFrequencies
                .OrderByDescending(static pair => pair.Value)
                .ThenBy(static pair => pair.Key, ByteArrayComparer.Instance)
                .Take(10)
                .Select(static pair => new KeyStructurePrefixHeat(pair.Key.ToArray(), pair.Value))
                .ToArray(),
            _keyCount);
    }

    private float CalculateEntropy()
    {
        if (_totalBytes == 0)
        {
            return 0;
        }

        float entropy = 0;
        foreach (long count in _byteFrequencies)
        {
            if (count == 0)
            {
                continue;
            }

            float probability = (float)count / _totalBytes;
            entropy -= probability * MathF.Log2(probability);
        }

        return entropy;
    }

    private static int CommonPrefixLength(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        int length = Math.Min(left.Length, right.Length);
        var index = 0;
        while (index < length && left[index] == right[index])
        {
            index++;
        }

        return index;
    }
}

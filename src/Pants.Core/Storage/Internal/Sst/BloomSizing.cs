namespace Cntryl.Pants.Storage.Internal.Sst;

/// <summary>
///     Sizes SST block blooms the way the reference engine's bloom writer does, so a Pants writer
///     produces the same bit count and hash count for the same key count.
/// </summary>
static class BloomSizing
{
    const int MinimumBits = 64;
    const byte MinimumHashFunctions = 1;
    const byte MaximumHashFunctions = 8;

    /// <summary>
    ///     m = ceil(-n * ln(p) / ln(2)^2), floored at 64 bits. An empty filter stays at the 64-bit floor.
    /// </summary>
    internal static int CalculateBitSize(int keyCount, double falsePositiveRate)
    {
        if (keyCount == 0)
        {
            return MinimumBits;
        }

        var bits = Math.Ceiling(
            -keyCount * Math.Log(falsePositiveRate) / Math.Pow(Math.Log(2), 2));
        return checked((int)Math.Max(MinimumBits, bits));
    }

    /// <summary>
    ///     k = round((m / n) * ln(2)), clamped to [1, 8]. An empty filter uses a single probe.
    /// </summary>
    internal static byte CalculateHashFunctions(int numberOfBits, int keyCount)
    {
        if (keyCount == 0 || numberOfBits == 0)
        {
            return MinimumHashFunctions;
        }

        var rounded = Math.Round(
            (double)numberOfBits / keyCount * Math.Log(2),
            MidpointRounding.AwayFromZero);
        return (byte)Math.Clamp(rounded, MinimumHashFunctions, MaximumHashFunctions);
    }
}

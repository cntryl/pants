namespace Cntryl.Pants.Storage.Internal.Accounting;

static class MetadataAccountingArithmetic
{
    public const int CounterCount = 25;
    public const int BucketCount = 18;
    public const int LatencyBins = 80;
    public const int SizeBins = 64;

    public static bool Add(ref ulong target, ulong value)
    {
        if (value > ulong.MaxValue - target)
        {
            target = ulong.MaxValue;
            return false;
        }

        target += value;
        return true;
    }

    public static ulong Nanoseconds(TimeSpan elapsed, out bool valid)
    {
        valid = elapsed.Ticks >= 0 && (ulong)elapsed.Ticks <= ulong.MaxValue / 100;
        return valid ? (ulong)elapsed.Ticks * 100 : ulong.MaxValue;
    }

    public static int LatencyBin(ulong nanoseconds)
    {
        if (nanoseconds < 16_000_000)
        {
            return (int)(nanoseconds / 250_000);
        }

        for (var index = 64; index < LatencyBins - 1; index++)
        {
            if (nanoseconds < (16_000_000UL << (index - 63)))
            {
                return index;
            }
        }

        return LatencyBins - 1;
    }
}

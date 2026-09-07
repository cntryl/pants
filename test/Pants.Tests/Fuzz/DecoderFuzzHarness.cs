using System.Buffers.Binary;
using System.Diagnostics;
using Xunit.Sdk;

namespace Cntryl.Pants.Fuzz;

/// <summary>
///     Drives a persisted-format decoder over deterministically mutated inputs and requires that
///     every one of them either decodes or fails closed.
/// </summary>
/// <remarks>
///     Focused tests pin the invariants someone thought to write down. This explores the
///     combinations nobody enumerated — truncations, tampered length and count fields, flipped
///     checksum bits — against decoders that in production parse bytes from disk or from a cloud
///     object, neither of which is trustworthy after a crash or a partial write.
///     Deterministic by construction: a fixed seed and a fixed iteration budget, so a failure is
///     reproducible from the reported seed and the suite's duration does not vary between runs.
/// </remarks>
static class DecoderFuzzHarness
{
    /// <summary>
    ///     Iterations per seed input. Raised by the scheduled lane through
    ///     <c>PANTS_FUZZ_ITERATIONS</c>; the default keeps pull-request runs short and fixed.
    /// </summary>
    public static int Iterations =>
        int.TryParse(
            Environment.GetEnvironmentVariable("PANTS_FUZZ_ITERATIONS"),
            out var configured) && configured > 0
            ? configured
            : 64;

    /// <summary>
    ///     Longest a single decode may run before it is treated as a non-terminating loop.
    /// </summary>
    static TimeSpan PerInputBudget => TimeSpan.FromSeconds(5);

    public static void Explore(
        string target,
        IReadOnlyList<byte[]> seeds,
        Action<byte[]> decode,
        int randomSeed = 20260907)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentNullException.ThrowIfNull(decode);
        Assert.NotEmpty(seeds);

        for (var seedIndex = 0; seedIndex < seeds.Count; seedIndex++)
        {
            var seed = seeds[seedIndex];
            Probe(target, seed, seed, decode, randomSeed, seedIndex, -1);
            for (var iteration = 0; iteration < Iterations; iteration++)
            {
                var random = new Random(MixSeed(randomSeed, seedIndex, iteration));
                var mutated = Mutate(seed, random);
                Probe(target, seed, mutated, decode, randomSeed, seedIndex, iteration);
            }
        }
    }

    internal static int MixSeed(int randomSeed, int seedIndex, int iteration)
    {
        unchecked
        {
            var hash = 2166136261U;
            hash = (hash ^ (uint)randomSeed) * 16777619U;
            hash = (hash ^ (uint)seedIndex) * 16777619U;
            hash = (hash ^ (uint)iteration) * 16777619U;
            return (int)hash;
        }
    }

    static void Probe(
        string target,
        byte[] seed,
        byte[] input,
        Action<byte[]> decode,
        int randomSeed,
        int seedIndex,
        int iteration)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            decode(input);
        }
        catch (PantsException)
        {
            // Failing closed with a typed error is the contract for untrusted bytes.
        }
        catch (Exception exception)
        {
            throw new XunitException(
                $"{target}: decoding produced {exception.GetType().Name} rather than a typed " +
                $"failure. Reproduce with seed {randomSeed}, input {seedIndex}, iteration " +
                $"{iteration}.{Environment.NewLine}" +
                $"Seed bytes:  {Convert.ToHexString(seed)}{Environment.NewLine}" +
                $"Input bytes: {Convert.ToHexString(input)}{Environment.NewLine}" +
                $"{exception}");
        }

        var elapsed = Stopwatch.GetElapsedTime(started);
        if (elapsed > PerInputBudget)
        {
            throw new XunitException(
                $"{target}: decoding {input.Length} bytes took {elapsed}, which exceeds the " +
                $"{PerInputBudget} per-input budget. Reproduce with seed {randomSeed}, input " +
                $"{seedIndex}, iteration {iteration}.{Environment.NewLine}" +
                $"Input bytes: {Convert.ToHexString(input)}");
        }
    }

    /// <summary>
    ///     Applies one shape of corruption a real disk or object store can produce.
    /// </summary>
    static byte[] Mutate(byte[] seed, Random random)
    {
        if (seed.Length == 0)
        {
            return seed;
        }

        var mutated = seed.ToArray();
        return random.Next(6) switch
        {
            // A flipped bit anywhere, including inside a checksum or a magic prefix.
            0 => FlipBit(mutated, random),

            // A torn tail, the classic outcome of a partial write.
            1 => mutated[..random.Next(1, mutated.Length)],

            // A declared length or count blown up to something the payload cannot hold.
            2 => OverwriteWord(mutated, random, uint.MaxValue),

            // The same field zeroed, which several decoders treat as a special case.
            3 => OverwriteWord(mutated, random, 0),

            // Trailing bytes after an otherwise valid record.
            4 => [.. mutated, .. RandomBytes(random)],

            // A whole run replaced, standing in for a misdirected write.
            _ => Splice(mutated, random)
        };
    }

    static byte[] FlipBit(byte[] bytes, Random random)
    {
        var index = random.Next(bytes.Length);
        bytes[index] ^= (byte)(1 << random.Next(8));
        return bytes;
    }

    static byte[] OverwriteWord(byte[] bytes, Random random, uint value)
    {
        if (bytes.Length < sizeof(uint))
        {
            return bytes;
        }

        var offset = random.Next(bytes.Length - sizeof(uint) + 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
        return bytes;
    }

    static byte[] Splice(byte[] bytes, Random random)
    {
        var start = random.Next(bytes.Length);
        var length = random.Next(1, Math.Min(32, bytes.Length - start + 1));
        random.NextBytes(bytes.AsSpan(start, length));
        return bytes;
    }

    static byte[] RandomBytes(Random random)
    {
        var extra = new byte[random.Next(1, 32)];
        random.NextBytes(extra);
        return extra;
    }
}

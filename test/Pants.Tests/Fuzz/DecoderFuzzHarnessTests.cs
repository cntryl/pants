using Xunit.Sdk;

namespace Cntryl.Pants.Fuzz;

/// <summary>
///     Proves the harness reports the defects it exists to find. A fuzz target that cannot fail is
///     worse than no target at all: it reports coverage it does not have.
/// </summary>
public sealed class DecoderFuzzHarnessTests
{
    static readonly byte[][] Seeds = [[1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12]];

    [Fact]
    public void ShouldUseAStableSeedMix()
    {
        Assert.Equal(-1220586514, DecoderFuzzHarness.MixSeed(20260907, 3, 17));
    }

    [Fact]
    public void ShouldReportADecoderThatThrowsAnUntypedException()
    {
        var failure = Assert.Throws<XunitException>(() =>
            DecoderFuzzHarness.Explore(
                "synthetic",
                Seeds,
                static bytes =>
                {
                    // Stands in for the real defect class: a length read from the input used as an
                    // index without being validated against the buffer.
                    _ = bytes[bytes[0] % 4 == 0 ? bytes.Length + 1 : 0];
                }));

        Assert.Contains("IndexOutOfRangeException", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Reproduce with seed", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Input bytes:", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ShouldAcceptADecoderThatFailsClosed()
    {
        DecoderFuzzHarness.Explore(
            "synthetic",
            Seeds,
            static _ => throw new PantsCorruptionException("Rejected."));
    }

    /// <summary>
    ///     The reported reproduction has to actually reproduce, or a failure is not actionable.
    /// </summary>
    [Fact]
    public void ShouldProduceAReproducibleFailure()
    {
        static string Run()
        {
            try
            {
                DecoderFuzzHarness.Explore(
                    "synthetic",
                    Seeds,
                    static bytes => _ = bytes[bytes[0] % 4 == 0 ? bytes.Length + 1 : 0]);
                return "no failure";
            }
            catch (XunitException exception)
            {
                return exception.Message;
            }
        }

        Assert.Equal(Run(), Run());
    }
}

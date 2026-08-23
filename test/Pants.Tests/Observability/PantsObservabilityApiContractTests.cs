namespace Cntryl.Pants.Tests;

public sealed class PantsObservabilityApiContractTests
{
    [Fact]
    public async Task ShouldRejectStorageVerificationGivenInMemoryDatabase()
    {
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());

        var error = await Assert.ThrowsAsync<PantsNotSupportedException>(
            () => database.VerifyStorageAsync(TimeSpan.FromSeconds(5)).AsTask());

        Assert.Contains("no persistent path", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ShouldExposeExplicitMemtableSizeGivenRuntimeMetrics()
    {
        const long sizeLimit = 128 * 1024;
        await using var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.InMemory().WithMemtableLimits(sizeLimit));

        var metrics = await database.GetRuntimeMetricsAsync();

        Assert.Equal(sizeLimit, metrics.MemtableSizeLimitBytes);
        Assert.Equal(sizeLimit, metrics.MemtableFlushThresholdBytes);
        Assert.Equal(0, metrics.MaximumMemtableWalSegmentGap);
    }

    [Fact]
    public async Task ShouldExposeExplicitMemtableLimitsGivenRuntimeMetrics()
    {
        const long sizeLimit = 256 * 1024;
        const long flushThreshold = 128 * 1024;
        await using var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.InMemory().WithMemtableLimits(sizeLimit, flushThreshold));

        var metrics = await database.GetRuntimeMetricsAsync();

        Assert.Equal(sizeLimit, metrics.MemtableSizeLimitBytes);
        Assert.Equal(flushThreshold, metrics.MemtableFlushThresholdBytes);
        Assert.Equal(0, metrics.MaximumMemtableWalSegmentGap);
    }
}

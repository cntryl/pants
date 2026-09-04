using Cntryl.Pants.Support.Failpoints;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Runtime;

public sealed class PantsStartupResidueCleanupTests
{
    static readonly TimeSpan AssertionTimeout = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task ShouldKeepHealthyWhenCloudRecoveryCleanupFails()
    {
        using var directory = new TemporaryDirectory();
        var options = CreateOptions(directory.Path);
        await CreateDatabaseAsync(options);
        var residue = Path.Combine(directory.Path, "cloud_recovery", "nested", "stale.sst");
        Directory.CreateDirectory(Path.GetDirectoryName(residue)!);
        await File.WriteAllTextAsync(residue, "stale");
        var failpoint = new NthStartupResidueDeleteFailpointHandler(1);

        await using var reopened = await OpenAsync(options, failpoint);

        Assert.True(File.Exists(residue));
        Assert.Equal(1, failpoint.FailureCount);
        Assert.Equal(PantsEngineHealth.Healthy, (await reopened.Diagnostics.GetRuntimeMetricsAsync()).Health);
    }

    [Fact]
    public async Task ShouldKeepHealthyWhenSstTemporaryCleanupFails()
    {
        using var directory = new TemporaryDirectory();
        var options = CreateOptions(directory.Path);
        await CreateDatabaseAsync(options);
        var residue = Path.Combine(directory.Path, "sst", "orphan.sst.tmp");
        await File.WriteAllTextAsync(residue, "stale");
        var failpoint = new NthStartupResidueDeleteFailpointHandler(2);

        await using var reopened = await OpenAsync(options, failpoint);

        Assert.True(File.Exists(residue));
        Assert.Equal(1, failpoint.FailureCount);
        Assert.Equal(PantsEngineHealth.Healthy, (await reopened.Diagnostics.GetRuntimeMetricsAsync()).Health);
    }

    [Fact]
    public async Task ShouldKeepHealthyWhenRootTemporaryCleanupFails()
    {
        using var directory = new TemporaryDirectory();
        var options = CreateOptions(directory.Path);
        await CreateDatabaseAsync(options);
        var residue = Path.Combine(directory.Path, "startup-residue.tmp");
        await File.WriteAllTextAsync(residue, "stale");
        var failpoint = new NthStartupResidueDeleteFailpointHandler(2);

        await using var reopened = await OpenAsync(options, failpoint);

        Assert.True(File.Exists(residue));
        Assert.Equal(1, failpoint.FailureCount);
        Assert.Equal(PantsEngineHealth.Healthy, (await reopened.Diagnostics.GetRuntimeMetricsAsync()).Health);
    }

    [Fact]
    public async Task ShouldReportDegradedWhenFlushStagingCleanupFails()
    {
        using var directory = new TemporaryDirectory();
        var options = CreateOptions(directory.Path);
        await CreateDatabaseAsync(options);
        var residue = Path.Combine(
            directory.Path,
            "sst",
            ".flush-staging",
            "nested",
            "unpublished.sst");
        Directory.CreateDirectory(Path.GetDirectoryName(residue)!);
        await File.WriteAllTextAsync(residue, "stale");
        var failpoint = new NthStartupResidueDeleteFailpointHandler(1);

        await using (var reopened = await OpenAsync(options, failpoint))
        {
            Assert.True(File.Exists(residue));
            Assert.Equal(1, failpoint.FailureCount);
            Assert.Equal(PantsEngineHealth.Degraded, (await reopened.Diagnostics.GetRuntimeMetricsAsync()).Health);
        }

        await using var healed = await PantsDatabase.OpenAsync(options);
        Assert.False(File.Exists(residue));
        Assert.Equal(PantsEngineHealth.Healthy, (await healed.Diagnostics.GetRuntimeMetricsAsync()).Health);
    }

    [Fact]
    public async Task ShouldReportDegradedWhenOrphanSstCleanupFails()
    {
        using var directory = new TemporaryDirectory();
        var options = CreateOptions(directory.Path);
        await CreateDatabaseAsync(options);
        var residue = Path.Combine(directory.Path, "sst", "orphan.sst");
        await File.WriteAllTextAsync(residue, "stale");
        var failpoint = new NthStartupResidueDeleteFailpointHandler(2);

        await using (var reopened = await OpenAsync(options, failpoint))
        {
            Assert.True(File.Exists(residue));
            Assert.Equal(1, failpoint.FailureCount);
            Assert.Equal(PantsEngineHealth.Degraded, (await reopened.Diagnostics.GetRuntimeMetricsAsync()).Health);
        }

        await using var healed = await PantsDatabase.OpenAsync(options);
        Assert.False(File.Exists(residue));
        Assert.Equal(PantsEngineHealth.Healthy, (await healed.Diagnostics.GetRuntimeMetricsAsync()).Health);
    }

    static PantsOpenOptions CreateOptions(string path) =>
        PantsOpenOptions.Local(path).WithBackgroundCompaction(false);

    static async Task CreateDatabaseAsync(PantsOpenOptions options)
    {
        await using var database = await PantsDatabase.OpenAsync(options);
        await database.ShutdownAsync(AssertionTimeout);
    }

    static ValueTask<IPantsDatabase> OpenAsync(
        PantsOpenOptions options,
        IFailpointHandler failpoints) =>
        PantsDatabase.OpenForTestingAsync(
            options,
            new RuntimeDependencies(failpoints));
}

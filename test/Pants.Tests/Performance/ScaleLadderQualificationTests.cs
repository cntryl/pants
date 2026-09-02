using Cntryl.Pants.Benches.Tier4;

namespace Cntryl.Pants.Tests.Performance;

public sealed class ScaleLadderQualificationTests
{
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public async Task ShouldRejectNonPositiveRecordCounts(string recordCount)
    {
        var exitCode = await ScaleLadderRunner.RunAsync([recordCount]);

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public void ShouldPrependTheAssemblyWhenTheCurrentProcessIsTheDotnetMuxer()
    {
        var start = ScaleLadderChildProcess.CreateForTesting(
            "/usr/local/share/dotnet/dotnet",
            null,
            "/tmp/Cntryl.Pants.Benches.dll",
            "child-mode",
            "argument");

        Assert.Equal("/usr/local/share/dotnet/dotnet", start.FileName);
        Assert.Equal(
            ["/tmp/Cntryl.Pants.Benches.dll", "child-mode", "argument"],
            start.ArgumentList);
    }

    [Fact]
    public void ShouldNotPrependTheAssemblyWhenTheCurrentProcessIsTheBenchmarkAppHost()
    {
        var start = ScaleLadderChildProcess.CreateForTesting(
            "/tmp/Cntryl.Pants.Benches",
            null,
            "/tmp/Cntryl.Pants.Benches.dll",
            "child-mode");

        Assert.Equal("/tmp/Cntryl.Pants.Benches", start.FileName);
        Assert.Equal(["child-mode"], start.ArgumentList);
    }

    [Fact]
    public void ShouldModelOnePrimaryRecordAndThreeLookupIndexesPerAddress()
    {
        var mutations = ScaleLadderRunner.CreateMutationsForRecord(42);

        Assert.Equal(4, ScaleLadderRunner.AddressIndexEntryMultiplier);
        Assert.Equal(ScaleLadderRunner.AddressIndexEntryMultiplier, mutations.Count);
        Assert.Equal(mutations.Count, mutations.Select(static mutation => mutation.Key).Distinct(
            ByteArrayComparer.Instance).Count());
        Assert.Equal(150, mutations[0].Value.Length);
        Assert.All(mutations.Skip(1), static mutation => Assert.Equal(sizeof(long), mutation.Value.Length));
    }

    [Fact]
    public void ShouldIncludeWalAndSstBytesInWriteAmplification()
    {
        var amplification = ScaleLadderRunner.CalculateWriteAmplification(
            logicalBytes: 100,
            sstBytesWritten: 200,
            walBytesWritten: 50);

        Assert.Equal(2.5, amplification);
    }

    [Fact]
    public void ShouldDescribeCacheStateAndPrefixCardinalityPrecisely()
    {
        Assert.Equal("Block-cache-cold (OS page cache not reset)", ScaleLadderRunner.ColdCacheQualifier);
        Assert.Contains("1,000-key group", ScaleLadderRunner.PrefixScanMetricName(warm: false));
        Assert.Contains("1,000-key group", ScaleLadderRunner.PrefixScanMetricName(warm: true));
        var query = ScaleLadderRunner.CreatePrefixQueryForGroup(42);
        Assert.NotNull(query.Prefix);
        Assert.Null(query.StartInclusive);
        Assert.Null(query.EndExclusive);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void ShouldFailQualificationWhenAnyRecoveryCheckFails(
        bool reopenCorrect,
        bool crashRecoveryPassed)
    {
        Assert.Equal(1, ScaleLadderRunner.ExitCodeFor(reopenCorrect, crashRecoveryPassed));
    }

    [Fact]
    public async Task ShouldLaunchTheFrameworkDependentReopenProbeWithTheTierBudget()
    {
        using var directory = new TemporaryDirectory();
        const long budgetBytes = 64L * 1024 * 1024;
        var options = PantsOpenOptions.Local(directory.Path)
            .WithBackgroundCompaction(false)
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(budgetBytes));
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            await using var transaction = await database.BeginTransactionAsync(
                database.DefaultColumnFamily,
                PantsTransactionMode.ReadWrite);
            foreach (var mutation in ScaleLadderRunner.CreateMutationsForRecord(0))
            {
                transaction.Put(mutation.Key, mutation.Value);
            }

            await transaction.CommitAsync(PantsWriteOptions.Sync);
            await database.FlushAsync(database.DefaultColumnFamily);
        }

        var result = await ScaleLadderReopenProbe.RunAsync(directory.Path, 1, budgetBytes);

        Assert.True(result.Success, result.Detail);
        Assert.True(result.PeakRssBytes > 0);
        Assert.Equal(budgetBytes, result.ConfiguredBudgetBytes);
    }

    [Fact]
    public async Task ShouldRecoverAcknowledgedWalAfterAnAbruptChildProcessCrash()
    {
        var result = await ScaleLadderCrashCheck.RunAsync(recordCount: 16);

        Assert.True(result.Success, result.Detail);
    }
}

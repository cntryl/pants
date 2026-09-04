using Cntryl.Pants.Support.TestDoubles;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Pants.Options;

public sealed class PantsCompactionOptionsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShouldHonorBackgroundEnabledFromTheLatestCompactionConfiguration(bool enabled)
    {
        var original = PantsOpenOptions.InMemory().WithBackgroundCompaction(!enabled);
        var configuration = new PantsCompactionConfiguration(L0FileCountTrigger: 9, BackgroundEnabled: enabled);

        var options = original.WithCompaction(configuration);
        var plan = RuntimePlan.Resolve(options);

        Assert.Equal(configuration, options.Compaction);
        Assert.Equal(enabled, plan.BackgroundCompaction);
        Assert.Equal(9, plan.Compaction.L0FileCountTrigger);
        Assert.Equal(!enabled, original.Compaction.BackgroundEnabled);
        Assert.Equal(
            RuntimePlan.Resolve(PantsOpenOptions.Create(new PantsStorageConfiguration.InMemory(), compaction: configuration)).Compaction,
            plan.Compaction);
    }

    [Theory]
    [InlineData(PantsPerformanceGoal.Throughput, PantsWorkloadProfile.WriteHeavy, false, false, 8)]
    [InlineData(PantsPerformanceGoal.Throughput, PantsWorkloadProfile.WriteHeavy, true, false, 8)]
    [InlineData(PantsPerformanceGoal.Latency, PantsWorkloadProfile.Mixed, false, false, 3)]
    [InlineData(PantsPerformanceGoal.Latency, PantsWorkloadProfile.Mixed, true, false, 3)]
    [InlineData(PantsPerformanceGoal.Throughput, PantsWorkloadProfile.Mixed, false, false, 6)]
    [InlineData(PantsPerformanceGoal.Throughput, PantsWorkloadProfile.WriteHeavy, false, true, 9)]
    [InlineData(PantsPerformanceGoal.Latency, PantsWorkloadProfile.Mixed, true, true, 9)]
    public async Task ShouldPreserveDerivedCompactionDefaultsUnlessBoundSettingsSpecifyThem(
        PantsPerformanceGoal goal,
        PantsWorkloadProfile workload,
        bool backgroundEnabled,
        bool explicitCompaction,
        int expectedTrigger)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Pants:PerformanceGoal"] = goal.ToString(),
            ["Pants:WorkloadProfile"] = workload.ToString(),
            ["Pants:BackgroundCompaction"] = backgroundEnabled ? "true" : "false"
        };
        if (explicitCompaction)
        {
            settings["Pants:Compaction:L0FileCountTrigger"] = "9";
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var factory = new CapturingPantsDatabaseFactory();
        var services = new ServiceCollection();
        services.AddSingleton<IPantsDatabaseFactory>(factory);
        services.AddSingleton<IConfiguration>(configuration);
        services.AddPants().BindConfiguration("Pants");
        await using var serviceProvider = services.BuildServiceProvider();
        _ = await serviceProvider.GetRequiredService<IPantsDatabaseProvider>().GetDatabaseAsync();

        var options = Assert.IsType<PantsOpenOptions>(factory.Options);
        var plan = RuntimePlan.Resolve(options);

        Assert.Equal(expectedTrigger, plan.Compaction.L0FileCountTrigger);
        Assert.Equal(backgroundEnabled, plan.BackgroundCompaction);
        Assert.Equal(backgroundEnabled, options.Compaction.BackgroundEnabled);
        Assert.Equal(explicitCompaction, options.ConfiguredCompaction is not null);
    }
}

using Cntryl.Pants.Support.TestDoubles;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Pants.Options;

public sealed class PantsWriteBackpressureOptionsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    public async Task ShouldRejectInvalidResolvedLimitsBeforeCreatingStorage(int invalidCase)
    {
        using var directory = new TemporaryDirectory();
        var delay = TimeSpan.FromMilliseconds(1);
        var policy = invalidCase switch
        {
            0 => new PantsWriteBackpressureConfiguration(L0SoftLimit: 0, MaximumAdmissionDelay: delay),
            1 => new PantsWriteBackpressureConfiguration(L0SoftLimit: 14, MaximumAdmissionDelay: delay),
            2 => new PantsWriteBackpressureConfiguration(MemorySoftLimitBytes: 0, MaximumAdmissionDelay: delay),
            3 => new PantsWriteBackpressureConfiguration(MemorySoftLimitBytes: 8192, MaximumAdmissionDelay: delay),
            4 => new PantsWriteBackpressureConfiguration(PendingCompactionSoftLimitBytes: 0,
                PendingCompactionHardLimitBytes: 2, MaximumAdmissionDelay: delay),
            5 => new PantsWriteBackpressureConfiguration(PendingCompactionSoftLimitBytes: 1, MaximumAdmissionDelay: delay),
            6 => new PantsWriteBackpressureConfiguration(PendingCompactionSoftLimitBytes: 2,
                PendingCompactionHardLimitBytes: 2, MaximumAdmissionDelay: delay),
            7 => new PantsWriteBackpressureConfiguration(PendingCompactionHardLimitBytes: 0),
            8 => new PantsWriteBackpressureConfiguration(BackgroundWriteBytesPerSecond: 0),
            9 => new PantsWriteBackpressureConfiguration(L0SoftLimit: 1),
            10 => new PantsWriteBackpressureConfiguration(MaximumAdmissionDelay: -delay),
            _ => new PantsWriteBackpressureConfiguration(MaximumAdmissionDelay: TimeSpan.MaxValue)
        };
        var options = PantsOpenOptions.Local(directory.Path).WithMemtableLimits(8192, 4096)
            .WithCompaction(new PantsCompactionConfiguration(L0FileCountTrigger: 3))
            .WithWriteBackpressure(policy);

        await Assert.ThrowsAsync<PantsInvalidArgumentException>(() => PantsDatabase.OpenAsync(options).AsTask());

        Assert.False(File.Exists(Path.Combine(directory.Path, "FORMAT")));
    }

    [Fact]
    public async Task ShouldBindAllControlsIntoTheEffectiveEnginePolicy()
    {
        using var directory = new TemporaryDirectory();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Pants:Storage:Kind"] = "Local",
            ["Pants:Storage:Path"] = directory.Path,
            ["Pants:WriteBackpressure:L0SoftLimit"] = "1",
            ["Pants:WriteBackpressure:MemorySoftLimitBytes"] = "2",
            ["Pants:WriteBackpressure:PendingCompactionSoftLimitBytes"] = "3",
            ["Pants:WriteBackpressure:PendingCompactionHardLimitBytes"] = "4",
            ["Pants:WriteBackpressure:MaximumAdmissionDelay"] = "00:00:00.001",
            ["Pants:WriteBackpressure:BackgroundWriteBytesPerSecond"] = "1048576"
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddPants().BindConfiguration("Pants");
        await using var provider = services.BuildServiceProvider();

        var database = await provider.GetRequiredService<IPantsDatabaseProvider>().GetDatabaseAsync();

        var expected = new PantsWriteBackpressureConfiguration(1, 2, 3, 4, TimeSpan.FromMilliseconds(1), 1048576);
        Assert.Equal(expected, database.Options.WriteBackpressure);
        Assert.Equal(expected, PantsOpenOptionsResolver.Resolve(database.Options).WriteBackpressure);
        await using var transaction = await database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put("bound"u8.ToArray(), "value"u8.ToArray());
        await transaction.CommitAsync(PantsWriteOptions.Sync);
    }
}

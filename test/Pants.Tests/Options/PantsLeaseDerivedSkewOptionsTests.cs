using Cntryl.Pants.Support.TestDoubles;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Pants.Options;

public sealed class PantsLeaseDerivedSkewOptionsTests
{
    [Theory]
    [InlineData(10, false)]
    [InlineData(60, false)]
    [InlineData(60, true)]
    public async Task ShouldKeepBoundUnspecifiedSkewDerivedAndAnExplicitOverrideStable(int ttl, bool explicitSkew)
    {
        using var directory = new TemporaryDirectory();
        var values = new Dictionary<string, string?>
        {
            ["Pants:Storage:Kind"] = "Local",
            ["Pants:Storage:Path"] = directory.Path,
            ["Pants:LeaseTimeToLive"] = TimeSpan.FromSeconds(ttl).ToString("c")
        };
        if (explicitSkew) { values["Pants:LeaseClockSkewTolerance"] = "00:00:02"; }
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        services.AddPants().BindConfiguration("Pants");
        await using var provider = services.BuildServiceProvider();
        var database = await provider.GetRequiredService<IPantsDatabaseProvider>().GetDatabaseAsync();

        Assert.Equal(TimeSpan.FromSeconds(explicitSkew ? 2 : ttl / 2), database.Options.Lease.ClockSkewTolerance);
        Assert.Equal(TimeSpan.FromSeconds(explicitSkew ? 2 : 5), database.Options.WithLeaseTimeToLive(TimeSpan.FromSeconds(10))
            .Lease.ClockSkewTolerance);
        await using var transaction = await database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put("bound"u8.ToArray(), "value"u8.ToArray());
        await transaction.CommitAsync(PantsWriteOptions.Sync);
    }
}

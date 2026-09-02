using Cntryl.Pants.Destroyer.Support;
using Cntryl.Pants.Transactions;

namespace Cntryl.Pants.Destroyer.Scenarios;

/// <summary>
/// Ported from midge-destroyer's <c>sqrzl-visibility</c> scenario:
/// cloud-only, faults <see cref="FaultClass.ProviderLatencySpike"/>,
/// <see cref="FaultClass.RegionPartition"/>, <see cref="FaultClass.LeaseStalenessWindow"/>,
/// <see cref="FaultExpectation.TemporarilyUnavailable"/>. Simplified from
/// midge-destroyer's Sqrzl-emulator cross-process visibility check to an
/// in-process one against Pants's simulated-cloud mode: a write committed
/// with <see cref="PantsWriteOptions.CloudStrict"/> must be immediately
/// visible to a fresh read-only transaction, not just to the writer that
/// made it.
/// </summary>
public sealed class SqrzlVisibilityScenarioTests
{
    [Fact]
    public async Task ShouldBeImmediatelyVisibleGivenCloudStrictCommit()
    {
        using var directory = DestroyerDatabase.CreateTempDirectory("pants-destroyer-sqrzl-visibility");
        await using var database = await PantsDatabase.OpenAsync(
            DestroyerCloud.CreateOptions(directory.Path, "sqrzl-visibility/", localBudgetBytes: 8 * 1024 * 1024));

        await DestroyerDatabase.PutAsync(
            database, database.DefaultColumnFamily, "visible-key", "visible-value", PantsWriteOptions.CloudStrict);

        await using var reader = await database.BeginTransactionAsync(
            database.DefaultColumnFamily, PantsTransactionMode.ReadOnly);
        var value = await reader.GetAsync("visible-key"u8.ToArray());

        Assert.True(value is not null, "CloudStrict commit was not visible to a fresh reader");
        Assert.Equal("visible-value", System.Text.Encoding.UTF8.GetString(value!.Value.Span));
    }
}

using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Contracts;

public sealed class PantsReadPathDiagnosticsContractTests
{
    [Fact]
    public async Task ShouldKeepReadPathDiagnosticsUnchangedGivenIdleWindow()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path));

        var before = await database.Diagnostics.GetReadPathDiagnosticsAsync();
        var after = await database.Diagnostics.GetReadPathDiagnosticsAsync();

        Assert.Equal(before, after);
    }

    [Fact]
    public async Task ShouldAvoidDataBlockReadGivenPersistedBloomRejectsMissingKey()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false));
        await using (var writer = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            for (var index = 0; index < 400; index += 2)
            {
                writer.Put(TestBytes.FromString($"key-{index:0000}"), "present"u8.ToArray());
            }

            await writer.CommitAsync(PantsWriteOptions.BestEffort);
        }

        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        var observedReject = false;
        for (var index = 1; index < 399; index += 2)
        {
            var before = await database.Diagnostics.GetRuntimeMetricsAsync();
            await using var reader = await database.Transactions.BeginAsync(
                database.ColumnFamilies.DefaultFamily,
                PantsTransactionMode.ReadOnly);
            Assert.Null(await reader.GetAsync(TestBytes.FromString($"key-{index:0000}")));
            var after = await database.Diagnostics.GetRuntimeMetricsAsync();
            if (after.SstBloomRejectsTotal <= before.SstBloomRejectsTotal)
            {
                continue;
            }

            Assert.True(after.SstBloomChecksTotal > before.SstBloomChecksTotal);
            Assert.Equal(before.SstDataBlocksReadTotal, after.SstDataBlocksReadTotal);
            observedReject = true;
            break;
        }

        Assert.True(observedReject);
    }
}

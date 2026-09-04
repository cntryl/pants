using Cntryl.Pants.Support.Failpoints;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

public sealed class PantsCloudRuntimeDeadlineTests
{
    static readonly TimeSpan AssertionTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ShouldFinishAcceptedCloudStrictDurabilityAfterCallerAbandonsResponse()
    {
        using var directory = new TemporaryDirectory();
        using var failpoint = new BlockingCloudWalUploadFailpointHandler();
        var options = PantsOpenOptions.SimulatedCloud(
                directory.Path,
                "pants-tests",
                "runtime-deadline/")
            .WithStorageTimeout(TimeSpan.FromMilliseconds(5))
            .WithRuntimeResponseTimeout(TimeSpan.FromMilliseconds(100));
        var database = await PantsDatabase.OpenForTestingAsync(
            options,
            new RuntimeDependencies(failpoint));

        await using (var transaction = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            transaction.Put("accepted-key"u8.ToArray(), "accepted-value"u8.ToArray());
            var commit = transaction.CommitAsync(PantsWriteOptions.CloudStrict).AsTask();
            await failpoint.WaitUntilEnteredAsync(AssertionTimeout);

            try
            {
                var exception = await Assert.ThrowsAsync<PantsTimeoutException>(() =>
                    commit.WaitAsync(AssertionTimeout));
                Assert.Contains("outcome is unknown", exception.Message, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("accepted-key", exception.Message, StringComparison.Ordinal);
                Assert.DoesNotContain("accepted-value", exception.Message, StringComparison.Ordinal);
            }
            finally
            {
                failpoint.Release();
            }
        }

        await database.ShutdownAsync(AssertionTimeout);
        await database.DisposeAsync();

        await using var reopened = await PantsDatabase.OpenAsync(options);
        await using var read = await reopened.Transactions.BeginAsync(
            reopened.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Equal(
            "accepted-value"u8.ToArray(),
            (await read.GetAsync("accepted-key"u8.ToArray()))?.ToArray());
    }

}

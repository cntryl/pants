using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

public sealed class PantsCloudDurableRecoveryCutTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldConfirmReservedHolesBelowTheRecoveredOpenWalWhenItsEntirePredecessorIsAcknowledged(bool provider)
    {
        using var directory = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        var options = Options(directory.Path, provider);
        var dependencies = new RuntimeDependencies(cloudHttpClient: client);
        await using (var database = await PantsDatabase.OpenForTestingAsync(options, dependencies))
        {
            await database.ShutdownAsync(TestTimeouts.Expected);
        }
        await using var reopened = await PantsDatabase.OpenForTestingAsync(options, dependencies);
        var open = WalLedgerStore.LoadExisting(Path.Combine(directory.Path, "wal")).Active.Open;
        Assert.True(open.FirstSeq > 1);
        var holeEnd = open.FirstSeq - 1;
        Assert.Equal(0UL, open.Prev.LastSeq);
        await using var reader = await reopened.Transactions.BeginAsync(reopened.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        Assert.Null(await reader.GetAsync("absent"u8.ToArray()));
        var target = await reader.CommitWithSequenceAsync(PantsWriteOptions.CloudAsync);
        Assert.Equal(0UL, target);
        Assert.True(await reopened.Cloud!.GetDurableSequenceAsync() >= holeEnd);
        Assert.True(await reopened.Cloud.WaitForDurableAsync(holeEnd, TimeSpan.Zero));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldRestoreTheConfirmedCutAfterPublishedWalIsRetiredIntoAuthoritativeSsts(bool provider)
    {
        using var directory = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        var options = Options(directory.Path, provider);
        var dependencies = new RuntimeDependencies(cloudHttpClient: client);
        ulong confirmed;
        await using (var database = await PantsDatabase.OpenForTestingAsync(options, dependencies))
        {
            await using var write = await database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite);
            write.Put("key"u8.ToArray(), "published"u8.ToArray());
            confirmed = await write.CommitWithSequenceAsync(PantsWriteOptions.CloudStrict);
            Assert.True(await database.Cloud!.GetDurableSequenceAsync() >= confirmed);
            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
            await database.ShutdownAsync(TestTimeouts.Expected);
        }
        await using var reopened = await PantsDatabase.OpenForTestingAsync(options, dependencies);
        await using var reader = await reopened.Transactions.BeginAsync(reopened.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        Assert.Equal("published"u8.ToArray(), (await reader.GetAsync("key"u8.ToArray()))?.ToArray());
        Assert.True(await reopened.Cloud!.GetDurableSequenceAsync() >= confirmed);
        Assert.True(await reopened.Cloud.WaitForDurableAsync(confirmed, TimeSpan.Zero));
        Assert.Equal(confirmed, await reader.CommitWithSequenceAsync(PantsWriteOptions.CloudAsync));
    }

    static PantsOpenOptions Options(string path, bool provider) => (provider
            ? PantsOpenOptions.Cloud(path, new PantsCloudStorageLocation(
                new PantsAzureBlobProvider("account", "container", new Uri("https://storage.example.test"),
                    new PantsAzureCredentialSource.SasToken("sig=test")), "recovered-cut"))
            : PantsOpenOptions.SimulatedCloud(path, "pants-tests", "recovered-cut/"))
        .WithBackgroundCompaction(false);
}

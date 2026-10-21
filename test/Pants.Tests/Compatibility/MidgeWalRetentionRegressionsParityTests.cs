using System.Text.Json;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Compatibility;

public sealed class MidgeWalRetentionRegressionsParityTests
{
    [Fact]
    public async Task ShouldRetireCloudWalWhenEveryOrdinaryWriteIsFlushed()
    {
        var retained = await RetireHistoryAsync(new Pin.None());

        Assert.Equal(0, retained);
    }

    [Fact]
    public async Task ShouldRetireCloudWalWhenEmptyRangeHasNoReplayableEffect()
    {
        var retained = await RetireHistoryAsync(new Pin.EmptyRange());

        Assert.Equal(0, retained);
    }

    [Fact]
    public async Task ShouldRetireCloudWalWhenFlushedFamilyIsDropped()
    {
        var retained = await RetireHistoryAsync(new Pin.DroppedFamily(DiscardUnflushed: false));

        Assert.Equal(0, retained);
    }

    [Fact]
    public async Task ShouldRetireCloudWalWhenUnflushedFamilyIsDiscarded()
    {
        var retained = await RetireHistoryAsync(new Pin.DroppedFamily(DiscardUnflushed: true));

        Assert.Equal(0, retained);
    }

    [Fact]
    public async Task ShouldRetireProviderWalWhenUnflushedFamilyIsDiscarded()
    {
        using var cache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        var options = PantsOpenOptions.Cloud(cache.Path, CreateProviderLocation())
            .WithBackgroundCompaction(false);
        await using var database = await PantsDatabase.OpenForTestingAsync(
            options,
            new RuntimeDependencies(cloudHttpClient: client));
        var defaultFamily = database.ColumnFamilies.DefaultFamily;

        await CommitValueAsync(database, defaultFamily, "blocker"u8.ToArray());
        var discarded = await database.ColumnFamilies.CreateAsync("drop-me");
        await CommitValueAsync(database, discarded, "seed"u8.ToArray());
        await database.ColumnFamilies.DropDiscardingUnflushedAsync(discarded);

        for (var index = 0; index < 8; index++)
        {
            await CommitValueAsync(
                database,
                defaultFamily,
                System.Text.Encoding.UTF8.GetBytes($"later-{index}"));
            await database.Maintenance.FlushAsync(defaultFamily);
        }

        await TestWait.UntilAsync(
            () => ReadProviderCatalogSegments(handler).Length == 0,
            "provider WAL retirement removes every covered catalog segment");
    }

    static async Task<int> RetireHistoryAsync(Pin pin)
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.SimulatedCloud(directory.Path, "pants-tests", "wal-retention/")
            .WithBackgroundCompaction(false);
        await using var database = await PantsDatabase.OpenAsync(options);
        var defaultFamily = database.ColumnFamilies.DefaultFamily;

        await CommitValueAsync(database, defaultFamily, "blocker"u8.ToArray());

        var family = pin is Pin.DroppedFamily
            ? await database.ColumnFamilies.CreateAsync("drop-me")
            : defaultFamily;
        await using (var transaction = await database.Transactions.BeginAsync(
                         family,
                         PantsTransactionMode.ReadWrite))
        {
            transaction.Put("seed"u8.ToArray(), "value"u8.ToArray());
            if (pin is Pin.EmptyRange)
            {
                transaction.Put("r"u8.ToArray(), "kept"u8.ToArray());
                transaction.DeleteRange("r"u8.ToArray(), "r"u8.ToArray());
            }

            await transaction.CommitAsync(PantsWriteOptions.CloudStrict);
        }

        switch (pin)
        {
            case Pin.DroppedFamily { DiscardUnflushed: true }:
                await database.ColumnFamilies.DropDiscardingUnflushedAsync(family);
                break;
            case Pin.DroppedFamily:
                await database.Maintenance.FlushAsync(family);
                await database.ColumnFamilies.DropAsync(family);
                break;
            default:
                await database.Maintenance.FlushAsync(family);
                break;
        }

        for (var index = 0; index < 8; index++)
        {
            await CommitValueAsync(
                database,
                defaultFamily,
                System.Text.Encoding.UTF8.GetBytes($"later-{index}"));
            await database.Maintenance.FlushAsync(defaultFamily);
        }

        if (pin is Pin.EmptyRange)
        {
            await using var reader = await database.Transactions.BeginAsync(
                defaultFamily,
                PantsTransactionMode.ReadOnly);
            Assert.Equal("kept", TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(
                await reader.GetAsync("r"u8.ToArray()))));
        }

        await TestWait.UntilAsync(
            () => ReadCatalogSegments(directory.Path) == 0,
            "cloud WAL retirement removes every covered catalog segment");

        return ReadCatalogSegments(directory.Path);
    }

    static async Task CommitValueAsync(
        IPantsDatabase database,
        IPantsColumnFamily family,
        ReadOnlyMemory<byte> key)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            family,
            PantsTransactionMode.ReadWrite);
        transaction.Put(key, "value"u8.ToArray());
        await transaction.CommitAsync(PantsWriteOptions.CloudStrict);
    }

    static int ReadCatalogSegments(string root)
    {
        using var catalog = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(
            root,
            "cloud_store",
            "wal",
            "publication-catalog.v1.json")));
        return catalog.RootElement.GetProperty("segments").EnumerateObject().Count();
    }

    static JsonElement[] ReadProviderCatalogSegments(InMemoryAzureBlobHandler handler)
    {
        using var catalog = JsonDocument.Parse(
            handler.GetObjectText("/wal/publication-catalog.v1.json"));
        return catalog.RootElement.GetProperty("segments").EnumerateObject()
            .Select(static property => property.Value.Clone())
            .ToArray();
    }

    static PantsCloudStorageLocation CreateProviderLocation() => new(
        new PantsAzureBlobProvider(
            "account",
            "container",
            new Uri("https://storage.example.test"),
            new PantsAzureCredentialSource.SasToken("sig=test")),
        "database");

    abstract record Pin
    {
        public sealed record None : Pin;

        public sealed record EmptyRange : Pin;

        public sealed record DroppedFamily(bool DiscardUnflushed) : Pin;
    }
}

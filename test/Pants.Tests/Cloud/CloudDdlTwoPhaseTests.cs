using System.Text.Json;

namespace Cntryl.Pants.Tests;

public sealed class CloudDdlTwoPhaseTests
{
    [Fact]
    public void ShouldEncodeDistinctCompatibleRegistryBytesGivenWriterEpochs()
    {
        var registry = new CloudDdlRegistry();
        var canonical = CloudDdlJson.SerializeRegistry(registry);

        var first = CloudDdlFence.Encode(canonical, writerEpoch: 1);
        var second = CloudDdlFence.Encode(first, writerEpoch: 2);

        Assert.False(first.AsSpan().SequenceEqual(second));
        Assert.Equal(registry.Epoch, CloudDdlJson.DeserializeRegistry(first).Epoch);
        Assert.Equal(registry.Epoch, CloudDdlJson.DeserializeRegistry(second).Epoch);
    }

    [Fact]
    public async Task ShouldAppendCreateAndDropToAuthoritativeRegistryGivenNormalCloudDdl()
    {
        using var directory = new TemporaryDirectory();
        var options = CreateOptions(directory.Path);
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            _ = await database.CreateColumnFamilyAsync("two-phase");
        }

        await using (var reopened = await PantsDatabase.OpenAsync(options))
        {
            var active = Assert.IsAssignableFrom<IPantsColumnFamily>(
                await reopened.GetColumnFamilyAsync("two-phase"));
            await reopened.DropColumnFamilyAsync(active);
        }

        await using (var finalOpen = await PantsDatabase.OpenAsync(options))
        {
            Assert.Null(await finalOpen.GetColumnFamilyAsync("two-phase"));
        }

        using var registry = ReadRegistry(directory.Path);
        Assert.Equal(2UL, registry.RootElement.GetProperty("epoch").GetUInt64());
        var operations = registry.RootElement.GetProperty("operations").EnumerateArray().ToArray();
        Assert.Equal(2, operations.Length);
        Assert.Equal(2, operations.Select(static operation =>
            operation.GetProperty("op_id").GetString()).Distinct(StringComparer.Ordinal).Count());
        Assert.True(operations[0].GetProperty("edit").TryGetProperty(
            "CreateColumnFamily",
            out _));
        Assert.True(operations[1].GetProperty("edit").TryGetProperty(
            "DropColumnFamilyAt",
            out _));
    }

    [Fact]
    public async Task ShouldAbortPreparedCreateGivenRemoteCasFailureWhenRetrying()
    {
        using var directory = new TemporaryDirectory();
        var failpoints = new DdlFailpointHandler("BeforeDdlRemoteCas");
        await using var database = await PantsDatabase.OpenForTestingAsync(
            CreateOptions(directory.Path),
            new PantsRuntimeDependencies(failpoints));

        await Assert.ThrowsAnyAsync<PantsException>(
            () => database.CreateColumnFamilyAsync("cas-retry").AsTask());
        Assert.Null(await database.GetColumnFamilyAsync("cas-retry"));
        Assert.True(File.Exists(Path.Combine(directory.Path, "ddl.prepare.json")));

        var created = await database.CreateColumnFamilyAsync("cas-retry");

        Assert.Equal("cas-retry", created.Name);
        Assert.False(File.Exists(Path.Combine(directory.Path, "ddl.prepare.json")));
        using var registry = ReadRegistry(directory.Path);
        Assert.Equal(1UL, registry.RootElement.GetProperty("epoch").GetUInt64());
        Assert.Single(registry.RootElement.GetProperty("operations").EnumerateArray());
    }

    [Fact]
    public async Task ShouldConvergeDdlStateGivenRemoteCasFailureWhenReopening()
    {
        using var directory = new TemporaryDirectory();
        var options = CreateOptions(directory.Path);
        var failpoints = new DdlFailpointHandler("BeforeDdlRemoteCas");
        await using (var database = await PantsDatabase.OpenForTestingAsync(
                         options,
                         new PantsRuntimeDependencies(failpoints)))
        {
            await Assert.ThrowsAnyAsync<PantsException>(
                () => database.CreateColumnFamilyAsync("cas-reopen").AsTask());
            Assert.Null(await database.GetColumnFamilyAsync("cas-reopen"));
            Assert.True(File.Exists(Path.Combine(directory.Path, "ddl.prepare.json")));
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);
        Assert.False(File.Exists(Path.Combine(directory.Path, "ddl.prepare.json")));
        Assert.Null(await reopened.GetColumnFamilyAsync("cas-reopen"));

        var created = await reopened.CreateColumnFamilyAsync("cas-reopen");

        Assert.Equal("cas-reopen", created.Name);
        using var registry = ReadRegistry(directory.Path);
        Assert.Equal(1UL, registry.RootElement.GetProperty("epoch").GetUInt64());
        Assert.Single(registry.RootElement.GetProperty("operations").EnumerateArray());
    }

    [Fact]
    public async Task ShouldAbortTornDdlPrepareGivenLocalPrepareWithoutRemoteCommitWhenReopening()
    {
        using var directory = new TemporaryDirectory();
        var options = CreateOptions(directory.Path);
        var failpoints = new DdlFailpointHandler("BeforeDdlRemoteCas");
        await using (var database = await PantsDatabase.OpenForTestingAsync(
                         options,
                         new PantsRuntimeDependencies(failpoints)))
        {
            await Assert.ThrowsAnyAsync<PantsException>(
                () => database.CreateColumnFamilyAsync("torn-prepare").AsTask());
            Assert.True(File.Exists(Path.Combine(directory.Path, "ddl.prepare.json")));
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);
        Assert.Null(await reopened.GetColumnFamilyAsync("torn-prepare"));
        Assert.False(File.Exists(Path.Combine(directory.Path, "ddl.prepare.json")));
    }

    [Fact]
    public async Task ShouldReconcileRemoteCreateGivenLocalCommitFailureWhenReopening()
    {
        using var directory = new TemporaryDirectory();
        var options = CreateOptions(directory.Path);
        var failpoints = new DdlFailpointHandler("BeforeDdlLocalCommit");
        await using (var database = await PantsDatabase.OpenForTestingAsync(
                         options,
                         new PantsRuntimeDependencies(failpoints)))
        {
            var created = await database.CreateColumnFamilyAsync("local-retry");

            Assert.Equal("local-retry", created.Name);
            Assert.NotNull(await database.GetColumnFamilyAsync("local-retry"));
            Assert.Equal(
                PantsEngineHealth.Degraded,
                (await database.GetRuntimeMetricsAsync()).Health);
            Assert.True(File.Exists(Path.Combine(directory.Path, "ddl.prepare.json")));
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);
        Assert.NotNull(await reopened.GetColumnFamilyAsync("local-retry"));
        Assert.False(File.Exists(Path.Combine(directory.Path, "ddl.prepare.json")));
        Assert.Equal(PantsEngineHealth.Healthy, (await reopened.GetRuntimeMetricsAsync()).Health);
    }

    [Fact]
    public async Task ShouldFlushRemoteCommittedCreateGivenLocalCommitFailureBeforeReopen()
    {
        using var directory = new TemporaryDirectory();
        var options = CreateOptions(directory.Path);
        var failpoints = new DdlFailpointHandler("BeforeDdlLocalCommit");
        await using (var database = await PantsDatabase.OpenForTestingAsync(
                         options,
                         new PantsRuntimeDependencies(failpoints)))
        {
            var created = await database.CreateColumnFamilyAsync("live-local-retry");
            await using (var transaction = await database.BeginTransactionAsync(
                             created,
                             PantsTransactionMode.ReadWrite))
            {
                transaction.Put("key"u8.ToArray(), "value"u8.ToArray());
                await transaction.CommitAsync(PantsWriteOptions.CloudAsync);
            }

            await database.FlushAsync(created);
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);
        var recovered = Assert.IsAssignableFrom<IPantsColumnFamily>(
            await reopened.GetColumnFamilyAsync("live-local-retry"));
        await using var reader = await reopened.BeginTransactionAsync(
            recovered,
            PantsTransactionMode.ReadOnly);

        Assert.Equal(
            "value",
            TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(
                await reader.GetAsync("key"u8.ToArray()))));
    }

    [Fact]
    public async Task ShouldReplayWalForRemoteCommittedCreateGivenLocalCommitFailureWithoutFlush()
    {
        using var directory = new TemporaryDirectory();
        var options = CreateOptions(directory.Path);
        var failpoints = new DdlFailpointHandler("BeforeDdlLocalCommit");
        await using (var database = await PantsDatabase.OpenForTestingAsync(
                         options,
                         new PantsRuntimeDependencies(failpoints)))
        {
            var created = await database.CreateColumnFamilyAsync("wal-local-retry");
            await using var transaction = await database.BeginTransactionAsync(
                created,
                PantsTransactionMode.ReadWrite);
            transaction.Put("key"u8.ToArray(), "value"u8.ToArray());
            await transaction.CommitAsync(PantsWriteOptions.CloudAsync);
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);
        var recovered = Assert.IsAssignableFrom<IPantsColumnFamily>(
            await reopened.GetColumnFamilyAsync("wal-local-retry"));
        await using var reader = await reopened.BeginTransactionAsync(
            recovered,
            PantsTransactionMode.ReadOnly);

        Assert.Equal(
            "value",
            TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(
                await reader.GetAsync("key"u8.ToArray()))));
    }

    [Fact]
    public async Task ShouldFenceColumnFamilyWhenRemoteDropCommitsBeforeLocalCommit()
    {
        using var directory = new TemporaryDirectory();
        var options = CreateOptions(directory.Path);
        await using (var seed = await PantsDatabase.OpenAsync(options))
        {
            _ = await seed.CreateColumnFamilyAsync("drop-local-failure");
        }

        var failpoints = new DdlFailpointHandler("BeforeDdlLocalCommit");
        await using (var database = await PantsDatabase.OpenForTestingAsync(
                         options,
                         new PantsRuntimeDependencies(failpoints)))
        {
            var active = Assert.IsAssignableFrom<IPantsColumnFamily>(
                await database.GetColumnFamilyAsync("drop-local-failure"));

            await database.DropColumnFamilyAsync(active);

            Assert.Null(await database.GetColumnFamilyAsync("drop-local-failure"));
            Assert.Equal(
                PantsEngineHealth.Degraded,
                (await database.GetRuntimeMetricsAsync()).Health);
            Assert.True(File.Exists(Path.Combine(directory.Path, "ddl.prepare.json")));
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);
        Assert.Null(await reopened.GetColumnFamilyAsync("drop-local-failure"));
        Assert.False(File.Exists(Path.Combine(directory.Path, "ddl.prepare.json")));
    }

    [Fact]
    public async Task ShouldReturnUsableColumnFamilyWhenCreateMetadataMirrorFailsAfterCommit()
    {
        using var directory = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        await using var database = await PantsDatabase.OpenForTestingAsync(
            CreateProviderOptions(directory.Path),
            new PantsRuntimeDependencies(cloudHttpClient: client));
        handler.FailMetadataWrites = true;

        var created = await database.CreateColumnFamilyAsync("mirror-failure");

        var resolved = Assert.IsAssignableFrom<IPantsColumnFamily>(
            await database.GetColumnFamilyAsync("mirror-failure"));
        Assert.Equal(created.Id, resolved.Id);
        Assert.Equal(PantsEngineHealth.Degraded, (await database.GetRuntimeMetricsAsync()).Health);
        handler.FailMetadataWrites = false;
        await using var transaction = await database.BeginTransactionAsync(
            created,
            PantsTransactionMode.ReadWrite);
        transaction.Put("key"u8.ToArray(), "value"u8.ToArray());
        await transaction.CommitAsync(PantsWriteOptions.CloudStrict);
    }

    [Fact]
    public async Task ShouldReportSuccessWhenDropMetadataMirrorFailsAfterAuthoritySwitch()
    {
        using var directory = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        await using var database = await PantsDatabase.OpenForTestingAsync(
            CreateProviderOptions(directory.Path),
            new PantsRuntimeDependencies(cloudHttpClient: client));
        var created = await database.CreateColumnFamilyAsync("drop-mirror-failure");
        handler.FailMetadataWrites = true;

        await database.DropColumnFamilyAsync(created);

        Assert.Null(await database.GetColumnFamilyAsync(created.Name));
        Assert.Equal(PantsEngineHealth.Degraded, (await database.GetRuntimeMetricsAsync()).Health);
        handler.FailMetadataWrites = false;
    }

    [Fact]
    public async Task ShouldReplayRemoteDdlHistoryGivenLocalManifestBehindWithoutPrepare()
    {
        using var directory = new TemporaryDirectory();
        var options = CreateOptions(directory.Path);
        var failpoints = new DdlFailpointHandler("BeforeDdlLocalCommit");
        await using (var database = await PantsDatabase.OpenForTestingAsync(
                         options,
                         new PantsRuntimeDependencies(failpoints)))
        {
            _ = await database.CreateColumnFamilyAsync("history-replay");
        }

        File.Delete(Path.Combine(directory.Path, "ddl.prepare.json"));

        await using var reopened = await PantsDatabase.OpenAsync(options);
        Assert.NotNull(await reopened.GetColumnFamilyAsync("history-replay"));
        Assert.Equal(PantsEngineHealth.Healthy, (await reopened.GetRuntimeMetricsAsync()).Health);
    }

    [Fact]
    public async Task ShouldAdoptRemoteDropGivenLostCasResponseWhenReadbackConfirmsCommit()
    {
        using var directory = new TemporaryDirectory();
        var options = CreateOptions(directory.Path);
        await using var seed = await PantsDatabase.OpenAsync(options);
        var family = await seed.CreateColumnFamilyAsync("lost-response");
        await seed.DisposeAsync();

        var failpoints = new DdlFailpointHandler("AfterDdlRemoteCas");
        await using var database = await PantsDatabase.OpenForTestingAsync(
            options,
            new PantsRuntimeDependencies(failpoints));
        var active = Assert.IsAssignableFrom<IPantsColumnFamily>(
            await database.GetColumnFamilyAsync(family.Name));

        await database.DropColumnFamilyAsync(active);

        Assert.Null(await database.GetColumnFamilyAsync(family.Name));
        Assert.Equal(PantsEngineHealth.Degraded, (await database.GetRuntimeMetricsAsync()).Health);
        Assert.True(File.Exists(Path.Combine(directory.Path, "ddl.prepare.json")));
    }

    [Fact]
    public async Task ShouldFenceWritesGivenLostDropResponseWhenAuthorityReadbackFails()
    {
        using var directory = new TemporaryDirectory();
        var options = CreateOptions(directory.Path);
        await using var seed = await PantsDatabase.OpenAsync(options);
        var family = await seed.CreateColumnFamilyAsync("ambiguous-drop");
        await using (var transaction = await seed.BeginTransactionAsync(
                         family,
                         PantsTransactionMode.ReadWrite))
        {
            transaction.Put("discarded"u8.ToArray(), "value"u8.ToArray());
            await transaction.CommitAsync(PantsWriteOptions.CloudAsync);
        }

        await seed.DisposeAsync();

        var failpoints = new DdlFailpointHandler(
            "AfterDdlRemoteCas",
            "BeforeDdlAuthorityReadback");
        await using var database = await PantsDatabase.OpenForTestingAsync(
            options,
            new PantsRuntimeDependencies(failpoints));
        var active = Assert.IsAssignableFrom<IPantsColumnFamily>(
            await database.GetColumnFamilyAsync(family.Name));

        await Assert.ThrowsAsync<PantsFencedException>(
            () => database.DropColumnFamilyDiscardingUnflushedAsync(active).AsTask());
        Assert.NotNull(await database.GetColumnFamilyAsync(family.Name));
        await using (var transaction = await database.BeginTransactionAsync(
                         active,
                         PantsTransactionMode.ReadWrite))
        {
            transaction.Put("key"u8.ToArray(), "value"u8.ToArray());
            await Assert.ThrowsAsync<PantsFencedException>(
                () => transaction.CommitAsync(PantsWriteOptions.CloudAsync).AsTask());
        }

        await database.DropColumnFamilyAsync(active);

        Assert.Null(await database.GetColumnFamilyAsync(family.Name));
        Assert.False(File.Exists(Path.Combine(directory.Path, "ddl.prepare.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldFencePersistenceMaintenanceGivenUnresolvedDropAuthority(
        bool compact)
    {
        using var directory = new TemporaryDirectory();
        var options = CreateOptions(directory.Path);
        await using var seed = await PantsDatabase.OpenAsync(options);
        var family = await seed.CreateColumnFamilyAsync("ambiguous-maintenance");
        await using (var transaction = await seed.BeginTransactionAsync(
                         family,
                         PantsTransactionMode.ReadWrite))
        {
            transaction.Put("unflushed"u8.ToArray(), "value"u8.ToArray());
            await transaction.CommitAsync(PantsWriteOptions.CloudAsync);
        }

        await seed.DisposeAsync();

        var failpoints = new DdlFailpointHandler(
            "AfterDdlRemoteCas",
            "BeforeDdlAuthorityReadback");
        await using var database = await PantsDatabase.OpenForTestingAsync(
            options,
            new PantsRuntimeDependencies(failpoints));
        var active = Assert.IsAssignableFrom<IPantsColumnFamily>(
            await database.GetColumnFamilyAsync(family.Name));
        await Assert.ThrowsAsync<PantsFencedException>(
            () => database.DropColumnFamilyDiscardingUnflushedAsync(active).AsTask());

        var maintenance = compact
            ? database.CompactAllAsync().AsTask()
            : database.FlushAsync(active).AsTask();

        await Assert.ThrowsAsync<PantsFencedException>(() => maintenance);
    }

    static PantsOpenOptions CreateOptions(string path) =>
        PantsOpenOptions.SimulatedCloud(path, "pants-tests", "ddl/")
            .WithBackgroundCompaction(false);

    static PantsOpenOptions CreateProviderOptions(string path)
    {
        var location = new PantsCloudStorageLocation(
            new PantsCloudProviderConfiguration.AzureBlob(
                "account",
                "container",
                new Uri("https://storage.example.test"),
                new PantsAzureCredentialSource.SasToken("sig=test")),
            "database");
        return PantsOpenOptions.Cloud(path, location).WithBackgroundCompaction(false);
    }

    static JsonDocument ReadRegistry(string root) => JsonDocument.Parse(
        File.ReadAllBytes(Path.Combine(
            root,
            "cloud_store",
            "metadata",
            "ddl.registry.json")));
}

namespace Cntryl.Pants.Tests.Contracts;

public sealed class PantsPersistenceBoundaryParityTests
{
    [Theory]
    [InlineData(nameof(Failpoint.AfterCompactionOutputDurable))]
    [InlineData(nameof(Failpoint.BeforeCompactionManifestPublish))]
    [InlineData(nameof(Failpoint.AfterCompactionManifestPublish))]
    public async Task ShouldRecoverAllDataAtEveryCompactionPublicationBoundary(string failpointName)
    {
        using var directory = new TemporaryDirectory();
        var handler = new OneShotFailpointHandler(Enum.Parse<Failpoint>(failpointName));
        await using (var database = await PantsDatabase.OpenForTestingAsync(
                         PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false),
                         new RuntimeDependencies(handler)))
        {
            for (var index = 0; index < 3; index++)
            {
                await PutAsync(database, $"key-{index}", $"value-{index}");
                await database.FlushAsync(database.DefaultColumnFamily);
            }

            await Assert.ThrowsAnyAsync<PantsException>(() => database.CompactAllAsync().AsTask());
        }

        await using var reopened = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false));
        for (var index = 0; index < 3; index++)
        {
            Assert.Equal($"value-{index}", await ReadAsync(reopened, $"key-{index}"));
        }

        await reopened.CompactAllAsync();
        Assert.Equal(PantsEngineHealth.Healthy, (await reopened.GetRuntimeMetricsAsync()).Health);
    }

    [Theory]
    [InlineData(nameof(Failpoint.BeforeIntentLogReplace))]
    [InlineData(nameof(Failpoint.AfterIntentLogReplace))]
    [InlineData(nameof(Failpoint.BeforeWalRotation))]
    [InlineData(nameof(Failpoint.AfterWalRotation))]
    public async Task ShouldRecoverFlushAtIntentAndWalRotationBoundaries(string failpointName)
    {
        using var directory = new TemporaryDirectory();
        var handler = new OneShotFailpointHandler(Enum.Parse<Failpoint>(failpointName));
        await using (var database = await PantsDatabase.OpenForTestingAsync(
                         PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false),
                         new RuntimeDependencies(handler)))
        {
            await PutAsync(database, "key", "value");
            await Assert.ThrowsAnyAsync<PantsException>(() =>
                database.FlushAsync(database.DefaultColumnFamily).AsTask());
        }

        await using var reopened = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path));
        Assert.Equal("value", await ReadAsync(reopened, "key"));
        await reopened.FlushAsync(reopened.DefaultColumnFamily);
    }

    [Fact]
    public async Task ShouldKeepDdlStateAbsentWhenManifestAppendFails()
    {
        using var directory = new TemporaryDirectory();
        var createHandler = new OneShotFailpointHandler(Failpoint.BeforeManifestJournalAppend);
        await using (var database = await PantsDatabase.OpenForTestingAsync(
                         PantsOpenOptions.Local(directory.Path),
                         new RuntimeDependencies(createHandler)))
        {
            await Assert.ThrowsAnyAsync<PantsException>(() =>
                database.CreateColumnFamilyAsync("failed-create").AsTask());
            Assert.Null(await database.GetColumnFamilyAsync("failed-create"));
        }

        await using var reopened = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path));
        Assert.Null(await reopened.GetColumnFamilyAsync("failed-create"));
        var retained = await reopened.CreateColumnFamilyAsync("retained");
        await reopened.FlushAsync(retained);
    }

    [Theory]
    [InlineData(nameof(Failpoint.AfterManifestJournalAppend))]
    [InlineData(nameof(Failpoint.BeforeManifestJournalSync))]
    [InlineData(nameof(Failpoint.AfterManifestJournalSync))]
    public async Task ShouldRecoverCompleteDdlWhenFailureOccursAfterJournalAppend(string failpointName)
    {
        using var directory = new TemporaryDirectory();
        var handler = new OneShotFailpointHandler(Enum.Parse<Failpoint>(failpointName));
        await using (var database = await PantsDatabase.OpenForTestingAsync(
                         PantsOpenOptions.Local(directory.Path),
                         new RuntimeDependencies(handler)))
        {
            await Assert.ThrowsAnyAsync<PantsException>(() =>
                database.CreateColumnFamilyAsync("recovered-create").AsTask());
        }

        await using var reopened = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path));
        Assert.NotNull(await reopened.GetColumnFamilyAsync("recovered-create"));
    }

    [Fact]
    public async Task ShouldKeepColumnFamilyUsableWhenDropManifestAppendFails()
    {
        using var directory = new TemporaryDirectory();
        await using var seed = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path));
        var seeded = await seed.CreateColumnFamilyAsync("retained");
        await using (var transaction = await seed.BeginTransactionAsync(
                         seeded,
                         PantsTransactionMode.ReadWrite))
        {
            transaction.Put("key"u8.ToArray(), "value"u8.ToArray());
            await transaction.CommitAsync(PantsWriteOptions.Sync);
        }

        await seed.FlushAsync(seeded);
        await seed.DisposeAsync();

        var handler = new OneShotFailpointHandler(Failpoint.BeforeManifestJournalAppend);
        await using (var database = await PantsDatabase.OpenForTestingAsync(
                         PantsOpenOptions.Local(directory.Path),
                         new RuntimeDependencies(handler)))
        {
            var family = Assert.IsAssignableFrom<IPantsColumnFamily>(
                await database.GetColumnFamilyAsync("retained"));
            await Assert.ThrowsAnyAsync<PantsException>(() => database.DropColumnFamilyAsync(family).AsTask());
            await using var reader = await database.BeginTransactionAsync(
                family,
                PantsTransactionMode.ReadOnly);
            Assert.Equal("value", TestBytes.ToText((await reader.GetAsync("key"u8.ToArray()))!.Value));
        }

        await using var reopened = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path));
        Assert.NotNull(await reopened.GetColumnFamilyAsync("retained"));
    }

    [Theory]
    [InlineData(nameof(Failpoint.BeforeManifestCheckpointReplace))]
    [InlineData(nameof(Failpoint.AfterManifestCheckpointReplace))]
    public async Task ShouldRecoverWhenManifestCheckpointPublicationFails(string failpointName)
    {
        using var directory = new TemporaryDirectory();
        await using (var seed = await PantsDatabase.OpenAsync(
                         PantsOpenOptions.Local(directory.Path)))
        {
            await PutAsync(seed, "key", "value");
        }

        var handler = new OneShotFailpointHandler(Enum.Parse<Failpoint>(failpointName));
        await Assert.ThrowsAnyAsync<PantsException>(() => PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Local(directory.Path),
            new RuntimeDependencies(handler)).AsTask());

        await using var recovered = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path));
        Assert.Equal("value", await ReadAsync(recovered, "key"));
    }

    [Fact]
    public async Task ShouldRejectPersistedStateWithoutFormatMarker()
    {
        using var directory = new TemporaryDirectory();
        await using (var database = await PantsDatabase.OpenAsync(
                         PantsOpenOptions.Local(directory.Path)))
        {
            await PutAsync(database, "key", "value");
        }

        File.Delete(Path.Combine(directory.Path, "FORMAT"));

        var error = await Assert.ThrowsAsync<PantsCompatibilityException>(() =>
            PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path)).AsTask());
        Assert.Equal(PantsErrorCode.CompatibilityError, error.Code);
    }

    static async Task PutAsync(IPantsDatabase database, string key, string value)
    {
        await using var transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put(TestBytes.FromString(key), TestBytes.FromString(value));
        await transaction.CommitAsync(PantsWriteOptions.Sync);
    }

    static async Task<string?> ReadAsync(IPantsDatabase database, string key)
    {
        await using var transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        var value = await transaction.GetAsync(TestBytes.FromString(key));
        return value is null ? null : TestBytes.ToText(value.Value);
    }

    sealed class OneShotFailpointHandler(Failpoint target) : IFailpointHandler
    {
        int _triggered;

        public void Hit(Failpoint failpoint)
        {
            if (failpoint == target && Interlocked.Exchange(ref _triggered, 1) == 0)
            {
                throw new IOException($"Injected failure at {failpoint}.");
            }
        }
    }
}

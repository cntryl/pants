using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Contracts;

public sealed class PantsColumnFamilyParityTests
{
    static readonly ColumnFamilyStorageMode[] AllStorageModes =
    [
        ColumnFamilyStorageMode.InMemory,
        ColumnFamilyStorageMode.Local,
        ColumnFamilyStorageMode.SimulatedCloud
    ];

    static readonly ColumnFamilyStorageMode[] DurableStorageModes =
    [
        ColumnFamilyStorageMode.Local,
        ColumnFamilyStorageMode.SimulatedCloud
    ];

    [Fact]
    public async Task ShouldCreateColumnFamilyGivenValidNameWhenEngineOpen()
    {
        foreach (var mode in AllStorageModes)
        {
            using var directory = new TemporaryDirectory();
            await using var database = await OpenAsync(directory.Path, mode);

            var family = await database.ColumnFamilies.CreateAsync("test_cf");

            Assert.Equal("test_cf", family.Name);
            Assert.NotEqual(0u, family.Id);
        }
    }

    [Fact]
    public async Task ShouldCreateMultipleColumnFamiliesGivenUniqueNamesWhenEngineOpen()
    {
        foreach (var mode in AllStorageModes)
        {
            using var directory = new TemporaryDirectory();
            await using var database = await OpenAsync(directory.Path, mode);

            var first = await database.ColumnFamilies.CreateAsync("cf1");
            var second = await database.ColumnFamilies.CreateAsync("cf2");
            var third = await database.ColumnFamilies.CreateAsync("cf3");

            Assert.Equal(["cf1", "cf2", "cf3"], [first.Name, second.Name, third.Name]);
            Assert.Equal(3, new[] { first.Id, second.Id, third.Id }.Distinct().Count());
        }
    }

    [Fact]
    public async Task ShouldReturnExistingColumnFamilyGivenDuplicateNameWhenCreating()
    {
        foreach (var mode in AllStorageModes)
        {
            using var directory = new TemporaryDirectory();
            await using var database = await OpenAsync(directory.Path, mode);
            var existing = await database.ColumnFamilies.CreateAsync("test_cf");

            var duplicate = await database.ColumnFamilies.CreateAsync("test_cf");

            Assert.Equal(existing.Name, duplicate.Name);
            Assert.Equal(existing.Id, duplicate.Id);
            Assert.Equal(2, (await database.ColumnFamilies.ListAsync()).Count);
        }
    }

    [Fact]
    public async Task ShouldRejectDefaultNamedColumnFamilyGivenReservedNameWhenCreating()
    {
        foreach (var mode in AllStorageModes)
        {
            using var directory = new TemporaryDirectory();
            await using var database = await OpenAsync(directory.Path, mode);

            var error = await Assert.ThrowsAsync<PantsInvalidArgumentException>(
                () => database.ColumnFamilies.CreateAsync("default").AsTask());

            Assert.Contains("default", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Single(await database.ColumnFamilies.ListAsync());
            Assert.Equal(0u, database.ColumnFamilies.DefaultFamily.Id);
        }
    }

    [Fact]
    public async Task ShouldCreateColumnFamilyWithCustomConfigGivenConfigWhenCreating()
    {
        foreach (var mode in AllStorageModes)
        {
            using var directory = new TemporaryDirectory();
            await using var database = await OpenAsync(directory.Path, mode, 1024 * 1024);

            var family = await database.ColumnFamilies.CreateAsync("test_cf");
            var metrics = await database.Diagnostics.GetRuntimeMetricsAsync();

            Assert.Equal("test_cf", family.Name);
            Assert.Equal(1024 * 1024, metrics.MemtableSizeLimitBytes);
        }
    }

    [Fact]
    public Task ShouldRejectEmptyColumnFamilyNameBeforeManifestPersistence() =>
        AssertRejectedNameDoesNotPersistAsync(string.Empty);

    [Fact]
    public Task ShouldRejectNulColumnFamilyNameBeforeManifestPersistence() =>
        AssertRejectedNameDoesNotPersistAsync("tenant\0hidden");

    [Fact]
    public Task ShouldRejectOversizedColumnFamilyNameBeforeManifestPersistence() =>
        AssertRejectedNameDoesNotPersistAsync(new string('x', 256));

    [Fact]
    public async Task ShouldAcceptColumnFamilyNameAtMaximumByteLength()
    {
        var maximum = new string('x', 255);
        foreach (var mode in AllStorageModes)
        {
            using var directory = new TemporaryDirectory();
            await using var database = await OpenAsync(directory.Path, mode);

            var family = await database.ColumnFamilies.CreateAsync(maximum);
            var lookedUp = await database.ColumnFamilies.GetAsync(maximum);

            Assert.Equal(maximum, family.Name);
            Assert.NotNull(lookedUp);
            Assert.Equal(family.Id, lookedUp.Id);
        }
    }

    [Fact]
    public async Task ShouldGetColumnFamilyByNameGivenExistingCfWhenQuerying()
    {
        foreach (var mode in AllStorageModes)
        {
            using var directory = new TemporaryDirectory();
            await using var database = await OpenAsync(directory.Path, mode);
            var created = await database.ColumnFamilies.CreateAsync("test_cf");

            var lookedUp = Assert.IsAssignableFrom<IPantsColumnFamily>(
                await database.ColumnFamilies.GetAsync("test_cf"));

            Assert.Equal("test_cf", lookedUp.Name);
            Assert.Equal(created.Id, lookedUp.Id);
            Assert.NotEqual(0u, lookedUp.Id);
        }
    }

    [Fact]
    public async Task ShouldReturnNoneGivenNonexistentColumnFamilyNameWhenQuerying()
    {
        foreach (var mode in AllStorageModes)
        {
            using var directory = new TemporaryDirectory();
            await using var database = await OpenAsync(directory.Path, mode);

            Assert.Null(await database.ColumnFamilies.GetAsync("nonexistent"));
        }
    }

    [Fact]
    public async Task ShouldGetDefaultColumnFamilyGivenFreshEngineWhenQuerying()
    {
        foreach (var mode in AllStorageModes)
        {
            using var directory = new TemporaryDirectory();
            await using var database = await OpenAsync(directory.Path, mode);

            var family = Assert.IsAssignableFrom<IPantsColumnFamily>(
                await database.ColumnFamilies.GetAsync("default"));

            Assert.Equal("default", family.Name);
            Assert.Equal(0u, family.Id);
            Assert.Equal(family.Id, database.ColumnFamilies.DefaultFamily.Id);
        }
    }

    [Fact]
    public async Task ShouldListDefaultCfOnlyGivenNoCustomCfsWhenListing()
    {
        foreach (var mode in AllStorageModes)
        {
            using var directory = new TemporaryDirectory();
            await using var database = await OpenAsync(directory.Path, mode);

            var families = await database.ColumnFamilies.ListAsync();

            var family = Assert.Single(families);
            Assert.Equal("default", family.Name);
            Assert.Equal(0u, family.Id);
        }
    }

    [Fact]
    public async Task ShouldListAllColumnFamiliesGivenMultipleCfsWhenListing()
    {
        foreach (var mode in AllStorageModes)
        {
            using var directory = new TemporaryDirectory();
            await using var database = await OpenAsync(directory.Path, mode);
            await database.ColumnFamilies.CreateAsync("cf1");
            await database.ColumnFamilies.CreateAsync("cf2");

            var families = await database.ColumnFamilies.ListAsync();
            var names = families.Select(static family => family.Name).ToArray();

            Assert.Equal(3, families.Count);
            Assert.Contains("default", names);
            Assert.Contains("cf1", names);
            Assert.Contains("cf2", names);
        }
    }

    [Fact]
    public async Task ShouldNotListDroppedCfGivenCfDroppedWhenListing()
    {
        foreach (var mode in AllStorageModes)
        {
            using var directory = new TemporaryDirectory();
            await using var database = await OpenAsync(directory.Path, mode);
            var family = await database.ColumnFamilies.CreateAsync("test_cf");
            await database.ColumnFamilies.DropAsync(family);

            var names = (await database.ColumnFamilies.ListAsync())
                .Select(static item => item.Name)
                .ToArray();

            Assert.DoesNotContain("test_cf", names);
        }
    }

    [Fact]
    public async Task ShouldHandleOperationsOnDefaultCfGivenCustomCfsExistWhenOperating()
    {
        foreach (var mode in AllStorageModes)
        {
            using var directory = new TemporaryDirectory();
            await using var database = await OpenAsync(directory.Path, mode);
            await database.ColumnFamilies.CreateAsync("cf1");
            await database.ColumnFamilies.CreateAsync("cf2");
            var defaultFamily = database.ColumnFamilies.DefaultFamily;

            await PutBufferedAsync(database, defaultFamily, "key1", "default_value", mode);

            Assert.Equal("default_value", await ReadAsync(database, defaultFamily, "key1"));
        }
    }

    [Fact]
    public async Task ShouldIsolateKeysGivenSameKeyInDifferentCfsWhenReading()
    {
        foreach (var mode in AllStorageModes)
        {
            using var directory = new TemporaryDirectory();
            await using var database = await OpenAsync(directory.Path, mode);
            var defaultFamily = database.ColumnFamilies.DefaultFamily;
            var family = await database.ColumnFamilies.CreateAsync("cf1");
            await PutBufferedAsync(database, defaultFamily, "key1", "value_default", mode);
            await PutBufferedAsync(database, family, "key1", "value_cf1", mode);

            Assert.Equal("value_default", await ReadAsync(database, defaultFamily, "key1"));
            Assert.Equal("value_cf1", await ReadAsync(database, family, "key1"));
        }
    }

    [Fact]
    public async Task ShouldIsolateDeletesGivenDeleteInOneCfWhenOtherCfHasSameKey()
    {
        foreach (var mode in AllStorageModes)
        {
            using var directory = new TemporaryDirectory();
            await using var database = await OpenAsync(directory.Path, mode);
            var first = await database.ColumnFamilies.CreateAsync("cf1");
            var second = await database.ColumnFamilies.CreateAsync("cf2");
            await PutBufferedAsync(database, first, "key1", "value1", mode);
            await PutBufferedAsync(database, second, "key1", "value2", mode);

            await using (var transaction = await database.Transactions.BeginAsync(
                             first,
                             PantsTransactionMode.ReadWrite))
            {
                transaction.Delete(TestBytes.FromString("key1"));
                await transaction.CommitAsync(
                    mode == ColumnFamilyStorageMode.SimulatedCloud
                        ? PantsWriteOptions.CloudAsync
                        : PantsWriteOptions.Buffered);
            }

            Assert.Null(await ReadAsync(database, first, "key1"));
            Assert.Equal("value2", await ReadAsync(database, second, "key1"));
        }
    }

    [Fact]
    public async Task ShouldIsolateDataGivenDifferentDataVolumesWhenReading()
    {
        foreach (var mode in AllStorageModes)
        {
            using var directory = new TemporaryDirectory();
            await using var database = await OpenAsync(directory.Path, mode);
            var first = await database.ColumnFamilies.CreateAsync("cf1");
            var second = await database.ColumnFamilies.CreateAsync("cf2");

            for (var index = 0; index < 100; index++)
            {
                await PutBufferedAsync(database, first, $"key{index}", "value1", mode);
            }

            await PutBufferedAsync(database, second, "single_key", "value2", mode);

            Assert.Equal("value1", await ReadAsync(database, first, "key50"));
            Assert.Equal("value2", await ReadAsync(database, second, "single_key"));
            Assert.Null(await ReadAsync(database, first, "single_key"));
        }
    }

    [Fact]
    public async Task ShouldIsolateCompactionGivenPerCfDataWhenCompacting()
    {
        foreach (var mode in DurableStorageModes)
        {
            using var directory = new TemporaryDirectory();
            await using var database = await OpenAsync(directory.Path, mode);
            var first = await database.ColumnFamilies.CreateAsync("cf1");
            var second = await database.ColumnFamilies.CreateAsync("cf2");

            for (var generation = 0; generation < 4; generation++)
            {
                await PutBufferedAsync(database, first, $"key{generation}", "value1", mode);
                await database.Maintenance.FlushAsync(first);
                await PutBufferedAsync(database, second, $"key{generation}", "value2", mode);
                await database.Maintenance.FlushAsync(second);
            }

            var before = await database.Diagnostics.GetStorageLayoutAsync();
            var l0Before = before.Levels
                .Where(static level => level.Level == 0)
                .SelectMany(static level => level.Files)
                .ToArray();
            Assert.Equal(8, l0Before.Length);
            Assert.Equal(4, l0Before.Count(file => file.ColumnFamilyId == first.Id));
            Assert.Equal(4, l0Before.Count(file => file.ColumnFamilyId == second.Id));

            await database.Maintenance.CompactAllAsync();

            var after = await database.Diagnostics.GetStorageLayoutAsync();
            var l0After = after.Levels
                .Where(static level => level.Level == 0)
                .SelectMany(static level => level.Files)
                .ToArray();
            var l1After = after.Levels
                .Where(static level => level.Level == 1)
                .SelectMany(static level => level.Files)
                .ToArray();
            Assert.Empty(l0After);
            Assert.Contains(l1After, file => file.ColumnFamilyId == first.Id);
            Assert.Contains(l1After, file => file.ColumnFamilyId == second.Id);
            Assert.Equal(2, l1After.Select(static file => file.ColumnFamilyId).Distinct().Count());
            Assert.All(l1After, file => Assert.Contains(file.ColumnFamilyId, new[] { first.Id, second.Id }));
            Assert.Equal("value1", await ReadAsync(database, first, "key2"));
            Assert.Equal("value2", await ReadAsync(database, second, "key2"));
        }
    }

    [Fact]
    public async Task ShouldIsolateCfAfterFlushGivenSameKeyWhenReading()
    {
        foreach (var mode in DurableStorageModes)
        {
            using var directory = new TemporaryDirectory();
            await using var database = await OpenAsync(directory.Path, mode);
            var first = await database.ColumnFamilies.CreateAsync("cf1");
            var second = await database.ColumnFamilies.CreateAsync("cf2");
            await PutBufferedAsync(database, first, "key1", "value1", mode);
            await PutBufferedAsync(database, second, "key1", "value2", mode);
            await database.Maintenance.FlushAsync(first);
            await database.Maintenance.FlushAsync(second);

            var layout = await database.Diagnostics.GetStorageLayoutAsync();
            var files = layout.Levels.SelectMany(static level => level.Files).ToArray();
            Assert.Contains(files, file => file.ColumnFamilyId == first.Id);
            Assert.Contains(files, file => file.ColumnFamilyId == second.Id);
            Assert.Equal("value1", await ReadAsync(database, first, "key1"));
            Assert.Equal("value2", await ReadAsync(database, second, "key1"));
        }
    }

    [Fact]
    public async Task ShouldMaintainCfIsolationGivenManyCfsWhenOperating()
    {
        foreach (var mode in AllStorageModes)
        {
            using var directory = new TemporaryDirectory();
            await using var database = await OpenAsync(directory.Path, mode);
            var families = new List<IPantsColumnFamily>();
            for (var index = 0; index < 10; index++)
            {
                families.Add(await database.ColumnFamilies.CreateAsync($"cf{index}"));
            }

            for (var index = 0; index < families.Count; index++)
            {
                await PutBufferedAsync(database, families[index], "shared_key", $"value{index}", mode);
            }

            for (var index = 0; index < families.Count; index++)
            {
                Assert.Equal(
                    $"value{index}",
                    await ReadAsync(database, families[index], "shared_key"));
            }
        }
    }

    [Fact]
    public async Task ShouldPersistCfMetadataGivenRestartWhenCfCreated()
    {
        foreach (var mode in DurableStorageModes)
        {
            using var directory = new TemporaryDirectory();
            uint familyId;
            await using (var database = await OpenAsync(directory.Path, mode))
            {
                familyId = (await database.ColumnFamilies.CreateAsync("test_cf")).Id;
            }

            await using var reopened = await OpenAsync(directory.Path, mode);
            var family = Assert.IsAssignableFrom<IPantsColumnFamily>(
                await reopened.ColumnFamilies.GetAsync("test_cf"));
            Assert.Equal(familyId, family.Id);
            Assert.Contains(
                await reopened.ColumnFamilies.ListAsync(),
                candidate => candidate.Name == "test_cf");
        }
    }

    [Fact]
    public async Task ShouldPersistCfDataGivenRestartWhenDataFlushed()
    {
        foreach (var mode in DurableStorageModes)
        {
            using var directory = new TemporaryDirectory();
            uint familyId;
            await using (var database = await OpenAsync(directory.Path, mode))
            {
                var family = await database.ColumnFamilies.CreateAsync("test_cf");
                familyId = family.Id;
                await PutBufferedAsync(database, family, "key1", "value1", mode);
                await database.Maintenance.FlushAsync(family);
                var layout = await database.Diagnostics.GetStorageLayoutAsync();
                Assert.Contains(
                    layout.Levels.SelectMany(static level => level.Files),
                    file => file.ColumnFamilyId == familyId);
            }

            await using var reopened = await OpenAsync(directory.Path, mode);
            var familyReopened = Assert.IsAssignableFrom<IPantsColumnFamily>(
                await reopened.ColumnFamilies.GetAsync("test_cf"));
            Assert.Equal(familyId, familyReopened.Id);
            Assert.Equal("value1", await ReadAsync(reopened, familyReopened, "key1"));
        }
    }

    [Fact]
    public async Task ShouldPersistMultipleCfsGivenRestartWhenAllFlushed()
    {
        foreach (var mode in DurableStorageModes)
        {
            using var directory = new TemporaryDirectory();
            await using (var database = await OpenAsync(directory.Path, mode))
            {
                var first = await database.ColumnFamilies.CreateAsync("cf1");
                var second = await database.ColumnFamilies.CreateAsync("cf2");
                await PutBufferedAsync(database, first, "key1", "value1", mode);
                await PutBufferedAsync(database, second, "key2", "value2", mode);
                await database.Maintenance.FlushAsync(first);
                await database.Maintenance.FlushAsync(second);
                var layout = await database.Diagnostics.GetStorageLayoutAsync();
                var familyIds = layout.Levels
                    .SelectMany(static level => level.Files)
                    .Select(static file => file.ColumnFamilyId)
                    .ToHashSet();
                Assert.Contains(first.Id, familyIds);
                Assert.Contains(second.Id, familyIds);
            }

            await using var reopened = await OpenAsync(directory.Path, mode);
            Assert.Equal(3, (await reopened.ColumnFamilies.ListAsync()).Count);
            var firstReopened = Assert.IsAssignableFrom<IPantsColumnFamily>(
                await reopened.ColumnFamilies.GetAsync("cf1"));
            var secondReopened = Assert.IsAssignableFrom<IPantsColumnFamily>(
                await reopened.ColumnFamilies.GetAsync("cf2"));
            Assert.Equal("value1", await ReadAsync(reopened, firstReopened, "key1"));
            Assert.Equal("value2", await ReadAsync(reopened, secondReopened, "key2"));
        }
    }

    [Fact]
    public async Task ShouldDropColumnFamilyGivenEmptyCfWhenRequested()
    {
        foreach (var mode in AllStorageModes)
        {
            using var directory = new TemporaryDirectory();
            await using var database = await OpenAsync(directory.Path, mode);
            var family = await database.ColumnFamilies.CreateAsync("test_cf");

            await database.ColumnFamilies.DropAsync(family);

            Assert.Null(await database.ColumnFamilies.GetAsync("test_cf"));
            await Assert.ThrowsAsync<PantsInvalidArgumentException>(() =>
                database.Transactions.BeginAsync(family, PantsTransactionMode.ReadOnly).AsTask());
        }
    }

    [Fact]
    public async Task ShouldDropColumnFamilyGivenFlushedDataWhenRequested()
    {
        foreach (var mode in DurableStorageModes)
        {
            using var directory = new TemporaryDirectory();
            await using var database = await OpenAsync(directory.Path, mode);
            var family = await database.ColumnFamilies.CreateAsync("test_cf");
            await PutBufferedAsync(database, family, "key1", "value1", mode);
            await database.Maintenance.FlushAsync(family);
            Assert.True((await database.Diagnostics.GetRuntimeMetricsAsync()).SstCount > 0);

            await database.ColumnFamilies.DropAsync(family);

            Assert.Null(await database.ColumnFamilies.GetAsync("test_cf"));
            await Assert.ThrowsAsync<PantsInvalidArgumentException>(() =>
                database.Transactions.BeginAsync(family, PantsTransactionMode.ReadOnly).AsTask());
        }
    }

    [Fact]
    public async Task ShouldFailDropColumnFamilyGivenUnflushedDataWhenMemtableNotEmpty()
    {
        foreach (var mode in AllStorageModes)
        {
            using var directory = new TemporaryDirectory();
            await using var database = await OpenAsync(directory.Path, mode);
            var family = await database.ColumnFamilies.CreateAsync("test_cf");
            await PutBufferedAsync(database, family, "key1", "value1", mode);

            var error = await Assert.ThrowsAsync<PantsBusyException>(() =>
                database.ColumnFamilies.DropAsync(family).AsTask());

            Assert.Equal(PantsErrorCode.Busy, error.Code);
            Assert.Equal("value1", await ReadAsync(database, family, "key1"));
        }
    }

    [Fact]
    public async Task ShouldDiscardUnflushedDataGivenExplicitDestructiveDrop()
    {
        foreach (var mode in AllStorageModes)
        {
            using var directory = new TemporaryDirectory();
            await using var database = await OpenAsync(directory.Path, mode);
            var family = await database.ColumnFamilies.CreateAsync("discard-me");
            await PutBufferedAsync(database, family, "key", "value", mode);

            await database.ColumnFamilies.DropDiscardingUnflushedAsync(family);

            Assert.Null(await database.ColumnFamilies.GetAsync("discard-me"));
            await Assert.ThrowsAsync<PantsInvalidArgumentException>(() =>
                database.Transactions.BeginAsync(family, PantsTransactionMode.ReadOnly).AsTask());
        }
    }

    [Fact]
    public async Task ShouldReturnCleanErrorGivenWriteRacingConcurrentDropWhenSameCf()
    {
        using var directory = new TemporaryDirectory();
        await using (var database = await OpenAsync(directory.Path, ColumnFamilyStorageMode.Local))
        {
            var family = await database.ColumnFamilies.CreateAsync("racing-drop");
            using var start = new Barrier(3);
            var writer = Task.Run(async () =>
            {
                start.SignalAndWait();
                try
                {
                    await using var transaction = await database.Transactions.BeginAsync(
                        family,
                        PantsTransactionMode.ReadWrite);
                    transaction.Put("racing-key"u8.ToArray(), "racing-value"u8.ToArray());
                    await transaction.CommitAsync(PantsWriteOptions.Sync);
                    return (Succeeded: true, Error: (PantsInvalidArgumentException?)null);
                }
                catch (PantsInvalidArgumentException error)
                {
                    return (Succeeded: false, Error: error);
                }
            });
            var dropper = Task.Run(async () =>
            {
                start.SignalAndWait();
                await database.ColumnFamilies.DropDiscardingUnflushedAsync(family);
            });

            start.SignalAndWait();
            await dropper;
            var writeResult = await writer;

            Assert.True(writeResult.Succeeded || writeResult.Error?.Code == PantsErrorCode.InvalidArgument);
            Assert.Null(await database.ColumnFamilies.GetAsync("racing-drop"));
            await Assert.ThrowsAsync<PantsInvalidArgumentException>(() =>
                database.Transactions.BeginAsync(family, PantsTransactionMode.ReadOnly).AsTask());
        }

        await using var reopened = await OpenAsync(directory.Path, ColumnFamilyStorageMode.Local);
        Assert.Null(await reopened.ColumnFamilies.GetAsync("racing-drop"));
    }

    [Fact]
    public async Task ShouldFailDropDefaultColumnFamilyGivenDropRequestWhenDefaultCf()
    {
        foreach (var mode in AllStorageModes)
        {
            using var directory = new TemporaryDirectory();
            await using var database = await OpenAsync(directory.Path, mode);

            var error = await Assert.ThrowsAsync<PantsInvalidArgumentException>(() =>
                database.ColumnFamilies.DropAsync(database.ColumnFamilies.DefaultFamily).AsTask());

            Assert.Contains("default", error.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task ShouldReturnInvalidArgumentGivenSecondDropOfSameColumnFamily()
    {
        foreach (var mode in AllStorageModes)
        {
            using var directory = new TemporaryDirectory();
            await using var database = await OpenAsync(directory.Path, mode);
            var family = await database.ColumnFamilies.CreateAsync("drop-twice");
            await database.ColumnFamilies.DropAsync(family);

            var error = await Assert.ThrowsAsync<PantsInvalidArgumentException>(() =>
                database.ColumnFamilies.DropAsync(family).AsTask());

            Assert.Equal(PantsErrorCode.InvalidArgument, error.Code);
        }
    }

    [Fact]
    public async Task ShouldInvalidateHandleGivenCfDroppedWhenAccessing()
    {
        foreach (var mode in AllStorageModes)
        {
            using var directory = new TemporaryDirectory();
            await using var database = await OpenAsync(directory.Path, mode);
            var family = await database.ColumnFamilies.CreateAsync("test_cf");
            await database.ColumnFamilies.DropAsync(family);

            await Assert.ThrowsAsync<PantsInvalidArgumentException>(() =>
                database.Transactions.BeginAsync(family, PantsTransactionMode.ReadOnly).AsTask());
            await Assert.ThrowsAsync<PantsInvalidArgumentException>(() =>
                database.Transactions.BeginAsync(family, PantsTransactionMode.ReadWrite).AsTask());
        }
    }

    [Fact]
    public async Task ShouldDeleteCfDataGivenCfDroppedWhenPersisted()
    {
        foreach (var mode in DurableStorageModes)
        {
            using var directory = new TemporaryDirectory();
            var options = OptionsFor(mode, directory.Path);
            await using (var database = await PantsDatabase.OpenAsync(options))
            {
                var family = await database.ColumnFamilies.CreateAsync("test_cf");
                await PutBufferedAsync(database, family, "key1", "value1", mode);
                await database.ColumnFamilies.DropDiscardingUnflushedAsync(family);
            }

            await using var reopened = await PantsDatabase.OpenAsync(options);
            Assert.Null(await reopened.ColumnFamilies.GetAsync("test_cf"));
            var recreated = await reopened.ColumnFamilies.CreateAsync("test_cf");

            Assert.Null(await ReadAsync(reopened, recreated, "key1"));
        }
    }

    [Fact]
    public async Task ShouldAllocateMonotonicColumnFamilyIdsGivenDeletedColumnFamilyWhenCreating()
    {
        foreach (var mode in AllStorageModes)
        {
            using var directory = new TemporaryDirectory();
            await using var database = await OpenAsync(directory.Path, mode);
            var original = await database.ColumnFamilies.CreateAsync("test_cf");
            await PutBufferedAsync(database, original, "key1", "value1", mode);
            await database.ColumnFamilies.DropDiscardingUnflushedAsync(original);

            var recreated = await database.ColumnFamilies.CreateAsync("test_cf");
            await PutBufferedAsync(database, recreated, "key2", "value2", mode);

            Assert.True(recreated.Id > original.Id);
            await Assert.ThrowsAsync<PantsInvalidArgumentException>(() =>
                database.Transactions.BeginAsync(original, PantsTransactionMode.ReadOnly).AsTask());
            Assert.Null(await ReadAsync(database, recreated, "key1"));
            Assert.Equal("value2", await ReadAsync(database, recreated, "key2"));
        }
    }

    [Fact]
    public async Task ShouldPersistCfDropGivenRestartWhenCfWasDropped()
    {
        foreach (var mode in DurableStorageModes)
        {
            using var directory = new TemporaryDirectory();
            var options = OptionsFor(mode, directory.Path);
            await using (var database = await PantsDatabase.OpenAsync(options))
            {
                var family = await database.ColumnFamilies.CreateAsync("test_cf");
                await PutBufferedAsync(database, family, "key1", "value1", mode);
                await database.Maintenance.FlushAsync(family);
                await database.ColumnFamilies.DropAsync(family);
            }

            await using var reopened = await PantsDatabase.OpenAsync(options);

            Assert.Null(await reopened.ColumnFamilies.GetAsync("test_cf"));
            Assert.DoesNotContain(
                (await reopened.ColumnFamilies.ListAsync()).Select(static item => item.Name),
                static name => name == "test_cf");
        }
    }

    [Fact]
    public async Task ShouldValidateNamesAndAllocateMonotonicColumnFamilyIdentities()
    {
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        var maximumName = $"{new string('\u00e9', 127)}a";

        var first = await database.ColumnFamilies.CreateAsync("first");
        var duplicate = await database.ColumnFamilies.CreateAsync("first");
        Assert.Equal(first.Id, duplicate.Id);
        Assert.Equal(first.Name, duplicate.Name);
        Assert.Equal(2, (await database.ColumnFamilies.ListAsync()).Count);
        await database.ColumnFamilies.DropAsync(first);
        var recreated = await database.ColumnFamilies.CreateAsync("first");
        var maximum = await database.ColumnFamilies.CreateAsync(maximumName);

        Assert.True(recreated.Id > first.Id);
        Assert.Equal(maximumName, maximum.Name);
        await Assert.ThrowsAsync<PantsInvalidArgumentException>(() =>
            database.ColumnFamilies.CreateAsync(string.Empty).AsTask());
        await Assert.ThrowsAsync<PantsInvalidArgumentException>(() =>
            database.ColumnFamilies.CreateAsync("default").AsTask());
        await Assert.ThrowsAsync<PantsInvalidArgumentException>(() =>
            database.ColumnFamilies.CreateAsync("contains\0nul").AsTask());
        await Assert.ThrowsAsync<PantsInvalidArgumentException>(() =>
            database.ColumnFamilies.CreateAsync(new string('\u00e9', 128)).AsTask());
        await Assert.ThrowsAsync<PantsInvalidArgumentException>(() =>
            database.Transactions.BeginAsync(first, PantsTransactionMode.ReadOnly).AsTask());
        await Assert.ThrowsAsync<PantsInvalidArgumentException>(() =>
            database.ColumnFamilies.DropAsync(first).AsTask());
        await Assert.ThrowsAsync<PantsInvalidArgumentException>(() =>
            database.ColumnFamilies.DropAsync(database.ColumnFamilies.DefaultFamily).AsTask());
    }

    [Fact]
    public async Task ShouldIsolateAndPersistColumnFamiliesAcrossFlushAndCompaction()
    {
        using var directory = new TemporaryDirectory();
        await using (var database = await PantsDatabase.OpenAsync(
                         PantsOpenOptions.Local(directory.Path)))
        {
            var alpha = await database.ColumnFamilies.CreateAsync("alpha");
            var beta = await database.ColumnFamilies.CreateAsync("beta");
            await PutAsync(database, database.ColumnFamilies.DefaultFamily, "shared", "default");
            await PutAsync(database, alpha, "shared", "alpha");
            await PutAsync(database, beta, "shared", "beta");
            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
            await database.Maintenance.FlushAsync(alpha);
            await database.Maintenance.FlushAsync(beta);
            await database.Maintenance.CompactAllAsync();
        }

        await using var reopened = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path));
        var alphaReopened = Assert.IsAssignableFrom<IPantsColumnFamily>(
            await reopened.ColumnFamilies.GetAsync("alpha"));
        var betaReopened = Assert.IsAssignableFrom<IPantsColumnFamily>(
            await reopened.ColumnFamilies.GetAsync("beta"));

        Assert.Equal("default", await ReadAsync(reopened, reopened.ColumnFamilies.DefaultFamily, "shared"));
        Assert.Equal("alpha", await ReadAsync(reopened, alphaReopened, "shared"));
        Assert.Equal("beta", await ReadAsync(reopened, betaReopened, "shared"));
        Assert.Equal(
            ["default", "alpha", "beta"],
            (await reopened.ColumnFamilies.ListAsync()).Select(static family => family.Name));
    }

    [Fact]
    public async Task ShouldDistinguishSafeAndDestructiveColumnFamilyDrop()
    {
        using var directory = new TemporaryDirectory();
        await using (var database = await PantsDatabase.OpenAsync(
                         PantsOpenOptions.Local(directory.Path)))
        {
            var safe = await database.ColumnFamilies.CreateAsync("safe");
            var destructive = await database.ColumnFamilies.CreateAsync("destructive");
            await PutAsync(database, safe, "key", "safe");
            await PutAsync(database, destructive, "key", "discarded");

            var busy = await Assert.ThrowsAsync<PantsBusyException>(() =>
                database.ColumnFamilies.DropAsync(safe).AsTask());
            Assert.Equal(PantsErrorCode.Busy, busy.Code);

            await database.Maintenance.FlushAsync(safe);
            await database.ColumnFamilies.DropAsync(safe);
            await database.ColumnFamilies.DropDiscardingUnflushedAsync(destructive);
            Assert.Null(await database.ColumnFamilies.GetAsync("safe"));
            Assert.Null(await database.ColumnFamilies.GetAsync("destructive"));
        }

        await using var reopened = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path));
        Assert.Null(await reopened.ColumnFamilies.GetAsync("safe"));
        Assert.Null(await reopened.ColumnFamilies.GetAsync("destructive"));
        Assert.Equal(["default"], (await reopened.ColumnFamilies.ListAsync()).Select(static family => family.Name));
    }

    [Fact]
    public async Task ShouldRejectCommitThroughHandleDroppedAfterTransactionStart()
    {
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        var family = await database.ColumnFamilies.CreateAsync("temporary");
        await using var transaction = await database.Transactions.BeginAsync(
            family,
            PantsTransactionMode.ReadWrite);
        transaction.Put("key"u8.ToArray(), "value"u8.ToArray());

        await database.ColumnFamilies.DropAsync(family);

        var error = await Assert.ThrowsAsync<PantsInvalidArgumentException>(() =>
            transaction.CommitAsync(PantsWriteOptions.Buffered).AsTask());
        Assert.Equal(PantsErrorCode.InvalidArgument, error.Code);
    }

    [Fact]
    public async Task ShouldIsolateIndependentInMemoryDatabaseInstances()
    {
        await using var first = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await using var second = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await PutAsync(first, first.ColumnFamilies.DefaultFamily, "key", "first");
        await PutAsync(second, second.ColumnFamilies.DefaultFamily, "key", "second");

        Assert.Equal("first", await ReadAsync(first, first.ColumnFamilies.DefaultFamily, "key"));
        Assert.Equal("second", await ReadAsync(second, second.ColumnFamilies.DefaultFamily, "key"));
    }

    [Fact]
    public async Task ShouldKeepInMemoryOperationsEphemeralAndIsolated()
    {
        await using (var database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory()))
        {
            var family = await database.ColumnFamilies.CreateAsync("ephemeral");
            await using (var writes = await database.Transactions.BeginAsync(
                             family,
                             PantsTransactionMode.ReadWrite))
            {
                for (var index = 0; index < 100; index++)
                {
                    writes.Put(TestBytes.FromString($"key-{index:000}"), "initial"u8.ToArray());
                }

                await writes.CommitAsync(PantsWriteOptions.Buffered);
            }

            await using (var mutations = await database.Transactions.BeginAsync(
                             family,
                             PantsTransactionMode.ReadWrite))
            {
                for (var index = 0; index < 50; index++)
                {
                    mutations.Delete(TestBytes.FromString($"key-{index:000}"));
                }

                mutations.Put("key-050"u8.ToArray(), "updated"u8.ToArray());
                await mutations.CommitAsync(PantsWriteOptions.Buffered);
            }

            Assert.Null(await ReadAsync(database, family, "key-000"));
            Assert.Equal("updated", await ReadAsync(database, family, "key-050"));
            Assert.Equal("initial", await ReadAsync(database, family, "key-099"));
        }

        await using var reopened = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        Assert.Null(await reopened.ColumnFamilies.GetAsync("ephemeral"));
    }

    static async Task PutBufferedAsync(
        IPantsDatabase database,
        IPantsColumnFamily columnFamily,
        string key,
        string value,
        ColumnFamilyStorageMode mode)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            columnFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put(TestBytes.FromString(key), TestBytes.FromString(value));
        await transaction.CommitAsync(
            mode == ColumnFamilyStorageMode.SimulatedCloud
                ? PantsWriteOptions.CloudAsync
                : PantsWriteOptions.Buffered);
    }

    static async Task PutAsync(
        IPantsDatabase database,
        IPantsColumnFamily columnFamily,
        string key,
        string value)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            columnFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put(TestBytes.FromString(key), TestBytes.FromString(value));
        await transaction.CommitAsync(PantsWriteOptions.Sync);
    }

    static async Task<string?> ReadAsync(
        IPantsDatabase database,
        IPantsColumnFamily columnFamily,
        string key)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            columnFamily,
            PantsTransactionMode.ReadOnly);
        var value = await transaction.GetAsync(TestBytes.FromString(key));
        return value is null ? null : TestBytes.ToText(value.Value);
    }

    static async Task AssertRejectedNameDoesNotPersistAsync(string name)
    {
        foreach (var mode in DurableStorageModes)
        {
            using var directory = new TemporaryDirectory();
            var options = OptionsFor(mode, directory.Path);
            await using (var database = await PantsDatabase.OpenAsync(options))
            {
                await Assert.ThrowsAsync<PantsInvalidArgumentException>(
                    () => database.ColumnFamilies.CreateAsync(name).AsTask());
                Assert.Null(await database.ColumnFamilies.GetAsync(name));
            }

            await using var reopened = await PantsDatabase.OpenAsync(options);
            Assert.Null(await reopened.ColumnFamilies.GetAsync(name));
        }
    }

    static ValueTask<IPantsDatabase> OpenAsync(
        string path,
        ColumnFamilyStorageMode mode,
        long? memtableSizeLimitBytes = null)
    {
        var options = OptionsFor(mode, path);
        if (memtableSizeLimitBytes is { } sizeLimitBytes)
        {
            options = options.WithMemtableLimits(sizeLimitBytes);
        }

        return PantsDatabase.OpenAsync(options);
    }

    static PantsOpenOptions OptionsFor(ColumnFamilyStorageMode mode, string path) => mode switch
    {
        ColumnFamilyStorageMode.InMemory => PantsOpenOptions.InMemory(),
        ColumnFamilyStorageMode.Local => PantsOpenOptions.Local(path).WithBackgroundCompaction(false),
        ColumnFamilyStorageMode.SimulatedCloud => PantsOpenOptions
            .SimulatedCloud(path, "pants-tests", "column-family-parity/")
            .WithBackgroundCompaction(false),
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
    };

    enum ColumnFamilyStorageMode
    {
        InMemory,
        Local,
        SimulatedCloud
    }
}

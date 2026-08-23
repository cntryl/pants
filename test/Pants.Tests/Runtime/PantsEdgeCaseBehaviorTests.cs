namespace Cntryl.Pants.Tests.Runtime;

public sealed class PantsEdgeCaseBehaviorTests
{
    [Theory]
    [InlineData("memory")]
    [InlineData("local")]
    [InlineData("cloud")]
    public async Task ShouldRetrieveStoredKeyGivenFiveHundredKilobyteKey(string mode)
    {
        using var directory = new TemporaryDirectory();
        await using var database = await StorageModeTestHarness.OpenAsync(mode, directory.Path);
        var key = Enumerable.Repeat((byte)'k', 500_000).ToArray();

        await StorageModeTestHarness.PutAsync(database, mode, key, "value"u8.ToArray());

        Assert.Equal("value", TestBytes.ToText(
            (await StorageModeTestHarness.GetAsync(database, key))!.Value));
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("local")]
    [InlineData("cloud")]
    public async Task ShouldRetrieveStoredValueGivenTenMegabyteValue(string mode)
    {
        using var directory = new TemporaryDirectory();
        await using var database = await StorageModeTestHarness.OpenAsync(mode, directory.Path);
        var value = Enumerable.Repeat((byte)42, 10_000_000).ToArray();

        await StorageModeTestHarness.PutAsync(database, mode, "large-value"u8.ToArray(), value);

        var stored = (await StorageModeTestHarness.GetAsync(database, "large-value"u8.ToArray()))!.Value;
        Assert.Equal(value.Length, stored.Length);
        Assert.True(stored.Span.SequenceEqual(value));
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("local")]
    [InlineData("cloud")]
    public async Task ShouldHandleMixedValueSizesFromBytesToMegabytes(string mode)
    {
        using var directory = new TemporaryDirectory();
        await using var database = await StorageModeTestHarness.OpenAsync(mode, directory.Path);
        await using (var writer = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            writer.Put("tiny"u8.ToArray(), new byte[1]);
            writer.Put("small"u8.ToArray(), new byte[100]);
            writer.Put("medium"u8.ToArray(), new byte[100_000]);
            writer.Put("large"u8.ToArray(), new byte[1_000_000]);
            await writer.CommitAsync(StorageModeTestHarness.GetWriteOptions(mode));
        }

        Assert.Equal(1, (await StorageModeTestHarness.GetAsync(database, "tiny"u8.ToArray()))?.Length);
        Assert.Equal(100, (await StorageModeTestHarness.GetAsync(database, "small"u8.ToArray()))?.Length);
        Assert.Equal(100_000, (await StorageModeTestHarness.GetAsync(database, "medium"u8.ToArray()))?.Length);
        Assert.Equal(1_000_000, (await StorageModeTestHarness.GetAsync(database, "large"u8.ToArray()))?.Length);
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("local")]
    [InlineData("cloud")]
    public async Task ShouldHandleUtf8ControlAndBinaryKeys(string mode)
    {
        using var directory = new TemporaryDirectory();
        await using var database = await StorageModeTestHarness.OpenAsync(mode, directory.Path);
        byte[][] keys =
        [
            "normal-key"u8.ToArray(),
            TestBytes.FromString("unicode-😀-key"),
            [0, 1, 2, 3],
            "key\twith\ttabs"u8.ToArray(),
            "key\nwith\nnewlines"u8.ToArray()
        ];
        await using (var writer = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            for (var index = 0; index < keys.Length; index++)
            {
                writer.Put(keys[index], TestBytes.FromString($"value-{index}"));
            }

            await writer.CommitAsync(StorageModeTestHarness.GetWriteOptions(mode));
        }

        for (var index = 0; index < keys.Length; index++)
        {
            Assert.Equal(
                $"value-{index}",
                TestBytes.ToText((await StorageModeTestHarness.GetAsync(database, keys[index]))!.Value));
        }
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("local")]
    [InlineData("cloud")]
    public async Task ShouldRetrieveOnlyRecordAndMissOtherKey(string mode)
    {
        using var directory = new TemporaryDirectory();
        await using var database = await StorageModeTestHarness.OpenAsync(mode, directory.Path);

        await StorageModeTestHarness.PutAsync(database, mode, "only-key", "only-value");

        Assert.Equal("only-value", await StorageModeTestHarness.GetTextAsync(database, "only-key"));
        Assert.Null(await StorageModeTestHarness.GetTextAsync(database, "other-key"));
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("local")]
    [InlineData("cloud")]
    public async Task ShouldRetrieveBoundaryKeysAndMissOutsideRange(string mode)
    {
        using var directory = new TemporaryDirectory();
        await using var database = await StorageModeTestHarness.OpenAsync(mode, directory.Path);
        await using (var writer = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            for (var index = 0; index < 5; index++)
            {
                writer.Put(
                    TestBytes.FromString($"key-{index:00}"),
                    TestBytes.FromString($"value-{index}"));
            }

            await writer.CommitAsync(StorageModeTestHarness.GetWriteOptions(mode));
        }

        Assert.Equal("value-0", await StorageModeTestHarness.GetTextAsync(database, "key-00"));
        Assert.Equal("value-4", await StorageModeTestHarness.GetTextAsync(database, "key-04"));
        Assert.Null(await StorageModeTestHarness.GetTextAsync(database, "key-99"));
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("local")]
    [InlineData("cloud")]
    public async Task ShouldHandleOneThousandPutsGivenSingleRapidBatch(string mode)
    {
        using var directory = new TemporaryDirectory();
        await using var database = await StorageModeTestHarness.OpenAsync(mode, directory.Path);
        await using (var writer = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            for (var index = 0; index < 1_000; index++)
            {
                writer.Put(
                    TestBytes.FromString($"rapid-{index:00000}"),
                    TestBytes.FromString($"value-{index}"));
            }

            await writer.CommitAsync(StorageModeTestHarness.GetWriteOptions(mode));
        }

        for (var index = 0; index < 1_000; index += 100)
        {
            Assert.Equal(
                $"value-{index}",
                await StorageModeTestHarness.GetTextAsync(database, $"rapid-{index:00000}"));
        }
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("local")]
    [InlineData("cloud")]
    public async Task ShouldHandleDeleteAllPatternGivenOneHundredKeys(string mode)
    {
        using var directory = new TemporaryDirectory();
        await using var database = await StorageModeTestHarness.OpenAsync(mode, directory.Path);
        await using (var writer = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            for (var index = 0; index < 100; index++)
            {
                writer.Put(TestBytes.FromString($"delete-{index:000}"), "value"u8.ToArray());
            }

            await writer.CommitAsync(StorageModeTestHarness.GetWriteOptions(mode));
        }

        await using (var deleting = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            for (var index = 0; index < 100; index++)
            {
                deleting.Delete(TestBytes.FromString($"delete-{index:000}"));
            }

            await deleting.CommitAsync(StorageModeTestHarness.GetWriteOptions(mode));
        }

        for (var index = 0; index < 100; index++)
        {
            Assert.Null(await StorageModeTestHarness.GetTextAsync(database, $"delete-{index:000}"));
        }
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("local")]
    [InlineData("cloud")]
    public async Task ShouldKeepFinalTombstoneGivenRepeatedPutDeleteCycles(string mode)
    {
        using var directory = new TemporaryDirectory();
        await using var database = await StorageModeTestHarness.OpenAsync(mode, directory.Path);
        for (var cycle = 0; cycle < 10; cycle++)
        {
            await StorageModeTestHarness.PutAsync(database, mode, "key", $"cycle-{cycle}");
            await using var deleting = await database.BeginTransactionAsync(
                database.DefaultColumnFamily,
                PantsTransactionMode.ReadWrite);
            deleting.Delete("key"u8.ToArray());
            await deleting.CommitAsync(StorageModeTestHarness.GetWriteOptions(mode));
        }

        Assert.Null(await StorageModeTestHarness.GetTextAsync(database, "key"));
    }
}

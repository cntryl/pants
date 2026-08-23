namespace Cntryl.Pants.Tests.Storage;

public sealed class PantsStorageModeBehaviorTests
{
    [Theory]
    [InlineData("memory")]
    [InlineData("local")]
    [InlineData("cloud")]
    public async Task ShouldCreateEngineGivenSupportedStorageMode(string mode)
    {
        using var directory = new TemporaryDirectory();

        await using var database = await StorageModeTestHarness.OpenAsync(mode, directory.Path);

        Assert.Equal("default", database.DefaultColumnFamily.Name);
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("local")]
    [InlineData("cloud")]
    public async Task ShouldGetValueGivenExistingKeyWhenPut(string mode)
    {
        using var directory = new TemporaryDirectory();
        await using var database = await StorageModeTestHarness.OpenAsync(mode, directory.Path);

        await StorageModeTestHarness.PutAsync(database, mode, "key", "value");

        Assert.Equal("value", await StorageModeTestHarness.GetTextAsync(database, "key"));
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("local")]
    [InlineData("cloud")]
    public async Task ShouldReturnMissingGivenNonexistentKeyWhenGet(string mode)
    {
        using var directory = new TemporaryDirectory();
        await using var database = await StorageModeTestHarness.OpenAsync(mode, directory.Path);

        var value = await StorageModeTestHarness.GetTextAsync(database, "missing");

        Assert.Null(value);
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("local")]
    [InlineData("cloud")]
    public async Task ShouldOverwriteValueGivenExistingKeyWhenPut(string mode)
    {
        using var directory = new TemporaryDirectory();
        await using var database = await StorageModeTestHarness.OpenAsync(mode, directory.Path);
        await StorageModeTestHarness.PutAsync(database, mode, "key", "first");

        await StorageModeTestHarness.PutAsync(database, mode, "key", "second");

        Assert.Equal("second", await StorageModeTestHarness.GetTextAsync(database, "key"));
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("local")]
    [InlineData("cloud")]
    public async Task ShouldHandleEmptyValueWhenPut(string mode)
    {
        using var directory = new TemporaryDirectory();
        await using var database = await StorageModeTestHarness.OpenAsync(mode, directory.Path);

        await StorageModeTestHarness.PutAsync(database, mode, "key", string.Empty);

        Assert.Equal(string.Empty, await StorageModeTestHarness.GetTextAsync(database, "key"));
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("local")]
    [InlineData("cloud")]
    public async Task ShouldHandleBinaryDataWhenPut(string mode)
    {
        using var directory = new TemporaryDirectory();
        await using var database = await StorageModeTestHarness.OpenAsync(mode, directory.Path);
        byte[] value = [0, 1, 2, 3, 255, 254, 253];

        await using (var writer = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            writer.Put("binary-key"u8.ToArray(), value);
            await writer.CommitAsync(StorageModeTestHarness.GetWriteOptions(mode));
        }

        await using var reader = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Equal(value, (await reader.GetAsync("binary-key"u8.ToArray()))?.ToArray());
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("local")]
    [InlineData("cloud")]
    public async Task ShouldReturnMissingGivenDeletedKeyWhenGet(string mode)
    {
        using var directory = new TemporaryDirectory();
        await using var database = await StorageModeTestHarness.OpenAsync(mode, directory.Path);
        await StorageModeTestHarness.PutAsync(database, mode, "key", "value");
        await using (var deleting = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            deleting.Delete("key"u8.ToArray());
            await deleting.CommitAsync(StorageModeTestHarness.GetWriteOptions(mode));
        }

        var value = await StorageModeTestHarness.GetTextAsync(database, "key");

        Assert.Null(value);
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("local")]
    [InlineData("cloud")]
    public async Task ShouldSucceedGivenNonexistentKeyWhenDelete(string mode)
    {
        using var directory = new TemporaryDirectory();
        await using var database = await StorageModeTestHarness.OpenAsync(mode, directory.Path);
        await using var transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);

        transaction.Delete("missing"u8.ToArray());
        await transaction.CommitAsync(StorageModeTestHarness.GetWriteOptions(mode));

        Assert.Null(await StorageModeTestHarness.GetTextAsync(database, "missing"));
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("local")]
    [InlineData("cloud")]
    public async Task ShouldHandleManyOperationsWhenSequential(string mode)
    {
        using var directory = new TemporaryDirectory();
        await using var database = await StorageModeTestHarness.OpenAsync(mode, directory.Path);
        for (var index = 0; index < 100; index++)
        {
            await StorageModeTestHarness.PutAsync(database, mode, $"key-{index}", $"value-{index}");
        }

        for (var index = 0; index < 100; index++)
        {
            Assert.Equal(
                $"value-{index}",
                await StorageModeTestHarness.GetTextAsync(database, $"key-{index}"));
        }
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("local")]
    [InlineData("cloud")]
    public async Task ShouldRetrieveWrittenDataAcrossStorageModes(string mode)
    {
        using var directory = new TemporaryDirectory();
        await using var database = await StorageModeTestHarness.OpenAsync(mode, directory.Path);
        for (var index = 0; index < 50; index++)
        {
            await StorageModeTestHarness.PutAsync(database, mode, $"artifact-{index}", "value");
        }

        Assert.Equal("value", await StorageModeTestHarness.GetTextAsync(database, "artifact-0"));
        Assert.Equal("value", await StorageModeTestHarness.GetTextAsync(database, "artifact-49"));
    }
}

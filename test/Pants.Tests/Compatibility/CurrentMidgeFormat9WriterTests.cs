using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Compatibility;

public sealed class CurrentMidgeFormat9WriterTests
{
    [Fact]
    public async Task ShouldReadExtendAndReopenCurrentMidgeWriterOutput()
    {
        using var directory = new TemporaryDirectory();
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Compatibility", "Midge",
            "c684310d9d8ce8ecd1151c47151d4bea98b348cf", "alternating-local-midge0");
        foreach (var file in Directory.EnumerateFiles(fixture, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(directory.Path, Path.GetRelativePath(fixture, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }

        var options = PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false);
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            var side = await database.ColumnFamilies.GetAsync("side");
            Assert.NotNull(side);
            await using (var reader = await database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily,
                             PantsTransactionMode.ReadOnly))
            {
                Assert.Equal("v0"u8.ToArray(), (await reader.GetAsync("point"u8.ToArray()))?.ToArray());
                Assert.Equal(new byte[] { 0, 255, 0 }, (await reader.GetAsync(Array.Empty<byte>()))?.ToArray());
                Assert.Equal("old"u8.ToArray(), (await reader.GetAsync("delete"u8.ToArray()))?.ToArray());
            }

            await using (var writer = await database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily,
                             PantsTransactionMode.ReadWrite))
            {
                writer.Put("point"u8.ToArray(), "v1"u8.ToArray());
                writer.Put(Array.Empty<byte>(), new byte[] { 0, 255, 1 });
                writer.Put("writer/1"u8.ToArray(), "v1"u8.ToArray());
                writer.Delete("delete"u8.ToArray());
                writer.DeleteRange("range/a"u8.ToArray(), "range/z"u8.ToArray());
                await writer.CommitAsync(PantsWriteOptions.Sync);
            }

            await using (var writer = await database.Transactions.BeginAsync(side, PantsTransactionMode.ReadWrite))
            {
                Assert.Equal("side0"u8.ToArray(), (await writer.GetAsync("cf"u8.ToArray()))?.ToArray());
                writer.Put("cf"u8.ToArray(), "side1"u8.ToArray());
                await writer.CommitAsync(PantsWriteOptions.Sync);
            }

            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
            await database.Maintenance.FlushAsync(side);
            await database.Maintenance.CompactAllAsync();
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);
        await using var recovered = await reopened.Transactions.BeginAsync(reopened.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Equal("v1"u8.ToArray(), (await recovered.GetAsync("point"u8.ToArray()))?.ToArray());
        Assert.Equal(new byte[] { 0, 255, 1 }, (await recovered.GetAsync(Array.Empty<byte>()))?.ToArray());
        Assert.Equal("v0"u8.ToArray(), (await recovered.GetAsync("writer/0"u8.ToArray()))?.ToArray());
        Assert.Equal("v1"u8.ToArray(), (await recovered.GetAsync("writer/1"u8.ToArray()))?.ToArray());
        Assert.Null(await recovered.GetAsync("delete"u8.ToArray()));
        Assert.Null(await recovered.GetAsync("range/a"u8.ToArray()));
        Assert.Equal("end"u8.ToArray(), (await recovered.GetAsync("range/z"u8.ToArray()))?.ToArray());
        var recoveredSide = await reopened.ColumnFamilies.GetAsync("side");
        Assert.NotNull(recoveredSide);
        await using var sideReader = await reopened.Transactions.BeginAsync(recoveredSide, PantsTransactionMode.ReadOnly);
        Assert.Equal("side1"u8.ToArray(), (await sideReader.GetAsync("cf"u8.ToArray()))?.ToArray());
    }
}

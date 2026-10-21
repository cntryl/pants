using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Compatibility;

public sealed class MidgeMemoryModeIsolationParityTests
{
    [Fact]
    public async Task ShouldPreserveEveryMemberWhenCommittingOneHundredMemoryWrites()
    {
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        var family = await database.ColumnFamilies.CreateAsync("test");
        await using (var transaction = await database.Transactions.BeginAsync(
                         family,
                         PantsTransactionMode.ReadWrite))
        {
            for (var index = 0; index < 100; index++)
            {
                transaction.Put(
                    TestBytes.FromString($"write_test_{index:000}"),
                    TestBytes.FromString("value"));
            }

            await transaction.CommitAsync(PantsWriteOptions.Buffered);
        }

        await using var reader = await database.Transactions.BeginAsync(
            family,
            PantsTransactionMode.ReadOnly);
        for (var index = 0; index < 100; index++)
        {
            var key = TestBytes.FromString($"write_test_{index:000}");
            Assert.Equal("value", TestBytes.ToText((await reader.GetAsync(key))!.Value));
        }
    }

    [Fact]
    public async Task ShouldPreserveFinalValuesWhenPutDeleteAndOverwriteAreCommitted()
    {
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        var family = await database.ColumnFamilies.CreateAsync("test");
        await using (var transaction = await database.Transactions.BeginAsync(
                         family,
                         PantsTransactionMode.ReadWrite))
        {
            transaction.Put(TestBytes.FromString("key1"), TestBytes.FromString("v1"));
            transaction.Put(TestBytes.FromString("key2"), TestBytes.FromString("v2"));
            await transaction.CommitAsync(PantsWriteOptions.Buffered);
        }

        await using (var transaction = await database.Transactions.BeginAsync(
                         family,
                         PantsTransactionMode.ReadWrite))
        {
            transaction.Delete(TestBytes.FromString("key1"));
            await transaction.CommitAsync(PantsWriteOptions.Buffered);
        }

        await using (var transaction = await database.Transactions.BeginAsync(
                         family,
                         PantsTransactionMode.ReadWrite))
        {
            transaction.Put(TestBytes.FromString("key1"), TestBytes.FromString("v1_new"));
            transaction.Put(TestBytes.FromString("key3"), TestBytes.FromString("v3"));
            await transaction.CommitAsync(PantsWriteOptions.Buffered);
        }

        await using var reader = await database.Transactions.BeginAsync(
            family,
            PantsTransactionMode.ReadOnly);
        Assert.Equal(
            "v1_new",
            TestBytes.ToText((await reader.GetAsync(TestBytes.FromString("key1")))!.Value));
        Assert.Equal(
            "v2",
            TestBytes.ToText((await reader.GetAsync(TestBytes.FromString("key2")))!.Value));
        Assert.Equal(
            "v3",
            TestBytes.ToText((await reader.GetAsync(TestBytes.FromString("key3")))!.Value));
    }

    [Fact]
    public async Task ShouldRemoveEveryMemberWhenDeletingFiftyMemoryKeys()
    {
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        var family = await database.ColumnFamilies.CreateAsync("test");
        await using (var transaction = await database.Transactions.BeginAsync(
                         family,
                         PantsTransactionMode.ReadWrite))
        {
            for (var index = 0; index < 50; index++)
            {
                transaction.Put(
                    TestBytes.FromString($"delete_test_{index:00}"),
                    TestBytes.FromString("value"));
            }

            await transaction.CommitAsync(PantsWriteOptions.Buffered);
        }

        await using (var transaction = await database.Transactions.BeginAsync(
                         family,
                         PantsTransactionMode.ReadWrite))
        {
            for (var index = 0; index < 50; index++)
            {
                transaction.Delete(TestBytes.FromString($"delete_test_{index:00}"));
            }

            await transaction.CommitAsync(PantsWriteOptions.Buffered);
        }

        await using var reader = await database.Transactions.BeginAsync(
            family,
            PantsTransactionMode.ReadOnly);
        for (var index = 0; index < 50; index++)
        {
            Assert.Null(await reader.GetAsync(TestBytes.FromString($"delete_test_{index:00}")));
        }
    }
}

using System.Text.Json;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Transactions;

public sealed class PantsSequenceExhaustionTests
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    [Fact]
    public async Task ShouldRejectMemoryCommitWithResourceLimitWithoutMutatingStateWhenSequenceSpaceIsExhausted()
    {
        await using var database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.InMemory(),
            new RuntimeDependencies(initialMemorySequence: long.MaxValue));

        await AssertExhaustedCommitLeavesStateUnchangedAsync(database, long.MaxValue);
    }

    [Fact]
    public async Task ShouldRejectLocalCommitWithResourceLimitWithoutMutatingStateWhenSequenceSpaceIsExhausted()
    {
        using var directory = new TemporaryDirectory();
        await SeedManifestSequenceAsync(directory.Path, long.MaxValue - 1);
        await using var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false));

        await AssertExhaustedCommitLeavesStateUnchangedAsync(database, long.MaxValue - 1);
    }

    [Fact]
    public async Task ShouldRejectCoalescedCommitsWithResourceLimitWithoutMutatingStateWhenSequenceSpaceIsExhausted()
    {
        using var directory = new TemporaryDirectory();
        await SeedManifestSequenceAsync(directory.Path, long.MaxValue - 1);
        await using var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false));

        var transactions = new List<IPantsTransaction>();
        for (var index = 0; index < 8; index++)
        {
            var transaction = await database.Transactions.BeginAsync(
                database.ColumnFamilies.DefaultFamily,
                PantsTransactionMode.ReadWrite);
            transaction.Put(TestBytes.FromString($"key-{index}"), TestBytes.FromString("value"));
            transactions.Add(transaction);
        }

        var errors = await Task.WhenAll(transactions.Select(transaction => Task.Run(async () =>
        {
            try
            {
                await transaction.CommitAsync(PantsWriteOptions.Sync);
                return null;
            }
            catch (PantsException exception)
            {
                return exception;
            }
            finally
            {
                await transaction.DisposeAsync();
            }
        })));

        Assert.All(errors, error =>
        {
            Assert.IsType<PantsResourceLimitException>(error);
            Assert.Equal(PantsErrorCode.ResourceLimit, error!.Code);
        });
        var metrics = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.Equal(long.MaxValue - 1, metrics.CurrentSequence);
        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Null(await reader.GetAsync(TestBytes.FromString("key-0")));
    }

    static async Task AssertExhaustedCommitLeavesStateUnchangedAsync(
        IPantsDatabase database,
        long expectedSequence)
    {
        await using (var writer = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            writer.Put(TestBytes.FromString("key"), TestBytes.FromString("value"));
            var error = await Assert.ThrowsAsync<PantsResourceLimitException>(() =>
                writer.CommitAsync(PantsWriteOptions.Sync).AsTask());
            Assert.Equal(PantsErrorCode.ResourceLimit, error.Code);
        }

        var metrics = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.Equal(expectedSequence, metrics.CurrentSequence);
        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Null(await reader.GetAsync(TestBytes.FromString("key")));
    }

    static async Task SeedManifestSequenceAsync(string path, long sequence)
    {
        var manifest = new ManifestState
        {
            LastPersistedSequence = (ulong)sequence,
            ColumnFamilies = [new ColumnFamilyMeta { Id = 0, Name = "default" }]
        };
        await File.WriteAllTextAsync(Path.Combine(path, "FORMAT"), "midge-format-version=3\n");
        await File.WriteAllTextAsync(
            Path.Combine(path, "manifest.json"),
            JsonSerializer.Serialize(manifest, JsonOptions));
    }
}

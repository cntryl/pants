using System.Text.Json;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Transactions;

public sealed class PantsSequenceExhaustionTests
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldAdmitOnlyAnEpochWhoseWholeReservationFitsWithoutChangingRejectedWal(bool pastBoundary)
    {
        using var directory = new TemporaryDirectory();
        var source = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Compatibility", "Midge",
            "c684310d9d8ce8ecd1151c47151d4bea98b348cf", "unsigned-sequence-high-sst");
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(directory.Path, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }

        var floor = ulong.MaxValue - (1UL << 20) + (pastBoundary ? 1UL : 0UL);
        var walDirectory = Path.Combine(directory.Path, "wal");
        WalLedgerStore.LoadExisting(walDirectory).Reserve(floor);
        var options = PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false);
        if (!pastBoundary)
        {
            await using var database = await PantsDatabase.OpenAsync(options);
            Assert.Equal(floor, (await database.Diagnostics.GetRuntimeMetricsAsync()).CurrentSequence);
            Assert.Equal(ulong.MaxValue, WalLedgerStore.LoadExisting(walDirectory).ReservedThrough);
            await using (var transaction = await database.Transactions.BeginAsync(
                             database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite))
            {
                transaction.Put("last-epoch"u8.ToArray(), "accepted"u8.ToArray());
                await transaction.CommitAsync(PantsWriteOptions.Sync);
            }

            Assert.Equal(floor + 3, (await database.Diagnostics.GetRuntimeMetricsAsync()).CurrentSequence);
            await using (var reader = await database.Transactions.BeginAsync(
                             database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly))
            {
                Assert.Equal("accepted"u8.ToArray(), (await reader.GetAsync("last-epoch"u8.ToArray()))?.ToArray());
                Assert.Equal("midge-codec"u8.ToArray(), (await reader.GetAsync("u64-row"u8.ToArray()))?.ToArray());
            }

            await database.ShutdownAsync(TestTimeouts.Expected);
        }

        var walBefore = Directory.GetFiles(walDirectory).Where(static path => Path.GetExtension(path) == ".wal" || Path.GetFileName(path) == "LEDGER").ToDictionary(static path => Path.GetFileName(path)!, File.ReadAllBytes);
        var error = await Assert.ThrowsAsync<PantsResourceLimitException>(() =>
            PantsDatabase.OpenAsync(options).AsTask());
        Assert.Equal(PantsErrorCode.ResourceLimit, error.Code);
        Assert.Equal(walBefore.Keys.Order(), Directory.GetFiles(walDirectory).Where(static path => Path.GetExtension(path) == ".wal" || Path.GetFileName(path) == "LEDGER").Select(Path.GetFileName).Order());
        foreach (var (name, bytes) in walBefore)
        {
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(walDirectory, name!)));
        }

        // Offline readers can verify the valid u64 history even when no writer epoch fits.
        var report = await PantsDatabase.VerifyPathAsync(directory.Path);
        Assert.True(report.Authoritative);
        if (!pastBoundary)
        {
            Assert.True(report.WalBoundary > long.MaxValue);
        }
    }

    [Fact]
    public async Task ShouldRejectMemoryCommitWithResourceLimitWithoutMutatingStateWhenSequenceSpaceIsExhausted()
    {
        await using var database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.InMemory(),
            new RuntimeDependencies(initialMemorySequence: ulong.MaxValue));

        await AssertExhaustedCommitLeavesStateUnchangedAsync(database, ulong.MaxValue);
    }

    [Fact]
    public async Task ShouldRejectOpeningExhaustedLocalSequenceSpaceWithResourceLimit()
    {
        using var directory = new TemporaryDirectory();
        await SeedManifestSequenceAsync(directory.Path, ulong.MaxValue - 1);
        var error = await Assert.ThrowsAsync<PantsResourceLimitException>(() => PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false)).AsTask());
        Assert.Equal(PantsErrorCode.ResourceLimit, error.Code);
    }

    [Fact]
    public async Task ShouldRejectConcurrentMemoryCommitsWithResourceLimitWithoutMutatingStateWhenSequenceSpaceIsExhausted()
    {
        await using var database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.InMemory(),
            new RuntimeDependencies(initialMemorySequence: ulong.MaxValue - 1));

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
        Assert.Equal(ulong.MaxValue - 1, metrics.CurrentSequence);
        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Null(await reader.GetAsync(TestBytes.FromString("key-0")));
    }

    static async Task AssertExhaustedCommitLeavesStateUnchangedAsync(
        IPantsDatabase database,
        ulong expectedSequence)
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

    static async Task SeedManifestSequenceAsync(string path, ulong sequence)
    {
        var manifest = new ManifestState
        {
            FlushedThrough = new Dictionary<uint, ulong> { [0] = sequence }
        };
        await File.WriteAllTextAsync(Path.Combine(path, "FORMAT"), "midge-format-version=9\n");
        await File.WriteAllBytesAsync(
            Path.Combine(path, "manifest.snapshot.json"),
            ManifestCodec.Encode(manifest, JsonOptions));
    }
}

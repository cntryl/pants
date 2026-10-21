using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Compatibility;

public sealed class MidgeTransactionIsolationLwwParityTests
{
    static readonly string[] AllModes = ["memory", "local", "cloud"];

    [Fact]
    public async Task ShouldHideUncommittedWritesGivenUncommittedWriteWhenReadDifferentMode()
    {
        await ForEachModeAsync(AllModes, async (mode, path) =>
        {
            await using var database = await OpenAsync(mode, path);
            await using var writer = await BeginWriteAsync(database);
            writer.Put("key"u8.ToArray(), "uncommitted"u8.ToArray());
            await using var reader = await BeginReadAsync(database);

            Assert.Null(await reader.GetAsync("key"u8.ToArray()));
        });
    }

    [Fact]
    public async Task ShouldApplyLastCommittedWriteGivenMultipleCommitsWhenLastWriteWins()
    {
        await ForEachModeAsync(AllModes, async (mode, path) =>
        {
            await using var database = await OpenAsync(mode, path);
            await using var first = await BeginWriteAsync(database);
            await using var second = await BeginWriteAsync(database);
            first.Put("key"u8.ToArray(), "from_txn1"u8.ToArray());
            second.Put("key"u8.ToArray(), "from_txn2"u8.ToArray());
            await first.CommitAsync(WriteOptions(mode));
            await second.CommitAsync(WriteOptions(mode));

            Assert.Equal("from_txn2", await ReadAsync(database, "key"));
        });
    }

    [Fact]
    public async Task ShouldAllowLostUpdateGivenConcurrentWritesWhenLostUpdateOccurs()
    {
        await ForEachModeAsync(AllModes, async (mode, path) =>
        {
            await using var database = await OpenAsync(mode, path);
            await PutAsync(database, mode, "counter", "0");
            await using var first = await BeginWriteAsync(database);
            await using var second = await BeginWriteAsync(database);
            Assert.Equal("0", TestBytes.ToText((await first.GetAsync("counter"u8.ToArray()))!.Value));
            Assert.Equal("0", TestBytes.ToText((await second.GetAsync("counter"u8.ToArray()))!.Value));
            first.Put("counter"u8.ToArray(), "1"u8.ToArray());
            second.Put("counter"u8.ToArray(), "1"u8.ToArray());
            await first.CommitAsync(WriteOptions(mode));
            await second.CommitAsync(WriteOptions(mode));

            Assert.Equal("1", await ReadAsync(database, "counter"));
        });
    }

    [Fact]
    public async Task ShouldAllowDisjointWritesAfterSharedReadWhenTransactionsBothCommit()
    {
        await ForEachModeAsync(AllModes, async (mode, path) =>
        {
            await using var database = await OpenAsync(mode, path);
            await PutAsync(database, mode, "shared", "base_value");
            await PutAsync(database, mode, "flag1", "false");
            await PutAsync(database, mode, "flag2", "false");
            await using var first = await BeginWriteAsync(database);
            await using var second = await BeginWriteAsync(database);
            Assert.Equal("base_value", TestBytes.ToText((await first.GetAsync("shared"u8.ToArray()))!.Value));
            Assert.Equal("base_value", TestBytes.ToText((await second.GetAsync("shared"u8.ToArray()))!.Value));
            first.Put("flag1"u8.ToArray(), "true"u8.ToArray());
            second.Put("flag2"u8.ToArray(), "true"u8.ToArray());
            await first.CommitAsync(WriteOptions(mode));
            await second.CommitAsync(WriteOptions(mode));

            Assert.Equal("true", await ReadAsync(database, "flag1"));
            Assert.Equal("true", await ReadAsync(database, "flag2"));
        });
    }

    [Fact]
    public async Task ShouldAbortSecondCommitGivenConflictingWritesWhenAbortOnWriteConflictEnabled()
    {
        await ForEachModeAsync(AllModes, async (mode, path) =>
        {
            await using var database = await OpenAsync(mode, path);
            await using var first = await BeginWriteAsync(database);
            await using var second = await BeginWriteAsync(database);
            first.SetConflictPolicy(PantsConflictPolicy.AbortOnWriteConflict);
            second.SetConflictPolicy(PantsConflictPolicy.AbortOnWriteConflict);
            first.Put("key"u8.ToArray(), "from_txn1"u8.ToArray());
            second.Put("key"u8.ToArray(), "from_txn2"u8.ToArray());
            await first.CommitAsync(WriteOptions(mode));

            await Assert.ThrowsAsync<PantsWriteConflictException>(() =>
                second.CommitAsync(WriteOptions(mode)).AsTask());

            Assert.Equal("from_txn1", await ReadAsync(database, "key"));
        });
    }

    [Fact]
    public async Task ShouldAbortDeleteRangeCommitGivenOverlappingRecentWritesWhenAbortOnWriteConflictEnabled()
    {
        await ForEachModeAsync(AllModes, async (mode, path) =>
        {
            await using var database = await OpenAsync(mode, path);
            await using var pointWrite = await BeginWriteAsync(database);
            await using var rangeDelete = await BeginWriteAsync(database);
            pointWrite.SetConflictPolicy(PantsConflictPolicy.AbortOnWriteConflict);
            rangeDelete.SetConflictPolicy(PantsConflictPolicy.AbortOnWriteConflict);
            pointWrite.Put("m"u8.ToArray(), "v1"u8.ToArray());
            rangeDelete.DeleteRange("a"u8.ToArray(), "z"u8.ToArray());
            await pointWrite.CommitAsync(WriteOptions(mode));

            var conflict = await Assert.ThrowsAsync<PantsWriteConflictException>(() =>
                rangeDelete.CommitAsync(WriteOptions(mode)).AsTask());

            Assert.True(conflict.IsRangeConflict);
            Assert.Equal("v1", await ReadAsync(database, "m"));
        });
    }

    [Fact]
    public async Task ShouldAbortPointWriteCommitGivenRecentOverlappingDeleteRangeWhenAbortOnWriteConflictEnabled()
    {
        await ForEachModeAsync(AllModes, async (mode, path) =>
        {
            await using var database = await OpenAsync(mode, path);
            await using var rangeDelete = await BeginWriteAsync(database);
            await using var pointWrite = await BeginWriteAsync(database);
            rangeDelete.SetConflictPolicy(PantsConflictPolicy.AbortOnWriteConflict);
            pointWrite.SetConflictPolicy(PantsConflictPolicy.AbortOnWriteConflict);
            rangeDelete.DeleteRange("a"u8.ToArray(), "z"u8.ToArray());
            pointWrite.Put("m"u8.ToArray(), "v2"u8.ToArray());
            await rangeDelete.CommitAsync(WriteOptions(mode));

            var conflict = await Assert.ThrowsAsync<PantsWriteConflictException>(() =>
                pointWrite.CommitAsync(WriteOptions(mode)).AsTask());

            Assert.False(conflict.IsRangeConflict);
            Assert.Null(await ReadAsync(database, "m"));
        });
    }

    static async Task ForEachModeAsync(string[] modes, Func<string, string, Task> scenario)
    {
        foreach (var mode in modes)
        {
            using var directory = new TemporaryDirectory();
            await scenario(mode, directory.Path);
        }
    }

    static ValueTask<IPantsDatabase> OpenAsync(string mode, string path) => mode switch
    {
        "memory" => PantsDatabase.OpenAsync(PantsOpenOptions.InMemory()),
        "local" => PantsDatabase.OpenAsync(PantsOpenOptions.Local(path).WithBackgroundCompaction(false)),
        "cloud" => PantsDatabase.OpenAsync(
            PantsOpenOptions.SimulatedCloud(path, "pants-tests", "midge-isolation-lww/")
                .WithBackgroundCompaction(false)),
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown storage mode.")
    };

    static ValueTask<IPantsTransaction> BeginWriteAsync(IPantsDatabase database) =>
        database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite);

    static ValueTask<IPantsTransaction> BeginReadAsync(IPantsDatabase database) =>
        database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);

    static PantsWriteOptions WriteOptions(string mode) =>
        mode == "cloud" ? PantsWriteOptions.CloudAsync : PantsWriteOptions.Buffered;

    static async Task PutAsync(IPantsDatabase database, string mode, string key, string value)
    {
        await using var transaction = await BeginWriteAsync(database);
        transaction.Put(TestBytes.FromString(key), TestBytes.FromString(value));
        await transaction.CommitAsync(WriteOptions(mode));
    }

    static async Task<string?> ReadAsync(IPantsDatabase database, string key)
    {
        await using var reader = await BeginReadAsync(database);
        var value = await reader.GetAsync(TestBytes.FromString(key));
        return value is { } present ? TestBytes.ToText(present) : null;
    }
}

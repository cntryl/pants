using System.Buffers.Binary;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Transactions.Spill;

public sealed class PantsPostWalApplyFailureTests
{
    [Fact]
    public async Task ShouldRetainTheOldPublishedCutAndFenceWritesWhenSpillCorruptsAfterWalCommit()
    {
        using var directory = new TemporaryDirectory();
        var corruption = new CorruptBeforeApply();
        var options = TransactionSpillHardeningTestHarness.CreateLocalOptions(directory.Path, 2048);
        await using (var database = await PantsDatabase.OpenForTestingAsync(options, new RuntimeDependencies(corruption)))
        {
            await using var frozen = await database.Transactions.BeginAsync(
                database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
            await using var write = await database.Transactions.BeginAsync(
                database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite);
            for (var index = 0; index < 12; index++)
            {
                write.Put(TestBytes.FromString($"row:{index:000}"), "v"u8.ToArray());
            }

            write.Put("zz:spill-trigger"u8.ToArray(), new byte[1800]);
            corruption.Path = Assert.Single(Directory.EnumerateFiles(Path.Combine(directory.Path, "txn"), "*.run"));
            var failure = await Record.ExceptionAsync(() => write.CommitAsync(PantsWriteOptions.Sync).AsTask());
            var laterFailure = await Record.ExceptionAsync(async () =>
            {
                await using var later = await database.Transactions.BeginAsync(
                    database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite);
                later.Put("later"u8.ToArray(), "value"u8.ToArray());
                await later.CommitAsync(PantsWriteOptions.Sync);
            });
            await using var current = await database.Transactions.BeginAsync(
                database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
            Assert.Null(await current.GetAsync("row:000"u8.ToArray()));
            Assert.Null(await current.GetAsync("row:001"u8.ToArray()));
            Assert.Null(await current.GetAsync("later"u8.ToArray()));
            Assert.Null(await frozen.GetAsync("row:000"u8.ToArray()));
            Assert.IsType<PantsCommitOutcomeUnknownException>(failure);
            Assert.IsType<PantsFencedException>(laterFailure);
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);
        await using var reader = await reopened.Transactions.BeginAsync(
            reopened.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        for (var index = 0; index < 12; index++)
        {
            Assert.Equal("v"u8.ToArray(), (await reader.GetAsync(TestBytes.FromString($"row:{index:000}")))?.ToArray());
        }

        Assert.Equal(1800, (await reader.GetAsync("zz:spill-trigger"u8.ToArray()))?.Length);
        Assert.Null(await reader.GetAsync("later"u8.ToArray()));
    }

    sealed class CorruptBeforeApply : IFailpointHandler
    {
        public string? Path { get; set; }

        public void Hit(Failpoint failpoint)
        {
            if (failpoint != Failpoint.BeforeCommitApply || Path is not { } path)
            {
                return;
            }

            Path = null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            long offset = 48;
            Span<byte> frame = stackalloc byte[8];
            for (var index = 0; index <= 3; index++)
            {
                stream.Position = offset;
                stream.ReadExactly(frame);
                var length = BinaryPrimitives.ReadUInt32LittleEndian(frame);
                if (index == 3)
                {
                    stream.Position = offset + 8 + length - 1;
                    var original = stream.ReadByte();
                    stream.Position--;
                    stream.WriteByte((byte)(original ^ 0xff));
                    stream.Flush(true);
                }

                offset += 8 + length;
            }
        }
    }
}

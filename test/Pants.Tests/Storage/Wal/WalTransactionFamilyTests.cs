using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage.Wal;

public sealed class WalTransactionFamilyTests
{
    [Theory]
    [InlineData(1U)]
    [InlineData(7U)]
    [InlineData(uint.MaxValue)]
    public void ShouldEncodeTheTransactionFamilyInTheBatchEnvelope(uint family)
    {
        var bytes = WalCodec.EncodeTransactionBatch(1, 1, 1, [Mutation(family)]);

        var record = WalCodec.DecodeRecord(bytes);

        Assert.Equal(family, record.ColumnFamilyId);
        Assert.Equal(family, Assert.Single(WalCodec.DecodeTransactionBatch(record, out _, out _)).ColumnFamilyId);
    }

    [Fact]
    public void ShouldRejectBatchMembersOutsideTheirEnvelopeFamily()
    {
        var bytes = WalCodec.EncodeTransactionBatch(1, 1, 1, [Mutation(7)]);
        var foreign = WalCodec.DecodeRecord(bytes) with { ColumnFamilyId = 3 };

        Assert.Throws<PantsCorruptionException>(() => WalCodec.DecodeTransactionBatch(foreign, out _, out _));
    }

    [Fact]
    public void ShouldRejectEncodingOneTransactionAcrossFamilies()
    {
        Assert.Throws<PantsInvalidArgumentException>(() =>
            WalCodec.EncodeTransactionBatch(1, 1, 1, [Mutation(0), Mutation(1)]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShouldRejectSplitTransactionMembersOrCommitFromAnotherFamily(bool wrongCommit)
    {
        using var directory = new TemporaryDirectory();
        using var recovery = new WalRecoveryStateMachine(directory.Path);
        var applied = 0;
        var begin = WalCodec.DecodeRecord(WalCodec.EncodeTransactionMarker(WalOperation.TransactionBegin, 1, 1, 1)) with
        {
            ColumnFamilyId = 7
        };
        recovery.Accept(begin, (_, _) => applied++);
        var member = WalCodec.DecodeRecord(WalCodec.EncodeTransactionMutation(Mutation(wrongCommit ? 7U : 3U), 1, 1));
        if (wrongCommit)
        {
            recovery.Accept(member, (_, _) => applied++);
            var commit = WalCodec.DecodeRecord(WalCodec.EncodeTransactionMarker(WalOperation.TransactionCommit, 1, 3, 1)) with
            {
                ColumnFamilyId = 3
            };
            Assert.Throws<PantsCorruptionException>(() => recovery.Accept(commit, (_, _) => applied++));
        }
        else
        {
            Assert.Throws<PantsCorruptionException>(() => recovery.Accept(member, (_, _) => applied++));
        }

        Assert.Equal(0, applied);
    }

    [Fact]
    public async Task ShouldWriteTheFamilyOnEverySpilledTransactionRecordAndRecoverIt()
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.Local(directory.Path).WithTransactionMemoryPool(16 * 1024)
            .WithBackgroundCompaction(false);
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            var family = await database.ColumnFamilies.CreateAsync("side");
            await using (var writer = await database.Transactions.BeginAsync(family, PantsTransactionMode.ReadWrite))
            {
                for (var index = 0; index < 100; index++)
                {
                    writer.Put(BitConverter.GetBytes(index), new byte[512]);
                }

                await writer.CommitAsync(PantsWriteOptions.Sync);
            }

            var records = new List<WalRecord>();
            WalFrameReader.Visit(File.ReadAllBytes(WalTestFiles.ActivePath(directory.Path)),
                (record, _) => records.Add(record));
            Assert.Contains(records, static record => record.Operation == WalOperation.TransactionBegin);
            Assert.Contains(records, static record => record.Operation == WalOperation.TransactionCommit);
            Assert.All(records, record => Assert.Equal(family.Id, record.ColumnFamilyId));
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);
        var recoveredFamily = await reopened.ColumnFamilies.GetAsync("side");
        Assert.NotNull(recoveredFamily);
        await using var reader = await reopened.Transactions.BeginAsync(recoveredFamily, PantsTransactionMode.ReadOnly);
        for (var index = 0; index < 100; index++)
        {
            Assert.Equal(new byte[512], (await reader.GetAsync(BitConverter.GetBytes(index)))?.ToArray());
        }
    }

    static WalMutation Mutation(uint family) => new(family, WalOperation.Put, "key"u8.ToArray(), "value"u8.ToArray(), 2, null, null);
}

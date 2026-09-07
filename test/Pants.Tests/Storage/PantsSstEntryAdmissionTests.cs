using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage;

public sealed class PantsSstEntryAdmissionTests
{
    /// <summary>
    ///     Prefix compression cannot be relied on for admission: flush or compaction may place any
    ///     entry first in a block, where it carries its key in full. The boundary is therefore the
    ///     worst-case encoded size, including the wider header a long key forces.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(ushort.MaxValue)]
    [InlineData(ushort.MaxValue + 1)]
    public void ShouldAdmitOnlyEntriesWithinTheDecodedBlockLimit(int keyLength)
    {
        var header = keyLength > ushort.MaxValue ? 34 : 26;
        var valueLength = DiskFormat.MaximumDecodedBlockBytes - header - keyLength;

        SstCodec.ValidateEntrySize(keyLength, valueLength);

        Assert.Throws<PantsResourceLimitException>(() =>
            SstCodec.ValidateEntrySize(keyLength, valueLength + 1));
    }

    [Fact]
    public void ShouldRejectEntrySizeGivenLengthsOverflow()
    {
        Assert.Throws<PantsResourceLimitException>(() =>
            SstCodec.ValidateEntrySize(int.MaxValue, 1));
        Assert.Throws<PantsResourceLimitException>(() =>
            SstCodec.ValidateEntrySize(1, int.MaxValue));
    }

    /// <summary>
    ///     Rejection has to happen where the caller staged the write, not later at flush: accepting
    ///     data the engine cannot subsequently write down surfaces the failure detached from its
    ///     cause, on a background path the caller cannot handle.
    /// </summary>
    [Fact]
    public async Task ShouldRejectOversizedValueWhenStagedAndStillAcceptLaterCommit()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false));
        var family = database.ColumnFamilies.DefaultFamily;

        await using (var transaction = await database.Transactions.BeginAsync(
                         family,
                         PantsTransactionMode.ReadWrite))
        {
            Assert.Throws<PantsResourceLimitException>(() =>
                transaction.Put(
                    TestBytes.FromString("oversized"),
                    new byte[DiskFormat.MaximumDecodedBlockBytes]));

            transaction.Put(TestBytes.FromString("kept"), TestBytes.FromString("value"));
            await transaction.CommitAsync(PantsWriteOptions.Sync);
        }

        await using var reader = await database.Transactions.BeginAsync(
            family,
            PantsTransactionMode.ReadOnly);
        Assert.Null(await reader.GetAsync(TestBytes.FromString("oversized")));
        Assert.Equal(
            "value",
            TestBytes.ToText((await reader.GetAsync(TestBytes.FromString("kept")))!.Value));
    }
}

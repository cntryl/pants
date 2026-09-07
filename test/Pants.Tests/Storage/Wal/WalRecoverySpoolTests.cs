using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage.Wal;

public sealed class WalRecoverySpoolTests
{
    /// <summary>
    ///     Recovery opens one spool per transaction left open in the WAL, and that count is bounded
    ///     only by the WAL's contents. Deferring the file until something is actually spooled keeps
    ///     a WAL full of empty or interleaved transactions from exhausting file handles, without
    ///     capping the count — a cap could refuse a WAL this engine legitimately wrote.
    /// </summary>
    [Fact]
    public void ShouldNotOpenAFileUntilARecordIsSpooled()
    {
        using var directory = new TemporaryDirectory();

        var spools = Enumerable.Range(0, 64)
            .Select(_ => new WalRecoverySpool(directory.Path))
            .ToArray();

        try
        {
            Assert.Empty(Directory.GetFiles(directory.Path));
        }
        finally
        {
            foreach (var spool in spools)
            {
                spool.Dispose();
            }
        }
    }

    [Fact]
    public void ShouldRoundTripSpooledRecordsBeneathTheDatabaseDirectory()
    {
        using var directory = new TemporaryDirectory();
        using var spool = new WalRecoverySpool(directory.Path);
        var record = new WalRecord(
            1,
            WalOperation.Put,
            TestBytes.FromString("key"),
            TestBytes.FromString("value"),
            7,
            null,
            null,
            3,
            9);

        spool.Append(record);

        Assert.NotEmpty(Directory.GetFiles(directory.Path));
        var replayed = new List<WalRecord>();
        spool.Replay(replayed.Add);
        var single = Assert.Single(replayed);
        Assert.Equal(WalOperation.Put, single.Operation);
        Assert.Equal("value", TestBytes.ToText(single.Value));
    }

    [Fact]
    public void ShouldRemoveItsFileOnDispose()
    {
        using var directory = new TemporaryDirectory();
        using (var spool = new WalRecoverySpool(directory.Path))
        {
            spool.Append(new WalRecord(
                1,
                WalOperation.Put,
                TestBytes.FromString("key"),
                TestBytes.FromString("value"),
                7,
                null,
                null,
                3,
                9));
            Assert.NotEmpty(Directory.GetFiles(directory.Path));
        }

        Assert.Empty(Directory.GetFiles(directory.Path));
    }
}

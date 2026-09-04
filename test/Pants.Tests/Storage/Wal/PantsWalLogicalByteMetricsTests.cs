using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage.Wal;

public sealed class PantsWalLogicalByteMetricsTests
{
    [Fact]
    public async Task ShouldCountPutKeyAndValueBytesExactlyWhenVerifyingAndRecovering()
    {
        var record = CreateRecord(
            WalOperation.Put,
            "alpha",
            "bravo",
            1);

        await AssertWalMetricsAsync([record], 1, 10);
    }

    [Fact]
    public async Task ShouldCountDeleteRangeBoundaryBytesExactlyWhenVerifyingAndRecovering()
    {
        var record = CreateRecord(
            WalOperation.DeleteRange,
            "alpha",
            null,
            1,
            "omega");

        await AssertWalMetricsAsync([record], 1, 10);
    }

    [Fact]
    public async Task ShouldCountOuterBatchKeyAndPayloadBytesExactlyWhenVerifyingAndRecovering()
    {
        var record = WalCodec.EncodeTransactionBatch(
            7,
            1,
            1,
            [
                new WalMutation(
                    0,
                    WalOperation.Put,
                    "batch-key"u8.ToArray(),
                    "batch-value"u8.ToArray(),
                    0,
                    null,
                    null)
            ]);

        await AssertWalMetricsAsync([record], 1, 78);
    }

    [Fact]
    public async Task ShouldCountEncodedMarkerKeysExactlyWhenVerifyingAndRecovering()
    {
        var begin = WalCodec.EncodeTransactionMarker(
            WalOperation.TransactionBegin,
            7,
            1,
            1);
        var commit = WalCodec.EncodeTransactionMarker(
            WalOperation.TransactionCommit,
            7,
            2,
            1);

        await AssertWalMetricsAsync([begin, commit], 2, 6);
    }

    static async Task AssertWalMetricsAsync(
        IReadOnlyList<byte[]> records,
        long expectedRecords,
        long expectedBytes)
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.Local(directory.Path)
            .WithBackgroundCompaction(false);
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
        }

        await File.WriteAllBytesAsync(
            Path.Combine(directory.Path, "wal", "wal.log"),
            Frame(records));

        var verification = await PantsDatabase.VerifyPathAsync(directory.Path);
        Assert.Equal(expectedRecords, verification.WalRecoveryRecordsReplayed);
        Assert.Equal(expectedBytes, verification.WalRecoveryBytesReplayed);

        await using var reopened = await PantsDatabase.OpenAsync(options);
        var recovery = await reopened.Diagnostics.GetRecoveryMetricsAsync();
        Assert.Equal(expectedRecords, recovery.WalRecordsReplayed);
        Assert.Equal(expectedBytes, recovery.WalBytesReplayed);
    }

    static byte[] CreateRecord(
        WalOperation operation,
        string key,
        string? value,
        ulong sequence,
        string? rangeEnd = null) =>
        WalCodec.EncodeRecord(new WalRecord(
            0,
            operation,
            TestBytes.FromString(key),
            value is null ? null : TestBytes.FromString(value),
            sequence,
            null,
            rangeEnd is null ? null : TestBytes.FromString(rangeEnd),
            null,
            1));

    static byte[] Frame(IEnumerable<byte[]> payloads)
    {
        using var stream = new MemoryStream();
        foreach (var payload in payloads)
        {
            DiskFormat.WriteUInt32(stream, checked((uint)payload.Length));
            DiskFormat.WriteUInt32(stream, DiskFormat.Crc32C(payload));
            stream.Write(payload);
        }

        return stream.ToArray();
    }
}

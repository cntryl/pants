using System.Buffers.Binary;

namespace Cntryl.Pants.Tests;

public sealed class PantsWalVerificationTailParityTests
{
    const string SealedWalName = "00000000000000000001.wal";

    [Fact]
    public async Task ShouldAcceptIncompleteFinalActiveWalTailWhenVerifyingOffline()
    {
        using var directory = new TemporaryDirectory();
        var walPath = await CreateWalAsync(directory.Path, commitCount: 2);
        await TruncateFinalFrameAsync(walPath);
        var truncatedLength = new FileInfo(walPath).Length;

        var report = await PantsDatabase.VerifyPathAsync(directory.Path);

        Assert.Equal(1, report.WalRecoveryRecordsReplayed);
        Assert.NotNull(report.WalBoundary);
        Assert.Equal(truncatedLength, new FileInfo(walPath).Length);
    }

    [Fact]
    public async Task ShouldAcceptZeroFilledFinalActiveWalTailWhenVerifyingOffline()
    {
        using var directory = new TemporaryDirectory();
        var walPath = await CreateWalAsync(directory.Path, commitCount: 1);
        await AppendZeroTailAsync(walPath);
        var preallocatedLength = new FileInfo(walPath).Length;

        var report = await PantsDatabase.VerifyPathAsync(directory.Path);

        Assert.Equal(1, report.WalRecoveryRecordsReplayed);
        Assert.NotNull(report.WalBoundary);
        Assert.Equal(preallocatedLength, new FileInfo(walPath).Length);
    }

    [Fact]
    public async Task ShouldRejectIncompleteFinalSealedWalTailWhenVerifyingOffline()
    {
        using var directory = new TemporaryDirectory();
        var walPath = await CreateSealedWalAsync(directory.Path, commitCount: 2);
        await TruncateFinalFrameAsync(walPath);

        var exception = await Assert.ThrowsAsync<PantsCorruptionException>(
            () => PantsDatabase.VerifyPathAsync(directory.Path).AsTask());

        Assert.Equal(PantsErrorCode.Corruption, exception.Code);
    }

    [Fact]
    public async Task ShouldRejectZeroFilledFinalSealedWalTailWhenVerifyingOffline()
    {
        using var directory = new TemporaryDirectory();
        var walPath = await CreateSealedWalAsync(directory.Path, commitCount: 1);
        await AppendZeroTailAsync(walPath);

        var exception = await Assert.ThrowsAsync<PantsCorruptionException>(
            () => PantsDatabase.VerifyPathAsync(directory.Path).AsTask());

        Assert.Equal(PantsErrorCode.Corruption, exception.Code);
    }

    [Fact]
    public async Task ShouldRejectChecksumCorruptionBeforeIncompleteActiveTailWhenVerifyingOffline()
    {
        using var directory = new TemporaryDirectory();
        var walPath = await CreateWalAsync(directory.Path, commitCount: 2);
        var bytes = await File.ReadAllBytesAsync(walPath);
        bytes[sizeof(uint)] ^= byte.MaxValue;
        await File.WriteAllBytesAsync(walPath, bytes[..^3]);

        var exception = await Assert.ThrowsAsync<PantsCorruptionException>(
            () => PantsDatabase.VerifyPathAsync(directory.Path).AsTask());

        Assert.Equal(PantsErrorCode.Corruption, exception.Code);
    }

    [Fact]
    public async Task ShouldRejectStructuralCorruptionBeforeIncompleteActiveTailWhenVerifyingOffline()
    {
        using var directory = new TemporaryDirectory();
        var walPath = await CreateWalAsync(directory.Path, commitCount: 2);
        var bytes = await File.ReadAllBytesAsync(walPath);
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes,
            MidgeDiskFormat.WalMaximumRecordBytes + 1U);
        await File.WriteAllBytesAsync(walPath, bytes[..^3]);

        var exception = await Assert.ThrowsAsync<PantsCorruptionException>(
            () => PantsDatabase.VerifyPathAsync(directory.Path).AsTask());

        Assert.Equal(PantsErrorCode.Corruption, exception.Code);
    }

    [Fact]
    public async Task ShouldRejectCorruptedLengthHidingVerifiedActiveWalSuffixWhenVerifyingOffline()
    {
        using var directory = new TemporaryDirectory();
        var walPath = await CreateWalAsync(directory.Path, commitCount: 3);
        var bytes = await File.ReadAllBytesAsync(walPath);
        var firstPayloadLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes));
        var secondFrameOffset = (2 * sizeof(uint)) + firstPayloadLength;
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(secondFrameOffset),
            checked((uint)bytes.Length));
        await File.WriteAllBytesAsync(walPath, bytes);

        var exception = await Assert.ThrowsAsync<PantsCorruptionException>(
            () => PantsDatabase.VerifyPathAsync(directory.Path).AsTask());

        Assert.Equal(PantsErrorCode.Corruption, exception.Code);
    }

    static async Task<string> CreateWalAsync(string path, int commitCount)
    {
        await using (var database = await PantsDatabase.OpenAsync(
                         PantsOpenOptions.Local(path).WithBackgroundCompaction(false)))
        {
            for (var index = 0; index < commitCount; index++)
            {
                await using var transaction = await database.BeginTransactionAsync(
                    database.DefaultColumnFamily,
                    PantsTransactionMode.ReadWrite);
                transaction.Put(
                    TestBytes.FromString($"wal-tail-key-{index}"),
                    TestBytes.FromString($"wal-tail-value-{index}"));
                await transaction.CommitAsync(PantsWriteOptions.Sync);
            }
        }

        return Path.Combine(path, "wal", "wal.log");
    }

    static async Task<string> CreateSealedWalAsync(string path, int commitCount)
    {
        var activePath = await CreateWalAsync(path, commitCount);
        var sealedPath = Path.Combine(path, "wal", SealedWalName);
        File.Move(activePath, sealedPath);
        return sealedPath;
    }

    static async Task TruncateFinalFrameAsync(string path)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Write,
            FileShare.None);
        stream.SetLength(stream.Length - 3);
        await stream.FlushAsync();
    }

    static async Task AppendZeroTailAsync(string path)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.None);
        await stream.WriteAsync(new byte[16 * 1024]);
        await stream.FlushAsync();
    }
}

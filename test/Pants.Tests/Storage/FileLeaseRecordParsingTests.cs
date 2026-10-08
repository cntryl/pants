using System.Text;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage;

public sealed class FileLeaseRecordParsingTests
{
    static readonly TimeSpan LongHeartbeatInterval = TimeSpan.FromHours(1);
    static readonly DateTimeOffset Now = new(2040, 1, 1, 12, 0, 0, TimeSpan.Zero);
    static readonly TimeSpan TimeToLive = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task ShouldTreatNonUtf8LeaderRecordAsIndeterminate()
    {
        using var directory = new TemporaryDirectory();
        var prefix = Encoding.UTF8.GetBytes("epoch: 5\nholder_id: writer-");
        var suffix = Encoding.UTF8.GetBytes("\nacquired_at: 1970-01-01T00:00:00Z\n");
        await File.WriteAllBytesAsync(
            LeaderPath(directory.Path),
            [.. prefix, 0xFF, 0xFE, .. suffix]);

        Assert.Throws<PantsLeaseIndeterminateException>(() => Acquire(directory.Path));
    }

    [Fact]
    public async Task ShouldNotStripByteOrderMarkFromLeaderRecord()
    {
        using var directory = new TemporaryDirectory();
        await File.WriteAllBytesAsync(
            LeaderPath(directory.Path),
            [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(
                "epoch: 5\nholder_id: previous-writer\nacquired_at: 1970-01-01T00:00:00Z\n")]);

        Assert.Throws<PantsLeaseIndeterminateException>(() => Acquire(directory.Path));
    }

    [Theory]
    [InlineData("2040-01-01T12:00:00")]
    [InlineData("2040-01-01T12:00:00.0000000")]
    [InlineData("01/01/2040 12:00:00 +00:00")]
    [InlineData("Sun, 01 Jan 2040 12:00:00 GMT")]
    [InlineData("2040-01-01")]
    [InlineData("2040-1-1T12:00:00Z")]
    [InlineData("2040-01-01T12:00Z")]
    [InlineData("2040-01-01T12:00:00.Z")]
    [InlineData("2040-01-01T12:00:00+0000")]
    [InlineData("2040-01-01T12:00:00+00")]
    [InlineData(" 2040-01-01T12:00:00Z")]
    [InlineData("2040-01-01T12:00:00Z ")]
    [InlineData("2040-02-30T12:00:00Z")]
    [InlineData("2040-01-01T24:00:00Z")]
    [InlineData("2040-01-01T12:00:00+24:00")]
    [InlineData("2040-01-01T12:00:00Ζ")]
    [InlineData("２０４０-01-01T12:00:00Z")]
    public async Task ShouldTreatAcquiredAtThatIsNotStrictRfc3339AsIndeterminate(string acquiredAt)
    {
        using var directory = new TemporaryDirectory();
        await WriteRecordAsync(directory.Path, "5", acquiredAt);

        Assert.Throws<PantsLeaseIndeterminateException>(() => Acquire(directory.Path));
    }

    [Theory]
    [InlineData("2040-01-01T11:58:00Z")]
    [InlineData("2040-01-01t11:58:00z")]
    [InlineData("2040-01-01T11:58:00.000000000+00:00")]
    [InlineData("2040-01-01T11:58:00.5-00:00")]
    [InlineData("2040-01-01T17:28:00+05:30")]
    [InlineData("2040-01-01T06:58:00.123456789-05:00")]
    [InlineData("1970-01-01T00:00:00Z")]
    public async Task ShouldTakeOverStaleRecordWithAnyRfc3339Offset(string acquiredAt)
    {
        using var directory = new TemporaryDirectory();
        await WriteRecordAsync(directory.Path, "5", acquiredAt);

        using var lease = Acquire(directory.Path);

        Assert.Equal(6UL, lease.Epoch);
    }

    [Theory]
    [InlineData("2040-01-01T11:59:30Z")]
    [InlineData("2040-01-01T17:29:30+05:30")]
    [InlineData("2040-01-01T06:59:30.999999999-05:00")]
    public async Task ShouldHonorExplicitOffsetWhenRecordIsStillLive(string acquiredAt)
    {
        using var directory = new TemporaryDirectory();
        await WriteRecordAsync(directory.Path, "5", acquiredAt);

        Assert.Throws<PantsLeaseHeldException>(() => Acquire(directory.Path));
    }

    [Fact]
    public async Task ShouldTakeOverAtExactBoundaryWhenFractionalDigitsExceedTickPrecision()
    {
        using var directory = new TemporaryDirectory();
        await WriteRecordAsync(directory.Path, "5", "2040-01-01T11:59:00.000000000Z");

        using var lease = Acquire(directory.Path);

        Assert.Equal(6UL, lease.Epoch);
    }

    [Fact]
    public async Task ShouldKeepRecordLiveWhenSubTickFractionLeavesItShortOfBoundary()
    {
        using var directory = new TemporaryDirectory();
        await WriteRecordAsync(directory.Path, "5", "2040-01-01T11:59:00.000000001Z");

        Assert.Throws<PantsLeaseHeldException>(() => Acquire(directory.Path));
    }

    [Theory]
    [InlineData("+5")]
    [InlineData("-0")]
    [InlineData(" 5")]
    [InlineData("5 ")]
    [InlineData("5\t")]
    [InlineData("0x5")]
    [InlineData("5.0")]
    [InlineData("1,000")]
    [InlineData("٥")]
    [InlineData("")]
    [InlineData("18446744073709551616")]
    public async Task ShouldTreatEpochThatIsNotPlainDecimalDigitsAsIndeterminate(string epoch)
    {
        using var directory = new TemporaryDirectory();
        await WriteRecordAsync(directory.Path, epoch, "1970-01-01T00:00:00Z");

        Assert.Throws<PantsLeaseIndeterminateException>(() => Acquire(directory.Path));
    }

    [Theory]
    [InlineData("+{0}")]
    [InlineData(" {0}")]
    [InlineData("{0} ")]
    public async Task ShouldTreatChecksumThatIsNotPlainDecimalDigitsAsIndeterminate(string format)
    {
        using var directory = new TemporaryDirectory();
        const string Body = "epoch: 5\nholder_id: previous-writer\nacquired_at: 1970-01-01T00:00:00Z\n";
        var checksum = DiskFormat.Crc32C(Encoding.UTF8.GetBytes(Body));
        await File.WriteAllTextAsync(
            LeaderPath(directory.Path),
            $"{Body}checksum: {string.Format(System.Globalization.CultureInfo.InvariantCulture, format, checksum)}\n");

        Assert.Throws<PantsLeaseIndeterminateException>(() => Acquire(directory.Path));
    }

    [Fact]
    public async Task ShouldAcceptPlainDecimalEpochWithLeadingZeros()
    {
        using var directory = new TemporaryDirectory();
        await WriteRecordAsync(directory.Path, "007", "1970-01-01T00:00:00Z");

        using var lease = Acquire(directory.Path);

        Assert.Equal(8UL, lease.Epoch);
    }

    static FileLease Acquire(string root) =>
        FileLease.Acquire(
            root,
            0,
            TimeSpan.Zero,
            null,
            LongHeartbeatInterval,
            new ManualClock(Now),
            TimeToLive);

    static string LeaderPath(string root) => Path.Combine(root, ".midge_leader");

    static Task WriteRecordAsync(string root, string epoch, string acquiredAt) =>
        File.WriteAllTextAsync(
            LeaderPath(root),
            $"epoch: {epoch}\nholder_id: previous-writer\nacquired_at: {acquiredAt}\n");
}

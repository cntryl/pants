using System.Globalization;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage;

public sealed class FileLeaseTests
{
    static readonly TimeSpan LongHeartbeatInterval = TimeSpan.FromHours(1);

    [Fact]
    public async Task ShouldUseInjectedClockForTakeoverAgeWithoutWallClockDelay()
    {
        using var directory = new TemporaryDirectory();
        var clock = new ManualClock(new DateTimeOffset(2040, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await WriteLeaseRecordAsync(
            directory.Path,
            5,
            "previous-writer",
            clock.UtcNow - TimeSpan.FromSeconds(59));

        Assert.Throws<PantsLeaseHeldException>(() => FileLease.Acquire(
            directory.Path,
            0,
            TimeSpan.Zero,
            null,
            LongHeartbeatInterval,
            clock,
            TimeSpan.FromSeconds(60)));

        await WriteLeaseRecordAsync(
            directory.Path,
            5,
            "previous-writer",
            clock.UtcNow - TimeSpan.FromSeconds(60));
        Assert.Throws<PantsLeaseHeldException>(() => FileLease.Acquire(
            directory.Path,
            0,
            TimeSpan.Zero,
            null,
            LongHeartbeatInterval,
            clock,
            TimeSpan.FromSeconds(60)));

        clock.UtcNow += TimeSpan.FromTicks(1);
        using var lease = FileLease.Acquire(
            directory.Path,
            0,
            TimeSpan.Zero,
            null,
            LongHeartbeatInterval,
            clock,
            TimeSpan.FromSeconds(60));

        Assert.Equal(6UL, lease.Epoch);
    }

    [Fact]
    public async Task ShouldTakeOverOnlyAfterConfiguredLeaseBoundary()
    {
        using var directory = new TemporaryDirectory();
        var clock = new ManualClock(new DateTimeOffset(2040, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var ttl = TimeSpan.FromSeconds(2);
        var skew = TimeSpan.FromSeconds(1);
        await WriteLeaseRecordAsync(
            directory.Path,
            17,
            "previous-writer",
            clock.UtcNow - ttl - skew);

        Assert.Throws<PantsLeaseHeldException>(() => FileLease.Acquire(
            directory.Path,
            0,
            skew,
            null,
            LongHeartbeatInterval,
            clock,
            ttl));

        clock.UtcNow += TimeSpan.FromTicks(1);
        using var lease = FileLease.Acquire(
            directory.Path,
            0,
            skew,
            null,
            LongHeartbeatInterval,
            clock,
            ttl);

        Assert.Equal(18UL, lease.Epoch);
    }

    [Fact]
    public async Task ShouldNotTakeOverAtOldHardCodedBoundaryGivenLongLease()
    {
        using var directory = new TemporaryDirectory();
        var clock = new ManualClock(new DateTimeOffset(2040, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await WriteLeaseRecordAsync(
            directory.Path,
            4,
            "long-lived-writer",
            clock.UtcNow - TimeSpan.FromSeconds(75));

        Assert.Throws<PantsLeaseHeldException>(() => FileLease.Acquire(
            directory.Path,
            0,
            TimeSpan.Zero,
            null,
            LongHeartbeatInterval,
            clock,
            TimeSpan.FromMinutes(2)));
    }

    [Fact]
    public async Task ShouldFenceOldOwnerAfterConfiguredLeaseTakeover()
    {
        using var directory = new TemporaryDirectory();
        var clock = new ManualClock(new DateTimeOffset(2040, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var ttl = TimeSpan.FromSeconds(2);
        using var first = FileLease.Acquire(
            directory.Path,
            0,
            TimeSpan.Zero,
            null,
            LongHeartbeatInterval,
            clock,
            ttl);
        await WriteLeaseRecordAsync(
            directory.Path,
            first.Epoch,
            "first-writer",
            clock.UtcNow - ttl - TimeSpan.FromTicks(1));
        using var second = FileLease.Acquire(
            directory.Path,
            0,
            TimeSpan.Zero,
            null,
            LongHeartbeatInterval,
            clock,
            ttl);

        Assert.False(first.RenewForTesting());
        Assert.Throws<PantsFencedException>(first.EnsureValid);
        second.EnsureValid();
        Assert.True(second.Epoch > first.Epoch);
    }

    // ----- Issue #39: PantsOpenOptions.MinimumEpoch floor plumbed through to a local open -----

    [Fact]
    public async Task LocalOpenHonorsConfiguredMinimumEpochFloor()
    {
        using var directory = new TemporaryDirectory();
        await WriteLeaseRecordAsync(directory.Path, 5, "previous-writer", DateTimeOffset.UnixEpoch);

        await using var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path).WithMinimumEpoch(10));

        Assert.Equal(11UL, await ReadLeaseEpochAsync(directory.Path));
    }

    [Fact]
    public async Task LocalOpenGrantsEpochAboveWalWriterEpochWhenLeaderRecordIsMissing()
    {
        using var directory = new TemporaryDirectory();
        await using (var seed = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path)))
        {
        }

        var wal = WalCodec.EncodeRecord(new WalRecord(
            0,
            WalOperation.Put,
            "key"u8.ToArray(),
            "value"u8.ToArray(),
            7,
            null,
            null,
            null,
            9));
        using (var frame = new MemoryStream())
        {
            DiskFormat.WriteUInt32(frame, checked((uint)wal.Length));
            DiskFormat.WriteUInt32(frame, DiskFormat.Crc32C(wal));
            frame.Write(wal);
            await File.WriteAllBytesAsync(
                Path.Combine(directory.Path, "wal", "wal.log"),
                frame.ToArray());
        }

        File.Delete(Path.Combine(directory.Path, ".midge_leader"));

        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path));

        Assert.True(await ReadLeaseEpochAsync(directory.Path) > 9UL);
    }

    [Fact]
    public async Task ShouldWriteChecksumMidgeVerifiesAndSurviveRenewalAndRelease()
    {
        using var directory = new TemporaryDirectory();
        var clock = new ManualClock(DateTimeOffset.UnixEpoch + TimeSpan.FromDays(1));
        using (var lease = FileLease.Acquire(
                   directory.Path,
                   0,
                   TimeSpan.Zero,
                   null,
                   LongHeartbeatInterval,
                   clock,
                   TimeSpan.FromSeconds(60)))
        {
            Assert.True(lease.RenewForTesting());
            AssertChecksumVerifies(await File.ReadAllTextAsync(Path.Combine(directory.Path, ".midge_leader")));
        }

        // Release rewrites the record; it must still carry a valid checksum.
        AssertChecksumVerifies(await File.ReadAllTextAsync(Path.Combine(directory.Path, ".midge_leader")));
    }

    [Fact]
    public async Task ShouldAcceptRecordWithoutChecksumForBackwardCompatibility()
    {
        using var directory = new TemporaryDirectory();
        await WriteLeaseRecordAsync(directory.Path, 5, "previous-writer", DateTimeOffset.UnixEpoch);

        using var lease = FileLease.Acquire(
            directory.Path,
            0,
            TimeSpan.Zero,
            null,
            LongHeartbeatInterval,
            new ManualClock(DateTimeOffset.UnixEpoch + TimeSpan.FromDays(1)),
            TimeSpan.FromSeconds(60));

        Assert.Equal(6UL, lease.Epoch);
    }

    [Theory]
    [InlineData("checksum: not-a-number")]
    [InlineData("checksum: 12345")]
    public async Task ShouldTreatPresentButWrongChecksumAsIndeterminate(string checksumLine)
    {
        using var directory = new TemporaryDirectory();
        await File.WriteAllTextAsync(
            Path.Combine(directory.Path, ".midge_leader"),
            $"epoch: 5\nholder_id: previous-writer\nacquired_at: {DateTimeOffset.UnixEpoch:O}\n{checksumLine}\n");

        Assert.Throws<PantsLeaseIndeterminateException>(() => FileLease.Acquire(
            directory.Path,
            0,
            TimeSpan.Zero,
            null,
            LongHeartbeatInterval,
            new ManualClock(DateTimeOffset.UnixEpoch + TimeSpan.FromDays(1)),
            TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public async Task ShouldRejectBitFlippedEpochInChecksummedRecord()
    {
        using var directory = new TemporaryDirectory();
        var clock = new ManualClock(DateTimeOffset.UnixEpoch + TimeSpan.FromDays(1));
        using (FileLease.Acquire(
                   directory.Path,
                   0,
                   TimeSpan.Zero,
                   null,
                   LongHeartbeatInterval,
                   clock,
                   TimeSpan.FromSeconds(60)))
        {
        }

        var path = Path.Combine(directory.Path, ".midge_leader");
        var text = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path, text.Replace("epoch: 1\n", "epoch: 3\n", StringComparison.Ordinal));
        clock.UtcNow += TimeSpan.FromDays(1);

        Assert.Throws<PantsLeaseIndeterminateException>(() => FileLease.Acquire(
            directory.Path,
            0,
            TimeSpan.Zero,
            null,
            LongHeartbeatInterval,
            clock,
            TimeSpan.FromSeconds(60)));
    }

    static void AssertChecksumVerifies(string content)
    {
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, lines.Length);
        var body = string.Join('\n', lines.Take(3)) + "\n";
        var expected = uint.Parse(
            lines[3]["checksum: ".Length..],
            CultureInfo.InvariantCulture);
        Assert.Equal(DiskFormat.Crc32C(System.Text.Encoding.UTF8.GetBytes(body)), expected);
    }

    [Fact]
    public async Task LocalOpenDefaultMinimumEpochLeavesBehaviorUnchanged()
    {
        using var directory = new TemporaryDirectory();
        await WriteLeaseRecordAsync(directory.Path, 5, "previous-writer", DateTimeOffset.UnixEpoch);

        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path));

        Assert.Equal(6UL, await ReadLeaseEpochAsync(directory.Path));
    }

    // ----- Issue #40: duplicate leader-record fields resolve last-occurrence-wins -----

    [Fact]
    public void DuplicateLeaderRecordFieldResolvesToLastOccurrence()
    {
        using var directory = new TemporaryDirectory();
        var leaderPath = Path.Combine(directory.Path, ".midge_leader");
        File.WriteAllText(
            leaderPath,
            "epoch: 5\n" +
            "holder_id: writer-a\n" +
            "acquired_at: 1970-01-01T00:00:00.0000000+00:00\n" +
            "epoch: 9\n" +
            "holder_id: writer-b\n" +
            "acquired_at: 1970-01-01T00:00:00.0000000+00:00\n");

        using var lease = FileLease.Acquire(
            directory.Path,
            0,
            TimeSpan.Zero,
            null,
            LongHeartbeatInterval);

        // max(9, 0) + 1 == 10, proving the later "epoch: 9" line (not the earlier "epoch: 5")
        // was the value used for the takeover computation.
        Assert.Equal(10UL, lease.Epoch);
    }

    [Fact]
    public void MalformedLeaderRecordMissingRequiredFieldStillThrowsIndeterminate()
    {
        using var directory = new TemporaryDirectory();
        var leaderPath = Path.Combine(directory.Path, ".midge_leader");
        File.WriteAllText(leaderPath, "epoch: 5\nholder_id: writer-a\n");

        Assert.Throws<PantsLeaseIndeterminateException>(() => FileLease.Acquire(
            directory.Path,
            0,
            TimeSpan.Zero,
            null,
            LongHeartbeatInterval));
    }

    // ----- Issue #41: Renew re-verifies the write took effect (self-fences otherwise) -----

    [Fact]
    public void RenewSelfFencesWhenReadbackDoesNotMatchTheJustWrittenRecord()
    {
        using var directory = new TemporaryDirectory();
        using var lease = FileLease.Acquire(
            directory.Path,
            0,
            TimeSpan.Zero,
            null,
            LongHeartbeatInterval);

        var leaderPath = Path.Combine(directory.Path, ".midge_leader");
        lease.RenewWriteInterferenceHookForTesting = () => File.WriteAllText(
            leaderPath,
            "epoch: 999\nholder_id: intruder\nacquired_at: 2020-01-01T00:00:00.0000000+00:00\n");

        var renewed = lease.RenewForTesting();

        Assert.False(renewed);
        Assert.Throws<PantsFencedException>(lease.EnsureValid);
    }

    [Fact]
    public void RenewSucceedsWhenNoInterferenceOccurs()
    {
        using var directory = new TemporaryDirectory();
        using var lease = FileLease.Acquire(
            directory.Path,
            0,
            TimeSpan.Zero,
            null,
            LongHeartbeatInterval);

        var renewed = lease.RenewForTesting();

        Assert.True(renewed);
        lease.EnsureValid();
    }

    // ----- Issue #42: mutation-lock release verifies owner_token before deleting -----

    [Fact]
    public void DisposalSkipsMutationLockDeletionWhenOwnerTokenChangedDuringRelease()
    {
        using var directory = new TemporaryDirectory();
        var lease = FileLease.Acquire(
            directory.Path,
            0,
            TimeSpan.Zero,
            null,
            LongHeartbeatInterval);

        var lockPath = Path.Combine(directory.Path, ".midge_leader.lock");
        lease.MutationLockDisposalInterferenceHookForTesting = () => File.WriteAllText(
            lockPath,
            "holder_id=intruder\nowner_token=intruder-token\ncreated_at=2020-01-01T00:00:00.0000000+00:00\n");
        lease.Dispose();

        Assert.True(File.Exists(lockPath));
        Assert.Contains("intruder-token", File.ReadAllText(lockPath));
    }

    [Fact]
    public void DisposalDeletesMutationLockWhenNoInterferenceOccurs()
    {
        using var directory = new TemporaryDirectory();
        var lockPath = Path.Combine(directory.Path, ".midge_leader.lock");
        var lease = FileLease.Acquire(
            directory.Path,
            0,
            TimeSpan.Zero,
            null,
            LongHeartbeatInterval);

        lease.Dispose();

        Assert.False(File.Exists(lockPath));
    }

    static async Task WriteLeaseRecordAsync(string path, ulong epoch, string holderId, DateTimeOffset acquiredAt)
    {
        Directory.CreateDirectory(path);
        await File.WriteAllTextAsync(
            Path.Combine(path, ".midge_leader"),
            $"epoch: {epoch}\nholder_id: {holderId}\nacquired_at: {acquiredAt:O}\n");
    }

    static async Task<ulong> ReadLeaseEpochAsync(string path)
    {
        var content = await File.ReadAllTextAsync(Path.Combine(path, ".midge_leader"));
        foreach (var line in content.Split('\n'))
        {
            var parts = line.Split(": ", 2);
            if (parts.Length == 2 && parts[0] == "epoch")
            {
                return ulong.Parse(parts[1], CultureInfo.InvariantCulture);
            }
        }

        throw new InvalidOperationException("No epoch field found in leader record.");
    }

    [Fact]
    public void ShouldFenceAtMonotonicDeadlineWhenRenewalNeverRuns()
    {
        using var directory = new TemporaryDirectory();
        var time = new ManualTimeProvider();
        var losses = 0;
        using var lease = AcquireWithMonotonicTime(directory.Path, time, () => losses++);
        lease.EnsureValid();

        time.Advance(TimeSpan.FromSeconds(61));

        Assert.Throws<PantsFencedException>(lease.EnsureValid);
        Assert.Throws<PantsFencedException>(lease.EnsureValid);
        Assert.Equal(1, losses);
    }

    [Fact]
    public void ShouldReportLossAtDeadlineWithoutCallerActivity()
    {
        using var directory = new TemporaryDirectory();
        var time = new ManualTimeProvider();
        var losses = 0;
        using var lease = AcquireWithMonotonicTime(directory.Path, time, () => losses++);

        time.Advance(TimeSpan.FromSeconds(61));
        lease.CheckExpiryForTesting();
        lease.CheckExpiryForTesting();

        Assert.Equal(1, losses);
        Assert.Throws<PantsFencedException>(lease.EnsureValid);
    }

    [Fact]
    public void ShouldStayValidPastOriginalDeadlineWhenRenewalAdvancesInTime()
    {
        using var directory = new TemporaryDirectory();
        var time = new ManualTimeProvider();
        using var lease = AcquireWithMonotonicTime(directory.Path, time, null);

        time.Advance(TimeSpan.FromSeconds(40));
        Assert.True(lease.RenewForTesting());
        time.Advance(TimeSpan.FromSeconds(40));

        lease.EnsureValid();
    }

    [Fact]
    public void ShouldNotRestoreLeaseWhenRenewalArrivesAfterDeadline()
    {
        using var directory = new TemporaryDirectory();
        var time = new ManualTimeProvider();
        using var lease = AcquireWithMonotonicTime(directory.Path, time, null);

        time.Advance(TimeSpan.FromSeconds(61));

        Assert.False(lease.RenewForTesting());
        Assert.Throws<PantsFencedException>(lease.EnsureValid);
    }

    [Fact]
    public void ShouldNotExtendDeadlineWhenWallClockStepsBackward()
    {
        using var directory = new TemporaryDirectory();
        var time = new ManualTimeProvider();
        var clock = new ManualClock(new DateTimeOffset(2040, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var lease = FileLease.Acquire(
            directory.Path,
            0,
            TimeSpan.Zero,
            null,
            LongHeartbeatInterval,
            clock,
            TimeSpan.FromSeconds(60),
            time);

        clock.UtcNow -= TimeSpan.FromHours(1);
        time.Advance(TimeSpan.FromSeconds(61));

        Assert.Throws<PantsFencedException>(lease.EnsureValid);
    }

    static FileLease AcquireWithMonotonicTime(
        string root,
        ManualTimeProvider time,
        Action? leaseLossCallback) =>
        FileLease.Acquire(
            root,
            0,
            TimeSpan.Zero,
            leaseLossCallback,
            LongHeartbeatInterval,
            new ManualClock(new DateTimeOffset(2040, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            TimeSpan.FromSeconds(60),
            time);
}

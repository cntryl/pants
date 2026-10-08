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
    public async Task ShouldLetSuccessorTakeOverImmediatelyAfterTheLeaseFencesItselfWithoutDisposal()
    {
        using var directory = new TemporaryDirectory();
        var clock = new ManualClock(DateTimeOffset.UnixEpoch + TimeSpan.FromDays(1));
        var released = 0;
        using var fenced = FileLease.Acquire(
            directory.Path,
            0,
            TimeSpan.Zero,
            null,
            LongHeartbeatInterval,
            clock,
            TimeSpan.FromSeconds(60));
        fenced.FencedRelease = () => Interlocked.Increment(ref released);
        Assert.Throws<PantsLeaseHeldException>(() => FileLease.Acquire(
            directory.Path,
            0,
            TimeSpan.Zero,
            null,
            LongHeartbeatInterval,
            clock,
            TimeSpan.FromSeconds(60)));
        fenced.RenewWriteInterferenceHookForTesting = () => throw new IOException("injected");

        Assert.False(fenced.RenewForTesting());

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        FileLease? successor = null;
        while (successor is null)
        {
            try
            {
                successor = FileLease.Acquire(
                    directory.Path,
                    0,
                    TimeSpan.Zero,
                    null,
                    LongHeartbeatInterval,
                    clock,
                    TimeSpan.FromSeconds(60));
            }
            catch (Exception exception) when (
                exception is PantsLeaseHeldException or PantsLeaseUnavailableException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token);
            }
        }

        using (successor)
        {
            Assert.Equal(fenced.Epoch + 1, successor.Epoch);
        }

        while (Volatile.Read(ref released) == 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }

        Assert.Equal(1, Volatile.Read(ref released));
    }

    [Fact]
    public async Task ShouldReleaseSameHostLockWhenTheEngineFencesItselfSoASuccessorCanOpen()
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false);
        await using var fenced = await PantsDatabase.OpenForTestingAsync(
            options,
            new RuntimeDependencies(leaseHeartbeatInterval: TimeSpan.FromMilliseconds(50)));
        var epoch = await ReadLeaseEpochAsync(directory.Path);
        // Another writer superseded this one and has since expired, so only the same-host LOCK
        // handle could still keep a successor out.
        await WriteLeaseRecordAsync(
            directory.Path,
            epoch + 1,
            "superseding-writer",
            DateTimeOffset.UnixEpoch);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while ((await fenced.Diagnostics.GetRuntimeMetricsAsync()).Health != PantsEngineHealth.Degraded &&
               !await Task.Run(() => IsFenced(fenced), timeout.Token))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token);
        }

        // The successor must get past the lease and LOCK. On Windows the undisposed instance
        // still holds the WAL file open, so reaching that file (a storage error) is also proof
        // the LOCK handle is gone; being refused as lease-held is the failure.
        IPantsDatabase? successor = null;
        while (true)
        {
            try
            {
                successor = await PantsDatabase.OpenAsync(options);
                break;
            }
            catch (Exception exception) when (
                exception is PantsLeaseHeldException or PantsLeaseUnavailableException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token);
            }
            catch (PantsIOException) when (OperatingSystem.IsWindows())
            {
                return;
            }
        }

        await using (successor)
        {
            Assert.True(await ReadLeaseEpochAsync(directory.Path) > epoch + 1);
        }
    }

    static bool IsFenced(IPantsDatabase database) => !database.PersistentStorage!.IsPrimaryLeaseHealthy;

    [Fact]
    public async Task ShouldNotOverwriteARecordAnotherWriterOwnsWhenFencing()
    {
        using var directory = new TemporaryDirectory();
        var clock = new ManualClock(DateTimeOffset.UnixEpoch + TimeSpan.FromDays(1));
        using var fenced = FileLease.Acquire(
            directory.Path,
            0,
            TimeSpan.Zero,
            null,
            LongHeartbeatInterval,
            clock,
            TimeSpan.FromSeconds(60));
        var path = Path.Combine(directory.Path, ".midge_leader");
        var foreign = $"epoch: 9\nholder_id: someone-else\nacquired_at: {clock.UtcNow:O}\n";
        File.WriteAllText(path, foreign);

        Assert.False(fenced.RenewForTesting());
        await Task.Delay(TimeSpan.FromMilliseconds(300));

        Assert.Equal(foreign, await File.ReadAllTextAsync(path));
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

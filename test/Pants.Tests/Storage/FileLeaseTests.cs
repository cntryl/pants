namespace Cntryl.Pants.Tests.Storage;

public sealed class FileLeaseTests
{
    static readonly TimeSpan LongHeartbeatInterval = TimeSpan.FromHours(1);

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
                return ulong.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        throw new InvalidOperationException("No epoch field found in leader record.");
    }
}

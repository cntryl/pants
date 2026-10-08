using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage;

public sealed class FileLeaseIoTests
{
    static readonly TimeSpan LongHeartbeatInterval = TimeSpan.FromHours(1);
    static readonly DateTimeOffset Now = new(2040, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ShouldRenewWhileAnotherHandleReadsTheLeaderRecord()
    {
        using var directory = new TemporaryDirectory();
        using var lease = Acquire(directory.Path);
        // Shares read, write, and delete, as Midge's reader and Pants' own reader both do.
        using var reader = SharedReadFile.Open(LeaderPath(directory.Path))!;

        Assert.True(lease.RenewForTesting());
        lease.EnsureValid();
    }

    [Fact]
    public async Task ShouldTakeOverWhileAnotherHandleReadsTheStaleLeaderRecord()
    {
        using var directory = new TemporaryDirectory();
        await File.WriteAllTextAsync(
            LeaderPath(directory.Path),
            "epoch: 5\nholder_id: crashed-writer\nacquired_at: 1970-01-01T00:00:00Z\n");
        using var reader = SharedReadFile.Open(LeaderPath(directory.Path))!;

        using var lease = Acquire(directory.Path);

        Assert.Equal(6UL, lease.Epoch);
    }

    [Fact]
    public void ShouldSurfaceLeaseUnavailableGivenTheLeaderRecordCannotBePublished()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(LeaderPath(directory.Path));

        var exception = Assert.Throws<PantsLeaseUnavailableException>(() => Acquire(directory.Path));

        Assert.True(exception.InnerException is IOException or UnauthorizedAccessException);
    }

    [Fact]
    public void ShouldSurfaceLeaseUnavailableGivenTheLeaderRecordCannotBeRead()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        var path = LeaderPath(directory.Path);
        File.WriteAllText(path, "epoch: 5\nholder_id: writer\nacquired_at: 1970-01-01T00:00:00Z\n");
        File.SetUnixFileMode(path, UnixFileMode.None);
        try
        {
            if (CanRead(path))
            {
                // A privileged user bypasses file modes, so the failure cannot be produced.
                return;
            }

            Assert.Throws<PantsLeaseUnavailableException>(() => Acquire(directory.Path));
        }
        finally
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public async Task ShouldFailLocalOpenWithLeaseUnavailableGivenLeaderRecordIOFails()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(LeaderPath(directory.Path));

        await Assert.ThrowsAsync<PantsLeaseUnavailableException>(async () =>
        {
            await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path));
        });
    }

    static bool CanRead(string path)
    {
        try
        {
            _ = File.ReadAllBytes(path);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    static FileLease Acquire(string root) =>
        FileLease.Acquire(
            root,
            0,
            TimeSpan.Zero,
            null,
            LongHeartbeatInterval,
            new ManualClock(Now),
            TimeSpan.FromSeconds(60));

    static string LeaderPath(string root) => Path.Combine(root, ".midge_leader");
}

namespace Pants.Tests;

public sealed class PantsEngineExclusivityParityTests
{
    [Fact]
    public async Task ShouldFenceConcurrentLocalOpensAndReleaseLeaseAfterShutdown()
    {
        using var directory = new TemporaryDirectory();
        IPantsDatabase first = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path));
        Assert.True(first.IsPrimaryLeaseHealthy);

        PantsLeaseHeldException held = await Assert.ThrowsAsync<PantsLeaseHeldException>(
            () => PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path)).AsTask());
        Assert.Equal(PantsErrorCode.LeaseHeld, held.Code);
        Assert.Contains("writer", held.Message, StringComparison.OrdinalIgnoreCase);

        await first.ShutdownAsync(TimeSpan.FromSeconds(5));
        await using IPantsDatabase second = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path));
        Assert.True(second.IsPrimaryLeaseHealthy);
    }

    [Fact]
    public async Task ShouldAllowExactlyOneWinnerWhenLocalOpensRace()
    {
        using var directory = new TemporaryDirectory();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<OpenResult>[] attempts = Enumerable.Range(0, 8)
            .Select(_ => AttemptOpenAsync(directory.Path, start.Task))
            .ToArray();
        start.SetResult();

        OpenResult[] results = await Task.WhenAll(attempts).WaitAsync(TimeSpan.FromSeconds(10));

        OpenResult winner = Assert.Single(results, static result => result.Database is not null);
        Assert.All(
            results.Where(static result => result.Database is null),
            static result => Assert.IsType<PantsLeaseHeldException>(result.Error));
        await winner.Database!.DisposeAsync();
    }

    [Fact]
    public async Task ShouldSurviveRapidOpenShutdownCycles()
    {
        using var directory = new TemporaryDirectory();
        for (var cycle = 0; cycle < 10; cycle++)
        {
            await using IPantsDatabase database = await PantsDatabase.OpenAsync(
                PantsOpenOptions.Local(directory.Path));
            Assert.True(database.IsPrimaryLeaseHealthy);
            await database.ShutdownAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task ShouldFenceWritesAfterLocalLeaseOwnershipIsLost()
    {
        using var directory = new TemporaryDirectory();
        var leaseLost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PantsOpenOptions options = PantsOpenOptions.Local(directory.Path)
            .WithLeaseLossCallback(() => leaseLost.TrySetResult());
        await using IPantsDatabase database = await PantsDatabase.OpenForTestingAsync(
            options,
            new PantsRuntimeDependencies(leaseHeartbeatInterval: TimeSpan.FromMilliseconds(10)));
        await File.WriteAllTextAsync(
            Path.Combine(directory.Path, ".midge_leader"),
            $"epoch: 999\nholder_id: replacement\nacquired_at: {DateTimeOffset.UtcNow:O}\n");

        await leaseLost.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(database.IsPrimaryLeaseHealthy);
        await using IPantsTransaction transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put("fenced"u8.ToArray(), "value"u8.ToArray());
        PantsFencedException error = await Assert.ThrowsAsync<PantsFencedException>(
            () => transaction.CommitAsync(PantsWriteOptions.Sync).AsTask());
        Assert.Equal(PantsErrorCode.Fenced, error.Code);
    }

    private static async Task<OpenResult> AttemptOpenAsync(string path, Task start)
    {
        await start;
        try
        {
            IPantsDatabase database = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(path));
            return new OpenResult(database, null);
        }
        catch (Exception exception)
        {
            return new OpenResult(null, exception);
        }
    }

    private sealed record OpenResult(IPantsDatabase? Database, Exception? Error);
}

using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage;

public sealed class RemoteFileCursorTests
{
    [Fact]
    public async Task ShouldStopWaitingOnARemoteRangeWhenTheCompactionIsCancelled()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "remote.sst");
        File.WriteAllBytes(
            path,
            SstCodec.Encode(
                [new SstEntry(TestBytes.FromString("a"), TestBytes.FromString("v"), 1, null, false)],
                [],
                PantsPerformanceGoal.Latency));
        var source = new GatedSource(LocalAsyncSstSource.Open(path));
        var reader = await AsyncSstReader.OpenAsync(
            source,
            new FileMeta { Name = "remote.sst" },
            CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        using var cursor = new RemoteFileCursor(reader, null, cancellation.Token);
        source.Gate = true;

        var advance = Task.Run(cursor.MoveNext);
        await source.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            advance.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    sealed class GatedSource(IAsyncSstSource inner) : IAsyncSstSource
    {
        public bool Gate { get; set; }

        public TaskCompletionSource Blocked { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public long Length => inner.Length;

        public async ValueTask<byte[]> ReadExactlyAsync(
            long offset,
            int length,
            CancellationToken cancellationToken)
        {
            if (Gate)
            {
                Blocked.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return await inner.ReadExactlyAsync(offset, length, cancellationToken);
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}

using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage;

public sealed class SstSharedReadMetadataTests
{
    [Fact]
    public async Task ShouldRefuseMetadataAdmissionBeforeTheFirstNativeRead()
    {
        using var directory = new TemporaryDirectory();
        var (path, file) = CreateSst(directory.Path);
        var source = new CountingSource(LocalAsyncSstSource.Open(path));
        var budget = new ResourceBudget(511);

        await Assert.ThrowsAsync<PantsResourceLimitException>(async () =>
            await AsyncSstReader.OpenAsync(source, file, CancellationToken.None, budget));

        Assert.Equal(0, source.Reads);
        Assert.True(source.Closed);
        Assert.Equal(0, budget.Current);
    }

    [Fact]
    public async Task ShouldRetainSharedMetadataUntilTheFinalPhysicalReaderLeaseCloses()
    {
        using var directory = new TemporaryDirectory();
        var (path, file) = CreateSst(directory.Path);
        var source = new CountingSource(LocalAsyncSstSource.Open(path));
        var budget = new ResourceBudget(1024 * 1024);
        var reader = await AsyncSstReader.OpenAsync(source, file, CancellationToken.None, budget);
        var retained = budget.Current;
        Assert.True(retained > 0);
        var held = reader.Share();

        await reader.DisposeAsync();
        Assert.Equal(retained, budget.Current);
        Assert.False(source.Closed);
        Assert.True(held.DataBlockCount > 0);
        await held.DisposeAsync();
        Assert.True(source.Closed);
        Assert.Equal(0, budget.Current);
        Assert.InRange(budget.Peak, retained, budget.Limit);
    }

    [Fact]
    public async Task ShouldPreserveHeldReaderAndCloseRefusedSourceWhenSharedMetadataIsFull()
    {
        using var directory = new TemporaryDirectory();
        var (path, file) = CreateSst(directory.Path);
        var budget = new ResourceBudget(1024 * 1024);
        await using var held = await AsyncSstReader.OpenAsync(
            LocalAsyncSstSource.Open(path), file, CancellationToken.None, budget);
        Assert.True(budget.Current > 0);
        using var occupied = budget.Reserve(budget.Limit - budget.Current);
        var source = new CountingSource(LocalAsyncSstSource.Open(path));

        await Assert.ThrowsAsync<PantsResourceLimitException>(async () =>
            await AsyncSstReader.OpenAsync(source, file, CancellationToken.None, budget));

        Assert.Equal(0, source.Reads);
        Assert.True(source.Closed);
        Assert.True(held.DataBlockCount > 0);
        Assert.Equal(budget.Limit, budget.Current);
    }

    [Fact]
    public async Task ShouldReuseTheAccountedPhysicalReaderWhenAScanAdvances()
    {
        using var directory = new TemporaryDirectory();
        var (path, file) = CreateSst(directory.Path);
        var source = new CountingSource(LocalAsyncSstSource.Open(path));
        var budget = new ResourceBudget(1024 * 1024);
        var reader = await AsyncSstReader.OpenAsync(source, file, CancellationToken.None, budget);
        var retained = budget.Current;
        var reopens = 0;
        await using var scan = await AsyncSstScanSource.CreateAsync(
            file, reader, _ =>
            {
                reopens++;
                return ValueTask.FromException<AsyncSstReader>(new InvalidOperationException("Reader reopened."));
            }, PantsScanDirection.Forward, null, null, budget);

        Assert.Equal(retained, budget.Current);
        Assert.False(source.Closed);
        Assert.True(await scan.MoveNextAsync(CancellationToken.None));
        Assert.Equal("key-0000", TestBytes.ToText(scan.Current.Key));
        Assert.Equal(0, reopens);
        await scan.DisposeAsync();
        Assert.True(source.Closed);
        Assert.Equal(0, budget.Current);
    }

    [Fact]
    public async Task ShouldRefuseExplicitlyBudgetedBlockWorkspaceBeforeIssuingTheNativeRead()
    {
        using var directory = new TemporaryDirectory();
        var (path, file) = CreateSst(directory.Path);
        var source = new CountingSource(LocalAsyncSstSource.Open(path));
        var budget = new ResourceBudget(1024 * 1024);
        var reader = await AsyncSstReader.OpenAsync(source, file, CancellationToken.None, budget);
        await using var iterator = new AsyncSstBlockIterator(reader, PantsScanDirection.Forward, null, null, budget);
        using var occupied = budget.Reserve(budget.Limit - budget.Current);
        var metadataReads = source.Reads;

        await Assert.ThrowsAsync<PantsResourceLimitException>(async () =>
            await iterator.MoveNextAsync(CancellationToken.None));

        Assert.Equal(metadataReads, source.Reads);
        Assert.False(source.Closed);
        Assert.Equal(budget.Limit, budget.Current);
    }

    [Fact]
    public async Task ShouldAdvanceAnAdmittedPublicScanWhileItsMetadataAllowanceIsFullyHeld()
    {
        using var directory = new TemporaryDirectory();
        var (path, file) = CreateSst(directory.Path);
        var budget = new ResourceBudget(1024 * 1024);
        var reader = await AsyncSstReader.OpenAsync(LocalAsyncSstSource.Open(path), file, CancellationToken.None, budget);
        await using var scan = await AsyncSstScanSource.CreateAsync(
            file, reader, static _ => throw new InvalidOperationException("Unexpected reopen."),
            PantsScanDirection.Forward, null, null, budget);
        using var occupied = budget.Reserve(budget.Limit - budget.Current);

        var keys = new List<string>();
        while (await scan.MoveNextAsync(CancellationToken.None))
        {
            keys.Add(TestBytes.ToText(scan.Current.Key));
        }

        Assert.Equal(["key-0000", "key-0001", "key-0002", "key-0003"], keys);
        Assert.Equal(budget.Limit, budget.Current);
        Assert.InRange(budget.Peak, 1, budget.Limit);
    }

    [Fact]
    public async Task ShouldBoundConcurrentNativeOpensAndReleaseCancelledAdmission()
    {
        using var directory = new TemporaryDirectory();
        var (path, file) = CreateSst(directory.Path);
        using var resources = new SstReadResources(32 * 1024);
        var firstRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var opens = 0;
        async Task<AsyncSstReader> OpenAsync(string name, CancellationToken token)
        {
            using var admission = await resources.EnterOpenAsync(name, token);
            Interlocked.Increment(ref opens);
            return await AsyncSstReader.OpenAsync(
                new CountingSource(LocalAsyncSstSource.Open(path), async cancellationToken =>
                {
                    firstRead.TrySetResult();
                    await releaseRead.Task.WaitAsync(cancellationToken);
                }), file, token, resources.MetadataBudget);
        }
        var first = OpenAsync("first.sst", CancellationToken.None);
        await firstRead.Task.WaitAsync(TestTimeouts.Expected);
        var held = resources.MetadataBudget.Current;
        using var cancellation = new CancellationTokenSource();
        var second = OpenAsync("second.sst", cancellation.Token);
        Assert.False(second.IsCompleted);
        Assert.Equal(1, Volatile.Read(ref opens));
        Assert.Equal(held, resources.MetadataBudget.Current);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.Equal(held, resources.MetadataBudget.Current);
        releaseRead.SetResult();
        await using (var reader = await first.WaitAsync(TestTimeouts.Expected))
        {
            Assert.True(reader.DataBlockCount > 0);
        }
        Assert.Equal(0, resources.MetadataBudget.Current);
        await using (var retry = await OpenAsync("retry.sst", CancellationToken.None))
        {
            Assert.True(retry.DataBlockCount > 0);
        }
        Assert.Equal(2, Volatile.Read(ref opens));
        Assert.Equal(0, resources.MetadataBudget.Current);
    }

    [Fact]
    public async Task ShouldRetainMetadataClaimWhenThePhysicalSourceCannotProveItClosed()
    {
        using var directory = new TemporaryDirectory();
        var (path, file) = CreateSst(directory.Path);
        var source = new CountingSource(LocalAsyncSstSource.Open(path)) { FailClose = true };
        var budget = new ResourceBudget(1024 * 1024);
        var reader = await AsyncSstReader.OpenAsync(source, file, CancellationToken.None, budget);
        var retained = budget.Current;
        await Assert.ThrowsAsync<IOException>(() => reader.DisposeAsync().AsTask());
        Assert.False(source.Closed);
        Assert.Equal(retained, budget.Current);
        source.FailClose = false;
        await source.DisposeAsync();
        Assert.True(source.Closed);
        // Only the physical reader owner may discharge its claim; an uncertain close cannot
        // create duplicate admission capacity while a source may still be alive.
        Assert.Equal(retained, budget.Current);
    }

    [Fact]
    public void ShouldRefuseExplicitlyBudgetedLocalBlockBeforeReadingATruncatedDataExtent()
    {
        using var directory = new TemporaryDirectory();
        var (path, _) = CreateSst(directory.Path);
        using var reader = SstReader.Open(path);
        File.WriteAllBytes(path, []);
        using var refused = SstBlockIterator.Create(reader, PantsScanDirection.Forward,
            resourceBudget: new ResourceBudget(0));

        Assert.Throws<PantsResourceLimitException>(() => refused.MoveNext());
        Assert.True(reader.DataBlockCount > 0);
        using var admitted = SstBlockIterator.Create(reader, PantsScanDirection.Forward,
            resourceBudget: new ResourceBudget(1024 * 1024));
        Assert.Throws<EndOfStreamException>(() => admitted.MoveNext());
    }

    static (string Path, FileMeta File) CreateSst(string root)
    {
        var entries = Enumerable.Range(0, 4).Select(index => new SstEntry(
            TestBytes.FromString($"key-{index:0000}"), new byte[32], checked((ulong)index + 1), null, false)).ToArray();
        var bytes = SstCodec.Encode(entries, [], PantsPerformanceGoal.Latency);
        var path = Path.Combine(root, "shared.sst");
        File.WriteAllBytes(path, bytes);
        return (path, new FileMeta { Name = "shared.sst", SizeBytes = checked((ulong)bytes.Length) });
    }

    sealed class CountingSource(IAsyncSstSource inner, Func<CancellationToken, Task>? beforeRead = null) : IAsyncSstSource
    {
        public long Length => inner.Length;
        public int Reads { get; private set; }
        public bool Closed { get; private set; }
        public bool FailClose { get; set; }
        public async ValueTask<byte[]> ReadExactlyAsync(long offset, int length, CancellationToken cancellationToken)
        {
            Reads++;
            if (beforeRead is not null)
            {
                await beforeRead(cancellationToken);
            }
            return await inner.ReadExactlyAsync(offset, length, cancellationToken);
        }
        public async ValueTask DisposeAsync()
        {
            if (FailClose)
            {
                throw new IOException("Physical close not confirmed.");
            }
            await inner.DisposeAsync();
            Closed = true;
        }
    }
}

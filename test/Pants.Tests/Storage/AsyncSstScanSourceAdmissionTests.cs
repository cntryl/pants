using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage;

public sealed class AsyncSstScanSourceAdmissionTests
{
    [Fact]
    public async Task ShouldRejectScanSourceWhenItsReaderMetadataCannotFitTheSharedBudget()
    {
        using var directory = new TemporaryDirectory();
        var (path, file) = CreateSst(directory.Path);
        long metadataBytes;
        await using (var probe = await OpenAsync(path, file))
        {
            metadataBytes = probe.EstimatedScanMetadataBytes;
        }

        var budget = new ResourceBudget(metadataBytes - 1);
        var reader = await OpenAsync(path, file);

        await Assert.ThrowsAsync<PantsResourceLimitException>(async () =>
            await AsyncSstScanSource.CreateAsync(
                file,
                reader,
                static _ => ValueTask.FromException<AsyncSstReader>(new NotSupportedException()),
                PantsScanDirection.Forward,
                null,
                null,
                budget));

        Assert.Equal(0, budget.Current);
        Assert.Throws<ObjectDisposedException>(() =>
            reader.GetPointReadDecision(TestBytes.FromString("key-0000")));
    }

    [Fact]
    public async Task ShouldReleaseReservationAndReaderWhenSourceMetadataIsInvalid()
    {
        using var directory = new TemporaryDirectory();
        var (path, file) = CreateEmptySst(directory.Path);
        var reader = await OpenAsync(path, file);
        var budget = new ResourceBudget(reader.EstimatedScanMetadataBytes);

        await Assert.ThrowsAsync<PantsCorruptionException>(async () =>
            await AsyncSstScanSource.CreateAsync(
                file,
                reader,
                static _ => ValueTask.FromException<AsyncSstReader>(new NotSupportedException()),
                PantsScanDirection.Forward,
                null,
                null,
                budget));

        Assert.Equal(0, budget.Current);
        Assert.Throws<ObjectDisposedException>(() => reader.GetPointReadDecision(TestBytes.FromString("x")));
    }

    [Fact]
    public async Task ShouldHoldReaderMetadataReservationUntilScanSourceDisposal()
    {
        using var directory = new TemporaryDirectory();
        var (path, file) = CreateSst(directory.Path);
        await using var probe = await OpenAsync(path, file);
        var metadataBytes = probe.EstimatedScanMetadataBytes;
        var budget = new ResourceBudget(metadataBytes);
        var reader = await OpenAsync(path, file);

        var source = await AsyncSstScanSource.CreateAsync(
            file,
            reader,
            static _ => ValueTask.FromException<AsyncSstReader>(new NotSupportedException()),
            PantsScanDirection.Forward,
            null,
            null,
            budget);

        Assert.Equal(metadataBytes, budget.Current);
        await source.DisposeAsync();
        await source.DisposeAsync();
        Assert.Equal(0, budget.Current);
    }

    static async ValueTask<AsyncSstReader> OpenAsync(string path, FileMeta file) =>
        await AsyncSstReader.OpenAsync(
            LocalAsyncSstSource.Open(path),
            file,
            CancellationToken.None);

    static (string Path, FileMeta File) CreateSst(string root)
    {
        var path = Path.Combine(root, "scan.sst");
        var entries = Enumerable.Range(0, 16)
            .Select(index => new SstEntry(
                TestBytes.FromString($"key-{index:0000}"),
                new byte[128],
                checked((ulong)index + 1),
                null,
                false))
            .ToArray();
        var bytes = SstCodec.Encode(entries, [], PantsPerformanceGoal.Latency);
        File.WriteAllBytes(path, bytes);

        return (path, new FileMeta
        {
            Name = "scan.sst",
            SizeBytes = checked((ulong)bytes.Length),
            ColumnFamilyId = 0,
            SstSequence = 1,
            SmallestKey = entries[0].Key,
            LargestKey = entries[^1].Key,
            KeyBoundsComplete = true,
            SmallestSequence = 1,
            LargestSequence = (ulong)entries.Length
        });
    }

    static (string Path, FileMeta File) CreateEmptySst(string root)
    {
        var path = Path.Combine(root, "empty.sst");
        var bytes = SstCodec.Encode([], [], PantsPerformanceGoal.Latency);
        File.WriteAllBytes(path, bytes);
        return (path, new FileMeta
        {
            Name = "empty.sst",
            SizeBytes = checked((ulong)bytes.Length)
        });
    }
}

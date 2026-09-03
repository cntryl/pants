namespace Cntryl.Pants.Tests.Cloud;

public sealed class PantsCloudDiskResidentFailureTests
{
    [Fact]
    public async Task ShouldFailOpenWithoutCachePollutionGivenRemoteSstIsMissing()
    {
        using var directory = new TemporaryDirectory();
        var remoteSst = await CreateRemoteOnlyCorpusAsync(directory.Path);
        File.Delete(remoteSst);

        await Assert.ThrowsAnyAsync<PantsException>(() => OpenAsync(directory.Path).AsTask());

        AssertCacheIsClean(directory.Path);
    }

    [Fact]
    public async Task ShouldFailOpenWithoutCachePollutionGivenRemoteSstIsTruncated()
    {
        using var directory = new TemporaryDirectory();
        var remoteSst = await CreateRemoteOnlyCorpusAsync(directory.Path);
        using (var stream = new FileStream(remoteSst, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            stream.SetLength(stream.Length - 1);
        }

        await Assert.ThrowsAnyAsync<PantsException>(() => OpenAsync(directory.Path).AsTask());

        AssertCacheIsClean(directory.Path);
    }

    [Fact]
    public async Task ShouldFailOpenWithoutCachePollutionGivenRemoteSstMetadataIsCorrupt()
    {
        using var directory = new TemporaryDirectory();
        var remoteSst = await CreateRemoteOnlyCorpusAsync(directory.Path);
        using (var stream = new FileStream(remoteSst, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            stream.Position = stream.Length - 1;
            var value = stream.ReadByte();
            stream.Position = stream.Length - 1;
            stream.WriteByte((byte)(value ^ 0xFF));
        }

        await Assert.ThrowsAnyAsync<PantsException>(() => OpenAsync(directory.Path).AsTask());

        AssertCacheIsClean(directory.Path);
    }

    [Fact]
    public async Task ShouldRejectACorruptRemoteDataBlockOnlyWhenReadWithoutCachePollution()
    {
        using var directory = new TemporaryDirectory();
        var remoteSst = await CreateRemoteOnlyCorpusAsync(directory.Path);
        SstBlockHandle firstBlock;
        using (var reader = SstReader.Open(remoteSst))
        {
            firstBlock = reader.GetDataBlockHandle(0);
        }

        using (var stream = new FileStream(remoteSst, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            stream.Position = checked((long)firstBlock.Offset);
            var value = stream.ReadByte();
            stream.Position = checked((long)firstBlock.Offset);
            stream.WriteByte((byte)(value ^ 0xFF));
        }

        await using var database = await OpenAsync(directory.Path);
        await using var readerTransaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);

        await Assert.ThrowsAnyAsync<PantsException>(() =>
            readerTransaction.GetAsync(Key(0)).AsTask());

        await using (var scan = await readerTransaction.ScanAsync(new PantsScanQuery()))
        {
            await Assert.ThrowsAnyAsync<PantsException>(async () =>
            {
                await foreach (var _ in scan)
                {
                }
            });
        }

        var metrics = await database.GetRuntimeMetricsAsync();
        Assert.Equal(0, metrics.BlockCacheUsedBytes);
        Assert.True(metrics.ScanBufferPeakBytes <= metrics.ScanBufferCapacityBytes);
        Assert.Equal(0, metrics.ScanBufferUsedBytes);
        AssertCacheIsClean(directory.Path);
    }

    static async Task<string> CreateRemoteOnlyCorpusAsync(string path)
    {
        await using (var database = await OpenAsync(path))
        {
            await using var writer = await database.BeginTransactionAsync(
                database.DefaultColumnFamily,
                PantsTransactionMode.ReadWrite);
            for (var index = 0; index < 32; index++)
            {
                writer.Put(Key(index), Value(index));
            }

            await writer.CommitAsync(PantsWriteOptions.CloudStrict);
            await database.FlushAsync(database.DefaultColumnFamily);
        }

        foreach (var local in LocalSsts(path))
        {
            File.Delete(local);
        }

        return Assert.Single(Directory.GetFiles(
            Path.Combine(path, "cloud_store", "sst"),
            "*.sst"));
    }

    static ValueTask<IPantsDatabase> OpenAsync(string path) =>
        PantsDatabase.OpenAsync(
            PantsOpenOptions.SimulatedCloud(path, "pants-tests", "remote-failures/")
                .WithBackgroundCompaction(false));

    static void AssertCacheIsClean(string path)
    {
        Assert.Empty(LocalSsts(path));
        Assert.Empty(Directory.GetFiles(path, "*.tmp", SearchOption.AllDirectories));
    }

    static string[] LocalSsts(string path) =>
        Directory.GetFiles(Path.Combine(path, "sst"), "*.sst");

    static byte[] Key(int index) => TestBytes.FromString($"failure:{index:D4}");

    static byte[] Value(int index)
    {
        var value = new byte[2 * 1024];
        new Random(index).NextBytes(value);
        return value;
    }
}

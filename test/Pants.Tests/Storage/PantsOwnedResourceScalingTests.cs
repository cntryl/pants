namespace Cntryl.Pants.Tests.Storage;

public sealed class PantsOwnedResourceScalingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldGrowDiskPartitionsWithoutGrowingRetainedPayloadAcrossN2N4N(
        bool simulatedCloud)
    {
        var observations = new List<(int Count, PantsRuntimeMetrics Metrics)>();
        foreach (var count in new[] { 32, 64, 128 })
        {
            using var directory = new TemporaryDirectory();
            var options = CreateOptions(directory.Path, simulatedCloud);
            await using (var database = await PantsDatabase.OpenAsync(options))
            {
                for (var start = 0; start < count; start += 16)
                {
                    await using var writer = await database.BeginTransactionAsync(
                        database.DefaultColumnFamily,
                        PantsTransactionMode.ReadWrite);
                    for (var index = start; index < start + 16; index++)
                    {
                        writer.Put(Key(index), Value(index));
                    }

                    await writer.CommitAsync(
                        simulatedCloud ? PantsWriteOptions.CloudStrict : PantsWriteOptions.Sync);
                    await database.FlushAsync(database.DefaultColumnFamily);
                }
            }

            if (simulatedCloud)
            {
                foreach (var path in Directory.GetFiles(Path.Combine(directory.Path, "sst"), "*.sst"))
                {
                    File.Delete(path);
                }
            }

            await using var reopened = await PantsDatabase.OpenAsync(options);
            await using (var reader = await reopened.BeginTransactionAsync(
                             reopened.DefaultColumnFamily,
                             PantsTransactionMode.ReadOnly))
            await using (var scan = await reader.ScanAsync(new PantsScanQuery()))
            {
                var seen = 0;
                await foreach (var entry in scan)
                {
                    Assert.Equal(Key(seen), entry.Key.ToArray());
                    Assert.Equal(Value(seen), entry.Value.ToArray());
                    seen++;
                }

                Assert.Equal(count, seen);
            }

            var metrics = await reopened.GetRuntimeMetricsAsync();
            Assert.Equal(0, metrics.TotalMemtableBytes);
            Assert.Equal(0, metrics.ActiveMemtableBytes);
            Assert.Equal(0, metrics.ImmutableMemtableBytes);
            Assert.Equal(0, metrics.CompactionBufferUsedBytes);
            Assert.Equal(0, metrics.ScanBufferUsedBytes);
            Assert.True(metrics.BlockCacheUsedBytes <= metrics.BlockCacheCapacityBytes);
            Assert.True(metrics.CompactionBufferPeakBytes <= metrics.CompactionBufferCapacityBytes);
            Assert.InRange(metrics.ScanBufferPeakBytes, 1, metrics.ScanBufferCapacityBytes);
            observations.Add((count, metrics));
        }

        Assert.True(observations[0].Metrics.SstCount < observations[1].Metrics.SstCount);
        Assert.True(observations[1].Metrics.SstCount < observations[2].Metrics.SstCount);
        Assert.True(observations[0].Metrics.SstBytes < observations[1].Metrics.SstBytes);
        Assert.True(observations[1].Metrics.SstBytes < observations[2].Metrics.SstBytes);
        Assert.All(observations, observation =>
            Assert.Equal(0, observation.Metrics.TotalMemtableBytes));
    }

    static PantsOpenOptions CreateOptions(string path, bool simulatedCloud) =>
        (simulatedCloud
            ? PantsOpenOptions.SimulatedCloud(path, "pants-tests", "resource-scaling/")
            : PantsOpenOptions.Local(path))
        .WithMemoryBudget(PantsMemoryBudget.FromBytes(32L * 1024 * 1024))
        .WithMemtableLimits(2 * 1024 * 1024)
        .WithBackgroundCompaction(false);

    static byte[] Key(int index) => TestBytes.FromString($"scale:{index:D6}");

    static byte[] Value(int index)
    {
        var value = new byte[512];
        new Random(index).NextBytes(value);
        return value;
    }
}

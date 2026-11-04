using System.Buffers.Binary;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage;

public sealed class SstPointReadAllocationTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public async Task ShouldRejectConflictingUnrelatedEntriesGivenPointRead(bool asynchronous, int conflict)
    {
        using var directory = new TemporaryDirectory();
        using var store = LocalDiskStore.Open(directory.Path,
            new RuntimeState(new ManualClock(DateTimeOffset.UnixEpoch), new RuntimeTelemetry()));
        var first = new SstEntry(Key(1), "first"u8.ToArray(), 1, null, false);
        var duplicate = conflict switch
        {
            0 => first with { Value = "other"u8.ToArray() },
            1 => first with { EntryType = 1 },
            _ => first with { Expiration = 0 }
        };
        var bytes = SstCodec.Encode(
            [new SstEntry(Key(0), "found"u8.ToArray(), 2, null, false), first, duplicate],
            [], PantsPerformanceGoal.Latency);
        const string name = "000000_00_00000000000000000001.sst";
        File.WriteAllBytes(Path.Combine(directory.Path, "sst", name), bytes);
        var files = new[] { new FileMeta { Name = name, SizeBytes = (ulong)bytes.Length } };

        if (asynchronous)
        {
            await Assert.ThrowsAsync<PantsCorruptionException>(() =>
                store.TryReadPointValueAsync(files, Key(0), CancellationToken.None).AsTask());
        }
        else
        {
            Assert.Throws<PantsCorruptionException>(() => store.TryReadPointValue(files, Key(0)));
        }
    }

    [Fact]
    public void ShouldAvoidMaterializingUnrelatedValuesGivenWarmPointRead()
    {
        using var directory = new TemporaryDirectory();
        using var store = LocalDiskStore.Open(directory.Path,
            new RuntimeState(new ManualClock(DateTimeOffset.UnixEpoch), new RuntimeTelemetry()),
            blockCacheBytes: 1024 * 1024);
        var value = new byte[128];
        var entries = Enumerable.Range(0, 128)
            .Select(index => new SstEntry(Key(index), value, (ulong)index + 1, null, false)).ToArray();
        var bytes = SstCodec.Encode(entries, [], PantsPerformanceGoal.Latency);
        const string name = "000000_00_00000000000000000001.sst";
        File.WriteAllBytes(Path.Combine(directory.Path, "sst", name), bytes);
        var files = new[] { new FileMeta { Name = name, SizeBytes = (ulong)bytes.Length } };
        var key = Key(64);
        _ = store.TryReadPointValue(files, key);
        var before = GC.GetAllocatedBytesForCurrentThread();

        var found = store.TryReadPointValue(files, key);

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.NotNull(found);
        Assert.Equal(value, found.Value);
        Assert.True(allocated < 16 * 1024,
            $"A warm read allocated {allocated} bytes while selecting one of 128 values.");
    }

    static byte[] Key(int index)
    {
        var key = new byte[16];
        BinaryPrimitives.WriteUInt64BigEndian(key, checked((ulong)index));
        return key;
    }
}

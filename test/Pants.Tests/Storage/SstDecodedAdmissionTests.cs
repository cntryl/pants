using System.Buffers.Binary;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage;

public sealed class SstDecodedAdmissionTests
{
    [Fact]
    public void ShouldRejectPrefixAmplificationBeforeAllocatingExpandedKeys()
    {
        var bytes = CreatePrefixEncodedSst();
        var block = SstCodec.ReadBlock(bytes,
            new SstBlockHandle(0, BinaryPrimitives.ReadUInt32LittleEndian(bytes) + 4UL));
        _ = CaptureResourceLimit(block);
        var before = GC.GetAllocatedBytesForCurrentThread();

        var error = CaptureResourceLimit(block);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.NotNull(error);
        Assert.True(allocated < 64 * 1024,
            $"Rejected preflight allocated {allocated} bytes for a block expanding into more than 512 KiB of keys.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldRejectExpandedEntriesWithinRemainingIteratorBudget(bool asynchronous)
    {
        using var directory = new TemporaryDirectory();
        const string name = "000000_00_00000000000000000001.sst";
        var path = Path.Combine(directory.Path, name);
        var bytes = CreatePrefixEncodedSst();
        await File.WriteAllBytesAsync(path, bytes);
        var budget = new ResourceBudget(64 * 1024);
        if (asynchronous)
        {
            var source = LocalAsyncSstSource.Open(path);
            var reader = await AsyncSstReader.OpenAsync(source,
                new FileMeta { Name = name, SizeBytes = (ulong)bytes.Length }, CancellationToken.None);
            await using var iterator = new AsyncSstBlockIterator(reader, PantsScanDirection.Forward, null, null, budget);

            await Assert.ThrowsAsync<PantsResourceLimitException>(() => iterator.MoveNextAsync(CancellationToken.None).AsTask());
        }
        else
        {
            using var reader = SstReader.Open(path);
            using var iterator = SstBlockIterator.Create(reader, PantsScanDirection.Forward, resourceBudget: budget);

            Assert.Throws<PantsResourceLimitException>(() => iterator.MoveNext());
        }

        Assert.Equal(0, budget.Current);
        Assert.True(budget.Peak <= budget.Limit);
    }

    static PantsResourceLimitException? CaptureResourceLimit(byte[] block)
    {
        try
        {
            _ = SstCodec.DecodeDataBlock(block, 16 * 1024);
            return null;
        }
        catch (PantsResourceLimitException exception)
        {
            return exception;
        }
    }

    static byte[] CreatePrefixEncodedSst()
    {
        var key = Enumerable.Repeat((byte)'k', 4096).ToArray();
        var entries = Enumerable.Range(0, 128)
            .Select(index => new SstEntry(key, [], (ulong)(128 - index), null, false)).ToArray();
        return SstCodec.Encode(entries, [], PantsPerformanceGoal.Latency);
    }
}

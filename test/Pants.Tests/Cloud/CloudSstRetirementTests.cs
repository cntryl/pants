using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

public sealed class CloudSstRetirementTests
{
    const string Name = "000000_00_00000000000000000001.sst";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldAcknowledgeExactRetiredIdentityAfterDeletionOrConfirmedAbsence(bool alreadyAbsent)
    {
        var store = new ScriptedCloudObjectStore();
        var bytes = SstCodec.Encode([new SstEntry("key"u8.ToArray(), "value"u8.ToArray(), 1, null, false)], [], PantsPerformanceGoal.Latency);
        var retired = CreateIdentity(bytes);
        if (!alreadyAbsent)
        {
            store.Write(PantsCloudObjectLayout.SstPrefix + Name, bytes);
        }

        var acknowledgements = 0;
        var budget = new ResourceBudget(256 * 1024);
        var collector = CreateCollector(store, retired, budget, () => true, file =>
        {
            Assert.Equal(retired.ContentCrc32C, file.ContentCrc32C);
            acknowledgements++;
        });

        Assert.True(await collector.CollectAsync(CancellationToken.None));

        Assert.Equal(1, acknowledgements);
        Assert.False(store.Contains(PantsCloudObjectLayout.SstPrefix + Name));
        Assert.Equal(0, budget.Current);
    }

    [Fact]
    public async Task ShouldRetainDeletionObligationWhenRemoteBytesDoNotMatchRetiredChecksum()
    {
        var store = new ScriptedCloudObjectStore();
        byte[] expected = [1, 2, 3, 4];
        store.Write(PantsCloudObjectLayout.SstPrefix + Name, [1, 2, 3, 5]);
        var acknowledgements = 0;
        var budget = new ResourceBudget(256 * 1024);
        var collector = CreateCollector(store, CreateIdentity(expected), budget, () => true, _ => acknowledgements++);

        Assert.False(await collector.CollectAsync(CancellationToken.None));

        Assert.Equal(0, acknowledgements);
        Assert.True(store.Contains(PantsCloudObjectLayout.SstPrefix + Name));
        Assert.DoesNotContain(store.Requests, static request => request.Operation == StoreOperation.Delete);
        Assert.Equal(0, budget.Current);
    }

    [Fact]
    public async Task ShouldRetainRetiredSstWhenReadDependencyPreventsDeletionAfterByteVerification()
    {
        var store = new ScriptedCloudObjectStore();
        byte[] bytes = [1, 2, 3, 4];
        store.Write(PantsCloudObjectLayout.SstPrefix + Name, bytes);
        var eligible = true;
        store.BeforeRangeRead = (_, _) =>
        {
            eligible = false;
            return ValueTask.CompletedTask;
        };
        var acknowledgements = 0;
        var budget = new ResourceBudget(256 * 1024);
        var collector = CreateCollector(store, CreateIdentity(bytes), budget, () => eligible, _ => acknowledgements++);

        Assert.False(await collector.CollectAsync(CancellationToken.None));

        Assert.Equal(0, acknowledgements);
        Assert.True(store.Contains(PantsCloudObjectLayout.SstPrefix + Name));
        Assert.DoesNotContain(store.Requests, static request => request.Operation == StoreOperation.Delete);
        Assert.Equal(0, budget.Current);
    }

    static FileMeta CreateIdentity(byte[] bytes) => new()
    {
        Name = Name,
        SizeBytes = checked((ulong)bytes.Length),
        ContentCrc32C = DiskFormat.Crc32C(bytes),
        ColumnFamilyId = 0,
        SstSequence = 1
    };

    static CloudSstGarbageCollector CreateCollector(
        ICloudObjectStore store, FileMeta retired, ResourceBudget budget, Func<bool> eligible, Action<FileMeta> acknowledge) => new(
        store,
        _ => ValueTask.FromResult(new CloudSstRetentionProof(
            new HashSet<string>(StringComparer.Ordinal), new Dictionary<string, string>(), new Dictionary<uint, ulong>(), [],
            RetiredSsts: new Dictionary<string, FileMeta>(StringComparer.Ordinal) { [Name] = retired },
            AcknowledgeRetiredSst: acknowledge, ValidateRetiredSst: _ => eligible(), ValidationBudget: budget)),
        static () => new HashSet<string>(StringComparer.Ordinal), static () => { }, static _ => ValueTask.CompletedTask,
        NullPantsFailpointHandler.Instance);
}

using System.Collections.Concurrent;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Observability;

public sealed class PantsRecoveryInventoryWorkTests
{
    [Theory]
    [InlineData("success")]
    [InlineData("missing")]
    [InlineData("wrong_size")]
    [InlineData("error")]
    public async Task ShouldCreditInventoryOnlyAfterAuthoritativeSstMetadataIsValidated(string outcome)
    {
        using var directory = new TemporaryDirectory();
        var sst = SstCodec.Encode(
            [new SstEntry("key"u8.ToArray(), "value"u8.ToArray(), 1, null, false)],
            [], PantsPerformanceGoal.Latency);
        var manifest = ManifestState.CreateInitial();
        var file = new FileMeta
        {
            Name = "000000_00_00000000000000000001.sst",
            ColumnFamilyId = 0,
            SstSequence = 1,
            Level = 0,
            SizeBytes = checked((ulong)sst.Length),
            ContentCrc32C = DiskFormat.Crc32C(sst),
            SmallestKey = "key"u8.ToArray(),
            LargestKey = "key"u8.ToArray(),
            KeyBoundsComplete = true,
            SmallestSequence = 1,
            LargestSequence = 1
        };
        manifest.Files.Add(file);
        manifest.NextSstSeqs[0] = 2;
        manifest.FlushedThrough[0] = 1;
        var control = new ScriptedCloudObjectStore();
        await CloudTestMetadata.SeedAsync(control, manifest);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heads = new HeadStore(async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            return outcome switch
            {
                "missing" => null,
                "error" => throw new IOException("The inventory request failed."),
                "wrong_size" => new CloudObjectMetadata(checked((ulong)sst.Length + 1), "sst", null, null),
                _ => new CloudObjectMetadata(checked((ulong)sst.Length), "sst", null, null)
            };
        });
        var completed = new ConcurrentQueue<RecoveryWorkCompleted>();
        using var sink = new RecoveryWorkSink(completed.Enqueue);
        var hydration = ProviderCloudPersistence.HydrateLocalCacheAsync(directory.Path,
            new ScriptedCloudObjectStore(), heads, control, PantsRecoveryPolicy.Strict,
            static _ => ValueTask.CompletedTask, CancellationToken.None, recoveryWork: sink).AsTask();
        try
        {
            await entered.Task.WaitAsync(TestTimeouts.Expected);
            Assert.DoesNotContain(completed, static work => work.Stage == "sst_inventory");
        }
        finally
        {
            release.TrySetResult();
        }

        if (outcome != "success")
        {
            await Assert.ThrowsAnyAsync<Exception>(() => hydration.WaitAsync(TestTimeouts.Expected));
            Assert.DoesNotContain(completed, static work => work.Stage == "sst_inventory");
            return;
        }

        await hydration.WaitAsync(TestTimeouts.Expected);
        Assert.Contains(completed, static work => work.Stage == "remote_read" && work.Bytes > 0);
        var inventory = Assert.Single(completed, static work => work.Stage == "sst_inventory");
        Assert.Equal(1, inventory.Operations);
        Directory.CreateDirectory(Path.Combine(directory.Path, "sst"));
        File.WriteAllBytes(Path.Combine(directory.Path, "sst", file.Name), sst);
        await using var recovered = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path));
        await using var reader = await recovered.Transactions.BeginAsync(
            recovered.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        Assert.Equal("value"u8.ToArray(), (await reader.GetAsync("key"u8.ToArray()))?.ToArray());
    }

    sealed class HeadStore(Func<string, CancellationToken, ValueTask<CloudObjectMetadata?>> read) : ICloudObjectStore
    {
        public ValueTask<CloudObjectMetadata?> HeadAsync(string objectKey, CancellationToken cancellationToken) =>
            read(objectKey, cancellationToken);

        public ValueTask<CloudObject?> GetAsync(string objectKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<bool> PutAsync(string objectKey, ReadOnlyMemory<byte> data,
            CloudObjectWriteCondition condition, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<CloudObjectListPage> ListPageAsync(string prefix, string? continuationToken,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<CloudObjectDeleteOutcome> DeleteAsync(string objectKey,
            CloudObjectDeleteCondition condition, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}

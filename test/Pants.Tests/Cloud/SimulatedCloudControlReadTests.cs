using Cntryl.Pants.Cloud.Internal.Objects;
using Cntryl.Pants.Storage.Internal.IO;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

public sealed class SimulatedCloudControlReadTests
{
    [Fact]
    public async Task ShouldReturnSortedSimulatedCloudListPagesOfAtMostThirtyTwoObjects()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new SimulatedCloudObjectStore(directory.Path);
        var expectedKeys = Enumerable.Range(1, 40)
            .Select(index => $"sst/{index:D3}.sst")
            .Order(StringComparer.Ordinal)
            .ToArray();
        foreach (var objectKey in expectedKeys.Reverse())
        {
            Assert.True(await store.PutAsync(
                objectKey,
                new byte[] { 1 },
                new CloudObjectWriteCondition.IfAbsent(),
                CancellationToken.None));
        }

        var firstPage = await store.ListPageAsync("sst/", null, CancellationToken.None);

        Assert.Equal(expectedKeys[..32], firstPage.ObjectKeys);
        Assert.NotNull(firstPage.ContinuationToken);

        var secondPage = await store.ListPageAsync("sst/", firstPage.ContinuationToken, CancellationToken.None);

        Assert.Equal(expectedKeys[32..], secondPage.ObjectKeys);
        Assert.Null(secondPage.ContinuationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldReleaseAnAdmittedCaptureAndNativeLockWhenItsOwnerStops(bool disposeOwner)
    {
        using var directory = new TemporaryDirectory();
        await using var store = new SimulatedCloudObjectStore(directory.Path);
        const string key = "metadata/catalog.json";
        Assert.True(await store.PutAsync(key, new byte[257], new CloudObjectWriteCondition.IfAbsent()));
        var budget = new ResourceBudget(16 * 1024);
        using var cancellation = new CancellationTokenSource();
        ProviderCloudControlMetadataReadSet? reads = null;
        using (reads = new ProviderCloudControlMetadataReadSet(store, budget, metadataAdmitted: () =>
               {
                   Assert.True(budget.Current > 257);
                   if (disposeOwner)
                   {
                       reads!.Dispose();
                   }
                   else
                   {
                       cancellation.Cancel();
                   }
               }))
        {
            if (disposeOwner)
            {
                await Assert.ThrowsAsync<ObjectDisposedException>(() => reads.ReadAsync(key, cancellation.Token).AsTask());
            }
            else
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reads.ReadAsync(key, cancellation.Token).AsTask());
            }
            Assert.Equal(0, budget.Current);
        }
        Assert.True(await store.PutAsync(key, "successor"u8.ToArray(), new CloudObjectWriteCondition.Unconditional()));
        using var successor = new ProviderCloudControlMetadataReadSet(store, budget);
        var value = Assert.IsType<CloudObject>(await successor.ReadAsync(key, CancellationToken.None));
        Assert.Equal("successor"u8.ToArray(), value.Data.ToArray());
    }

    [Fact]
    public async Task ShouldKeepAnAdmittedControlReadCoherentWhileAnotherNativeWriterReplacesTheObject()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new SimulatedCloudObjectStore(directory.Path);
        const string key = "metadata/catalog.json";
        var original = Enumerable.Repeat((byte)1, ProviderCloudControlMetadataReadSet.ReadRangeBytes + 7).ToArray();
        var replacement = Enumerable.Repeat((byte)2, original.Length + 11).ToArray();
        Assert.True(await store.PutAsync(key, original, new CloudObjectWriteCondition.IfAbsent()));
        var budget = new ResourceBudget(4 * 1024 * 1024);
        var contended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? mutation = null;
        using (var reads = new ProviderCloudControlMetadataReadSet(store, budget, metadataAdmitted: () =>
               {
                   mutation = Task.Run(() =>
                   {
                       using var held = SimulatedCloudMutationLock.Acquire(directory.Path,
                           Path.Combine(directory.Path, "metadata", "catalog.json"), CancellationToken.None,
                           () => contended.TrySetResult());
                       AtomicStagedFile.Write(Path.Combine(directory.Path, "metadata", "catalog.json"), replacement);
                   });
                   _ = Task.WhenAny(mutation, contended.Task).WaitAsync(TestTimeouts.Expected)
                       .GetAwaiter().GetResult();
               }))
        {
            try
            {
                var captured = Assert.IsType<CloudObject>(await reads.ReadAsync(key, CancellationToken.None));
                Assert.Equal(original, captured.Data.ToArray());
                Assert.True(budget.Current > original.Length);
            }
            finally
            {
                if (mutation is not null)
                {
                    await mutation.WaitAsync(TestTimeouts.Expected);
                }
            }
        }
        Assert.Equal(0, budget.Current);
        Assert.True(contended.Task.IsCompletedSuccessfully);
        using var successorReads = new ProviderCloudControlMetadataReadSet(store, budget);
        var successor = Assert.IsType<CloudObject>(await successorReads.ReadAsync(key, CancellationToken.None));
        Assert.Equal(replacement, successor.Data.ToArray());
    }
}

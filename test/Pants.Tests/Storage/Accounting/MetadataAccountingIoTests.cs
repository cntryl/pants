using Cntryl.Pants.Storage.Internal.Accounting;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage.Accounting;

public sealed class MetadataAccountingIoTests
{
    [Fact]
    public async Task ShouldObserveActualStreamedMetadataPayloadAndNamespaceDeletion()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "metadata.bin");
        var owner = new MetadataAccountingOwner();
        using var operation = owner.Begin(MetadataOperationKind.OtherMetadata, MetadataOrigin.Bootstrap, MetadataMedium.Persistent);
        var observation = operation.Observe(MetadataPayload.OtherMetadata);
        var bytes = "streamed metadata body"u8.ToArray();
        await AtomicStagedFile.WriteStreamedAsync(path,
            file => observation.WriteAsync(file, bytes, 0), observation: observation);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        AtomicStagedFile.Delete(path, observation);
        operation.Finish(true);
        Assert.False(File.Exists(path));
        var snapshot = owner.Handle.Snapshot();
        var bucket = snapshot.Bucket(MetadataOrigin.Bootstrap, MetadataMedium.Persistent);
        Assert.Equal((ulong)bytes.Length, bucket[MetadataCounter.OtherIssuedBytes]);
        Assert.Equal((ulong)bytes.Length, bucket[MetadataCounter.OtherReturnedBytes]);
        Assert.Equal(0UL, snapshot.LateMutations);
        Assert.Equal(0UL, bucket.ActiveOperations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldInvalidateActualZeroPayloadDirectoryBarrierReturningAfterSeal(bool error)
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "manifest.journal");
        var owner = new MetadataAccountingOwner();
        var before = owner.Handle.Snapshot();
        using var operation = owner.Begin(MetadataOperationKind.Checkpoint, MetadataOrigin.Administration, MetadataMedium.Persistent);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var failure = new IOException("held directory barrier return failed");
        var worker = Task.Run(() => AtomicStagedFile.Write(path, [],
            observation: operation.Observe(MetadataPayload.Journal),
            flushDirectory: parent =>
            {
                AtomicStagedFile.FlushDirectory(parent);
                entered.Set();
                if (!release.Wait(TestTimeouts.Expected))
                {
                    throw new TimeoutException("directory barrier was not released");
                }

                if (error)
                {
                    throw failure;
                }
            }));
        try
        {
            Assert.True(entered.Wait(TestTimeouts.Expected));
            Assert.False(worker.IsCompleted);
            operation.Finish(false);
        }
        finally
        {
            release.Set();
        }

        if (error)
        {
            Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => worker));
        }
        else
        {
            await worker;
        }

        Assert.True(File.Exists(path));
        Assert.Empty(File.ReadAllBytes(path));
        var after = owner.Handle.Snapshot();
        Assert.True(after.LateMutations > 0);
        Assert.Equal(0UL, after.Bucket(MetadataOrigin.Administration, MetadataMedium.Persistent)[MetadataCounter.JournalIssuedBytes]);
        Assert.Throws<InvalidOperationException>(() => after.Delta(before));
    }

    [Fact]
    public void ShouldRetainReturnedPayloadWhenActualJournalSyncFails()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "manifest.journal");
        var owner = new MetadataAccountingOwner();
        using var operation = owner.Begin(MetadataOperationKind.JournalAppend, MetadataOrigin.Ddl, MetadataMedium.Persistent);
        var payload = "actual framed journal bytes"u8.ToArray();
        var failure = new IOException("required sync failed");

        Assert.Same(failure, Assert.Throws<IOException>(() => PositionalFile.AppendAndFlush(
            path, [payload], beforeFlush: () => throw failure,
            observation: operation.Observe(MetadataPayload.Journal))));
        operation.Finish(false);

        Assert.Equal(payload, File.ReadAllBytes(path));
        var bucket = owner.Handle.Snapshot().Bucket(MetadataOrigin.Ddl, MetadataMedium.Persistent);
        Assert.Equal((ulong)payload.Length, bucket[MetadataCounter.JournalIssuedBytes]);
        Assert.Equal((ulong)payload.Length, bucket[MetadataCounter.JournalReturnedBytes]);
        Assert.Equal(0UL, bucket[MetadataCounter.JournalDurableBytes]);
        Assert.Equal(1UL, bucket[MetadataCounter.OperationFailures]);
        Assert.Equal(0UL, bucket.ActiveOperations);
    }

    [Fact]
    public async Task ShouldInvalidateLateErrorAfterActualDelegatedAppendChangesBytes()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "manifest.journal");
        var owner = new MetadataAccountingOwner();
        var handle = owner.Handle;
        var before = handle.Snapshot();
        using var operation = owner.Begin(MetadataOperationKind.JournalAppend, MetadataOrigin.Recovery, MetadataMedium.Persistent);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var payload = "actual bytes before held error"u8.ToArray();
        var failure = new IOException("delegated error after actual write");
        var worker = Task.Run(() => PositionalFile.AppendAndFlush(
            path, [payload], observation: operation.Observe(MetadataPayload.Journal),
            writeOperation: (file, buffers, offset) =>
            {
                RandomAccess.Write(file, buffers, offset);
                entered.Set();
                if (!release.Wait(TestTimeouts.Expected))
                {
                    throw new TimeoutException("held write was not released");
                }

                throw failure;
            }));
        try
        {
            Assert.True(entered.Wait(TestTimeouts.Expected));
            Assert.False(worker.IsCompleted);
            operation.Finish(false);
        }
        finally
        {
            release.Set();
        }

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => worker));
        Assert.Equal(payload, File.ReadAllBytes(path));
        var after = handle.Snapshot();
        var bucket = after.Bucket(MetadataOrigin.Recovery, MetadataMedium.Persistent);
        Assert.Equal((ulong)payload.Length, bucket[MetadataCounter.JournalIssuedBytes]);
        Assert.Equal(0UL, bucket[MetadataCounter.JournalReturnedBytes]);
        Assert.True(after.LateMutations > 0);
        Assert.Throws<InvalidOperationException>(() => after.Delta(before));
    }

    [Fact]
    public void ShouldSealOnceAndRejectOverflowWithoutChangingOperationResult()
    {
        var owner = new MetadataAccountingOwner();
        var before = owner.Handle.Snapshot();
        using (var operation = owner.Begin(MetadataOperationKind.Checkpoint, MetadataOrigin.Administration, MetadataMedium.MemoryOnly))
        {
            operation.Observe(MetadataPayload.Snapshot).Issued(ulong.MaxValue);
            operation.Observe(MetadataPayload.Snapshot).Issued(1);
            operation.Finish(true);
            operation.Finish(true);
        }

        var after = owner.Handle.Snapshot();
        var bucket = after.Bucket(MetadataOrigin.Administration, MetadataMedium.MemoryOnly);
        Assert.Equal(1UL, bucket[MetadataCounter.OperationAttempts]);
        Assert.Equal(0UL, bucket.ActiveOperations);
        Assert.True(after.Overflow);
        Assert.Throws<InvalidOperationException>(() => after.Delta(before));
        Assert.Throws<InvalidOperationException>(() => after.Delta(new MetadataAccountingOwner().Handle.Snapshot()));
    }
}

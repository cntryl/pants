using System.Buffers.Binary;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Transactions.Spill;

public sealed class TransactionOperationSourceTests
{
    static readonly ColumnFamilyIdentity Family = new(0, "default", 0);

    [Fact]
    public void ShouldStreamSpilledAndResidentOperationsGivenOneCommitTimestamp()
    {
        using var directory = new TemporaryDirectory();
        using var store = new TransactionSpillStore(directory.Path, 1, Family);
        store.WriteRun(
        [
            Put(0, "first", TimeSpan.FromSeconds(30)),
            Put(1, "second")
        ]);
        var commitTime = new DateTimeOffset(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);
        var source = new TransactionOperationSource(
            store,
            [Put(2, "third", TimeSpan.FromSeconds(30))],
            3,
            commitTime);
        var firstPass = new List<TransactionIntentOperation>();
        var secondPass = new List<TransactionIntentOperation>();

        source.ForEach(firstPass.Add);
        source.ForEach(secondPass.Add);

        Assert.True(source.IsSpilled);
        Assert.Equal(3UL, source.Count);
        Assert.Equal([0UL, 1UL, 2UL], firstPass.Select(static operation => operation.Ordinal));
        var expectedExpiration = UnixTimestamp.ExpirationFromTimeToLive(
            commitTime,
            TimeSpan.FromSeconds(30));
        Assert.Equal(expectedExpiration, firstPass[0].ExpirationUnixMilliseconds);
        Assert.Null(firstPass[1].ExpirationUnixMilliseconds);
        Assert.Equal(expectedExpiration, firstPass[2].ExpirationUnixMilliseconds);
        Assert.Equal(
            firstPass.Select(static operation => operation.ExpirationUnixMilliseconds),
            secondPass.Select(static operation => operation.ExpirationUnixMilliseconds));
    }

    [Fact]
    public void ShouldRejectMissingOrdinalGivenSpilledAndResidentOperations()
    {
        using var directory = new TemporaryDirectory();
        using var store = new TransactionSpillStore(directory.Path, 1, Family);
        store.WriteRun([Put(0, "first")]);
        var source = new TransactionOperationSource(
            store,
            [Put(2, "third")],
            3,
            DateTimeOffset.UnixEpoch);

        var error = Assert.Throws<PantsCorruptionException>(source.Validate);

        Assert.Equal(PantsErrorCode.Corruption, error.Code);
    }

    [Fact]
    public void ShouldRejectCorruptSparseIndexGivenStreamedSpillRun()
    {
        using var directory = new TemporaryDirectory();
        using var store = new TransactionSpillStore(directory.Path, 1, Family);
        store.WriteRun(Enumerable.Range(0, 17)
            .Select(index => Put(checked((ulong)index), $"key-{index:00}"))
            .ToArray());
        var runPath = Assert.Single(Directory.GetFiles(Path.Combine(directory.Path, "txn"), "*.run"));
        var bytes = File.ReadAllBytes(runPath);
        var sparseIndexOffset = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(28)));
        bytes[sparseIndexOffset + 8] ^= 0x55;
        File.WriteAllBytes(runPath, bytes);
        var source = new TransactionOperationSource(store, [], 17, DateTimeOffset.UnixEpoch);

        var error = Assert.Throws<PantsCorruptionException>(source.Validate);

        Assert.Equal(PantsErrorCode.Corruption, error.Code);
    }

    [Fact]
    public void ShouldRejectSparseIndexKeyThatDoesNotMatchIndexedOperation()
    {
        using var directory = new TemporaryDirectory();
        using var store = new TransactionSpillStore(directory.Path, 1, Family);
        store.WriteRun(Enumerable.Range(0, 17)
            .Select(index => Put(checked((ulong)index), $"key-{index:00}"))
            .ToArray());
        var runPath = Assert.Single(Directory.GetFiles(Path.Combine(directory.Path, "txn"), "*.run"));
        var bytes = File.ReadAllBytes(runPath);
        var sparseIndexOffset = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(28)));
        var firstSparseKeyOffset = sparseIndexOffset + 8 + sizeof(uint);
        bytes[firstSparseKeyOffset + "key-0".Length] = (byte)'1';
        RewriteFrameChecksum(bytes, sparseIndexOffset);
        File.WriteAllBytes(runPath, bytes);
        var source = new TransactionOperationSource(store, [], 17, DateTimeOffset.UnixEpoch);

        var error = Assert.Throws<PantsCorruptionException>(source.Validate);

        Assert.Equal(PantsErrorCode.Corruption, error.Code);
    }

    [Fact]
    public void ShouldRejectRangeChildCycleGivenStreamedSpillRun()
    {
        using var directory = new TemporaryDirectory();
        using var store = new TransactionSpillStore(directory.Path, 1, Family);
        store.WriteRun(
        [
            DeleteRange(0, "alpha", "zulu"),
            DeleteRange(1, "bravo", "charlie"),
            DeleteRange(2, "delta", "echo")
        ]);
        var rangePath = Assert.Single(Directory.GetFiles(Path.Combine(directory.Path, "txn"), "*.ranges"));
        var bytes = File.ReadAllBytes(rangePath);
        var rootOffset = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(32)));
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(rootOffset + 16), 0);
        RewriteFrameChecksum(bytes, rootOffset);
        File.WriteAllBytes(rangePath, bytes);
        var source = new TransactionOperationSource(store, [], 3, DateTimeOffset.UnixEpoch);

        var error = Assert.Throws<PantsCorruptionException>(source.Validate);

        Assert.Equal(PantsErrorCode.Corruption, error.Code);
    }

    [Fact]
    public void ShouldRejectIncorrectRangeSubtreeMaximumGivenStreamedSpillRun()
    {
        using var directory = new TemporaryDirectory();
        using var store = new TransactionSpillStore(directory.Path, 1, Family);
        store.WriteRun(
        [
            DeleteRange(0, "alpha", "zulu"),
            DeleteRange(1, "bravo", "charlie"),
            DeleteRange(2, "delta", "echo")
        ]);
        var rangePath = Assert.Single(Directory.GetFiles(Path.Combine(directory.Path, "txn"), "*.ranges"));
        var bytes = File.ReadAllBytes(rangePath);
        var rootOffset = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(32)));
        var maximumEndOffset = FindMaximumEndOffset(bytes, rootOffset);
        "zzzz"u8.CopyTo(bytes.AsSpan(maximumEndOffset, 4));
        RewriteFrameChecksum(bytes, rootOffset);
        File.WriteAllBytes(rangePath, bytes);
        var source = new TransactionOperationSource(store, [], 3, DateTimeOffset.UnixEpoch);

        var error = Assert.Throws<PantsCorruptionException>(source.Validate);

        Assert.Equal(PantsErrorCode.Corruption, error.Code);
    }

    [Fact]
    public void ShouldRejectRangeNodeThatDoesNotMatchDeleteRangeOperation()
    {
        using var directory = new TemporaryDirectory();
        using var store = new TransactionSpillStore(directory.Path, 1, Family);
        store.WriteRun([DeleteRange(0, "alpha", "zulu")]);
        var rangePath = Assert.Single(Directory.GetFiles(Path.Combine(directory.Path, "txn"), "*.ranges"));
        var bytes = File.ReadAllBytes(rangePath);
        var rootOffset = GetRangeNodeOffset(bytes, 0);
        var startOffset = rootOffset + 8 + 24 + sizeof(uint);
        "omega"u8.CopyTo(bytes.AsSpan(startOffset, 5));
        RewriteFrameChecksum(bytes, rootOffset);
        File.WriteAllBytes(rangePath, bytes);
        var source = new TransactionOperationSource(store, [], 1, DateTimeOffset.UnixEpoch);

        var error = Assert.Throws<PantsCorruptionException>(source.Validate);

        Assert.Equal(PantsErrorCode.Corruption, error.Code);
    }

    [Fact]
    public void ShouldRejectRangeTreeThatViolatesAncestorOrderingBounds()
    {
        using var directory = new TemporaryDirectory();
        using var store = new TransactionSpillStore(directory.Path, 1, Family);
        store.WriteRun(
        [
            DeleteRange(0, "a", "z"),
            DeleteRange(1, "b", "z"),
            DeleteRange(2, "c", "z"),
            DeleteRange(3, "d", "z"),
            DeleteRange(4, "e", "z"),
            DeleteRange(5, "f", "z"),
            DeleteRange(6, "g", "z")
        ]);
        var rangePath = Assert.Single(Directory.GetFiles(Path.Combine(directory.Path, "txn"), "*.ranges"));
        var bytes = File.ReadAllBytes(rangePath);
        const ulong noChild = ulong.MaxValue;
        RewriteRangeChildren(bytes, 1, noChild, 5);
        RewriteRangeChildren(bytes, 5, 2, noChild);
        RewriteRangeChildren(bytes, 2, noChild, 3);
        RewriteRangeChildren(bytes, 4, noChild, 6);
        File.WriteAllBytes(rangePath, bytes);
        var source = new TransactionOperationSource(store, [], 7, DateTimeOffset.UnixEpoch);

        var error = Assert.Throws<PantsCorruptionException>(source.Validate);

        Assert.Equal(PantsErrorCode.Corruption, error.Code);
    }

    [Fact]
    public void ShouldReturnCorruptionGivenSpilledTimeToLiveExceedingTimeSpan()
    {
        using var directory = new TemporaryDirectory();
        using var store = new TransactionSpillStore(directory.Path, 1, Family);
        store.WriteRun([Put(0, "key", TimeSpan.FromSeconds(1))]);
        var runPath = Assert.Single(Directory.GetFiles(Path.Combine(directory.Path, "txn"), "*.run"));
        var bytes = File.ReadAllBytes(runPath);
        const int operationFrameOffset = 48;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(operationFrameOffset + 22), ulong.MaxValue);
        RewriteFrameChecksum(bytes, operationFrameOffset);
        File.WriteAllBytes(runPath, bytes);
        var source = new TransactionOperationSource(store, [], 1, DateTimeOffset.UnixEpoch);

        var error = Assert.Throws<PantsCorruptionException>(source.Validate);

        Assert.Equal(PantsErrorCode.Corruption, error.Code);
    }

    [Fact]
    public void ShouldResolveLatestPointAndRangeIntentStrictlyBeforeOrdinalCeiling()
    {
        using var directory = new TemporaryDirectory();
        using var store = new TransactionSpillStore(directory.Path, 1, Family);
        store.WriteRun(
        [
            Put(0, "middle"),
            DeleteRange(1, "alpha", "omega")
        ]);
        store.WriteRun(
        [
            Put(2, "middle"),
            DeleteRange(3, "bravo", "november")
        ]);
        var source = new TransactionOperationSource(
            store,
            [Put(4, "middle")],
            5,
            DateTimeOffset.UnixEpoch);
        var key = "middle"u8;

        var beforeFirst = source.LatestBefore(0, key);
        var beforeFirstRange = source.LatestBefore(1, key);
        var beforeSecondPut = source.LatestBefore(2, key);
        var beforeSecondRange = source.LatestBefore(3, key);
        var beforeResidentPut = source.LatestBefore(4, key);
        var afterResidentPut = source.LatestBefore(5, key);

        Assert.Null(beforeFirst);
        Assert.Equal(0UL, beforeFirstRange!.Ordinal);
        Assert.False(beforeFirstRange.IsDeleted);
        Assert.Equal(1UL, beforeSecondPut!.Ordinal);
        Assert.True(beforeSecondPut.IsDeleted);
        Assert.Equal(2UL, beforeSecondRange!.Ordinal);
        Assert.False(beforeSecondRange.IsDeleted);
        Assert.Equal(3UL, beforeResidentPut!.Ordinal);
        Assert.True(beforeResidentPut.IsDeleted);
        Assert.Equal(4UL, afterResidentPut!.Ordinal);
        Assert.False(afterResidentPut.IsDeleted);
    }

    [Fact]
    public void ShouldResolveEarlierOrdinalGivenRepeatedKeySpansSparseIndexStrides()
    {
        using var directory = new TemporaryDirectory();
        using var store = new TransactionSpillStore(directory.Path, 1, Family);
        store.WriteRun(Enumerable.Range(0, 33)
            .Select(index => Put(checked((ulong)index), "same-key"))
            .ToArray());
        var source = new TransactionOperationSource(
            store,
            [],
            33,
            DateTimeOffset.UnixEpoch);

        var latest = source.LatestBefore(5, "same-key"u8);

        Assert.NotNull(latest);
        Assert.Equal(4UL, latest.Ordinal);
        Assert.False(latest.IsDeleted);
    }

    static TransactionIntentOperation Put(ulong ordinal, string key, TimeSpan? timeToLive = null) =>
        new(
            ordinal,
            CommitOperationKind.Put,
            Family,
            TestBytes.FromString(key),
            null,
            "value"u8.ToArray(),
            timeToLive,
            null,
            false);

    static TransactionIntentOperation DeleteRange(ulong ordinal, string start, string end) =>
        new(
            ordinal,
            CommitOperationKind.DeleteRange,
            Family,
            TestBytes.FromString(start),
            TestBytes.FromString(end),
            null,
            null,
            null,
            false);

    static void RewriteFrameChecksum(byte[] bytes, int frameOffset)
    {
        var payloadLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(frameOffset)));
        var payload = bytes.AsSpan(frameOffset + 8, payloadLength);
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(frameOffset + 4),
            DiskFormat.Crc32C(payload));
    }

    static int GetRangeNodeOffset(byte[] bytes, int nodeIndex) =>
        checked((int)BinaryPrimitives.ReadUInt64LittleEndian(
            bytes.AsSpan(32 + checked(nodeIndex * 12))));

    static void RewriteRangeChildren(
        byte[] bytes,
        int nodeIndex,
        ulong left,
        ulong right)
    {
        var frameOffset = GetRangeNodeOffset(bytes, nodeIndex);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(frameOffset + 16), left);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(frameOffset + 24), right);
        RewriteFrameChecksum(bytes, frameOffset);
    }

    static int FindMaximumEndOffset(byte[] bytes, int frameOffset)
    {
        var cursor = frameOffset + 8 + 24;
        cursor += sizeof(uint) + checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(cursor)));
        cursor += sizeof(uint) + checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(cursor)));
        var maximumEndLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(cursor)));
        Assert.Equal(4, maximumEndLength);
        return cursor + sizeof(uint);
    }
}

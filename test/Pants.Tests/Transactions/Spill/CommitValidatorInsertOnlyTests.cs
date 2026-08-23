using System.Runtime.CompilerServices;

namespace Cntryl.Pants.Tests.Transactions.Spill;

public sealed class CommitValidatorInsertOnlyTests
{
    const int DistinctLargeKeyBytes = 32 * 1_024;
    const int DistinctLargeKeyCount = 96;
    static readonly ColumnFamilyIdentity Family = new(0, "default", 0);

    [Fact]
    public void ShouldTraverseSpilledSourceOnceGivenManyInsertOnlyOperations()
    {
        const int operationCount = 128;
        using var directory = new TemporaryDirectory();
        using var store = new TransactionSpillStore(directory.Path, 1, Family);
        store.WriteRun(Enumerable.Range(0, operationCount)
            .Select(index => Put(
                checked((ulong)index),
                Family,
                $"key-{index:000}",
                true))
            .ToArray());
        var inner = new TransactionOperationSource(
            store,
            [],
            checked(operationCount),
            DateTimeOffset.UnixEpoch);
        var source = new CountingTransactionOperationSource(inner);
        var state = CreateState();

        CommitValidator.Validate(state, CreatePayload(state, source));

        Assert.True(source.IsSpilled);
        Assert.Equal(1, source.TraversalCount);
        Assert.Equal(operationCount, source.VisitCount);
        Assert.Equal(operationCount, source.LatestBeforeCount);
    }

    [Fact]
    public void ShouldNotRetainDistinctLargeSpilledKeysWhileValidatingInsertOnlyOperations()
    {
        using var directory = new TemporaryDirectory();
        using var store = new TransactionSpillStore(directory.Path, 1, Family);
        WriteDistinctLargeInsertRun(store);
        var inner = new TransactionOperationSource(
            store,
            [],
            DistinctLargeKeyCount,
            DateTimeOffset.UnixEpoch);
        var source = new RetentionMeasuringTransactionOperationSource(inner);
        var state = CreateState();

        CommitValidator.Validate(state, CreatePayload(state, source));

        Assert.True(source.IsSpilled);
        Assert.InRange(source.RetainedVisitedKeyCount, 0, 4);
    }

    [Fact]
    public void ShouldTraverseSpilledSourceOnceGivenStrictConflictValidation()
    {
        const int operationCount = 32;
        using var directory = new TemporaryDirectory();
        using var store = new TransactionSpillStore(directory.Path, 1, Family);
        store.WriteRun(Enumerable.Range(0, operationCount)
            .Select(index => Put(
                checked((ulong)index),
                Family,
                $"strict-{index:000}"))
            .ToArray());
        var inner = new TransactionOperationSource(
            store,
            [],
            checked(operationCount),
            DateTimeOffset.UnixEpoch);
        var source = new CountingTransactionOperationSource(inner);
        var state = CreateState();

        CommitValidator.Validate(
            state,
            CreatePayload(state, source, PantsConflictPolicy.AbortOnWriteConflict));

        Assert.Equal(1, source.TraversalCount);
        Assert.Equal(operationCount, source.VisitCount);
        Assert.Equal(0, source.LatestBeforeCount);
    }

    [Fact]
    public void ShouldAllowInsertGivenRangeDeleteAfterPriorPut()
    {
        var state = CreateState();
        var source = CreateSource(
            Put(0, Family, "key"),
            DeleteRange(1, Family, "a", "z"),
            Put(2, Family, "key", true));

        CommitValidator.Validate(state, CreatePayload(state, source));
    }

    [Fact]
    public void ShouldAllowInsertGivenRangeDeleteOfExistingValue()
    {
        var state = CreateState();
        state.FamilyData[Family]["key"u8.ToArray()] = new CellState(
            "existing"u8.ToArray(),
            1,
            null);
        var source = CreateSource(
            DeleteRange(0, Family, "a", "z"),
            Put(1, Family, "key", true));

        CommitValidator.Validate(state, CreatePayload(state, source));
    }

    [Fact]
    public void ShouldRejectInsertGivenPutAfterPriorRangeDelete()
    {
        var state = CreateState();
        var source = CreateSource(
            DeleteRange(0, Family, "a", "z"),
            Put(1, Family, "key"),
            Put(2, Family, "key", true));

        var error = Assert.Throws<PantsInvalidArgumentException>(() =>
            CommitValidator.Validate(state, CreatePayload(state, source)));

        Assert.Equal(PantsErrorCode.InvalidArgument, error.Code);
        Assert.Equal("Insert requires an absent key.", error.Message);
    }

    [Fact]
    public void ShouldAllowInsertGivenPointDeleteAfterPriorPut()
    {
        var state = CreateState();
        var source = CreateSource(
            Put(0, Family, "key"),
            Delete(1, Family, "key"),
            Put(2, Family, "key", true));

        CommitValidator.Validate(state, CreatePayload(state, source));
    }

    [Fact]
    public void ShouldRejectNonInsertOperationGivenStaleFamily()
    {
        var state = CreateState();
        var staleFamily = Family with { Generation = 1 };
        var source = CreateSource(Put(0, staleFamily, "key"));

        var error = Assert.Throws<PantsInvalidArgumentException>(() =>
            CommitValidator.Validate(state, CreatePayload(state, source)));

        Assert.Equal(PantsErrorCode.InvalidArgument, error.Code);
    }

    static RuntimeState CreateState() =>
        new(new ManualClock(DateTimeOffset.UnixEpoch), new RuntimeTelemetry());

    static CommitPayload CreatePayload(
        RuntimeState state,
        ITransactionOperationSource operations,
        PantsConflictPolicy conflictPolicy = PantsConflictPolicy.LastWriteWins) =>
        new(
            1,
            PantsTransactionMode.ReadWrite,
            conflictPolicy,
            DateTimeOffset.UnixEpoch,
            state.CreateSnapshot(),
            operations,
            []);

    static TransactionOperationSource CreateSource(params TransactionIntentOperation[] operations) =>
        new(
            null,
            operations,
            checked((ulong)operations.Length),
            DateTimeOffset.UnixEpoch);

    static TransactionIntentOperation Put(
        ulong ordinal,
        ColumnFamilyIdentity family,
        string key,
        bool insertOnly = false) =>
        new(
            ordinal,
            CommitOperationKind.Put,
            family,
            TestBytes.FromString(key),
            null,
            "value"u8.ToArray(),
            null,
            null,
            insertOnly);

    static TransactionIntentOperation Delete(
        ulong ordinal,
        ColumnFamilyIdentity family,
        string key) =>
        new(
            ordinal,
            CommitOperationKind.Delete,
            family,
            TestBytes.FromString(key),
            null,
            null,
            null,
            null,
            false);

    static TransactionIntentOperation DeleteRange(
        ulong ordinal,
        ColumnFamilyIdentity family,
        string start,
        string end) =>
        new(
            ordinal,
            CommitOperationKind.DeleteRange,
            family,
            TestBytes.FromString(start),
            TestBytes.FromString(end),
            null,
            null,
            null,
            false);

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void WriteDistinctLargeInsertRun(TransactionSpillStore store)
    {
        var operations = Enumerable.Range(0, DistinctLargeKeyCount)
            .Select(index =>
            {
                var key = new byte[DistinctLargeKeyBytes];
                TestBytes.FromString($"key-{index:000}").CopyTo(key, 0);
                return new TransactionIntentOperation(
                    checked((ulong)index),
                    CommitOperationKind.Put,
                    Family,
                    key,
                    null,
                    "value"u8.ToArray(),
                    null,
                    null,
                    true);
            })
            .ToArray();
        store.WriteRun(operations);
    }
}

namespace Cntryl.Pants.Runtime.Internal.Services.Wal;

sealed class WalRuntimeService(
    int capacity,
    ILocalWalStore? walStore,
    IFailpointHandler failpoints,
    WalMetricsRecorder metrics,
    Action? validateCloudWriteAuthority = null,
    Func<CancellationToken, ValueTask>? validateCloudWalAppendAuthorityAsync = null)
    : ChannelRuntimeService<WalRuntimeRequest, WalRuntimeResult>(capacity)
{
    readonly WalMetricsRecorder _metrics = metrics;
    readonly Func<CancellationToken, ValueTask>? _validateCloudWalAppendAuthorityAsync =
        validateCloudWalAppendAuthorityAsync;

    public async ValueTask<WalCommitResult> AppendCommitAsync(
        CommitPayload payload,
        RuntimeState state,
        PantsDurability durability,
        OperationDeadline deadline = default,
        CancellationToken cancellationToken = default)
    {
        var result = await ExecuteAsync(
                new AppendWalCommitRuntimeRequest(payload, state, durability, deadline),
                cancellationToken)
            .ConfigureAwait(false);
        return result.Commit ??
               throw new PantsInternalException("A WAL write returned no commit result.");
    }

    public async ValueTask<TimeSpan> FlushDurabilityBoundaryAsync(
        Failpoint? beforeBoundary = null,
        CancellationToken cancellationToken = default)
    {
        var result = await ExecuteAsync(
                new FlushWalDurabilityRuntimeRequest(beforeBoundary),
                cancellationToken)
            .ConfigureAwait(false);
        return result.FsyncElapsed ??
               throw new PantsInternalException("A WAL fsync returned no elapsed duration.");
    }

    public async ValueTask<WalCommitGroupResult> AppendCommitGroupAsync(
        IReadOnlyList<WalCommitGroupEntry> commits,
        RuntimeState state,
        PantsDurability durability,
        Failpoint beforeSync,
        OperationDeadline deadline = default,
        CancellationToken cancellationToken = default)
    {
        var result = await ExecuteAsync(
                new AppendWalCommitGroupRuntimeRequest(
                    commits,
                    state,
                    durability,
                    beforeSync,
                    deadline),
                cancellationToken)
            .ConfigureAwait(false);
        return result.CommitGroup ??
               throw new PantsInternalException("A coalesced WAL write returned no group result.");
    }

    public async ValueTask<SealedWalSegment?> SealCloudWalAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await ExecuteAsync(
                new SealCloudWalRuntimeRequest(),
                cancellationToken)
            .ConfigureAwait(false);
        return result.SealedSegment;
    }

    public async ValueTask CompleteCloudWalSealAsync(
        SealedWalSegment segment,
        CancellationToken cancellationToken = default) =>
        _ = await ExecuteAsync(
                new CompleteCloudWalSealRuntimeRequest(segment),
                cancellationToken)
            .ConfigureAwait(false);

    public async ValueTask<ulong?> RotateLocalWalAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await ExecuteAsync(
                new RotateLocalWalRuntimeRequest(),
                cancellationToken)
            .ConfigureAwait(false);
        return result.LocalMaximumSequence;
    }

    public async ValueTask DeleteCloudDurableSegmentAsync(
        SealedWalSegment segment,
        CancellationToken cancellationToken = default) =>
        _ = await ExecuteAsync(
                new DeleteCloudDurableWalRuntimeRequest(segment),
                cancellationToken)
            .ConfigureAwait(false);

    public async ValueTask<SealedWalBacklogBatch> GetCloudBacklogAsync(
        ulong? throughSegmentId,
        CancellationToken cancellationToken = default)
    {
        var result = await ExecuteAsync(
                new GetCloudWalBacklogRuntimeRequest(throughSegmentId),
                cancellationToken)
            .ConfigureAwait(false);
        return result.CloudBacklog ?? new SealedWalBacklogBatch([], throughSegmentId);
    }

    protected override ValueTask<WalRuntimeResult> DispatchAsync(
        WalRuntimeRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var store = walStore ??
                    throw new PantsInternalException("A WAL runtime request requires local storage.");
        return request switch
        {
            AppendWalCommitRuntimeRequest append => AppendCommitWithAuthorityCheckAsync(
                store,
                append,
                append.Deadline,
                cancellationToken),
            AppendWalCommitGroupRuntimeRequest group => AppendCommitGroupWithAuthorityCheckAsync(
                store,
                group,
                group.Deadline,
                cancellationToken),
            FlushWalDurabilityRuntimeRequest flush => FlushDurabilityBoundary(store, flush),
            SealCloudWalRuntimeRequest => ValueTask.FromResult(
                new WalRuntimeResult(store.SealActiveWalForCloud(
                    _metrics,
                    validateCloudWriteAuthority))),
            CompleteCloudWalSealRuntimeRequest complete => CompleteCloudWalSeal(store, complete),
            RotateLocalWalRuntimeRequest => ValueTask.FromResult(
                new WalRuntimeResult(LocalMaximumSequence: store.RotateActiveLocalWal(_metrics))),
            DeleteCloudDurableWalRuntimeRequest delete => DeleteCloudDurableSegment(store, delete),
            GetCloudWalBacklogRuntimeRequest backlog => ValueTask.FromResult(
                new WalRuntimeResult(CloudBacklog:
                    store.GetSealedWalSegmentsForCloudPublication(backlog.ThroughSegmentId))),
            _ => throw new PantsInternalException(
                $"Unknown WAL runtime request '{request.GetType().Name}'.")
        };
    }

    ValueTask<WalRuntimeResult> AppendCommitWithAuthorityCheckAsync(
        ILocalWalStore store,
        AppendWalCommitRuntimeRequest request,
        OperationDeadline deadline,
        CancellationToken cancellationToken) =>
        _validateCloudWalAppendAuthorityAsync is null
            ? AppendCommit(store, request)
            : AppendCommitAfterAuthorityCheckAsync(store, request, deadline, cancellationToken);

    async ValueTask<WalRuntimeResult> AppendCommitAfterAuthorityCheckAsync(
        ILocalWalStore store,
        AppendWalCommitRuntimeRequest request,
        OperationDeadline deadline,
        CancellationToken cancellationToken)
    {
        await deadline.RunAsync(
                _validateCloudWalAppendAuthorityAsync!,
                cancellationToken)
            .ConfigureAwait(false);
        return await AppendCommit(store, request).ConfigureAwait(false);
    }

    ValueTask<WalRuntimeResult> AppendCommitGroupWithAuthorityCheckAsync(
        ILocalWalStore store,
        AppendWalCommitGroupRuntimeRequest request,
        OperationDeadline deadline,
        CancellationToken cancellationToken) =>
        _validateCloudWalAppendAuthorityAsync is null
            ? AppendCommitGroup(store, request)
            : AppendCommitGroupAfterAuthorityCheckAsync(store, request, deadline, cancellationToken);

    async ValueTask<WalRuntimeResult> AppendCommitGroupAfterAuthorityCheckAsync(
        ILocalWalStore store,
        AppendWalCommitGroupRuntimeRequest request,
        OperationDeadline deadline,
        CancellationToken cancellationToken)
    {
        await deadline.RunAsync(
                _validateCloudWalAppendAuthorityAsync!,
                cancellationToken)
            .ConfigureAwait(false);
        return await AppendCommitGroup(store, request).ConfigureAwait(false);
    }

    ValueTask<WalRuntimeResult> AppendCommit(
        ILocalWalStore store,
        AppendWalCommitRuntimeRequest request)
    {
        var result = store.AppendCommit(
            request.Payload,
            request.State,
            request.Durability,
            _metrics);
        return ValueTask.FromResult(new WalRuntimeResult(Commit: result));
    }

    ValueTask<WalRuntimeResult> AppendCommitGroup(
        ILocalWalStore store,
        AppendWalCommitGroupRuntimeRequest request)
    {
        var result = store.AppendCommitGroup(
            request.Commits,
            request.State,
            request.Durability,
            () => failpoints.Hit(request.BeforeSync),
            _metrics);
        return ValueTask.FromResult(new WalRuntimeResult(CommitGroup: result));
    }

    ValueTask<WalRuntimeResult> FlushDurabilityBoundary(
        ILocalWalStore store,
        FlushWalDurabilityRuntimeRequest request)
    {
        if (request.BeforeBoundary.HasValue)
        {
            failpoints.Hit(request.BeforeBoundary.Value);
        }

        var elapsed = store.FlushDurabilityBoundary(_metrics);
        return ValueTask.FromResult(new WalRuntimeResult(FsyncElapsed: elapsed));
    }

    static ValueTask<WalRuntimeResult> DeleteCloudDurableSegment(
        ILocalWalStore store,
        DeleteCloudDurableWalRuntimeRequest request)
    {
        store.DeleteCloudDurableWalSegment(request.Segment);
        return ValueTask.FromResult(default(WalRuntimeResult));
    }

    static ValueTask<WalRuntimeResult> CompleteCloudWalSeal(
        ILocalWalStore store,
        CompleteCloudWalSealRuntimeRequest request)
    {
        store.CompleteCloudWalSeal(request.Segment);
        return ValueTask.FromResult(default(WalRuntimeResult));
    }
}

namespace Cntryl.Pants;

sealed class WalRuntimeService(
    int capacity,
    LocalDiskStore? diskStore,
    IPantsFailpointHandler failpoints,
    WalMetricsRecorder metrics,
    Action? validateCloudWriteAuthority = null)
    : ChannelRuntimeService<WalRuntimeRequest, WalRuntimeResult>(capacity)
{
    readonly WalMetricsRecorder _metrics = metrics;

    public async ValueTask<WalCommitResult> AppendCommitAsync(
        CommitPayload payload,
        PantsRuntimeState state,
        PantsDurability durability,
        CancellationToken cancellationToken = default)
    {
        var result = await ExecuteAsync(
                new AppendWalCommitRuntimeRequest(payload, state, durability),
                cancellationToken)
            .ConfigureAwait(false);
        return result.Commit ??
            throw new PantsInternalException("A WAL write returned no commit result.");
    }

    public async ValueTask<TimeSpan> FlushDurabilityBoundaryAsync(
        PantsFailpoint? beforeBoundary = null,
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
        PantsRuntimeState state,
        PantsDurability durability,
        PantsFailpoint beforeSync,
        CancellationToken cancellationToken = default)
    {
        var result = await ExecuteAsync(
                new AppendWalCommitGroupRuntimeRequest(
                    commits,
                    state,
                    durability,
                    beforeSync),
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

    public async ValueTask<IReadOnlyList<SealedWalSegment>> GetCloudBacklogAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await ExecuteAsync(
                new GetCloudWalBacklogRuntimeRequest(),
                cancellationToken)
            .ConfigureAwait(false);
        return result.CloudBacklog ?? [];
    }

    protected override ValueTask<WalRuntimeResult> DispatchAsync(
        WalRuntimeRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var store = diskStore ??
            throw new PantsInternalException("A WAL runtime request requires local storage.");
        return request switch
        {
            AppendWalCommitRuntimeRequest append => AppendCommit(store, append),
            AppendWalCommitGroupRuntimeRequest group => AppendCommitGroup(store, group),
            FlushWalDurabilityRuntimeRequest flush => FlushDurabilityBoundary(store, flush),
            SealCloudWalRuntimeRequest => ValueTask.FromResult(
                new WalRuntimeResult(store.SealActiveWalForCloud(
                    _metrics,
                    validateCloudWriteAuthority))),
            CompleteCloudWalSealRuntimeRequest complete => CompleteCloudWalSeal(store, complete),
            RotateLocalWalRuntimeRequest => ValueTask.FromResult(
                new WalRuntimeResult(LocalMaximumSequence: store.RotateActiveLocalWal(_metrics))),
            DeleteCloudDurableWalRuntimeRequest delete => DeleteCloudDurableSegment(store, delete),
            GetCloudWalBacklogRuntimeRequest => ValueTask.FromResult(
                new WalRuntimeResult(CloudBacklog:
                    store.GetSealedWalSegmentsForCloudPublication())),
            _ => throw new PantsInternalException(
                $"Unknown WAL runtime request '{request.GetType().Name}'.")
        };
    }

    ValueTask<WalRuntimeResult> AppendCommit(
        LocalDiskStore store,
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
        LocalDiskStore store,
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
        LocalDiskStore store,
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
        LocalDiskStore store,
        DeleteCloudDurableWalRuntimeRequest request)
    {
        store.DeleteCloudDurableWalSegment(request.Segment);
        return ValueTask.FromResult(default(WalRuntimeResult));
    }

    static ValueTask<WalRuntimeResult> CompleteCloudWalSeal(
        LocalDiskStore store,
        CompleteCloudWalSealRuntimeRequest request)
    {
        store.CompleteCloudWalSeal(request.Segment);
        return ValueTask.FromResult(default(WalRuntimeResult));
    }
}

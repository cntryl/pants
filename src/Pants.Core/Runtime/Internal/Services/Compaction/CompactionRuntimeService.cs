namespace Cntryl.Pants.Runtime.Internal.Services.Compaction;

sealed class CompactionRuntimeService(
    int capacity,
    ILocalCompactionStore? compactionStore,
    RuntimeTelemetry telemetry,
    CompactionMemory memory)
    : ChannelRuntimeService<CompactionRuntimeRequest, CompactionResult>(capacity)
{
    readonly object _operationGate = new();
    int _compactingSsts;
    int _scheduledOperations;
    CancellationTokenSource? _activeCancellation;
    TaskCompletionSource _scheduledOperationsIdle = CompletedSource();
    long _generation;

    public ResourceBudget BufferBudget => memory.Budget;

    public int CompactingSsts => Volatile.Read(ref _compactingSsts);

    public bool HasScheduledOperations => Volatile.Read(ref _scheduledOperations) != 0;

    public async ValueTask WaitForIdleAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task idle;
            lock (_operationGate)
            {
                if (_scheduledOperations == 0)
                {
                    return;
                }

                idle = _scheduledOperationsIdle.Task;
            }

            await idle.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public ValueTask<Task<CompactionResult>> ScheduleCompactAsync(
        CompactionWorkSnapshot snapshot,
        CompactionWorkSnapshotProvider snapshotProvider,
        CompactionPublicationDispatcher publicationDispatcher,
        bool force,
        CloudCompactionOutputPublisher? outputPublisher,
        Func<IReadOnlyList<string>, CancellationToken, ValueTask<IDisposable?>>? prepareInputs = null,
        CompactionOutputStaging? staging = null,
        CancellationToken cancellationToken = default)
    {
        var request = CreateRequest(
            snapshot,
            snapshotProvider,
            publicationDispatcher,
            force,
            outputPublisher,
            prepareInputs,
            staging,
            false,
            cancellationToken);
        return ScheduleTrackedAsync(request, cancellationToken);
    }

    public ValueTask<Task<CompactionResult>> ScheduleDrainDebtAsync(
        CompactionWorkSnapshot snapshot,
        CompactionWorkSnapshotProvider snapshotProvider,
        CompactionPublicationDispatcher publicationDispatcher,
        CloudCompactionOutputPublisher? outputPublisher,
        Func<IReadOnlyList<string>, CancellationToken, ValueTask<IDisposable?>>? prepareInputs = null,
        CompactionOutputStaging? staging = null,
        CancellationToken cancellationToken = default)
    {
        var request = CreateRequest(
            snapshot,
            snapshotProvider,
            publicationDispatcher,
            true,
            outputPublisher,
            prepareInputs,
            staging,
            true,
            cancellationToken);
        return ScheduleTrackedAsync(request, cancellationToken);
    }

    public void CancelOperations()
    {
        lock (_operationGate)
        {
            _generation = checked(_generation + 1);
            _activeCancellation?.Cancel();
        }
    }

    protected override async ValueTask<CompactionResult> DispatchAsync(
        CompactionRuntimeRequest request,
        CancellationToken cancellationToken)
    {
        var store = compactionStore ??
                    throw new PantsInternalException("A compaction runtime request requires local storage.");
        CancellationTokenSource operationCancellation;
        lock (_operationGate)
        {
            if (request.Generation != _generation)
            {
                throw new OperationCanceledException(new CancellationToken(true));
            }

            operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                request.RequestCancellationToken);
            _activeCancellation = operationCancellation;
        }

        try
        {
            var snapshot = await request.SnapshotProvider(operationCancellation.Token)
                    .ConfigureAwait(false) ??
                throw new OperationCanceledException(operationCancellation.Token);
            operationCancellation.Token.ThrowIfCancellationRequested();
            Volatile.Write(
                ref _compactingSsts,
                snapshot.Plans.Sum(static plan => plan.Inputs.Count));
            if (request.DrainDebt)
            {
                return await store.DrainCompactionDebtAsync(
                        snapshot,
                        request.SnapshotProvider,
                        request.PublicationDispatcher,
                        request.OutputPublisher,
                        telemetry.RecordCompaction,
                        memory,
                        request.PrepareInputs,
                        request.Staging,
                        operationCancellation.Token)
                    .ConfigureAwait(false);
            }

            return await store.CompactAsync(
                    snapshot,
                    request.SnapshotProvider,
                    request.PublicationDispatcher,
                    request.Force,
                    request.OutputPublisher,
                    telemetry.RecordCompaction,
                    memory,
                    request.PrepareInputs,
                    request.Staging,
                    operationCancellation.Token)
                .ConfigureAwait(false);
        }
        catch
        {
            telemetry.RecordCompactionFailure();
            throw;
        }
        finally
        {
            Volatile.Write(ref _compactingSsts, 0);
            lock (_operationGate)
            {
                if (ReferenceEquals(_activeCancellation, operationCancellation))
                {
                    _activeCancellation = null;
                }
            }

            operationCancellation.Dispose();
        }
    }

    async ValueTask<Task<CompactionResult>> ScheduleTrackedAsync(
        CompactionRuntimeRequest request,
        CancellationToken cancellationToken)
    {
        lock (_operationGate)
        {
            if (_scheduledOperations == 0)
            {
                _scheduledOperationsIdle = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }

            _scheduledOperations = checked(_scheduledOperations + 1);
        }

        try
        {
            var completion = await ScheduleAsync(request, cancellationToken).ConfigureAwait(false);
            _ = CompleteScheduledOperationAsync(completion);
            return completion;
        }
        catch
        {
            CompleteScheduledOperation();
            throw;
        }
    }

    async Task CompleteScheduledOperationAsync(Task<CompactionResult> completion)
    {
        try
        {
            await completion.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
        finally
        {
            CompleteScheduledOperation();
        }
    }

    void CompleteScheduledOperation()
    {
        lock (_operationGate)
        {
            _scheduledOperations--;
            if (_scheduledOperations == 0)
            {
                _scheduledOperationsIdle.TrySetResult();
            }
        }
    }

    static TaskCompletionSource CompletedSource()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        completed.SetResult();
        return completed;
    }

    CompactionRuntimeRequest CreateRequest(
        CompactionWorkSnapshot snapshot,
        CompactionWorkSnapshotProvider snapshotProvider,
        CompactionPublicationDispatcher publicationDispatcher,
        bool force,
        CloudCompactionOutputPublisher? outputPublisher,
        Func<IReadOnlyList<string>, CancellationToken, ValueTask<IDisposable?>>? prepareInputs,
        CompactionOutputStaging? staging,
        bool drainDebt,
        CancellationToken cancellationToken)
    {
        lock (_operationGate)
        {
            return new CompactionRuntimeRequest(
                snapshot,
                snapshotProvider,
                publicationDispatcher,
                force,
                outputPublisher,
                prepareInputs,
                staging,
                drainDebt,
                _generation,
                cancellationToken);
        }
    }
}

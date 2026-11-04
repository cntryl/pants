namespace Cntryl.Pants.Cloud.Internal;

/// <summary>Caller routes settle once; accepted publication has a separate physical owner.</summary>
sealed class CloudDurabilityWaitState(
    TimeProvider timeProvider,
    TimeSpan confirmationBudget,
    Func<ulong> frontier,
    Action ensureAuthority,
    Func<ulong, OperationDeadline, Action<Exception?>, CancellationToken, ValueTask> request)
{
    readonly Lock _gate = new();
    readonly HashSet<Observer> _observers = [];
    Exception? _terminalFailure;

    public CloudDurabilityWaitState(
        TimeProvider timeProvider,
        TimeSpan confirmationBudget,
        Func<ulong> frontier,
        Action ensureAuthority,
        Func<ulong, OperationDeadline, CancellationToken, ValueTask> request)
        : this(timeProvider, confirmationBudget, frontier, ensureAuthority,
            async (sequence, deadline, admitted, cancellationToken) =>
            {
                try
                {
                    await request(sequence, deadline, cancellationToken).ConfigureAwait(false);
                    admitted(null);
                }
                catch (Exception failure)
                {
                    admitted(failure);
                    throw;
                }
            })
    {
    }

    public void Notify(Exception? failure = null)
    {
        lock (_gate)
        {
            if (_terminalFailure is not null)
            {
                return;
            }
        }
        // Reading proof must not hold the route lock: shutdown or abandonment can
        // claim an observer while a concurrent proof is still being obtained.
        var confirmed = failure is null ? frontier() : 0;
        lock (_gate)
        {
            foreach (var observer in _observers.ToArray())
            {
                if (!observer.Admitted)
                {
                    continue;
                }
                if (failure is not null)
                {
                    CompleteUnderGate(observer, FailureOutcome(observer, failure));
                }
                else if (confirmed >= observer.Sequence)
                {
                    CompleteUnderGate(observer, new Completion(CompletionKind.Covered));
                }
            }
        }
    }

    public void StopForShutdown()
    {
        lock (_gate)
        {
            if (_terminalFailure is not null)
            {
                return;
            }
            _terminalFailure = new PantsBusyException("The runtime is shutting down.");
            foreach (var observer in _observers.ToArray())
            {
                CompleteUnderGate(observer, new Completion(CompletionKind.Failure, _terminalFailure));
            }
        }
    }

    public async ValueTask<bool> WaitAsync(ulong sequence, TimeSpan timeout, CancellationToken caller)
    {
        caller.ThrowIfCancellationRequested();
        if (timeout < TimeSpan.Zero)
        {
            throw PantsException.InvalidArgument("A cloud durability wait must not have a negative timeout.");
        }
        var budget = timeout < confirmationBudget ? timeout : confirmationBudget;
        var deadline = OperationDeadline.FromBudget(budget, timeProvider);
        var observer = new Observer(sequence, caller);
        lock (_gate)
        {
            if (_terminalFailure is { } stopped)
            {
                observer.Completion.SetResult(new Completion(CompletionKind.Failure, stopped));
            }
            else
            {
                _observers.Add(observer);
            }
        }
        try
        {
            using var callerRegistration = caller.Register(() =>
                Complete(observer, new Completion(CompletionKind.Canceled, Caller: caller)));
            // A zero budget remains an authority-checked observation of an already covered
            // target. Positive budgets also count time spent validating initial authority.
            using var deadlineCancellation = budget == TimeSpan.Zero
                ? null
                : new OperationDeadlineCancellation(deadline, timeProvider, CancellationToken.None);
            using var deadlineRegistration = deadlineCancellation?.Token.Register(() =>
                Complete(observer, new Completion(CompletionKind.NotCovered))) ?? default;
            CheckAuthority(observer);
            if (!observer.Completion.Task.IsCompleted)
            {
                Confirm(observer, frontier());
            }
            if (deadline.IsExpired)
            {
                Complete(observer, new Completion(CompletionKind.NotCovered));
            }
            if (observer.Completion.Task.IsCompleted)
            {
                return ReadCompletion(await observer.Completion.Task.ConfigureAwait(false));
            }

            // Cancellation and expiry abandon only this route. Their original token and
            // deadline also bound submission, without canceling accepted physical work.
            using var submissionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                caller, deadlineCancellation?.Token ?? CancellationToken.None);
            if (!observer.Completion.Task.IsCompleted)
            {
                _ = RunRequestAsync(observer, deadline, submissionCancellation.Token);
            }
            while (!observer.Completion.Task.IsCompleted)
            {
                try
                {
                    _ = await observer.Completion.Task.WaitAsync(TimeSpan.FromMilliseconds(100), timeProvider, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    CheckAuthority(observer);
                }
            }
            // The owner already claimed this typed outcome. Later shutdown, cancellation,
            // expiry or lease loss cannot replace an acknowledgement routed beforehand.
            return ReadCompletion(await observer.Completion.Task.ConfigureAwait(false));
        }
        finally
        {
            lock (_gate)
            {
                _observers.Remove(observer);
            }
        }
    }

    async Task RunRequestAsync(Observer observer, OperationDeadline deadline, CancellationToken cancellationToken)
    {
        try
        {
            await request(observer.Sequence, deadline, failure => Admit(observer, failure), cancellationToken)
                .ConfigureAwait(false);
            Admit(observer, null);
        }
        catch (Exception failure)
        {
            Complete(observer, FailureOutcome(observer, failure));
        }
    }

    void Admit(Observer observer, Exception? failure)
    {
        if (failure is not null)
        {
            Complete(observer, FailureOutcome(observer, failure));
            return;
        }
        lock (_gate)
        {
            if (!_observers.Contains(observer) || observer.Admitted)
            {
                return;
            }
            observer.Admitted = true;
        }
        Confirm(observer, frontier());
    }

    void CheckAuthority(Observer observer)
    {
        if (observer.Completion.Task.IsCompleted)
        {
            return;
        }
        try
        {
            ensureAuthority();
        }
        catch (Exception failure)
        {
            Complete(observer, FailureOutcome(observer, failure));
        }
    }

    void Confirm(Observer observer, ulong confirmed)
    {
        if (confirmed >= observer.Sequence)
        {
            Complete(observer, new Completion(CompletionKind.Covered));
        }
    }

    void Complete(Observer observer, Completion completion)
    {
        lock (_gate)
        {
            CompleteUnderGate(observer, completion);
        }
    }

    void CompleteUnderGate(Observer observer, Completion completion)
    {
        if (_observers.Remove(observer))
        {
            observer.Completion.SetResult(completion);
        }
    }

    static Completion FailureOutcome(Observer observer, Exception failure) => failure is OperationCanceledException
        ? observer.Caller.IsCancellationRequested
            ? new Completion(CompletionKind.Canceled, Caller: observer.Caller)
            : new Completion(CompletionKind.NotCovered)
        : new Completion(CompletionKind.Failure, failure);

    static bool ReadCompletion(Completion completion) => completion.Kind switch
    {
        CompletionKind.Covered => true,
        CompletionKind.NotCovered => false,
        CompletionKind.Canceled => throw new OperationCanceledException(completion.Caller),
        _ => HandleFailure(completion.Failure!)
    };

    static bool HandleFailure(Exception failure)
    {
        var mapped = RuntimeExceptionMapper.ToPublicException(failure);
        if (mapped is PantsException { Severity: PantsErrorSeverity.Transient or PantsErrorSeverity.Backpressure })
        {
            return false;
        }
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(mapped).Throw();
        return false;
    }

    sealed class Observer(ulong sequence, CancellationToken caller)
    {
        public ulong Sequence { get; } = sequence;
        public CancellationToken Caller { get; } = caller;
        public bool Admitted { get; set; }
        public TaskCompletionSource<Completion> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    enum CompletionKind
    {
        Covered,
        NotCovered,
        Canceled,
        Failure
    }

    readonly record struct Completion(CompletionKind Kind, Exception? Failure = null, CancellationToken Caller = default);
}

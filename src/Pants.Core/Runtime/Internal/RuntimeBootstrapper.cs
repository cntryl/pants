namespace Cntryl.Pants.Runtime.Internal;

static class RuntimeBootstrapper
{
    public static async ValueTask<RuntimeComposition> OpenAsync(
        RuntimePlan plan,
        IPantsClock ttlClock,
        RuntimeTelemetry telemetry,
        RuntimeDependencies dependencies,
        CancellationToken cancellationToken)
    {
        if (plan.OpenTimeout is not null || cancellationToken.CanBeCanceled)
        {
            return await OpenOwnedAsync(plan, ttlClock, telemetry, dependencies, cancellationToken)
                .ConfigureAwait(false);
        }

        var coordinator = await Actor.OpenAsync(
                plan,
                ttlClock,
                telemetry,
                dependencies,
                cancellationToken)
            .ConfigureAwait(false);
        return new RuntimeComposition(coordinator);
    }

    static async ValueTask<RuntimeComposition> OpenOwnedAsync(
        RuntimePlan plan,
        IPantsClock ttlClock,
        RuntimeTelemetry telemetry,
        RuntimeDependencies dependencies,
        CancellationToken cancellationToken)
    {
        var scope = new StartupScope(plan.OpenTimeout, dependencies.RuntimeTimeProvider, cancellationToken);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Actor? preparedActor = null;
        Exception? preparationFailure = null;
        _ = Task.Run(PrepareAsync, CancellationToken.None);
        try
        {
            _ = await Task.WhenAny(ready.Task, scope.Cancelled).ConfigureAwait(false);
            if (!ready.Task.IsCompleted)
            {
                throw await scope.Cancelled.ConfigureAwait(false);
            }

            if (preparationFailure is { } failure)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            }
            var actor = scope.Accept(() => Volatile.Read(ref preparedActor));
            Interlocked.Exchange(ref preparedActor, null);
            return new RuntimeComposition(actor);
        }
        catch
        {
            scope.Cancel(cancellationToken.IsCancellationRequested);
            throw;
        }

        async Task PrepareAsync()
        {
            Actor? actor = null;
            var admitted = false;
            try
            {
                scope.Check();
                actor = await Actor.OpenAsync(plan, ttlClock, telemetry, dependencies,
                    scope, scope.Token).ConfigureAwait(false);
                scope.Prepared();
                dependencies.ObserveStartup(StartupEvent.Prepared);
                dependencies.Failpoints.Hit(Failpoint.AfterStartupPrepared);
                Volatile.Write(ref preparedActor, actor);
                ready.TrySetResult();
                if (await scope.Decision.ConfigureAwait(false))
                {
                    admitted = true;
                    actor = null;
                    dependencies.ObserveStartup(StartupEvent.Admitted);
                }
            }
            catch (Exception exception)
            {
                preparationFailure = exception;
                ready.TrySetResult();
                _ = await scope.Decision.ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Exchange(ref preparedActor, null);
                if (actor is not null)
                {
                    scope.Cancel(false);
                    dependencies.ObserveStartup(StartupEvent.Aborted);
                    await scope.WaitForCancellationCallbacksAsync().ConfigureAwait(false);
                    try
                    {
                        await actor.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // Disposal retains authority until actual owned work stops.
                    }
                    await actor.OwnedRuntimeResourcesReleased.ConfigureAwait(false);
                }
                await scope.WaitForCancellationCallbacksAsync().ConfigureAwait(false);
                scope.Dispose();
                if (!admitted)
                {
                    dependencies.ObserveStartup(StartupEvent.CleanupFinished);
                }
            }
        }
    }
}

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
        var coordinator = await Actor.OpenAsync(
                plan,
                ttlClock,
                telemetry,
                dependencies,
                cancellationToken)
            .ConfigureAwait(false);
        return new RuntimeComposition(coordinator);
    }
}

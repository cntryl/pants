using Cntryl.Pants.Runtime.Internal;

namespace Cntryl.Pants.Destroyer.Support;

/// <summary>
/// Opens a local Pants database with a custom <see cref="IFailpointHandler"/>
/// wired in, for scenarios that cut Pants at an exact internal boundary
/// rather than at an arbitrary point in wall-clock time.
/// </summary>
static class DestroyerFailpoints
{
    public static ValueTask<IPantsDatabase> OpenWithFailpointAsync(
        string path,
        IFailpointHandler failpoints,
        TimeSpan? leaseHeartbeatInterval = null) =>
        PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Local(path),
            new RuntimeDependencies(failpoints, leaseHeartbeatInterval: leaseHeartbeatInterval));

    /// <summary>
    /// Runs an operation expected to fail because it lands on an armed
    /// failpoint's cut. Whether it actually throws depends on exactly where
    /// the cut falls relative to the caller's own await points, so the
    /// exception (if any) is swallowed here - the recovery check the caller
    /// makes afterward is what actually verifies the scenario's invariant.
    /// </summary>
    public static async Task IgnoreInjectedFailureAsync(Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch
        {
            // Expected: the armed failpoint cut this operation.
        }
    }
}

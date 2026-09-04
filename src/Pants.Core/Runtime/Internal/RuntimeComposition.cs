namespace Cntryl.Pants.Runtime.Internal;

sealed class RuntimeComposition(Actor coordinator) : IAsyncDisposable
{
    public Actor Coordinator { get; } = coordinator;

    public ValueTask DisposeAsync() => Coordinator.DisposeAsync();
}

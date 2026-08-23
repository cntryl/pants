namespace Cntryl.Pants;

sealed class OnlineVerificationBarrier
{
    readonly TaskCompletionSource _released =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public OnlineVerificationBarrier(
        long token,
        string? path,
        PantsEngineHealth runtimeHealth)
    {
        Token = token;
        Path = path;
        RuntimeHealth = runtimeHealth;
    }

    public long Token { get; }

    public string? Path { get; }

    public PantsEngineHealth RuntimeHealth { get; }

    public Task Released => _released.Task;

    public void Release() => _released.TrySetResult();
}

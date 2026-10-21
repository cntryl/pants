namespace Cntryl.Pants.Runtime.Internal.Verification;

sealed class OnlineVerificationBarrier
{
    readonly TaskCompletionSource _released =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public OnlineVerificationBarrier(
        long token,
        string? path,
        long sequence,
        PantsEngineHealth runtimeHealth)
    {
        Token = token;
        Path = path;
        Sequence = sequence;
        RuntimeHealth = runtimeHealth;
    }

    public long Token { get; }

    public string? Path { get; }

    public long Sequence { get; }

    public PantsEngineHealth RuntimeHealth { get; }

    public Task Released => _released.Task;

    public void Release() => _released.TrySetResult();
}

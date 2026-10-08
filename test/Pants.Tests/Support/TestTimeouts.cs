namespace Cntryl.Pants.Support;

static class TestTimeouts
{
    /// <summary>
    /// Upper bound for waits on events a test expects to happen. It only guards against a hung
    /// test and is not a latency assertion, so it stays generous for slow CI runners.
    /// </summary>
    public static readonly TimeSpan Expected = TimeSpan.FromSeconds(30);
}

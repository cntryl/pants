namespace Cntryl.Pants.Support;

static class TestWait
{
    /// <summary>
    ///     Waits for background work to make <paramref name="condition" /> true, bounded by
    ///     <see cref="TestTimeouts.Expected" />.
    /// </summary>
    public static async Task UntilAsync(Func<bool> condition, string description)
    {
        using var timeout = new CancellationTokenSource(TestTimeouts.Expected);
        while (!condition())
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                Assert.Fail($"Timed out waiting until {description}.");
            }
        }
    }

    /// <inheritdoc cref="UntilAsync(Func{bool}, string)" />
    public static async Task UntilAsync(Func<Task<bool>> condition, string description)
    {
        using var timeout = new CancellationTokenSource(TestTimeouts.Expected);
        while (!await condition())
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                Assert.Fail($"Timed out waiting until {description}.");
            }
        }
    }
}

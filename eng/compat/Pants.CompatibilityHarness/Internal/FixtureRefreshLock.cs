namespace Pants.CompatibilityHarness.Internal;

internal sealed class FixtureRefreshLock : IDisposable
{
    readonly FileStream _stream;

    FixtureRefreshLock(FileStream stream)
    {
        _stream = stream;
    }

    public static async Task<FixtureRefreshLock> AcquireAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);

        var gitPath = await ProcessRunner.RunAsync(
            "git",
            [
                "-C",
                repositoryRoot,
                "rev-parse",
                "--path-format=absolute",
                "--git-path",
                "pants-compat-refresh.lock"
            ],
            repositoryRoot,
            cancellationToken).ConfigureAwait(false);
        var path = Path.GetFullPath(gitPath.StandardOutput.Trim(), repositoryRoot);
        try
        {
            var stream = new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
            return new FixtureRefreshLock(stream);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException(
                $"Another compatibility refresh is using repository '{repositoryRoot}'.",
                exception);
        }
    }

    public void Dispose() => _stream.Dispose();
}

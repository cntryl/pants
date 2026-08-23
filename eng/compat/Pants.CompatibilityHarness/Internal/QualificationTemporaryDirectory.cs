namespace Pants.CompatibilityHarness.Internal;

internal sealed class QualificationTemporaryDirectory : IDisposable
{
    bool _disposed;

    public QualificationTemporaryDirectory()
    {
        RootPath = Path.Combine(
            Path.GetTempPath(),
            $"pants-compat-qualification-{Guid.NewGuid():N}");
        _ = Directory.CreateDirectory(RootPath);
    }

    public string RootPath { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            Directory.Delete(RootPath, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(
                $"warning: could not remove qualification directory '{RootPath}': {exception.Message}");
        }
    }
}

namespace Cntryl.Pants.Support.TestDoubles;

sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"pants-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    /// <summary>
    ///     Marks the directory as held open by a database whose shutdown failed permanently, which
    ///     by design keeps its lease and LOCK until the process exits. Cleanup is then best effort.
    /// </summary>
    public void AbandonCleanup() => BestEffortCleanup = true;

    bool BestEffortCleanup { get; set; }

    public void Dispose()
    {
        if (!Directory.Exists(Path))
        {
            return;
        }

        try
        {
            Directory.Delete(Path, true);
        }
        catch (IOException) when (BestEffortCleanup)
        {
            // The held LOCK file cannot be removed on Windows; the OS temp cleanup reclaims it.
        }
    }
}

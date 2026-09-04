namespace Cntryl.Pants.Tests.Compatibility;

static class MidgeCompatibilityFixture
{
    const string PinnedSha = "75dcc39f7a9b87df480ed91c3a5c93fe1389ca71";

    public static TemporaryDirectory CopyToTemporaryDirectory(string fixtureName)
    {
        var sourcePath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "Compatibility",
            "Midge",
            PinnedSha,
            fixtureName);
        if (!Directory.Exists(sourcePath))
        {
            throw new DirectoryNotFoundException($"Missing committed Midge compatibility fixture: {sourcePath}");
        }

        var directory = new TemporaryDirectory();
        try
        {
            foreach (var sourceFilePath in Directory.EnumerateFiles(
                         sourcePath,
                         "*",
                         SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(sourcePath, sourceFilePath);
                var destinationFilePath = Path.Combine(directory.Path, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destinationFilePath)!);
                File.Copy(sourceFilePath, destinationFilePath);
            }

            return directory;
        }
        catch
        {
            directory.Dispose();
            throw;
        }
    }
}

namespace Cntryl.Pants.Tests;

static class MidgeCompatibilityFixture
{
    const string PinnedSha = "c5ffc2d3284c76b6f7cd03444a5b0a38ae8bbc33";

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

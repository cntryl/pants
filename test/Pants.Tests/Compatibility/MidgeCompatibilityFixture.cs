using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Compatibility;

static class MidgeCompatibilityFixture
{
    const string PinnedSha = "7d39f86217fdb07191a83bd885a514dd5ea9723f";

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

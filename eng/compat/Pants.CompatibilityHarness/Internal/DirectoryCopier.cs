namespace Pants.CompatibilityHarness.Internal;

internal static class DirectoryCopier
{
    public static void Copy(string sourcePath, string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var source = new DirectoryInfo(sourcePath);
        if (!source.Exists)
        {
            throw new DirectoryNotFoundException(
                $"Source directory '{source.FullName}' does not exist.");
        }

        _ = Directory.CreateDirectory(destinationPath);
        foreach (var file in source.EnumerateFiles())
        {
            file.CopyTo(Path.Combine(destinationPath, file.Name), overwrite: false);
        }

        foreach (var directory in source.EnumerateDirectories())
        {
            Copy(directory.FullName, Path.Combine(destinationPath, directory.Name));
        }
    }
}

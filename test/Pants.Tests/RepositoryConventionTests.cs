using System.Xml.Linq;

namespace Cntryl.Pants;

public sealed class RepositoryConventionTests
{
    [Fact]
    public void RootNamespaceIsDeclaredOnceForEveryProject()
    {
        var repository = FindRepositoryRoot();
        var declarations = Directory
            .EnumerateFiles(repository, "*.*", SearchOption.AllDirectories)
            .Where(static path => path.EndsWith(".props", StringComparison.Ordinal) ||
                                  path.EndsWith(".csproj", StringComparison.Ordinal))
            .Where(path => !IsBuildOutput(repository, path))
            .SelectMany(static path => XDocument
                .Load(path)
                .Descendants()
                .Where(static element => element.Name.LocalName == "RootNamespace")
                .Select(element => (Path: path, element.Value)))
            .ToArray();

        var declaration = Assert.Single(declarations);
        Assert.Equal(Path.Combine(repository, "Directory.Build.props"), declaration.Path);
        Assert.Equal("Cntryl.Pants", declaration.Value);
    }

    static bool IsBuildOutput(string repository, string path)
    {
        var segments = Path.GetRelativePath(repository, path)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Contains("bin", StringComparer.Ordinal) ||
               segments.Contains("obj", StringComparer.Ordinal);
    }

    static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Pants.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not locate the Pants repository root.");
    }
}

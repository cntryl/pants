using Pants.CompatibilityHarness.Internal;

namespace Pants.CompatibilityHarness.Tests;

internal sealed class CompatibilityTestDirectory : IDisposable
{
    public CompatibilityTestDirectory()
    {
        RootPath = Path.Combine(
            Path.GetTempPath(),
            $"pants-compat-harness-test-{Guid.NewGuid():N}");
        _ = Directory.CreateDirectory(RootPath);
    }

    public string RootPath { get; }

    public PantsRepositoryPaths CreateRepository(string fixtureValue, string manifestValue)
    {
        var testProject = Path.Combine(RootPath, "test", "Pants.Tests");
        var fixtures = Path.Combine(testProject, "Fixtures", "Compatibility");
        var manifest = Path.Combine(testProject, "MidgeContractManifest.json");
        _ = Directory.CreateDirectory(fixtures);
        File.WriteAllText(Path.Combine(fixtures, "fixture.txt"), fixtureValue);
        File.WriteAllText(manifest, manifestValue);
        return new PantsRepositoryPaths(RootPath, testProject, fixtures, manifest);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(RootPath, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(
                $"warning: could not remove compatibility harness test directory "
                + $"'{RootPath}': {exception.Message}");
        }
    }
}

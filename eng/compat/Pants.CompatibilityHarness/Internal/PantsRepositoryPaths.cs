namespace Cntryl.Pants.CompatibilityHarness.Internal;

internal sealed record PantsRepositoryPaths(
    string Root,
    string TestProject,
    string CompatibilityFixtures,
    string ContractManifest)
{
    public static PantsRepositoryPaths Find()
    {
        foreach (var startingPath in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(Path.GetFullPath(startingPath));
                 directory is not null;
                 directory = directory.Parent)
            {
                var solutionPath = Path.Combine(directory.FullName, "Pants.slnx");
                var testProject = Path.Combine(directory.FullName, "test", "Pants.Tests");
                var contractManifest = Path.Combine(testProject, "MidgeContractManifest.json");
                if (File.Exists(solutionPath) && File.Exists(contractManifest))
                {
                    return new PantsRepositoryPaths(
                        directory.FullName,
                        testProject,
                        Path.Combine(testProject, "Fixtures", "Compatibility"),
                        contractManifest);
                }
            }
        }

        throw new DirectoryNotFoundException(
            "Could not locate the Pants repository root from the current directory "
            + "or compatibility harness assembly path.");
    }
}

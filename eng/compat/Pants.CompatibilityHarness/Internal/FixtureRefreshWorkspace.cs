namespace Pants.CompatibilityHarness.Internal;

internal sealed class FixtureRefreshWorkspace : IDisposable
{
    readonly string _root;

    public FixtureRefreshWorkspace(PantsRepositoryPaths repository)
    {
        ArgumentNullException.ThrowIfNull(repository);

        var fixturesParent = Path.GetDirectoryName(repository.CompatibilityFixtures)
            ?? throw new InvalidOperationException("The compatibility fixture path has no parent.");
        _root = Path.Combine(fixturesParent, $".compatibility-refresh-{Guid.NewGuid():N}");
        CompatibilityFixtures = Path.Combine(_root, "Compatibility");
        ContractManifest = Path.Combine(_root, "MidgeContractManifest.json");
        _ = Directory.CreateDirectory(CompatibilityFixtures);
    }

    public string CompatibilityFixtures { get; }

    public string ContractManifest { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException exception)
        {
            Console.Error.WriteLine(
                $"warning: could not remove fixture refresh staging directory '{_root}': "
                + exception.Message);
        }
        catch (UnauthorizedAccessException exception)
        {
            Console.Error.WriteLine(
                $"warning: could not remove fixture refresh staging directory '{_root}': "
                + exception.Message);
        }
    }
}

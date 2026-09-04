namespace Cntryl.Pants.Cloud;

public sealed class CloudQualificationRepositoryTests
{
    [Fact]
    public void ShouldKeepCloudQualificationSqrzlOnly()
    {
        var repository = FindRepositoryRoot();
        var liveProviderWorkflow = Path.Combine(
            repository,
            ".github",
            "workflows",
            "cloud-provider-qualification.yml");
        var providerQualificationTests = File.ReadAllText(Path.Combine(
            repository,
            "test",
            "Pants.Tests",
            "Cloud",
            "CloudProviderEngineQualificationTests.cs"));
        var documentation = File.ReadAllText(Path.Combine(
            repository,
            "docs",
            "testing",
            "cloud-provider-qualification.md"));

        Assert.False(File.Exists(liveProviderWorkflow));
        Assert.DoesNotContain("Environment.GetEnvironmentVariable", providerQualificationTests);
        Assert.DoesNotContain("DeleteAllAsync", providerQualificationTests);
        Assert.Contains("Sqrzl is the only cloud qualification environment", documentation);
        Assert.Matches("never\\s+accesses\\s+live\\s+provider\\s+accounts", documentation);
    }

    static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Pants.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the Pants repository root.");
    }
}

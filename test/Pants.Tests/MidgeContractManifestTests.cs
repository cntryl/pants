using System.Reflection;
using System.Text.Json;

namespace Pants.Tests;

public sealed class MidgeContractManifestTests
{
    private const string PinnedSha = "c5ffc2d3284c76b6f7cd03444a5b0a38ae8bbc33";
    private static readonly string[] ValidStatuses = ["mapped", "planned", "n/a"];

    [Fact]
    public void ShouldConsumeCommittedManifestWithoutSiblingCheckout()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "MidgeContractManifest.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
        JsonElement root = document.RootElement;
        JsonElement entries = root.GetProperty("entries");

        Assert.Equal(2, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(PinnedSha, root.GetProperty("midgeSha").GetString());
        Assert.True(entries.GetArrayLength() > 900);
        Assert.All(entries.EnumerateArray(), static entry =>
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("source").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("sourceSymbolOrTest").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("observableBehavior").GetString()));
            string status = Assert.IsType<string>(entry.GetProperty("status").GetString());
            Assert.Contains(status, ValidStatuses);
            Assert.True(entry.TryGetProperty("issue", out JsonElement issue));
            Assert.True(issue.ValueKind is JsonValueKind.Number or JsonValueKind.Null);

            if (status == "mapped")
            {
                JsonElement pantsTests = entry.GetProperty("pantsTests");
                Assert.NotEmpty(pantsTests.EnumerateArray());
                Assert.All(pantsTests.EnumerateArray(), static pantsTest =>
                    Assert.True(TestExists(Assert.IsType<string>(pantsTest.GetString()))));
            }

            if (status == "n/a")
            {
                Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("rationale").GetString()));
            }
        });
    }

    private static bool TestExists(string fullyQualifiedName)
    {
        int separator = fullyQualifiedName.LastIndexOf('.');
        if (separator <= 0 || separator == fullyQualifiedName.Length - 1)
        {
            return false;
        }

        string typeName = fullyQualifiedName[..separator];
        string methodName = fullyQualifiedName[(separator + 1)..];
        Type? type = typeof(MidgeContractManifestTests).Assembly.GetType(typeName);
        return type?.GetMethod(
            methodName,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static) is not null;
    }
}

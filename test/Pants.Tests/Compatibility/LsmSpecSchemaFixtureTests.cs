using System.Text.Json;
using Corvus.Text.Json.RuntimeEvaluator;
using Corvus.Text.Json.Validator;

namespace Cntryl.Pants.Compatibility;

public sealed class LsmSpecSchemaFixtureTests
{
    const string SnapshotRevision = "bbf7c71";

    static readonly string FixtureRoot = Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "Compatibility",
        "LsmSpec",
        SnapshotRevision);

    public static IEnumerable<object[]> PublishedSchemaFixtures =>
        ReadPublishedSchemaFixtures().Select(static fixture => new object[]
        {
            fixture.Id,
            fixture.SchemaPath,
            fixture.InstancePath,
            fixture.ExpectedValid,
            fixture.ReferencePaths
        });

    [Theory]
    [MemberData(nameof(PublishedSchemaFixtures))]
    public void ShouldMatchPublishedSchemaAcceptanceForLsmSpecExamples(
        string fixtureId,
        string schemaPath,
        string instancePath,
        bool expectedValid,
        string[] referencePaths)
    {
        var references = referencePaths
            .Select(static path => new AdditionalSchemaFile(ReadSchemaId(path), path))
            .ToArray();
        var options = new JsonSchema.Options(
            additionalSchemaFiles: references,
            allowFileSystemAndHttpResolution: false,
            defaultDialect: JsonSchemaDialect.Draft202012,
            alwaysAssertFormat: false);
        var schema = JsonSchema.FromFile(schemaPath, options);
        var instance = File.ReadAllBytes(instancePath);

        Assert.True(
            expectedValid == schema.Validate(instance),
            $"Pinned lsm-spec fixture '{fixtureId}' did not have its published schema result.");
    }

    static IEnumerable<SchemaFixture> ReadPublishedSchemaFixtures()
    {
        var indexPaths = new[]
        {
            Path.Combine(FixtureRoot, "conformance", "fixtures", "format8-schemas", "fixtures.json"),
            Path.Combine(FixtureRoot, "conformance", "fixtures", "format8-manifest", "fixtures.json")
        };

        foreach (var indexPath in indexPaths)
        {
            using var index = JsonDocument.Parse(File.ReadAllBytes(indexPath));
            var indexDirectory = Path.GetDirectoryName(indexPath)!;
            foreach (var fixture in index.RootElement.GetProperty("fixtures"u8).EnumerateArray())
            {
                var schemaRelativePath = fixture.TryGetProperty("schema"u8, out var schema)
                    ? schema.GetString()!
                    : fixture.GetProperty("manifest_schema"u8).GetString()!;
                var schemaPath = Resolve(indexDirectory, schemaRelativePath);
                var fixturePath = Resolve(indexDirectory, fixture.GetProperty("path"u8).GetString()!);
                var expected = fixture.TryGetProperty("expected"u8, out var expectedSchema)
                    ? expectedSchema.GetString()!
                    : fixture.TryGetProperty("schema_expected"u8, out var expectedManifest)
                        ? expectedManifest.GetString()!
                        : throw new InvalidDataException(
                            $"Pinned lsm-spec fixture '{fixture.GetProperty("id"u8).GetString()}' is missing a schema expectation.");
                var expectedValid = expected switch
                {
                    "accept" => true,
                    "reject" => false,
                    _ => throw new InvalidDataException(
                        $"Pinned lsm-spec fixture '{fixture.GetProperty("id"u8).GetString()}' has unsupported schema expectation '{expected}'.")
                };
                var referencePaths = fixture.TryGetProperty("references"u8, out var references)
                    ? references.EnumerateArray()
                        .Select(reference => Resolve(indexDirectory, reference.GetString()!))
                        .ToArray()
                    : [];
                var id = fixture.GetProperty("id"u8).GetString()!;

                if (Directory.Exists(fixturePath))
                {
                    foreach (var manifestPath in Directory.EnumerateFiles(
                                 fixturePath,
                                 "manifest*.json",
                                 SearchOption.TopDirectoryOnly))
                    {
                        yield return new SchemaFixture(
                            $"{id}:{Path.GetFileName(manifestPath)}",
                            schemaPath,
                            manifestPath,
                            expectedValid,
                            referencePaths);
                    }

                    continue;
                }

                yield return new SchemaFixture(
                    id,
                    schemaPath,
                    fixturePath,
                    expectedValid,
                    referencePaths);
            }
        }
    }

    static string ReadSchemaId(string schemaPath)
    {
        using var schema = JsonDocument.Parse(File.ReadAllBytes(schemaPath));
        return schema.RootElement.GetProperty("$id"u8).GetString()!;
    }

    static string Resolve(string basePath, string relativePath) =>
        Path.GetFullPath(Path.Combine(basePath, relativePath));

    sealed record SchemaFixture(
        string Id,
        string SchemaPath,
        string InstancePath,
        bool ExpectedValid,
        string[] ReferencePaths);
}

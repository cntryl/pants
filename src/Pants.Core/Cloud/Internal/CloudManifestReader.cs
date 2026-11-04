using System.Text.Json;
using Cntryl.Pants.Storage.Internal.Manifest;

namespace Cntryl.Pants.Cloud.Internal;

static class CloudManifestReader
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public static string[] ReadSstNames(string root)
    {
        var manifest = ReadManifest(root);
        return manifest?.Files
            .Select(static file => file.Name)
            .Where(static name => name.Length > 0)
            .Select(ValidateSstName)
            .ToArray() ?? [];
    }

    public static ulong ReadLastPersistedSequence(string root) =>
        ReadManifest(root)?.LastPersistedSequence ?? 0;

    public static ManifestState? ReadManifest(string root)
    {
        var snapshotPath = Path.Combine(root, "manifest.snapshot.json");
        var manifestPath = Path.Combine(root, "manifest.json");
        var path = File.Exists(snapshotPath) ? snapshotPath : manifestPath;
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var files = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal)
            {
                ["manifest.snapshot.json"] = PositionalFile.ReadAllBytes(path)
            };
            var journal = Path.Combine(root, "manifest.journal");
            if (File.Exists(journal))
            {
                files["manifest.journal"] = PositionalFile.ReadAllBytes(journal);
            }

            return DecodeMetadata(files);
        }
        catch (JsonException exception)
        {
            throw new PantsCorruptionException("Cloud manifest is malformed.", exception);
        }
    }

    public static ManifestState DecodeMetadata(IReadOnlyDictionary<string, ReadOnlyMemory<byte>> files)
    {
        if (!files.TryGetValue("manifest.snapshot.json", out var snapshot))
        {
            throw new PantsCorruptionException("Cloud metadata is missing a manifest snapshot.");
        }

        var manifest = DecodeManifest(snapshot.Span);
        if (files.TryGetValue("manifest.journal", out var journal))
        {
            LocalDiskStore.ReplayManifestMetadata(journal.Span, manifest);
        }

        return manifest;
    }

    public static ManifestState DecodeManifest(ReadOnlySpan<byte> bytes)
    {
        try
        {
            using var document = MetadataFrameCodec.Decode(bytes, 1);
            return ManifestCodec.Decode(document.RootElement, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new PantsCorruptionException("Cloud manifest is malformed.", exception);
        }
    }

    static string ValidateSstName(string name)
    {
        if (name != Path.GetFileName(name) ||
            !name.EndsWith(".sst", StringComparison.Ordinal) ||
            name.Contains(':') ||
            name.Contains('\\'))
        {
            throw new PantsCorruptionException(
                $"Cloud manifest SST name '{name}' is unsafe.");
        }

        return name;
    }
}

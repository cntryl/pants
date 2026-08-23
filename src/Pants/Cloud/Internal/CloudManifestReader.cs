using System.Text.Json;

namespace Cntryl.Pants;

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

    public static MidgeManifest? ReadManifest(string root)
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
            return DecodeManifest(File.ReadAllBytes(path));
        }
        catch (JsonException exception)
        {
            throw new PantsCorruptionException("Cloud manifest is malformed.", exception);
        }
    }

    public static MidgeManifest DecodeManifest(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return JsonSerializer.Deserialize<MidgeManifest>(bytes, JsonOptions) ??
                throw new JsonException("Cloud manifest is empty.");
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

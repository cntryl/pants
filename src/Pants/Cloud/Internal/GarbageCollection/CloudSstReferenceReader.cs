using System.Text.Json;

namespace Cntryl.Pants.Cloud.Internal.GarbageCollection;

static class CloudSstReferenceReader
{
    public static IReadOnlySet<string> ReadLocalProtectedNames(string localRoot)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var manifest = CloudManifestReader.ReadManifest(localRoot);
        if (manifest is not null)
        {
            AddManifestNames((MidgeManifest)manifest, names);
        }

        var sstDirectory = Path.Combine(localRoot, "sst");
        if (Directory.Exists(sstDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(
                         sstDirectory,
                         "*.sst",
                         SearchOption.TopDirectoryOnly))
            {
                AddValidatedName(Path.GetFileName(path), names);
            }
        }

        var intentPath = Path.Combine(localRoot, "intent_log.json");
        if (File.Exists(intentPath))
        {
            AddIntentNames(File.ReadAllBytes(intentPath), names);
        }

        return names;
    }

    public static void AddManifestNames(
        MidgeManifest manifest,
        ISet<string> names)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(names);
        foreach (var file in manifest.Files)
        {
            AddValidatedName(file.Name, names);
        }
    }

    public static void AddIntentNames(ReadOnlyMemory<byte> bytes, ISet<string> names)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            Visit(document.RootElement, names);
        }
        catch (JsonException exception)
        {
            throw new PantsCorruptionException(
                "Cloud SST garbage collection found a malformed intent log.",
                exception);
        }
    }

    static void Visit(JsonElement element, ISet<string> names)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    Visit(property.Value, names);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Visit(item, names);
                }

                break;
            case JsonValueKind.String:
                var value = element.GetString();
                if (value?.EndsWith(".sst", StringComparison.Ordinal) == true)
                {
                    AddValidatedName(value, names);
                }

                break;
        }
    }

    static void AddValidatedName(string name, ISet<string> names)
    {
        if (!CloudSstObjectKey.TryGetName(
                PantsCloudObjectLayout.SstPrefix + name,
                out var validatedName) ||
            !StringComparer.Ordinal.Equals(name, validatedName))
        {
            throw new PantsCorruptionException(
                $"Cloud SST reference '{name}' is unsafe.");
        }

        names.Add(validatedName);
    }
}

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
            AddManifestNames((ManifestState)manifest, names);
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
        ManifestState manifest,
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

    public static IReadOnlyList<CloudCompactionOutputSet> ReadOutputDurableCompactions(
        ReadOnlyMemory<byte> bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var intents = new List<CloudCompactionOutputSet>();
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || entry.EnumerateObject().Count() != 1)
            {
                continue;
            }

            var variant = entry.EnumerateObject().Single();
            if (variant.Name != "CompactionPublish" ||
                variant.Value.ValueKind != JsonValueKind.Object ||
                !variant.Value.TryGetProperty("phase", out var phase) ||
                phase.ValueKind != JsonValueKind.String ||
                phase.GetString() != "OutputDurable" ||
                !variant.Value.TryGetProperty("cf_id", out var columnFamilyElement) ||
                !columnFamilyElement.TryGetUInt32(out var columnFamilyId) ||
                !TryReadNames(variant.Value, "removed", false, columnFamilyId, out var removed) ||
                !TryReadNames(variant.Value, "added", true, columnFamilyId, out var added) ||
                removed.Count == 0 ||
                added.Count == 0)
            {
                continue;
            }

            intents.Add(new CloudCompactionOutputSet(columnFamilyId, removed, added));
        }

        return intents;
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

    static bool TryReadNames(
        JsonElement intent,
        string propertyName,
        bool objectNames,
        uint columnFamilyId,
        out IReadOnlySet<string> names)
    {
        var parsedNames = new HashSet<string>(StringComparer.Ordinal);
        names = parsedNames;
        if (!intent.TryGetProperty(propertyName, out var values) ||
            values.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var value in values.EnumerateArray())
        {
            JsonElement nameElement;
            if (objectNames)
            {
                if (value.ValueKind != JsonValueKind.Object ||
                    !value.TryGetProperty("name", out nameElement))
                {
                    return false;
                }
            }
            else
            {
                nameElement = value;
            }

            if (nameElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var name = nameElement.GetString();
            if (name is null ||
                !CloudSstObjectKey.TryGetName(
                    PantsCloudObjectLayout.SstPrefix + name,
                    out var validatedName) ||
                !StringComparer.Ordinal.Equals(name, validatedName) ||
                !CloudSstIdentity.TryParse(name, out var identity) ||
                identity.ColumnFamilyId != columnFamilyId)
            {
                return false;
            }

            if (!parsedNames.Add(validatedName))
            {
                return false;
            }
        }

        return true;
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

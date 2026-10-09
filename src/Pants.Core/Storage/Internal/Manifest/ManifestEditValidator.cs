using System.Text.Json;

namespace Cntryl.Pants.Storage.Internal.Manifest;

/// <summary>
///     Rejects a manifest edit whose content would be refused when it is applied, so the edit never
///     reaches the journal. A journaled edit that cannot be applied makes the database unopenable on
///     the next replay, so the check belongs before the append, not after it.
/// </summary>
static class ManifestEditValidator
{
    public static void Validate(ManifestEdit edit, JsonSerializerOptions options)
    {
        switch (edit.Value)
        {
            case FileMeta metadata when edit.Variant == "AddSst":
                RequireSafeName(metadata.Name);
                return;
            case List<ManifestEdit> nested when edit.Variant == "Batch":
                foreach (var nestedEdit in nested)
                {
                    Validate(nestedEdit, options);
                }

                return;
            case JsonElement element:
                ValidateElement(edit.Variant, element, options);
                return;
            default:
                ValidateElement(
                    edit.Variant,
                    edit.ToElement(options).GetProperty(edit.Variant),
                    options);
                return;
        }
    }

    static void ValidateElement(string variant, JsonElement value, JsonSerializerOptions options)
    {
        switch (variant)
        {
            case "RemoveSst":
                if (value.ValueKind == JsonValueKind.Object &&
                    value.TryGetProperty("name", out var name) &&
                    name.ValueKind == JsonValueKind.String)
                {
                    RequireSafeName(name.GetString()!);
                }

                return;
            case "DropColumnFamilyAt":
                RequireSafeNames(value, "dropped_sst_names");
                return;
            case "ReclaimColumnFamily":
                RequireSafeNames(value, "names");
                return;
            case "SetCloudCheckpoint":
                RequireSafeNames(value, "covering_ssts");
                return;
            case "Batch" when value.ValueKind == JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    Validate(ManifestEdit.FromElement(item), options);
                }

                return;
        }
    }

    static void RequireSafeNames(JsonElement value, string propertyName)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty(propertyName, out var names) ||
            names.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in names.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                RequireSafeName(item.GetString()!);
            }
        }
    }

    static void RequireSafeName(string name)
    {
        if (!SstFileName.IsSafe(name))
        {
            throw PantsException.Create(
                PantsErrorCode.InvalidArgument,
                $"Manifest SST name '{name}' is unsafe.");
        }
    }
}

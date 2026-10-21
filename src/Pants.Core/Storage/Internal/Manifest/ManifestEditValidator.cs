using System.Text.Json;
using Cntryl.Pants.Storage.Internal;

namespace Cntryl.Pants.Storage.Internal.Manifest;

/// <summary>
///     Rejects a manifest edit whose content would be refused when it is applied, so the edit never
///     reaches the journal. A journaled edit that cannot be applied makes the database unopenable on
///     the next replay, so the check belongs before the append, not after it.
/// </summary>
static class ManifestEditValidator
{
    public static void Validate(ManifestEdit edit, JsonSerializerOptions options)
        => ValidateEdit(edit, options, false);

    static void ValidateEdit(ManifestEdit edit, JsonSerializerOptions options, bool insideBatch)
    {
        if (edit.Variant == "Batch")
        {
            var nested = GetBatchEdits(edit, options);
            foreach (var nestedEdit in nested)
            {
                ValidateEdit(nestedEdit, options, true);
            }

            ValidateFlushFrontierPairs(nested, options);
            return;
        }

        if (edit.Variant == "AdvanceFlushFrontier" && !insideBatch)
        {
            throw PantsException.Create(
                PantsErrorCode.InvalidArgument,
                "A flush frontier must be published in the same manifest batch as its SST.");
        }

        switch (edit.Value)
        {
            case FileMeta metadata when edit.Variant == "AddSst":
                RequireSafeName(metadata.Name);
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

    static List<ManifestEdit> GetBatchEdits(ManifestEdit edit, JsonSerializerOptions options)
    {
        if (edit.Value is List<ManifestEdit> nested)
        {
            return nested;
        }

        var value = edit.Value is JsonElement element
            ? element
            : edit.ToElement(options).GetProperty("Batch");
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw PantsException.Create(PantsErrorCode.InvalidArgument, "A manifest batch must be an array.");
        }

        return value.EnumerateArray().Select(ManifestEdit.FromElement).ToList();
    }

    static void ValidateFlushFrontierPairs(
        IReadOnlyList<ManifestEdit> edits,
        JsonSerializerOptions options)
    {
        var flattened = new List<ManifestEdit>();
        foreach (var edit in edits)
        {
            if (edit.Variant == "Batch")
            {
                flattened.AddRange(GetBatchEdits(edit, options));
            }
            else
            {
                flattened.Add(edit);
            }
        }

        var advancedFamilies = new HashSet<uint>();
        for (var index = 0; index < flattened.Count; index++)
        {
            var frontierEdit = flattened[index];
            if (frontierEdit.Variant != "AdvanceFlushFrontier")
            {
                continue;
            }

            var frontier = GetValue(frontierEdit, options);
            var familyId = GetRequiredUInt32(frontier, "cf_id");
            var throughSequence = GetRequiredUInt64(frontier, "through_sequence");
            var sstName = GetRequiredString(frontier, "sst_name");
            if (!advancedFamilies.Add(familyId))
            {
                throw PantsException.Create(
                    PantsErrorCode.InvalidArgument,
                    $"A manifest batch advances family {familyId}'s flush frontier more than once.");
            }

            var pairedOutputExists = flattened.Take(index)
                .Where(static edit => edit.Variant == "AddSst")
                .Select(edit => GetSst(edit, options))
                .Any(output => output.Name == sstName &&
                               output.ColumnFamilyId == familyId &&
                               output.LargestSequence.HasValue &&
                               output.LargestSequence.Value <= throughSequence);
            var outputIsRemoved = flattened.Skip(index + 1)
                .Where(static edit => edit.Variant == "RemoveSst")
                .Select(edit => GetValue(edit, options))
                .Any(value => value.ValueKind == JsonValueKind.Object &&
                              value.TryGetProperty("name", out var name) &&
                              name.ValueKind == JsonValueKind.String &&
                              name.GetString() == sstName);
            if (!pairedOutputExists || outputIsRemoved)
            {
                throw PantsException.Create(
                    PantsErrorCode.InvalidArgument,
                    $"Flush frontier for family {familyId} must follow its retained SST in the same manifest batch.");
            }
        }
    }

    static FileMeta GetSst(ManifestEdit edit, JsonSerializerOptions options) =>
        edit.Value is FileMeta metadata
            ? metadata
            : GetValue(edit, options).Deserialize<FileMeta>(options) ??
              throw PantsException.Create(PantsErrorCode.InvalidArgument, "An AddSst edit is empty.");

    static JsonElement GetValue(ManifestEdit edit, JsonSerializerOptions options) =>
        edit.Value is JsonElement element
            ? element
            : edit.ToElement(options).GetProperty(edit.Variant);

    static string GetRequiredString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw PantsException.Create(PantsErrorCode.InvalidArgument, $"Manifest edit field '{name}' is invalid.");

    static uint GetRequiredUInt32(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetUInt32(out var result)
            ? result
            : throw PantsException.Create(PantsErrorCode.InvalidArgument, $"Manifest edit field '{name}' is invalid.");

    static ulong GetRequiredUInt64(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetUInt64(out var result)
            ? result
            : throw PantsException.Create(PantsErrorCode.InvalidArgument, $"Manifest edit field '{name}' is invalid.");

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
            case "AdvanceFlushFrontier":
                if (value.ValueKind != JsonValueKind.Object ||
                    !value.TryGetProperty("cf_id", out var familyId) ||
                    !familyId.TryGetUInt32(out _) ||
                    !value.TryGetProperty("through_sequence", out var through) ||
                    !through.TryGetUInt64(out var throughSequence) ||
                    throughSequence == ulong.MaxValue ||
                    !value.TryGetProperty("sst_name", out var sstName) ||
                    sstName.ValueKind != JsonValueKind.String)
                {
                    throw PantsException.Create(
                        PantsErrorCode.InvalidArgument,
                        "The flush-frontier edit is invalid.");
                }

                RequireSafeName(sstName.GetString()!);
                return;
            case "AdvanceWalHistoryStart":
                if (value.ValueKind != JsonValueKind.Object ||
                    !value.TryGetProperty("start_segment_id", out var start) ||
                    !start.TryGetUInt64(out _))
                {
                    throw PantsException.Create(
                        PantsErrorCode.InvalidArgument,
                        "The WAL history start edit is invalid.");
                }

                return;
            case "ReserveCommitSequence":
                if (value.ValueKind != JsonValueKind.Object ||
                    !value.TryGetProperty("through_sequence", out var reservedSequenceElement) ||
                    !reservedSequenceElement.TryGetUInt64(out var reservedThrough) ||
                    reservedThrough >= (ulong)long.MaxValue)
                {
                    throw PantsException.Create(
                        PantsErrorCode.InvalidArgument,
                        "A commit sequence reservation must contain a valid through_sequence.");
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

using System.Text.Json;

namespace Cntryl.Pants;

static class CloudDdlEdit
{
    public static void Validate(JsonElement edit)
    {
        var variant = GetVariant(edit);
        var value = variant.Value;
        _ = variant.Name switch
        {
            "CreateColumnFamily" => ValidateCreate(value),
            "DropColumnFamily" => ValidateDrop(value),
            "DropColumnFamilyAt" => ValidateDropAt(value),
            _ => throw new JsonException(
                "The cloud DDL registry accepts only column-family edits.")
        };
    }

    public static void Apply(
        List<CloudDdlColumnFamily> columnFamilies,
        JsonElement edit)
    {
        Validate(edit);
        var variant = GetVariant(edit);
        var value = variant.Value;
        var id = GetRequiredUInt32(value, "id");
        if (variant.Name == "CreateColumnFamily")
        {
            var name = GetRequiredString(value, "name");
            var createdAt = GetRequiredUInt64(value, "created_at");
            var existing = columnFamilies.SingleOrDefault(family => family.Id == id);
            if (existing is null)
            {
                columnFamilies.Add(new CloudDdlColumnFamily
                {
                    Id = id,
                    Name = name,
                    CreatedAt = createdAt
                });
            }
            else if (existing.DeletedAt.HasValue)
            {
                existing.Name = name;
                existing.CreatedAt = createdAt;
                existing.DeletedAt = null;
                existing.DropSequence = null;
                existing.DroppedSstNames = null;
                existing.Reclaimed = false;
            }

            return;
        }

        var family = columnFamilies.SingleOrDefault(candidate =>
            candidate.Id == id && !candidate.DeletedAt.HasValue);
        if (family is null)
        {
            return;
        }

        family.DeletedAt = checked((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        family.DropSequence = variant.Name == "DropColumnFamilyAt"
            ? GetRequiredUInt64(value, "drop_sequence")
            : 0;
        var droppedNames = variant.Name == "DropColumnFamilyAt"
            ? GetStringArray(value, "dropped_sst_names")
            : [];
        family.DroppedSstNames = droppedNames.Length == 0 ? null : [.. droppedNames];
        family.Reclaimed = false;
    }

    public static bool Matches(
        IReadOnlyList<MidgeColumnFamilyMeta> columnFamilies,
        JsonElement edit)
    {
        Validate(edit);
        var variant = GetVariant(edit);
        var value = variant.Value;
        var id = GetRequiredUInt32(value, "id");
        var family = columnFamilies.SingleOrDefault(candidate => candidate.Id == id);
        return variant.Name switch
        {
            "CreateColumnFamily" =>
                family is not null &&
                !family.DeletedAt.HasValue &&
                StringComparer.Ordinal.Equals(family.Name, GetRequiredString(value, "name")),
            "DropColumnFamily" => family?.DeletedAt.HasValue == true,
            "DropColumnFamilyAt" =>
                family?.DeletedAt.HasValue == true &&
                family.DropSequence == GetRequiredUInt64(value, "drop_sequence"),
            _ => false
        };
    }

    public static bool IsCreate(JsonElement edit) =>
        StringComparer.Ordinal.Equals(GetVariant(edit).Name, "CreateColumnFamily");

    public static uint GetColumnFamilyId(JsonElement edit) =>
        GetRequiredUInt32(GetVariant(edit).Value, "id");

    public static string GetColumnFamilyName(JsonElement edit) =>
        GetRequiredString(GetVariant(edit).Value, "name");

    static JsonProperty GetVariant(JsonElement edit)
    {
        if (edit.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("The cloud DDL edit must be an externally tagged object.");
        }

        using var properties = edit.EnumerateObject();
        if (!properties.MoveNext())
        {
            throw new JsonException("The cloud DDL edit must contain exactly one variant.");
        }

        var variant = properties.Current;
        if (properties.MoveNext() || variant.Value.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("The cloud DDL edit must contain exactly one object variant.");
        }

        return variant;
    }

    static bool ValidateCreate(JsonElement value)
    {
        _ = GetRequiredUInt32(value, "id");
        _ = GetRequiredString(value, "name");
        _ = GetRequiredUInt64(value, "created_at");
        return true;
    }

    static bool ValidateDrop(JsonElement value)
    {
        _ = GetRequiredUInt32(value, "id");
        return true;
    }

    static bool ValidateDropAt(JsonElement value)
    {
        _ = GetRequiredUInt32(value, "id");
        _ = GetRequiredUInt64(value, "drop_sequence");
        _ = GetStringArray(value, "dropped_sst_names");
        return true;
    }

    static string GetRequiredString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new JsonException($"The cloud DDL edit field '{name}' is invalid.");

    static uint GetRequiredUInt32(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetUInt32(out var result)
            ? result
            : throw new JsonException($"The cloud DDL edit field '{name}' is invalid.");

    static ulong GetRequiredUInt64(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetUInt64(out var result)
            ? result
            : throw new JsonException($"The cloud DDL edit field '{name}' is invalid.");

    static string[] GetStringArray(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return [];
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException($"The cloud DDL edit field '{name}' is invalid.");
        }

        return value.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String
                ? item.GetString()!
                : throw new JsonException($"The cloud DDL edit field '{name}' is invalid."))
            .ToArray();
    }
}

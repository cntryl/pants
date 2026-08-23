using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cntryl.Pants;

static class CloudDdlJson
{
    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static byte[] SerializeRegistry(CloudDdlRegistry registry)
    {
        ValidateRegistry(registry);
        return JsonSerializer.SerializeToUtf8Bytes(registry, Options);
    }

    public static CloudDdlRegistry DeserializeRegistry(ReadOnlySpan<byte> bytes)
    {
        try
        {
            var registry = JsonSerializer.Deserialize<CloudDdlRegistry>(bytes, Options) ??
                throw new JsonException("The cloud DDL registry is empty.");
            ValidateRegistry(registry);
            return registry;
        }
        catch (JsonException exception)
        {
            throw new PantsCorruptionException(
                "The cloud DDL registry cannot be decoded.",
                exception);
        }
    }

    public static byte[] SerializePrepare(CloudDdlPrepare prepare)
    {
        ValidatePrepare(prepare);
        return JsonSerializer.SerializeToUtf8Bytes(prepare, Options);
    }

    public static CloudDdlPrepare DeserializePrepare(ReadOnlySpan<byte> bytes)
    {
        try
        {
            var prepare = JsonSerializer.Deserialize<CloudDdlPrepare>(bytes, Options) ??
                throw new JsonException("The cloud DDL prepare is empty.");
            ValidatePrepare(prepare);
            return prepare;
        }
        catch (JsonException exception)
        {
            throw new PantsCorruptionException(
                "The local cloud DDL prepare cannot be decoded.",
                exception);
        }
    }

    static void ValidateRegistry(CloudDdlRegistry registry)
    {
        if (registry.ColumnFamilies is null || registry.Operations is null)
        {
            throw new JsonException("The cloud DDL registry collections cannot be null.");
        }

        var familyIds = new HashSet<uint>();
        foreach (var family in registry.ColumnFamilies)
        {
            if (family is null || !familyIds.Add(family.Id) || string.IsNullOrEmpty(family.Name))
            {
                throw new JsonException("The cloud DDL registry contains an invalid column family.");
            }
        }

        var operationIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var operation in registry.Operations)
        {
            if (operation is null ||
                string.IsNullOrWhiteSpace(operation.OperationId) ||
                !operationIds.Add(operation.OperationId))
            {
                throw new JsonException("The cloud DDL registry contains an invalid operation ID.");
            }

            CloudDdlEdit.Validate(operation.Edit);
        }
    }

    static void ValidatePrepare(CloudDdlPrepare prepare)
    {
        if (string.IsNullOrWhiteSpace(prepare.OperationId))
        {
            throw new JsonException("The cloud DDL prepare operation ID is invalid.");
        }

        CloudDdlEdit.Validate(prepare.Edit);
    }
}

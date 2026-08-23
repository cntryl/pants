using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pants;

sealed class CloudDdlOperation
{
    [JsonPropertyName("op_id")]
    public string OperationId { get; set; } = string.Empty;

    public JsonElement Edit { get; set; }

    public CloudDdlOperation Clone() => new()
    {
        OperationId = OperationId,
        Edit = Edit.Clone()
    };
}

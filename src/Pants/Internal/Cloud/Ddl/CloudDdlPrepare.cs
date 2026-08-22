using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pants;

sealed class CloudDdlPrepare
{
    [JsonPropertyName("op_id")]
    public string OperationId { get; set; } = string.Empty;

    public ulong ExpectedRemoteEpoch { get; set; }

    public JsonElement Edit { get; set; }
}

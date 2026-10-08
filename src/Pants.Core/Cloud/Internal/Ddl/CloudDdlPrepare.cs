using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cntryl.Pants.Cloud.Internal.Ddl;

sealed class CloudDdlPrepare
{
    [JsonPropertyName("op_id")] public string OperationId { get; set; } = string.Empty;

    public ulong ExpectedRemoteEpoch { get; set; }

    /// <summary>
    ///     Persisted before the registry CAS is submitted. A store may apply a conditional write
    ///     after the client gave up on it, so an unanswered CAS that reads back as absent is not
    ///     proof of abort; while this is set the engine fences instead of assuming the DDL failed.
    /// </summary>
    [JsonPropertyName("remote_cas_ambiguous")]
    public bool RemoteCasAmbiguous { get; set; }

    public JsonElement Edit { get; set; }
}

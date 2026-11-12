using System.Text.Json.Serialization;

namespace Cntryl.Pants.Cloud.Internal.Ddl;

sealed class CloudDdlRegistry
{
    [JsonRequired] public uint RegistryVersion { get; set; } = 2;

    [JsonRequired, JsonPropertyName("ddl_epoch")] public ulong Epoch { get; set; }

    [JsonRequired] public ulong WriterEpoch { get; set; } = 1;

    public List<CloudDdlColumnFamily> ColumnFamilies { get; set; } = [];

    public List<CloudDdlOperation> Operations { get; set; } = [];

    public CloudDdlRegistry Clone() => new()
    {
        RegistryVersion = RegistryVersion,
        WriterEpoch = WriterEpoch,
        Epoch = Epoch,
        ColumnFamilies = ColumnFamilies.Select(static family => family.Clone()).ToList(),
        Operations = Operations.Select(static operation => operation.Clone()).ToList()
    };
}

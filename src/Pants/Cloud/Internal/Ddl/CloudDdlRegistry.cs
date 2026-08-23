namespace Cntryl.Pants;

sealed class CloudDdlRegistry
{
    public ulong Epoch { get; set; }

    public List<CloudDdlColumnFamily> ColumnFamilies { get; set; } = [];

    public List<CloudDdlOperation> Operations { get; set; } = [];

    public CloudDdlRegistry Clone() => new()
    {
        Epoch = Epoch,
        ColumnFamilies = ColumnFamilies.Select(static family => family.Clone()).ToList(),
        Operations = Operations.Select(static operation => operation.Clone()).ToList()
    };
}

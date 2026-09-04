namespace Cntryl.Pants.Cloud;

/// <summary>Bytes and the conditional identity observed in the same object read.</summary>
public sealed record PantsCloudObject(
    ReadOnlyMemory<byte> Data,
    string Version)
{
    readonly string _version = CloudObjectIdentity.RequireVersion(Version);

    /// <summary>The non-empty provider identity to use for conditional mutations.</summary>
    public string Version
    {
        get => _version;
        init => _version = CloudObjectIdentity.RequireVersion(value);
    }
}

namespace Cntryl.Pants;

public sealed record PantsDatabaseCapabilities
{
    public PantsDatabaseCapabilities(
        bool isPersistent,
        bool isCloudBacked,
        IEnumerable<PantsDurability> supportedDurabilities)
    {
        ArgumentNullException.ThrowIfNull(supportedDurabilities);
        IsPersistent = isPersistent;
        IsCloudBacked = isCloudBacked;
        SupportedDurabilities = Array.AsReadOnly(
            supportedDurabilities.Distinct().Order().ToArray());
    }

    public bool IsPersistent { get; }

    public bool IsCloudBacked { get; }

    public IReadOnlyList<PantsDurability> SupportedDurabilities { get; }

    public bool Supports(PantsDurability durability) => SupportedDurabilities.Contains(durability);
}

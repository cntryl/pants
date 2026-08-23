namespace Cntryl.Pants;

public sealed class PantsWriteOptions
{
    private PantsWriteOptions(PantsDurability durability)
    {
        Durability = durability;
    }

    public PantsDurability Durability { get; }

    public static PantsWriteOptions Sync { get; } = new(PantsDurability.Sync);

    public static PantsWriteOptions Buffered { get; } = new(PantsDurability.Buffered);

    public static PantsWriteOptions BestEffort { get; } = new(PantsDurability.BestEffort);

    public static PantsWriteOptions CloudAsync { get; } = new(PantsDurability.CloudAsync);

    public static PantsWriteOptions CloudStrict { get; } = new(PantsDurability.CloudStrict);
}

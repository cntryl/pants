namespace Cntryl.Pants.Storage;

public readonly record struct PantsMemoryBudget
{
    PantsMemoryBudget(long? bytes)
    {
        Bytes = bytes;
    }

    public long? Bytes { get; }

    public bool IsAutomatic => Bytes is null;

    public static PantsMemoryBudget Auto => default;

    public static PantsMemoryBudget FromBytes(long bytes) => new(bytes);

    public override string ToString() => IsAutomatic ? "Auto" : $"{Bytes} bytes";
}

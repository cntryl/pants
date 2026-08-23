namespace Cntryl.Pants.Storage.Internal;

sealed class MidgeColumnFamilyMeta
{
    public uint Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public ulong CreatedAt { get; set; }

    public ulong? DeletedAt { get; set; }

    public ulong? DropSequence { get; set; }

    public List<string> DroppedSstNames { get; set; } = [];

    public bool Reclaimed { get; set; }

    public MidgeColumnFamilyMeta Clone() => new()
    {
        Id = Id,
        Name = Name,
        CreatedAt = CreatedAt,
        DeletedAt = DeletedAt,
        DropSequence = DropSequence,
        DroppedSstNames = [.. DroppedSstNames],
        Reclaimed = Reclaimed
    };
}

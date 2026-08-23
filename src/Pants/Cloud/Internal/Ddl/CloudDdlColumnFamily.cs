namespace Pants;

sealed class CloudDdlColumnFamily
{
    public uint Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public ulong CreatedAt { get; set; }

    public ulong? DeletedAt { get; set; }

    public ulong? DropSequence { get; set; }

    public List<string>? DroppedSstNames { get; set; }

    public bool Reclaimed { get; set; }

    public CloudDdlColumnFamily Clone() => new()
    {
        Id = Id,
        Name = Name,
        CreatedAt = CreatedAt,
        DeletedAt = DeletedAt,
        DropSequence = DropSequence,
        DroppedSstNames = DroppedSstNames is null ? null : [.. DroppedSstNames],
        Reclaimed = Reclaimed
    };

    public static CloudDdlColumnFamily FromManifest(MidgeColumnFamilyMeta metadata) => new()
    {
        Id = metadata.Id,
        Name = metadata.Name,
        CreatedAt = metadata.CreatedAt,
        DeletedAt = metadata.DeletedAt,
        DropSequence = metadata.DropSequence,
        DroppedSstNames = metadata.DroppedSstNames.Count == 0
            ? null
            : [.. metadata.DroppedSstNames],
        Reclaimed = metadata.Reclaimed
    };
}

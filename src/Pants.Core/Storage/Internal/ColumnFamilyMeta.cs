using System.Text.Json.Serialization;

namespace Cntryl.Pants.Storage.Internal;

sealed class ColumnFamilyMeta
{
    [JsonRequired]
    public uint Id { get; set; }

    [JsonRequired]
    public string Name { get; set; } = string.Empty;

    public ulong CreatedAt { get; set; }

    public ulong? DeletedAt { get; set; }

    public ulong? DropSequence { get; set; }

    public List<string> DroppedSstNames { get; set; } = [];

    public bool Reclaimed { get; set; }

    public ColumnFamilyMeta Clone() => new()
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

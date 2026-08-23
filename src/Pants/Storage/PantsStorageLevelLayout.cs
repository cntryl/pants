namespace Pants;

public sealed record PantsStorageLevelLayout(
    int Level,
    int FileCount,
    long TotalBytes,
    IReadOnlyList<PantsStorageFileLayout> Files);

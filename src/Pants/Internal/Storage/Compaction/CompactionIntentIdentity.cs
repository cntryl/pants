namespace Pants;

sealed record CompactionIntentIdentity(
    uint ColumnFamilyId,
    string RemovedFiles,
    string AddedFiles)
{
    public bool HasSameInputs(CompactionIntentIdentity other) =>
        ColumnFamilyId == other.ColumnFamilyId &&
        StringComparer.Ordinal.Equals(RemovedFiles, other.RemovedFiles);

    public bool HasSameOutputs(CompactionIntentIdentity other) =>
        AddedFiles.Length != 0 &&
        other.AddedFiles.Length != 0 &&
        StringComparer.Ordinal.Equals(AddedFiles, other.AddedFiles);

    public IEnumerable<string> GetAddedFileNames() =>
        AddedFiles.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    public static CompactionIntentIdentity Create(
        uint columnFamilyId,
        IEnumerable<string> removedFiles,
        IEnumerable<string> addedFiles) =>
        new(
            columnFamilyId,
            string.Join('\n', removedFiles.Order(StringComparer.Ordinal)),
            string.Join('\n', addedFiles.Order(StringComparer.Ordinal)));
}

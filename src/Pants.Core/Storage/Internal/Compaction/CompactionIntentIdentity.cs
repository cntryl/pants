namespace Cntryl.Pants.Storage.Internal.Compaction;

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

    public void ValidateReplacement(CompactionIntentIdentity replacement, string phase)
    {
        var inputsMatch = HasSameInputs(replacement);
        var outputsMatch = HasSameOutputs(replacement);
        if (outputsMatch && !inputsMatch)
        {
            throw new PantsCorruptionException(
                "A compaction output identity is owned by different inputs.");
        }

        if (phase == "ManifestPublished" && (inputsMatch || outputsMatch))
        {
            throw new PantsBusyException(
                "A manifest-published compaction intent cannot be replaced.");
        }
    }

    public static CompactionIntentIdentity Create(
        uint columnFamilyId,
        IEnumerable<string> removedFiles,
        IEnumerable<string> addedFiles) =>
        new(
            columnFamilyId,
            string.Join('\n', removedFiles.Order(StringComparer.Ordinal)),
            string.Join('\n', addedFiles.Order(StringComparer.Ordinal)));
}

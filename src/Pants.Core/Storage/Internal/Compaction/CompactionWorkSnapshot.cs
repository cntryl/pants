namespace Cntryl.Pants.Storage.Internal.Compaction;

sealed record CompactionWorkSnapshot(
    ManifestReadSnapshot Manifest,
    IReadOnlyList<CompactionPlan> Plans,
    IReadOnlyList<ColumnFamilyMeta> ReclaimableFamilies)
{
    public IReadOnlyList<string> InputNames => Plans
        .SelectMany(static plan => plan.Inputs)
        .Select(static input => input.Name)
        .Distinct(StringComparer.Ordinal)
        .ToArray();
}

readonly record struct CompactionPublicationResult(
    bool Published,
    bool PersistenceAnomaly);

delegate ValueTask<CompactionWorkSnapshot?> CompactionWorkSnapshotProvider(
    CancellationToken cancellationToken);

delegate ValueTask<CompactionPublicationResult> CompactionPublicationDispatcher(
    Func<CompactionPublicationResult> publish,
    CancellationToken cancellationToken);

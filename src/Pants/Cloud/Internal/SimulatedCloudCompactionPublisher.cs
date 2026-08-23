namespace Cntryl.Pants.Cloud.Internal;

sealed class SimulatedCloudCompactionPublisher
{
    const string IntentFileName = "intent_log.json";
    readonly string _cloudRoot;
    readonly IFailpointHandler _failpoints;

    readonly string _localRoot;

    public SimulatedCloudCompactionPublisher(
        string localRoot,
        IFailpointHandler failpoints)
    {
        _localRoot = Path.GetFullPath(localRoot);
        _cloudRoot = Path.Combine(_localRoot, "cloud_store");
        _failpoints = failpoints;
    }

    public ValueTask PublishAsync(
        IReadOnlyList<string> outputNames,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outputNames);
        cancellationToken.ThrowIfCancellationRequested();
        AtomicStagedFile.Write(
            Path.Combine(_cloudRoot, "metadata", IntentFileName),
            File.ReadAllBytes(Path.Combine(_localRoot, IntentFileName)));
        _failpoints.Hit(Failpoint.BeforeCloudUpload);
        foreach (var name in outputNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PublishOutput(name);
        }

        _failpoints.Hit(Failpoint.AfterCloudUpload);
        return ValueTask.CompletedTask;
    }

    void PublishOutput(string name)
    {
        var objectKey = PantsCloudObjectLayout.SstPrefix + name;
        if (!CloudSstObjectKey.TryGetName(objectKey, out var validatedName) ||
            !StringComparer.Ordinal.Equals(validatedName, name))
        {
            throw new PantsCorruptionException(
                $"Simulated-cloud compaction output name '{name}' is unsafe.");
        }

        var localBytes = File.ReadAllBytes(Path.Combine(_localRoot, "sst", validatedName));
        var remotePath = Path.Combine(_cloudRoot, "sst", validatedName);
        if (File.Exists(remotePath))
        {
            if (!File.ReadAllBytes(remotePath).AsSpan().SequenceEqual(localBytes))
            {
                throw new PantsFencedException(
                    $"Immutable simulated-cloud compaction output '{objectKey}' conflicts.");
            }

            return;
        }

        AtomicStagedFile.Write(remotePath, localBytes);
    }
}

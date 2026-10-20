namespace Cntryl.Pants.Cloud.Internal;

sealed class SimulatedCloudCompactionPublisher
{
    const string IntentFileName = "intent_log.json";
    readonly string _cloudRoot;
    readonly IFailpointHandler _failpoints;
    readonly SstPublicationAdmission _sstPublicationAdmission;

    readonly string _localRoot;

    public SimulatedCloudCompactionPublisher(
        string localRoot,
        IFailpointHandler failpoints,
        SstPublicationAdmission sstPublicationAdmission)
    {
        _localRoot = Path.GetFullPath(localRoot);
        _cloudRoot = Path.Combine(_localRoot, "cloud_store");
        _failpoints = failpoints;
        _sstPublicationAdmission = sstPublicationAdmission;
    }

    public async ValueTask PublishAsync(
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
            await PublishOutputAsync(name, cancellationToken).ConfigureAwait(false);
        }

        _failpoints.Hit(Failpoint.AfterCloudUpload);
    }

    async ValueTask PublishOutputAsync(string name, CancellationToken cancellationToken)
    {
        var objectKey = PantsCloudObjectLayout.SstPrefix + name;
        if (!CloudSstObjectKey.TryGetName(objectKey, out var validatedName) ||
            !StringComparer.Ordinal.Equals(validatedName, name))
        {
            throw new PantsCorruptionException(
                $"Simulated-cloud compaction output name '{name}' is unsafe.");
        }

        var localPath = Path.Combine(_localRoot, "sst", validatedName);
        var localLength = new FileInfo(localPath).Length;
        using var admission = await _sstPublicationAdmission.AdmitAsync(
                localLength,
                cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var localBytes = File.ReadAllBytes(localPath);
        var remotePath = Path.Combine(_cloudRoot, "sst", validatedName);
        if (File.Exists(remotePath))
        {
            if (new FileInfo(remotePath).Length != localLength)
            {
                throw new PantsFencedException(
                    $"Immutable simulated-cloud compaction output '{objectKey}' conflicts.");
            }

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

namespace Cntryl.Pants.Cloud.Internal;

sealed class ProviderCloudCompactionPublisher
{
    const string IntentFileName = "intent_log.json";
    readonly ICloudObjectStore _controlStore;
    readonly IFailpointHandler _failpoints;
    readonly CloudLeaseCoordinator _lease;

    readonly string _localRoot;
    readonly ProviderSstPublisher _sstPublisher;

    public ProviderCloudCompactionPublisher(
        string localRoot,
        ICloudObjectStore sstStore,
        ICloudObjectStore controlStore,
        CloudLeaseCoordinator lease,
        IFailpointHandler failpoints,
        SstPublicationAdmission? admission = null)
    {
        _localRoot = Path.GetFullPath(localRoot);
        _controlStore = controlStore;
        _lease = lease;
        _failpoints = failpoints;
        _sstPublisher = new ProviderSstPublisher(sstStore, lease.EnsureValid, admission);
    }

    public async ValueTask PublishAsync(
        IReadOnlyList<string> outputNames,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outputNames);
        cancellationToken.ThrowIfCancellationRequested();
        _lease.EnsureValid();
        var outputPaths = outputNames.Select(ResolveOutputPath).ToArray();
        await PublishIntentAsync(cancellationToken).ConfigureAwait(false);
        _failpoints.Hit(Failpoint.BeforeCloudUpload);
        for (var index = 0; index < outputNames.Count; index++)
        {
            await _sstPublisher.PublishOutputAsync(
                    outputNames[index],
                    outputPaths[index],
                    cancellationToken)
                .ConfigureAwait(false);
        }

        _failpoints.Hit(Failpoint.AfterCloudUpload);
    }

    async ValueTask PublishIntentAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(_localRoot, IntentFileName);
        var data = File.ReadAllBytes(path);
        var objectKey = PantsCloudObjectLayout.MetadataPrefix + IntentFileName;
        var current = await _controlStore.GetAsync(objectKey, cancellationToken)
            .ConfigureAwait(false);
        _lease.EnsureValid();
        var published = await _controlStore.PutAsync(
            objectKey,
            data,
            current is null
                ? new PantsCloudObjectWriteCondition.IfAbsent()
                : new PantsCloudObjectWriteCondition.IfVersion(current.Version),
            cancellationToken).ConfigureAwait(false);
        if (!published)
        {
            throw new PantsFencedException(
                "Cloud compaction intent lost its conditional publication race.");
        }

        var readback = await _controlStore.GetAsync(objectKey, cancellationToken)
            .ConfigureAwait(false) ?? throw new PantsLeaseIndeterminateException(
            "Cloud compaction intent was acknowledged without an authoritative object.");
        _lease.EnsureValid();
        if (!readback.Data.Span.SequenceEqual(data))
        {
            throw new PantsCorruptionException(
                "Cloud compaction intent read back different bytes after publication.");
        }
    }

    string ResolveOutputPath(string name)
    {
        var objectKey = PantsCloudObjectLayout.SstPrefix + name;
        if (!CloudSstObjectKey.TryGetName(objectKey, out var validatedName) ||
            !StringComparer.Ordinal.Equals(validatedName, name))
        {
            throw new PantsCorruptionException(
                $"Cloud compaction output name '{name}' is unsafe.");
        }

        return Path.Combine(_localRoot, "sst", validatedName);
    }
}

namespace Cntryl.Pants.Cloud.Internal;

sealed class ProviderCloudCompactionPublisher
{
    const string IntentFileName = "intent_log.json";
    readonly ICloudObjectStore _controlStore;
    readonly IPantsFailpointHandler _failpoints;
    readonly CloudLeaseCoordinator _lease;

    readonly string _localRoot;
    readonly ICloudObjectStore _sstStore;

    public ProviderCloudCompactionPublisher(
        string localRoot,
        ICloudObjectStore sstStore,
        ICloudObjectStore controlStore,
        CloudLeaseCoordinator lease,
        IPantsFailpointHandler failpoints)
    {
        _localRoot = Path.GetFullPath(localRoot);
        _sstStore = sstStore;
        _controlStore = controlStore;
        _lease = lease;
        _failpoints = failpoints;
    }

    public async ValueTask PublishAsync(
        IReadOnlyList<string> outputNames,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outputNames);
        cancellationToken.ThrowIfCancellationRequested();
        _lease.EnsureValid();
        await PublishIntentAsync(cancellationToken).ConfigureAwait(false);
        _failpoints.Hit(PantsFailpoint.BeforeCloudUpload);
        foreach (var name in outputNames)
        {
            await PublishOutputAsync(name, cancellationToken).ConfigureAwait(false);
        }

        _failpoints.Hit(PantsFailpoint.AfterCloudUpload);
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
                ? new CloudObjectWriteCondition.IfAbsent()
                : new CloudObjectWriteCondition.IfVersion(current.Version),
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

    async ValueTask PublishOutputAsync(
        string name,
        CancellationToken cancellationToken)
    {
        var objectKey = PantsCloudObjectLayout.SstPrefix + name;
        if (!CloudSstObjectKey.TryGetName(objectKey, out var validatedName) ||
            !StringComparer.Ordinal.Equals(validatedName, name))
        {
            throw new PantsCorruptionException(
                $"Cloud compaction output name '{name}' is unsafe.");
        }

        var data = File.ReadAllBytes(Path.Combine(_localRoot, "sst", validatedName));
        _lease.EnsureValid();
        var created = await _sstStore.PutAsync(
            objectKey,
            data,
            new CloudObjectWriteCondition.IfAbsent(),
            cancellationToken).ConfigureAwait(false);
        var readback = await _sstStore.GetAsync(objectKey, cancellationToken)
            .ConfigureAwait(false) ?? throw new PantsLeaseIndeterminateException(
            $"Cloud compaction output '{objectKey}' was acknowledged without an object.");
        _lease.EnsureValid();
        if (!readback.Data.Span.SequenceEqual(data))
        {
            throw created
                ? new PantsCorruptionException(
                    $"Cloud compaction output '{objectKey}' read back different bytes.")
                : new PantsFencedException(
                    $"Immutable cloud compaction output '{objectKey}' conflicts.");
        }
    }
}

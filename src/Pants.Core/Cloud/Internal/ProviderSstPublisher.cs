namespace Cntryl.Pants.Cloud.Internal;

/// <summary>
///     Publishes immutable SSTs to the provider once. An SST proven in this process is never
///     touched again, an object already present is identified by HEAD instead of downloaded, and a
///     new upload is verified with bounded range reads pinned to one object version.
/// </summary>
sealed class ProviderSstPublisher(ICloudObjectStore sstStore, Action ensureLeaseValid)
{
    public const int VerificationRangeBytes = 64 * 1024;

    readonly Dictionary<string, ulong> _proven = new(StringComparer.Ordinal);

    public bool IsProven(string name) => _proven.ContainsKey(name);

    /// <param name="name">Validated SST file name.</param>
    /// <param name="localPath">The local copy, or null when the SST is remote only.</param>
    /// <param name="proofs">Manifest entries that describe this SST.</param>
    public async ValueTask EnsurePublishedAsync(
        string name,
        string? localPath,
        IReadOnlyList<FileMeta> proofs,
        CancellationToken cancellationToken)
    {
        if (_proven.ContainsKey(name))
        {
            return;
        }

        ensureLeaseValid();
        var objectKey = PantsCloudObjectLayout.SstPrefix + name;
        var remote = await sstStore.HeadAsync(objectKey, cancellationToken).ConfigureAwait(false);
        ensureLeaseValid();
        if (remote is not null)
        {
            foreach (var proof in proofs)
            {
                if (proof.SizeBytes != 0 && remote.SizeBytes != proof.SizeBytes)
                {
                    throw new PantsCorruptionException(
                        $"Cloud SST '{name}' length differs from its manifest.");
                }
            }

            if (localPath is not null && remote.SizeBytes != checked((ulong)new FileInfo(localPath).Length))
            {
                throw new PantsFencedException($"Immutable cloud SST '{objectKey}' conflicts.");
            }

            _proven[name] = remote.SizeBytes;
            return;
        }

        if (localPath is null)
        {
            throw new PantsRecoveryFailedException(
                $"Manifest cloud SST '{name}' is unavailable for publication.");
        }

        var local = File.ReadAllBytes(localPath);
        foreach (var proof in proofs)
        {
            CloudSstValidator.Validate(local, proof);
        }

        var created = await sstStore.PutAsync(
            objectKey,
            local,
            new PantsCloudObjectWriteCondition.IfAbsent(),
            cancellationToken).ConfigureAwait(false);
        await VerifyAsync(objectKey, local, created, cancellationToken).ConfigureAwait(false);
        ensureLeaseValid();
        _proven[name] = checked((ulong)local.Length);
    }

    async ValueTask VerifyAsync(
        string objectKey,
        byte[] local,
        bool created,
        CancellationToken cancellationToken)
    {
        var metadata = await sstStore.HeadAsync(objectKey, cancellationToken).ConfigureAwait(false) ??
                       throw new PantsRecoveryFailedException(
                           $"Cloud SST '{objectKey}' is unavailable after publication.");
        if (metadata.SizeBytes != checked((ulong)local.Length))
        {
            throw Mismatch(objectKey, created);
        }

        try
        {
            for (var offset = 0; offset < local.Length; offset += VerificationRangeBytes)
            {
                var length = Math.Min(VerificationRangeBytes, local.Length - offset);
                var range = await sstStore.GetRangeAsync(
                    objectKey,
                    checked((ulong)offset),
                    length,
                    cancellationToken).ConfigureAwait(false) ?? throw Mismatch(objectKey, created);
                if (!StringComparer.Ordinal.Equals(range.Version, metadata.Version))
                {
                    throw new PantsFencedException(
                        $"Cloud SST '{objectKey}' changed identity during verification.");
                }

                if (!range.Data.Span.SequenceEqual(local.AsSpan(offset, length)))
                {
                    throw Mismatch(objectKey, created);
                }
            }
        }
        catch (PantsNotSupportedException)
        {
            // Without ranged reads the only proof is the object itself; this is bounded to the one
            // new output being published, never to previously published SSTs.
            var whole = await sstStore.GetAsync(objectKey, cancellationToken).ConfigureAwait(false) ??
                        throw Mismatch(objectKey, created);
            if (!whole.Data.Span.SequenceEqual(local))
            {
                throw Mismatch(objectKey, created);
            }
        }
    }

    static PantsException Mismatch(string objectKey, bool created) => created
        ? new PantsCorruptionException($"Cloud SST upload for '{objectKey}' read back different bytes.")
        : new PantsFencedException($"Immutable cloud SST '{objectKey}' conflicts.");
}

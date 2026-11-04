namespace Cntryl.Pants.Cloud.Internal;

// Retain the already-admitted immutable cut until its caller consumes the exact publication
// evidence. No second uncharged manifest/identity array is built after capture is released.
sealed class CloudMetadataPublicationReceipt(CloudControlMetadataSnapshot snapshot, ulong accountingOwnerId) : IDisposable
{
    readonly Lock _gate = new();
    CloudControlMetadataSnapshot? _snapshot = snapshot;

    public void InstallInto(MetadataAccountingOwner owner)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_snapshot is null, this);
            if (owner.Identity != accountingOwnerId)
            {
                owner.Invalidate();
                return;
            }

            var files = _snapshot.AuthoritativeManifest?.Files ?? [];
            owner.InstallPublishedCut(files.Select(static file =>
                new MetadataPublishedSstIdentity(file.Name, file.ColumnFamilyId,
                    file.SstSequence, file.SizeBytes, file.ContentCrc32C)));
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _snapshot?.Dispose();
            _snapshot = null;
        }
    }
}

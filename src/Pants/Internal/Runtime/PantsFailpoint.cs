namespace Pants;

internal enum PantsFailpoint
{
    BeforeWalAppend,
    MidWalAppend,
    AfterWalAppend,
    BeforeWalFlush,
    AfterWalFlush,
    BeforeWalRotation,
    AfterWalRotation,
    AfterFlushOutputDurable,
    BeforeFlushManifestPublish,
    AfterFlushManifestPublish,
    AfterCompactionOutputDurable,
    BeforeCompactionManifestPublish,
    AfterCompactionManifestPublish,
    BeforeManifestJournalAppend,
    AfterManifestJournalAppend,
    BeforeManifestJournalSync,
    AfterManifestJournalSync,
    BeforeManifestCheckpointReplace,
    AfterManifestCheckpointReplace,
    BeforeIntentLogReplace,
    AfterIntentLogReplace,
    BeforeCloudUpload,
    AfterCloudUpload,
    BeforeCloudCatalogPublish,
    AfterCloudCatalogPublish,
    BeforeLeaseAcquire,
    AfterLeaseAcquire,
    BeforeLeaseRenewal,
    AfterLeaseRenewal
}

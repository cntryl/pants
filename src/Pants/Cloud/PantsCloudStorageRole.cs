namespace Cntryl.Pants.Cloud;

/// <summary>Identifies every database object role associated with a checked location.</summary>
public enum PantsCloudStorageRole
{
    Wal,
    Sst,
    Control,
    Standalone
}

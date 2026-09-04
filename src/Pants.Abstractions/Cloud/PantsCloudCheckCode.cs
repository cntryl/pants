namespace Cntryl.Pants.Cloud;

/// <summary>Identifies a stable cloud validation or preflight check.</summary>
public enum PantsCloudCheckCode
{
    Configuration,
    BackendResolution,
    NamespaceList,
    ObjectHead,
    RangedRead
}

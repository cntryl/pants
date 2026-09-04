namespace Cntryl.Pants.Cloud;

/// <summary>Classifies a failed or unavailable cloud capability without exposing provider text.</summary>
public enum PantsCloudFailureKind
{
    None,
    Configuration,
    Unsupported,
    NotApplicable,
    Timeout,
    Authentication,
    Authorization,
    NotFound,
    EndpointOrTls,
    Provider
}

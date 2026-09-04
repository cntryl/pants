namespace Cntryl.Pants.Cloud;

/// <summary>Describes whether a check was side-effect-free structural validation or live I/O.</summary>
public enum PantsCloudValidationMode
{
    Structural,
    LivePreflight
}

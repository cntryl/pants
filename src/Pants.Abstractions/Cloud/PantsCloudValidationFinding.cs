using System.Collections.Immutable;

namespace Cntryl.Pants.Cloud;

/// <summary>An immutable, redacted result for one structural or live cloud check.</summary>
public sealed record PantsCloudValidationFinding(
    PantsCloudValidationProviderKind Provider,
    ImmutableArray<PantsCloudStorageRole> Roles,
    PantsCloudValidationMode Mode,
    PantsCloudCheckCode Code,
    PantsCloudCheckOutcome Outcome,
    PantsCloudCheckSeverity Severity,
    PantsCloudFailureKind FailureKind,
    string Message)
{
    public bool Equals(PantsCloudValidationFinding? other) =>
        other is not null &&
        Provider == other.Provider &&
        Roles.SequenceEqual(other.Roles) &&
        Mode == other.Mode &&
        Code == other.Code &&
        Outcome == other.Outcome &&
        Severity == other.Severity &&
        FailureKind == other.FailureKind &&
        StringComparer.Ordinal.Equals(Message, other.Message);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Provider);
        foreach (var role in Roles)
        {
            hash.Add(role);
        }

        hash.Add(Mode);
        hash.Add(Code);
        hash.Add(Outcome);
        hash.Add(Severity);
        hash.Add(FailureKind);
        hash.Add(Message, StringComparer.Ordinal);
        return hash.ToHashCode();
    }
}

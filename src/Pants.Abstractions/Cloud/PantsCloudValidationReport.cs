using System.Collections.Immutable;

namespace Cntryl.Pants.Cloud;

/// <summary>
///     An immutable cloud validation report. Structural validity does not prove credentials,
///     reachability, authorization, durability, or write access.
/// </summary>
public sealed record PantsCloudValidationReport
{
    public PantsCloudValidationReport(IEnumerable<PantsCloudValidationFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);
        Findings = findings.ToImmutableArray();
        IsValid = !Findings.Any(static finding =>
            finding.Mode == PantsCloudValidationMode.Structural &&
            finding.Outcome == PantsCloudCheckOutcome.Failed);
        var live = Findings.Any(static finding =>
            finding.Mode == PantsCloudValidationMode.LivePreflight);
        IsReady = IsValid && live &&
                  !Findings.Any(static finding =>
                      finding.Severity == PantsCloudCheckSeverity.Error) &&
                  Findings.Any(static finding =>
                      finding.Code == PantsCloudCheckCode.NamespaceList &&
                      finding.Outcome == PantsCloudCheckOutcome.Passed);
        IsFullyVerified = IsReady && Findings.All(static finding =>
            finding.Outcome is not PantsCloudCheckOutcome.Unverified and
                not PantsCloudCheckOutcome.Warning);
    }

    public bool IsValid { get; }

    public bool IsReady { get; }

    public bool IsFullyVerified { get; }

    public ImmutableArray<PantsCloudValidationFinding> Findings { get; }

    public bool Equals(PantsCloudValidationReport? other) =>
        other is not null && Findings.SequenceEqual(other.Findings);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var finding in Findings)
        {
            hash.Add(finding);
        }

        return hash.ToHashCode();
    }
}

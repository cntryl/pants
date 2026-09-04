using System.Collections.Immutable;

namespace Cntryl.Pants.Cloud.Internal;

static class CloudConfigurationValidator
{
    static readonly ImmutableArray<PantsCloudStorageRole> StandaloneRole =
        [PantsCloudStorageRole.Standalone];

    public static PantsCloudValidationReport Validate(IPantsCloudProvider provider) =>
        ValidateProviderAndPrefix(provider, null, StandaloneRole);

    public static PantsCloudValidationReport Validate(PantsCloudStorageLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        return ValidateProviderAndPrefix(location.Provider, location.Prefix, StandaloneRole);
    }

    public static PantsCloudValidationReport Validate(PantsCloudStorageTopology topology)
    {
        ArgumentNullException.ThrowIfNull(topology);
        return new PantsCloudValidationReport(CloudStorageLocations.Unique(topology)
            .SelectMany(static item =>
                ValidateProviderAndPrefix(item.Location.Provider, item.Location.Prefix, item.Roles)
                    .Findings));
    }

    internal static PantsCloudValidationReport Validate(
        PantsCloudStorageLocation location,
        ImmutableArray<PantsCloudStorageRole> roles) =>
        ValidateProviderAndPrefix(location.Provider, location.Prefix, roles);

    static PantsCloudValidationReport ValidateProviderAndPrefix(
        IPantsCloudProvider? provider,
        string? prefix,
        ImmutableArray<PantsCloudStorageRole> roles)
    {
        if (provider is null)
        {
            return new PantsCloudValidationReport([
                Failure(
                    new PantsCloudProviderId("unknown"),
                    roles,
                    "Cloud provider configuration is required.")
            ]);
        }

        var findings = provider.Validate().Findings
            .Select(finding => finding with { Roles = roles })
            .ToList();
        if (prefix is not null &&
            (prefix.StartsWith('/') ||
             prefix.Split('/').Any(static segment => segment is "." or "..")))
        {
            findings.Add(Failure(
                provider.Id,
                roles,
                "Cloud prefix must be relative and must not contain dot segments."));
        }

        if (findings.Count == 0)
        {
            findings.Add(new PantsCloudValidationFinding(
                provider.Id,
                roles,
                PantsCloudValidationMode.Structural,
                PantsCloudCheckCode.Configuration,
                PantsCloudCheckOutcome.Passed,
                PantsCloudCheckSeverity.Information,
                PantsCloudFailureKind.None,
                "Cloud provider configuration is structurally valid."));
        }

        return new PantsCloudValidationReport(findings);
    }

    static PantsCloudValidationFinding Failure(
        PantsCloudProviderId provider,
        ImmutableArray<PantsCloudStorageRole> roles,
        string message) => new(
        provider,
        roles,
        PantsCloudValidationMode.Structural,
        PantsCloudCheckCode.Configuration,
        PantsCloudCheckOutcome.Failed,
        PantsCloudCheckSeverity.Error,
        PantsCloudFailureKind.Configuration,
        message);
}

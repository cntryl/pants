using System.Collections.Immutable;
using System.Net;

namespace Cntryl.Pants.Cloud.Internal;

static class CloudConfigurationValidator
{
    static readonly ImmutableArray<PantsCloudStorageRole> StandaloneRole =
        [PantsCloudStorageRole.Standalone];

    public static PantsCloudValidationReport Validate(
        PantsCloudProviderConfiguration provider) =>
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
        PantsCloudProviderConfiguration? provider,
        string? prefix,
        ImmutableArray<PantsCloudStorageRole> roles)
    {
        var findings = new List<PantsCloudValidationFinding>();
        if (provider is null)
        {
            findings.Add(Failure(
                PantsCloudValidationProviderKind.S3Compatible,
                roles,
                "Cloud provider configuration is required."));
            return new PantsCloudValidationReport(findings);
        }

        switch (provider)
        {
            case PantsCloudProviderConfiguration.AwsS3 aws:
                ValidateAws(findings, roles, aws);
                break;
            case PantsCloudProviderConfiguration.S3Compatible compatible:
                ValidateS3Compatible(findings, roles, compatible);
                break;
            case PantsCloudProviderConfiguration.AzureBlob azure:
                ValidateAzure(findings, roles, azure);
                break;
            case PantsCloudProviderConfiguration.Gcs gcs:
                ValidateGcs(findings, roles, gcs);
                break;
            case PantsCloudProviderConfiguration.OciObjectStorage oci:
                ValidateOci(findings, roles, oci);
                break;
            default:
                findings.Add(Failure(
                    Kind(provider),
                    roles,
                    "Cloud provider configuration is unsupported.",
                    PantsCloudFailureKind.Unsupported));
                break;
        }

        if (prefix is not null &&
            (prefix.StartsWith('/') ||
             prefix.Split('/').Any(static segment => segment is "." or "..")))
        {
            findings.Add(Failure(
                Kind(provider),
                roles,
                "Cloud prefix must be relative and must not contain dot segments."));
        }

        if (findings.Count == 0)
        {
            findings.Add(new PantsCloudValidationFinding(
                Kind(provider),
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

    static void ValidateAws(
        List<PantsCloudValidationFinding> findings,
        ImmutableArray<PantsCloudStorageRole> roles,
        PantsCloudProviderConfiguration.AwsS3 provider)
    {
        if (!ValidAwsBucket(provider.Bucket))
        {
            findings.Add(Failure(
                PantsCloudValidationProviderKind.AwsS3,
                roles,
                "AWS bucket must satisfy native S3 naming rules."));
        }

        if (!ValidLowerDnsLabel(provider.Region))
        {
            findings.Add(Failure(
                PantsCloudValidationProviderKind.AwsS3,
                roles,
                "AWS region must be a lowercase DNS label."));
        }

        ValidateS3Credentials(
            findings,
            PantsCloudValidationProviderKind.AwsS3,
            roles,
            provider.Credentials,
            true);
    }

    static void ValidateS3Compatible(
        List<PantsCloudValidationFinding> findings,
        ImmutableArray<PantsCloudStorageRole> roles,
        PantsCloudProviderConfiguration.S3Compatible provider)
    {
        if (!ValidTransportComponent(provider.Bucket))
        {
            findings.Add(Failure(
                PantsCloudValidationProviderKind.S3Compatible,
                roles,
                "S3-compatible bucket must be one transport-safe component."));
        }

        if (!ValidTransportComponent(provider.Region))
        {
            findings.Add(Failure(
                PantsCloudValidationProviderKind.S3Compatible,
                roles,
                "S3-compatible region must be one transport-safe signing component."));
        }

        ValidateEndpoint(
            findings,
            PantsCloudValidationProviderKind.S3Compatible,
            roles,
            provider.Endpoint,
            false);
        ValidateS3Credentials(
            findings,
            PantsCloudValidationProviderKind.S3Compatible,
            roles,
            provider.Credentials,
            false);
    }

    static void ValidateOci(
        List<PantsCloudValidationFinding> findings,
        ImmutableArray<PantsCloudStorageRole> roles,
        PantsCloudProviderConfiguration.OciObjectStorage provider)
    {
        const PantsCloudValidationProviderKind kind =
            PantsCloudValidationProviderKind.OciObjectStorage;
        if (!SafeDnsComponent(provider.Namespace) || !SafeDnsComponent(provider.Region))
        {
            findings.Add(Failure(
                kind,
                roles,
                "OCI namespace and region must be safe DNS components."));
        }

        if (string.IsNullOrEmpty(provider.Bucket) ||
            provider.Bucket.Length > 256 ||
            !provider.Bucket.All(static character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'))
        {
            findings.Add(Failure(
                kind,
                roles,
                "OCI bucket must be 1-256 ASCII letters, digits, hyphens, underscores, or periods."));
        }

        if (provider.Endpoint is not null)
        {
            ValidateEndpoint(findings, kind, roles, provider.Endpoint, false);
        }

        switch (provider.Credentials)
        {
            case PantsOciCredentialSource.CustomerSecretKey credentials:
                if (string.IsNullOrWhiteSpace(credentials.AccessKey) ||
                    string.IsNullOrWhiteSpace(credentials.SecretKey))
                {
                    findings.Add(Failure(
                        kind,
                        roles,
                        "OCI customer secret key credentials must not be empty."));
                }

                break;
            case PantsOciCredentialSource.SharedProfile profile:
                if (BlankOptional(profile.Profile) ||
                    BlankOptional(profile.CredentialsFile) ||
                    BlankOptional(profile.ConfigFile))
                {
                    findings.Add(Failure(
                        kind,
                        roles,
                        "OCI profile names and explicit credential paths must not be empty."));
                }

                break;
            case PantsOciCredentialSource.Environment:
                break;
            case PantsOciCredentialSource.AwsDefaultChain:
            case null:
                findings.Add(Failure(
                    kind,
                    roles,
                    "AWS default credentials are incompatible with OCI Object Storage."));
                break;
            default:
                findings.Add(Failure(kind, roles, "OCI credential source is unsupported."));
                break;
        }
    }

    static void ValidateAzure(
        List<PantsCloudValidationFinding> findings,
        ImmutableArray<PantsCloudStorageRole> roles,
        PantsCloudProviderConfiguration.AzureBlob provider)
    {
        const PantsCloudValidationProviderKind kind = PantsCloudValidationProviderKind.AzureBlob;
        var connectionString = provider.Credential is PantsAzureCredentialSource.ConnectionString;
        if (!connectionString &&
            (provider.Account.Length is < 3 or > 24 ||
             !provider.Account.All(static character =>
                 char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character))))
        {
            findings.Add(Failure(
                kind,
                roles,
                "Azure account must be 3-24 lowercase ASCII letters or digits."));
        }

        if (!ValidAzureContainer(provider.Container))
        {
            findings.Add(Failure(
                kind,
                roles,
                "Azure container must satisfy native Blob Storage naming rules."));
        }

        if (provider.Endpoint is not null)
        {
            var identity = provider.Credential is
                PantsAzureCredentialSource.EnvironmentClientSecret or
                PantsAzureCredentialSource.WorkloadIdentity or
                PantsAzureCredentialSource.ManagedIdentity or
                PantsAzureCredentialSource.LightweightDefaultChain;
            ValidateEndpoint(findings, kind, roles, provider.Endpoint, identity);
        }

        switch (provider.Credential)
        {
            case PantsAzureCredentialSource.SharedKey shared
                when string.IsNullOrWhiteSpace(shared.AccountKey):
            case PantsAzureCredentialSource.SasToken sas when string.IsNullOrWhiteSpace(sas.Token):
            case PantsAzureCredentialSource.ConnectionString connection
                when string.IsNullOrWhiteSpace(connection.Value):
                findings.Add(Failure(kind, roles, "Azure credential value must not be empty."));
                break;
            case PantsAzureCredentialSource.WorkloadIdentity workload
                when BlankOptional(workload.TenantId) ||
                     BlankOptional(workload.ClientId) ||
                     BlankOptional(workload.TokenFile):
                findings.Add(Failure(
                    kind,
                    roles,
                    "Azure workload identity fields and paths must not be empty."));
                break;
            case PantsAzureCredentialSource.ManagedIdentity managed
                when BlankOptional(managed.ClientId):
                findings.Add(Failure(
                    kind,
                    roles,
                    "Azure managed identity client ID must not be empty."));
                break;
            case null:
                findings.Add(Failure(kind, roles, "Azure credential source is required."));
                break;
        }
    }

    static void ValidateGcs(
        List<PantsCloudValidationFinding> findings,
        ImmutableArray<PantsCloudStorageRole> roles,
        PantsCloudProviderConfiguration.Gcs provider)
    {
        const PantsCloudValidationProviderKind kind = PantsCloudValidationProviderKind.Gcs;
        if (!ValidGcsBucket(provider.Bucket))
        {
            findings.Add(Failure(
                kind,
                roles,
                "GCS bucket violates published naming rules."));
        }

        if (string.IsNullOrWhiteSpace(provider.ProjectId))
        {
            findings.Add(Failure(kind, roles, "GCS project ID must not be empty."));
        }

        if (provider.Endpoint is not null)
        {
            ValidateEndpoint(findings, kind, roles, provider.Endpoint, false);
        }

        if (!Enum.IsDefined(provider.ApiStyle))
        {
            findings.Add(Failure(kind, roles, "GCS API style is invalid."));
        }

        switch (provider.Credential)
        {
            case PantsGcsCredentialSource.BearerToken bearer
                when string.IsNullOrWhiteSpace(bearer.Token):
            case PantsGcsCredentialSource.HmacKey hmac
                when string.IsNullOrWhiteSpace(hmac.AccessId) ||
                     string.IsNullOrWhiteSpace(hmac.Secret):
                findings.Add(Failure(kind, roles, "GCS credential value must not be empty."));
                break;
            case PantsGcsCredentialSource.HmacKey when provider.ApiStyle != PantsGcsApiStyle.Xml:
                findings.Add(Failure(kind, roles, "GCS HMAC credentials require the XML API style."));
                break;
            case PantsGcsCredentialSource.ServiceAccountJsonFile file
                when string.IsNullOrEmpty(file.Path):
            case PantsGcsCredentialSource.AuthorizedUserJsonFile authorizedFile
                when string.IsNullOrEmpty(authorizedFile.Path):
                findings.Add(Failure(kind, roles, "GCS credential file path must not be empty."));
                break;
            case null:
                findings.Add(Failure(kind, roles, "GCS credential source is required."));
                break;
        }
    }

    static void ValidateS3Credentials(
        List<PantsCloudValidationFinding> findings,
        PantsCloudValidationProviderKind kind,
        ImmutableArray<PantsCloudStorageRole> roles,
        PantsS3CredentialSource? credential,
        bool allowAwsDefaultChain)
    {
        switch (credential)
        {
            case PantsS3CredentialSource.StaticCredentials value
                when string.IsNullOrWhiteSpace(value.AccessKey) ||
                     string.IsNullOrWhiteSpace(value.SecretKey) ||
                     value.SessionToken is not null && string.IsNullOrWhiteSpace(value.SessionToken):
                findings.Add(Failure(kind, roles, "S3 credential values must not be empty."));
                break;
            case PantsS3CredentialSource.SharedProfile profile
                when BlankOptional(profile.Profile) ||
                     BlankOptional(profile.CredentialsFile) ||
                     BlankOptional(profile.ConfigFile):
                findings.Add(Failure(
                    kind,
                    roles,
                    "S3 profile names and explicit credential paths must not be empty."));
                break;
            case PantsS3CredentialSource.AwsDefaultChain when !allowAwsDefaultChain:
                findings.Add(Failure(
                    kind,
                    roles,
                    "AWS default credentials are incompatible with this S3-compatible provider."));
                break;
            case null:
                findings.Add(Failure(kind, roles, "S3 credential source is required."));
                break;
        }
    }

    static void ValidateEndpoint(
        List<PantsCloudValidationFinding> findings,
        PantsCloudValidationProviderKind kind,
        ImmutableArray<PantsCloudStorageRole> roles,
        Uri? endpoint,
        bool requireHttpsOrigin)
    {
        var valid = endpoint is not null && endpoint.IsAbsoluteUri &&
                    (endpoint.Scheme == Uri.UriSchemeHttp || endpoint.Scheme == Uri.UriSchemeHttps) &&
                    !string.IsNullOrEmpty(endpoint.Host) &&
                    string.IsNullOrEmpty(endpoint.UserInfo) &&
                    string.IsNullOrEmpty(endpoint.Query) &&
                    string.IsNullOrEmpty(endpoint.Fragment) &&
                    (!requireHttpsOrigin ||
                     endpoint.Scheme == Uri.UriSchemeHttps && endpoint.AbsolutePath == "/");
        if (!valid)
        {
            findings.Add(Failure(
                kind,
                roles,
                requireHttpsOrigin
                    ? "Azure identity endpoint must be a pathless HTTPS origin without userinfo, query, or fragment."
                    : "Endpoint must be an absolute HTTP(S) base URL without userinfo, query, or fragment."));
        }
    }

    static PantsCloudValidationFinding Failure(
        PantsCloudValidationProviderKind kind,
        ImmutableArray<PantsCloudStorageRole> roles,
        string message,
        PantsCloudFailureKind failureKind = PantsCloudFailureKind.Configuration) => new(
            kind,
            roles,
            PantsCloudValidationMode.Structural,
            PantsCloudCheckCode.Configuration,
            PantsCloudCheckOutcome.Failed,
            PantsCloudCheckSeverity.Error,
            failureKind,
            message);

    internal static PantsCloudValidationProviderKind Kind(
        PantsCloudProviderConfiguration provider) => provider switch
        {
            PantsCloudProviderConfiguration.AwsS3 => PantsCloudValidationProviderKind.AwsS3,
            PantsCloudProviderConfiguration.S3Compatible =>
                PantsCloudValidationProviderKind.S3Compatible,
            PantsCloudProviderConfiguration.AzureBlob =>
                PantsCloudValidationProviderKind.AzureBlob,
            PantsCloudProviderConfiguration.Gcs => PantsCloudValidationProviderKind.Gcs,
            PantsCloudProviderConfiguration.OciObjectStorage =>
                PantsCloudValidationProviderKind.OciObjectStorage,
            _ => PantsCloudValidationProviderKind.S3Compatible
        };

    static bool ValidAwsBucket(string? value) =>
        value is { Length: >= 3 and <= 63 } &&
        !value.StartsWith("xn--", StringComparison.Ordinal) &&
        !value.StartsWith("sthree-", StringComparison.Ordinal) &&
        !value.StartsWith("amzn-s3-demo-", StringComparison.Ordinal) &&
        !value.EndsWith("-s3alias", StringComparison.Ordinal) &&
        !value.EndsWith("--ol-s3", StringComparison.Ordinal) &&
        !value.EndsWith(".mrap", StringComparison.Ordinal) &&
        !value.EndsWith("--x-s3", StringComparison.Ordinal) &&
        !value.EndsWith("--table-s3", StringComparison.Ordinal) &&
        !value.Contains("..", StringComparison.Ordinal) &&
        !IPAddress.TryParse(value, out _) &&
        IsLowerLetterOrDigit(value[0]) &&
        IsLowerLetterOrDigit(value[^1]) &&
        value.All(static character => IsLowerLetterOrDigit(character) || character is '-' or '.');

    static bool ValidLowerDnsLabel(string? value) =>
        value is { Length: >= 1 and <= 63 } &&
        IsLowerLetterOrDigit(value[0]) &&
        IsLowerLetterOrDigit(value[^1]) &&
        value.All(static character => IsLowerLetterOrDigit(character) || character == '-');

    static bool ValidTransportComponent(string? value) =>
        !string.IsNullOrEmpty(value) && value.All(static character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    static bool SafeDnsComponent(string? value) =>
        !string.IsNullOrEmpty(value) &&
        value[0] != '-' && value[^1] != '-' &&
        value.All(static character => char.IsAsciiLetterOrDigit(character) || character == '-');

    static bool ValidAzureContainer(string? value) =>
        value is { Length: >= 3 and <= 63 } &&
        value[0] != '-' && value[^1] != '-' &&
        !value.Contains("--", StringComparison.Ordinal) &&
        value.All(static character =>
            char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '-');

    static bool ValidGcsBucket(string? value)
    {
        if (value is null)
        {
            return false;
        }

        var maximumLength = value.Contains('.', StringComparison.Ordinal) ? 222 : 63;
        var lower = value.ToLowerInvariant();
        return value.Length >= 3 && value.Length <= maximumLength &&
               !lower.StartsWith("goog", StringComparison.Ordinal) &&
               !lower.Contains("google", StringComparison.Ordinal) &&
               !IPAddress.TryParse(value, out _) &&
               IsLowerLetterOrDigit(value[0]) && IsLowerLetterOrDigit(value[^1]) &&
               value.All(static character =>
                   IsLowerLetterOrDigit(character) || character is '-' or '_' or '.');
    }

    static bool BlankOptional(string? value) => value is not null && string.IsNullOrWhiteSpace(value);

    static bool IsLowerLetterOrDigit(char value) =>
        char.IsAsciiLetterLower(value) || char.IsAsciiDigit(value);
}

using Cntryl.Pants.Cloud;

namespace Cntryl.Pants.Options;

public sealed class PantsCloudProviderOptions
{
    public PantsCloudProviderKind Kind { get; set; } = PantsCloudProviderKind.AwsS3;

    public string? Bucket { get; set; }

    public string? Region { get; set; }

    public string? Namespace { get; set; }

    public string? Account { get; set; }

    public string? Container { get; set; }

    public string? ProjectId { get; set; }

    public Uri? Endpoint { get; set; }

    public bool PathStyle { get; set; }

    public PantsGcsApiStyle ApiStyle { get; set; } = PantsGcsApiStyle.Json;

    public PantsCloudCredentialOptions Credential { get; set; } = new();
}

using System.Collections.Immutable;

namespace Cntryl.Pants.Cloud;

public sealed class PantsCloudValidationTests
{
    [Fact]
    public void ShouldReportEverySharedRoleOnceWithoutResolvingCredentials()
    {
        var location = new PantsCloudStorageLocation(
            new PantsAwsS3Provider(
                "valid-bucket",
                "us-east-1",
                new PantsS3CredentialSource.SharedProfile(
                    "missing-profile",
                    "/definitely/missing/credentials",
                    "/definitely/missing/config")),
            "database");

        var report = PantsCloudStorageTopology.Shared(location).Validate();

        Assert.True(report.IsValid);
        var finding = Assert.Single(report.Findings);
        Assert.Equal(PantsCloudProviderId.AwsS3, finding.Provider);
        Assert.True(
            finding.Roles.SequenceEqual(ImmutableArray.Create(
                PantsCloudStorageRole.Wal,
                PantsCloudStorageRole.Sst,
                PantsCloudStorageRole.Control)));
        Assert.Equal(PantsCloudValidationMode.Structural, finding.Mode);
        Assert.Equal(PantsCloudCheckCode.Configuration, finding.Code);
        Assert.Equal(PantsCloudCheckOutcome.Passed, finding.Outcome);
        Assert.Equal(PantsCloudCheckSeverity.Information, finding.Severity);
        Assert.Equal(PantsCloudFailureKind.None, finding.FailureKind);
    }

    [Fact]
    public void ShouldReturnAllStructuralFailuresWithoutLeakingConfigurationSecrets()
    {
        const string accessKey = "ACCESS-SENSITIVE";
        const string secretKey = "SECRET-SENSITIVE";
        const string querySecret = "QUERY-SENSITIVE";
        var provider = new PantsS3CompatibleProvider(
            "tenant/bucket",
            "region/path",
            new Uri($"https://user:{secretKey}@example.test/path?token={querySecret}"),
            false,
            new PantsS3CredentialSource.StaticCredentials(accessKey, secretKey));

        var report = provider.Validate();
        var rendered = string.Join('|', report.Findings.Select(static finding => finding.Message));

        Assert.False(report.IsValid);
        Assert.All(
            report.Findings,
            static finding => Assert.Equal(PantsCloudCheckSeverity.Error, finding.Severity));
        Assert.DoesNotContain(accessKey, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(secretKey, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(querySecret, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("token=", rendered, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("foo_bar", "us-east-1")]
    [InlineData("foo..bar", "us-east-1")]
    [InlineData("valid-bucket", "US-EAST-1")]
    [InlineData("valid-bucket", "us-east-1/path")]
    public void ShouldRejectInvalidNativeAwsIdentifiers(string bucket, string region)
    {
        var provider = new PantsAwsS3Provider(
            bucket,
            region,
            new PantsS3CredentialSource.Environment());

        Assert.False(provider.Validate().IsValid);
    }

    [Theory]
    [InlineData("alpha/../beta")]
    [InlineData("./alpha")]
    public void ShouldRejectCloudPrefixDotSegments(string prefix)
    {
        var location = new PantsCloudStorageLocation(
            new PantsAwsS3Provider(
                "valid-bucket",
                "us-east-1",
                new PantsS3CredentialSource.Environment()),
            prefix);

        Assert.False(location.Validate().IsValid);
    }

    [Fact]
    public void ShouldValidateFirstClassOciConfigurationAndRedactCredentials()
    {
        const string accessKey = "OCI-ACCESS-SENSITIVE";
        const string secretKey = "OCI-SECRET-SENSITIVE";
        var provider = new PantsOciObjectStorageProvider(
            "namespace",
            "bucket_name",
            "us-ashburn-1",
            null,
            new PantsOciCredentialSource.CustomerSecretKey(accessKey, secretKey));

        var report = provider.Validate();
        var rendered = provider.ToString();

        Assert.True(report.IsValid);
        Assert.Equal(
            new Uri("https://namespace.compat.objectstorage.us-ashburn-1.oraclecloud.com"),
            provider.EffectiveEndpoint);
        Assert.Contains("REDACTED", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(accessKey, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(secretKey, rendered, StringComparison.Ordinal);
        Assert.Equal(
            PantsCloudProviderId.OciObjectStorage,
            Assert.Single(report.Findings).Provider);
    }

    [Fact]
    public void ShouldRejectAwsDefaultCredentialIdentityForOci()
    {
        var provider = new PantsOciObjectStorageProvider(
            "namespace",
            "bucket",
            "us-ashburn-1",
            null,
            new PantsOciCredentialSource.AwsDefaultChain());

        Assert.False(provider.Validate().IsValid);
    }

    [Fact]
    public void ShouldProvideValueEqualityForDeterministicReports()
    {
        var provider = new PantsAwsS3Provider(
            "valid-bucket",
            "us-east-1",
            new PantsS3CredentialSource.Environment());

        Assert.Equal(provider.Validate(), provider.Validate());
        Assert.Equal(provider.Validate().GetHashCode(), provider.Validate().GetHashCode());
    }

    [Fact]
    public void ShouldValidateAzureAndGcsProviderShapes()
    {
        var azure = new PantsAzureBlobProvider(
            "account1",
            "container",
            null,
            new PantsAzureCredentialSource.LightweightDefaultChain());
        var gcs = new PantsGcsProvider(
            "gcs-bucket",
            "project",
            null,
            PantsGcsApiStyle.Json,
            new PantsGcsCredentialSource.ApplicationDefault());
        var invalidAzure = azure with { Account = "Uppercase" };
        var invalidGcs = gcs with { Bucket = "goog-reserved" };

        Assert.True(azure.Validate().IsValid);
        Assert.True(gcs.Validate().IsValid);
        Assert.False(invalidAzure.Validate().IsValid);
        Assert.False(invalidGcs.Validate().IsValid);
        Assert.Equal(
            PantsCloudProviderId.AzureBlob,
            Assert.Single(azure.Validate().Findings).Provider);
        Assert.Equal(
            PantsCloudProviderId.Gcs,
            Assert.Single(gcs.Validate().Findings).Provider);
    }
}

namespace Cntryl.Pants.Tests.Cloud;

public sealed class CloudArchitectureTests
{
    [Fact]
    public void ShouldExposeProviderConfigurationWithoutImplementationFeatures()
    {
        var configurationTypes = typeof(PantsCloudProviderConfiguration).Assembly
            .GetExportedTypes()
            .Where(static type => type == typeof(PantsCloudProviderConfiguration) ||
                                  (type.IsNested && type.DeclaringType == typeof(PantsCloudProviderConfiguration)))
            .ToArray();

        Assert.Contains(typeof(PantsCloudProviderConfiguration.AwsS3), configurationTypes);
        Assert.Contains(typeof(PantsCloudProviderConfiguration.S3Compatible), configurationTypes);
        Assert.Contains(typeof(PantsCloudProviderConfiguration.AzureBlob), configurationTypes);
        Assert.Contains(typeof(PantsCloudProviderConfiguration.Gcs), configurationTypes);
        Assert.All(
            [typeof(S3ObjectStore), typeof(AzureBlobObjectStore), typeof(GcsObjectStore)],
            static type => Assert.False(type.IsPublic));
    }

    [Fact]
    public void ShouldKeepCommonCloudTransportProviderNeutralWhenFeaturesAreSplit()
    {
        var providerTypes = new HashSet<Type>
        {
            typeof(PantsCloudProviderConfiguration.AwsS3),
            typeof(PantsCloudProviderConfiguration.S3Compatible),
            typeof(PantsCloudProviderConfiguration.AzureBlob),
            typeof(PantsCloudProviderConfiguration.Gcs)
        };
        var exposedTypes = typeof(ICloudObjectStore).GetMethods()
            .SelectMany(static method => method.GetParameters()
                .Select(static parameter => parameter.ParameterType)
                .Append(method.ReturnType))
            .ToArray();

        Assert.DoesNotContain(exposedTypes, providerTypes.Contains);
    }

    [Fact]
    public void ShouldKeepProviderDtosInConfigurationLayer()
    {
        var publicSurface = typeof(PantsCloudProviderConfiguration).Assembly.GetExportedTypes();

        Assert.Contains(typeof(PantsCloudProviderConfiguration), publicSurface);
        Assert.Contains(typeof(PantsCloudStorageLocation), publicSurface);
        Assert.Contains(typeof(PantsCloudStorageTopology), publicSurface);
        Assert.DoesNotContain(publicSurface, static type =>
            type.Namespace == typeof(ICloudObjectStore).Namespace &&
            type.Name.EndsWith("ObjectStore", StringComparison.Ordinal));
    }
}

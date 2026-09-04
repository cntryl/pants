namespace Cntryl.Pants.Cloud;

public sealed class CloudArchitectureTests
{
    [Fact]
    public void ShouldExposeProviderConfigurationWithoutImplementationFeatures()
    {
        var contracts = typeof(IPantsCloudProvider).Assembly.GetExportedTypes();
        var builtIns = typeof(PantsAwsS3Provider).Assembly.GetExportedTypes();

        Assert.Contains(typeof(IPantsCloudProvider), contracts);
        Assert.Contains(typeof(IPantsCloudObjectStore), contracts);
        Assert.Contains(typeof(PantsAwsS3Provider), builtIns);
        Assert.Contains(typeof(PantsS3CompatibleProvider), builtIns);
        Assert.Contains(typeof(PantsAzureBlobProvider), builtIns);
        Assert.Contains(typeof(PantsGcsProvider), builtIns);
        Assert.Contains(typeof(PantsOciObjectStorageProvider), builtIns);
        Assert.All(
            [typeof(S3ObjectStore), typeof(AzureBlobObjectStore), typeof(GcsObjectStore)],
            static type => Assert.False(type.IsPublic));
    }

    [Fact]
    public void ShouldKeepCommonCloudTransportProviderNeutralWhenFeaturesAreSplit()
    {
        var providerTypes = new HashSet<Type>
        {
            typeof(PantsAwsS3Provider),
            typeof(PantsS3CompatibleProvider),
            typeof(PantsAzureBlobProvider),
            typeof(PantsGcsProvider)
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
        var contracts = typeof(IPantsCloudProvider).Assembly.GetExportedTypes();

        Assert.Same(typeof(IPantsDatabase).Assembly, typeof(IPantsCloudProvider).Assembly);
        Assert.NotSame(typeof(IPantsCloudProvider).Assembly, typeof(PantsAwsS3Provider).Assembly);
        Assert.Contains(typeof(PantsCloudStorageLocation), contracts);
        Assert.Contains(typeof(PantsCloudStorageTopology), contracts);
        Assert.DoesNotContain(
            contracts,
            static type => type.Name == "PantsCloudProviderConfiguration");
    }
}

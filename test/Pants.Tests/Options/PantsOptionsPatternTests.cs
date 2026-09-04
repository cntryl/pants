using System.Globalization;
using Cntryl.Pants.Support.TestDoubles;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Cntryl.Pants.Options;

public sealed class PantsOptionsPatternTests
{
    [Fact]
    public async Task AddPantsBindsConfigurationAndProjectsValidatedOpenOptions()
    {
        using var directory = new TemporaryDirectory();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Pants:Storage:Kind"] = "Local",
                ["Pants:Storage:Path"] = directory.Path,
                ["Pants:PerformanceGoal"] = "Throughput",
                ["Pants:WorkloadProfile"] = "RangeScan",
                ["Pants:MemoryBudgetBytes"] = (512L * 1024 * 1024)
                    .ToString(CultureInfo.InvariantCulture),
                ["Pants:StorageTimeout"] = "00:00:12",
                ["Pants:RuntimeResponseTimeout"] = "00:00:45",
                ["Pants:ShutdownTimeout"] = "00:00:20",
                ["Pants:LeaseTimeToLive"] = "00:00:45",
                ["Pants:LeaseClockSkewTolerance"] = "00:00:05",
                ["Pants:BackgroundCompaction"] = "false",
                ["Pants:WalBufferSizeBytes"] = (2 * 1024 * 1024)
                    .ToString(CultureInfo.InvariantCulture),
                ["Pants:MinimumEpoch"] = "7",
                ["Pants:Compaction:L0FileCountTrigger"] = "9"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);

        services.AddPants().BindConfiguration("Pants");

        await using var serviceProvider = services.BuildServiceProvider();
        serviceProvider.GetRequiredService<IStartupValidator>().Validate();
        var settings = serviceProvider.GetRequiredService<IOptions<PantsDatabaseOptions>>().Value;
        var database = await serviceProvider
            .GetRequiredService<IPantsDatabaseProvider>()
            .GetDatabaseAsync();

        Assert.Equal(PantsStorageKind.Local, settings.Storage.Kind);
        var storage = Assert.IsType<PantsStorageConfiguration.Local>(database.Options.Storage);
        Assert.Equal(directory.Path, storage.Path);
        Assert.Equal(PantsPerformanceGoal.Throughput, database.Options.Runtime.PerformanceGoal);
        Assert.Equal(PantsWorkloadProfile.RangeScan, database.Options.Runtime.WorkloadProfile);
        Assert.Equal(512L * 1024 * 1024, database.Options.Memory.Budget.Bytes);
        Assert.Equal(TimeSpan.FromSeconds(12), database.Options.Runtime.StorageTimeout);
        Assert.Equal(TimeSpan.FromSeconds(45), database.Options.Runtime.RuntimeResponseTimeout);
        Assert.Equal(TimeSpan.FromSeconds(20), database.Options.Runtime.ShutdownTimeout);
        Assert.Equal(TimeSpan.FromSeconds(45), database.Options.Lease.TimeToLive);
        Assert.Equal(TimeSpan.FromSeconds(5), database.Options.Lease.ClockSkewTolerance);
        Assert.False(database.Options.Compaction.BackgroundEnabled);
        Assert.Equal(2 * 1024 * 1024, database.Options.Memory.WalBufferSizeBytes);
        Assert.Equal((ulong)7, database.Options.Lease.MinimumEpoch);
        Assert.Equal(9, database.Options.Compaction.L0FileCountTrigger);
    }

    [Fact]
    public void AddPantsValidatesBoundConfigurationAtStartup()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Pants:Storage:Kind"] = "Local"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddPants().BindConfiguration("Pants");
        using var serviceProvider = services.BuildServiceProvider();

        var exception = Assert.Throws<OptionsValidationException>(() =>
            serviceProvider.GetRequiredService<IStartupValidator>().Validate());

        Assert.Contains("local storage path", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AddPantsRejectsFlushThresholdWithoutSizeLimitAtStartup()
    {
        var services = new ServiceCollection();
        services.AddPants().Configure(options =>
            options.MemtableFlushThresholdBytes = 8 * 1024 * 1024);
        using var serviceProvider = services.BuildServiceProvider();

        var exception = Assert.Throws<OptionsValidationException>(() =>
            serviceProvider.GetRequiredService<IStartupValidator>().Validate());

        Assert.Contains(
            "A memtable flush threshold requires a memtable size limit.",
            exception.Message);
    }

    [Fact]
    public async Task AddKeyedPantsUsesNamedOptionsForIndependentDatabases()
    {
        using var directory = new TemporaryDirectory();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Pants:Primary:Storage:Kind"] = "InMemory",
                ["Pants:Secondary:Storage:Kind"] = "Local",
                ["Pants:Secondary:Storage:Path"] = directory.Path
            })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddKeyedPants("primary").BindConfiguration("Pants:Primary");
        services.AddKeyedPants("secondary").BindConfiguration("Pants:Secondary");

        await using var serviceProvider = services.BuildServiceProvider();
        serviceProvider.GetRequiredService<IStartupValidator>().Validate();
        var namedOptions = serviceProvider.GetRequiredService<IOptionsMonitor<PantsDatabaseOptions>>();
        var primary = await serviceProvider
            .GetRequiredKeyedService<IPantsDatabaseProvider>("primary")
            .GetDatabaseAsync();
        var secondary = await serviceProvider
            .GetRequiredKeyedService<IPantsDatabaseProvider>("secondary")
            .GetDatabaseAsync();

        Assert.Equal(PantsStorageKind.InMemory, namedOptions.Get("primary").Storage.Kind);
        Assert.Equal(PantsStorageKind.Local, namedOptions.Get("secondary").Storage.Kind);
        Assert.IsType<PantsStorageConfiguration.InMemory>(primary.Options.Storage);
        Assert.Equal(
            directory.Path,
            Assert.IsType<PantsStorageConfiguration.Local>(secondary.Options.Storage).Path);
    }

    [Fact]
    public async Task AddPantsBindsCloudProviderAndCredentialSettings()
    {
        using var cache = new TemporaryDirectory();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Pants:Storage:Kind"] = "Cloud",
                ["Pants:Storage:Path"] = cache.Path,
                ["Pants:Storage:Cloud:Shared:Prefix"] = "catalog/",
                ["Pants:Storage:Cloud:Shared:Provider:Kind"] = "AwsS3",
                ["Pants:Storage:Cloud:Shared:Provider:Bucket"] = "catalog-production",
                ["Pants:Storage:Cloud:Shared:Provider:Region"] = "us-east-1",
                ["Pants:Storage:Cloud:Shared:Provider:Credential:Kind"] = "S3Static",
                ["Pants:Storage:Cloud:Shared:Provider:Credential:AccessKey"] = "access",
                ["Pants:Storage:Cloud:Shared:Provider:Credential:SecretKey"] = "secret"
            })
            .Build();
        var databaseFactory = new CapturingPantsDatabaseFactory();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IPantsDatabaseFactory>(databaseFactory);
        services.AddPants().BindConfiguration("Pants");

        await using var serviceProvider = services.BuildServiceProvider();
        serviceProvider.GetRequiredService<IStartupValidator>().Validate();
        _ = await serviceProvider
            .GetRequiredService<IPantsDatabaseProvider>()
            .GetDatabaseAsync();

        var storage = Assert.IsType<PantsStorageConfiguration.Cloud>(databaseFactory.Options!.Storage);
        Assert.Same(storage.Topology.Wal, storage.Topology.Sst);
        Assert.Same(storage.Topology.Wal, storage.Topology.Control);
        Assert.Equal("catalog/", storage.Topology.Wal.Prefix);
        var provider = Assert.IsType<PantsAwsS3Provider>(
            storage.Topology.Wal.Provider);
        Assert.Equal("catalog-production", provider.Bucket);
        Assert.Equal("us-east-1", provider.Region);
        var credentials = Assert.IsType<PantsS3CredentialSource.StaticCredentials>(
            provider.Credentials);
        Assert.Equal("access", credentials.AccessKey);
        Assert.Equal("secret", credentials.SecretKey);
    }

    [Theory]
    [InlineData(PantsCloudProviderKind.AwsS3)]
    [InlineData(PantsCloudProviderKind.S3Compatible)]
    [InlineData(PantsCloudProviderKind.AzureBlob)]
    [InlineData(PantsCloudProviderKind.Gcs)]
    [InlineData(PantsCloudProviderKind.OciObjectStorage)]
    public async Task AddPantsProjectsEveryCloudProviderWithItsDefaultCredential(
        PantsCloudProviderKind providerKind)
    {
        using var cache = new TemporaryDirectory();
        var databaseFactory = new CapturingPantsDatabaseFactory();
        var services = new ServiceCollection();
        services.AddSingleton<IPantsDatabaseFactory>(databaseFactory);
        services.AddPants().Configure(options =>
        {
            options.Storage = new PantsStorageOptions
            {
                Kind = PantsStorageKind.Cloud,
                Path = cache.Path,
                Cloud = new PantsCloudStorageOptions
                {
                    Shared = new PantsCloudLocationOptions
                    {
                        Prefix = "database/",
                        Provider = CreateProviderOptions(providerKind)
                    }
                }
            };
        });

        await using var serviceProvider = services.BuildServiceProvider();
        serviceProvider.GetRequiredService<IStartupValidator>().Validate();
        _ = await serviceProvider
            .GetRequiredService<IPantsDatabaseProvider>()
            .GetDatabaseAsync();

        var location = Assert.IsType<PantsStorageConfiguration.Cloud>(
            databaseFactory.Options!.Storage).Topology.Control;
        switch (providerKind)
        {
            case PantsCloudProviderKind.AwsS3:
                var aws = Assert.IsType<PantsAwsS3Provider>(location.Provider);
                Assert.IsType<PantsS3CredentialSource.AwsDefaultChain>(aws.Credentials);
                break;
            case PantsCloudProviderKind.S3Compatible:
                var s3 = Assert.IsType<PantsS3CompatibleProvider>(
                    location.Provider);
                Assert.IsType<PantsS3CredentialSource.Environment>(s3.Credentials);
                break;
            case PantsCloudProviderKind.AzureBlob:
                var azure = Assert.IsType<PantsAzureBlobProvider>(
                    location.Provider);
                Assert.IsType<PantsAzureCredentialSource.LightweightDefaultChain>(azure.Credential);
                break;
            case PantsCloudProviderKind.Gcs:
                var gcs = Assert.IsType<PantsGcsProvider>(location.Provider);
                Assert.IsType<PantsGcsCredentialSource.ApplicationDefault>(gcs.Credential);
                break;
            case PantsCloudProviderKind.OciObjectStorage:
                var oci = Assert.IsType<PantsOciObjectStorageProvider>(
                    location.Provider);
                Assert.IsType<PantsOciCredentialSource.Environment>(oci.Credentials);
                break;
            default:
                throw new InvalidOperationException("Unexpected provider kind.");
        }
    }

    [Theory]
    [InlineData(
        PantsCloudProviderKind.AwsS3,
        PantsCloudCredentialKind.S3Static,
        typeof(PantsS3CredentialSource.StaticCredentials))]
    [InlineData(
        PantsCloudProviderKind.AwsS3,
        PantsCloudCredentialKind.S3Environment,
        typeof(PantsS3CredentialSource.Environment))]
    [InlineData(
        PantsCloudProviderKind.AwsS3,
        PantsCloudCredentialKind.S3SharedProfile,
        typeof(PantsS3CredentialSource.SharedProfile))]
    [InlineData(
        PantsCloudProviderKind.AwsS3,
        PantsCloudCredentialKind.AwsDefaultChain,
        typeof(PantsS3CredentialSource.AwsDefaultChain))]
    [InlineData(
        PantsCloudProviderKind.AzureBlob,
        PantsCloudCredentialKind.AzureSharedKey,
        typeof(PantsAzureCredentialSource.SharedKey))]
    [InlineData(
        PantsCloudProviderKind.AzureBlob,
        PantsCloudCredentialKind.AzureSasToken,
        typeof(PantsAzureCredentialSource.SasToken))]
    [InlineData(
        PantsCloudProviderKind.AzureBlob,
        PantsCloudCredentialKind.AzureConnectionString,
        typeof(PantsAzureCredentialSource.ConnectionString))]
    [InlineData(
        PantsCloudProviderKind.AzureBlob,
        PantsCloudCredentialKind.AzureStorageEnvironment,
        typeof(PantsAzureCredentialSource.StorageEnvironment))]
    [InlineData(
        PantsCloudProviderKind.AzureBlob,
        PantsCloudCredentialKind.AzureEnvironmentClientSecret,
        typeof(PantsAzureCredentialSource.EnvironmentClientSecret))]
    [InlineData(
        PantsCloudProviderKind.AzureBlob,
        PantsCloudCredentialKind.AzureWorkloadIdentity,
        typeof(PantsAzureCredentialSource.WorkloadIdentity))]
    [InlineData(
        PantsCloudProviderKind.AzureBlob,
        PantsCloudCredentialKind.AzureManagedIdentity,
        typeof(PantsAzureCredentialSource.ManagedIdentity))]
    [InlineData(
        PantsCloudProviderKind.AzureBlob,
        PantsCloudCredentialKind.AzureLightweightDefaultChain,
        typeof(PantsAzureCredentialSource.LightweightDefaultChain))]
    [InlineData(
        PantsCloudProviderKind.Gcs,
        PantsCloudCredentialKind.GcsBearerToken,
        typeof(PantsGcsCredentialSource.BearerToken))]
    [InlineData(
        PantsCloudProviderKind.Gcs,
        PantsCloudCredentialKind.GcsHmacKey,
        typeof(PantsGcsCredentialSource.HmacKey))]
    [InlineData(
        PantsCloudProviderKind.Gcs,
        PantsCloudCredentialKind.GcsApplicationDefault,
        typeof(PantsGcsCredentialSource.ApplicationDefault))]
    [InlineData(
        PantsCloudProviderKind.Gcs,
        PantsCloudCredentialKind.GcsServiceAccountJsonFile,
        typeof(PantsGcsCredentialSource.ServiceAccountJsonFile))]
    [InlineData(
        PantsCloudProviderKind.Gcs,
        PantsCloudCredentialKind.GcsAuthorizedUserJsonFile,
        typeof(PantsGcsCredentialSource.AuthorizedUserJsonFile))]
    [InlineData(
        PantsCloudProviderKind.Gcs,
        PantsCloudCredentialKind.GcsMetadataServer,
        typeof(PantsGcsCredentialSource.MetadataServer))]
    [InlineData(
        PantsCloudProviderKind.OciObjectStorage,
        PantsCloudCredentialKind.OciCustomerSecretKey,
        typeof(PantsOciCredentialSource.CustomerSecretKey))]
    [InlineData(
        PantsCloudProviderKind.OciObjectStorage,
        PantsCloudCredentialKind.OciEnvironment,
        typeof(PantsOciCredentialSource.Environment))]
    [InlineData(
        PantsCloudProviderKind.OciObjectStorage,
        PantsCloudCredentialKind.OciSharedProfile,
        typeof(PantsOciCredentialSource.SharedProfile))]
    public async Task AddPantsProjectsEveryCloudCredentialVariant(
        PantsCloudProviderKind providerKind,
        PantsCloudCredentialKind credentialKind,
        Type expectedCredentialType)
    {
        using var cache = new TemporaryDirectory();
        var providerOptions = CreateProviderOptions(providerKind);
        providerOptions.Credential = CreateCredentialOptions(credentialKind);
        if (credentialKind == PantsCloudCredentialKind.GcsHmacKey)
        {
            providerOptions.ApiStyle = PantsGcsApiStyle.Xml;
        }

        var databaseFactory = new CapturingPantsDatabaseFactory();
        var services = new ServiceCollection();
        services.AddSingleton<IPantsDatabaseFactory>(databaseFactory);
        services.AddPants().Configure(options =>
        {
            options.Storage = new PantsStorageOptions
            {
                Kind = PantsStorageKind.Cloud,
                Path = cache.Path,
                Cloud = new PantsCloudStorageOptions
                {
                    Shared = new PantsCloudLocationOptions
                    {
                        Prefix = "database/",
                        Provider = providerOptions
                    }
                }
            };
        });

        await using var serviceProvider = services.BuildServiceProvider();
        serviceProvider.GetRequiredService<IStartupValidator>().Validate();
        _ = await serviceProvider
            .GetRequiredService<IPantsDatabaseProvider>()
            .GetDatabaseAsync();

        var provider = Assert.IsType<PantsStorageConfiguration.Cloud>(
            databaseFactory.Options!.Storage).Topology.Control.Provider;
        object credential = provider switch
        {
            PantsAwsS3Provider aws => aws.Credentials,
            PantsAzureBlobProvider azure => azure.Credential,
            PantsGcsProvider gcs => gcs.Credential,
            PantsOciObjectStorageProvider oci => oci.Credentials,
            _ => throw new InvalidOperationException("Unexpected provider configuration.")
        };
        Assert.Equal(expectedCredentialType, credential.GetType());
    }

    [Fact]
    public void AddPantsRejectsCredentialKindsFromAnotherProviderAtStartup()
    {
        var services = new ServiceCollection();
        services.AddPants().Configure(options =>
        {
            options.Storage = new PantsStorageOptions
            {
                Kind = PantsStorageKind.Cloud,
                Path = "cache",
                Cloud = new PantsCloudStorageOptions
                {
                    Shared = new PantsCloudLocationOptions
                    {
                        Prefix = "database/",
                        Provider = new PantsCloudProviderOptions
                        {
                            Kind = PantsCloudProviderKind.AzureBlob,
                            Account = "account",
                            Container = "container",
                            Credential = new PantsCloudCredentialOptions
                            {
                                Kind = PantsCloudCredentialKind.S3Environment
                            }
                        }
                    }
                }
            };
        });
        using var serviceProvider = services.BuildServiceProvider();

        var exception = Assert.Throws<OptionsValidationException>(() =>
            serviceProvider.GetRequiredService<IStartupValidator>().Validate());

        Assert.Contains("cannot be used with Azure Blob", exception.Message);
    }

    [Fact]
    public void AddPantsRejectsGcsHmacCredentialsWithJsonApiAtStartup()
    {
        var services = new ServiceCollection();
        services.AddPants().Configure(options =>
        {
            options.Storage = new PantsStorageOptions
            {
                Kind = PantsStorageKind.Cloud,
                Path = "cache",
                Cloud = new PantsCloudStorageOptions
                {
                    Shared = new PantsCloudLocationOptions
                    {
                        Prefix = "database/",
                        Provider = new PantsCloudProviderOptions
                        {
                            Kind = PantsCloudProviderKind.Gcs,
                            Bucket = "bucket",
                            ProjectId = "project",
                            ApiStyle = PantsGcsApiStyle.Json,
                            Credential = CreateCredentialOptions(
                                PantsCloudCredentialKind.GcsHmacKey)
                        }
                    }
                }
            };
        });
        using var serviceProvider = services.BuildServiceProvider();

        var exception = Assert.Throws<OptionsValidationException>(() =>
            serviceProvider.GetRequiredService<IStartupValidator>().Validate());

        Assert.Contains("HMAC credentials require the XML API style", exception.Message);
    }

    [Fact]
    public async Task BoundSettingsAreProjectedOnlyWhenTheDatabaseFirstOpens()
    {
        var configuration = new ConfigurationManager
        {
            ["Pants:PerformanceGoal"] = "Latency"
        };
        var databaseFactory = new CapturingPantsDatabaseFactory();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IPantsDatabaseFactory>(databaseFactory);
        services.AddPants().BindConfiguration("Pants");

        await using var serviceProvider = services.BuildServiceProvider();
        var provider = serviceProvider.GetRequiredService<IPantsDatabaseProvider>();
        var first = await provider.GetDatabaseAsync();
        configuration["Pants:PerformanceGoal"] = "Economy";
        ((IConfigurationRoot)configuration).Reload();
        var second = await provider.GetDatabaseAsync();

        Assert.Same(first, second);
        Assert.Equal(1, databaseFactory.OpenCount);
        Assert.Equal(
            PantsPerformanceGoal.Latency,
            databaseFactory.Options!.Runtime.PerformanceGoal);
        Assert.Equal(
            PantsPerformanceGoal.Economy,
            serviceProvider
                .GetRequiredService<IOptionsMonitor<PantsDatabaseOptions>>()
                .CurrentValue
                .PerformanceGoal);
    }

    static PantsCloudProviderOptions CreateProviderOptions(PantsCloudProviderKind kind) =>
        kind switch
        {
            PantsCloudProviderKind.AwsS3 => new PantsCloudProviderOptions
            {
                Kind = kind,
                Bucket = "bucket",
                Region = "us-east-1"
            },
            PantsCloudProviderKind.S3Compatible => new PantsCloudProviderOptions
            {
                Kind = kind,
                Bucket = "bucket",
                Region = "us-east-1",
                Endpoint = new Uri("https://objects.example.test")
            },
            PantsCloudProviderKind.AzureBlob => new PantsCloudProviderOptions
            {
                Kind = kind,
                Account = "account",
                Container = "container"
            },
            PantsCloudProviderKind.Gcs => new PantsCloudProviderOptions
            {
                Kind = kind,
                Bucket = "bucket",
                ProjectId = "project"
            },
            PantsCloudProviderKind.OciObjectStorage => new PantsCloudProviderOptions
            {
                Kind = kind,
                Namespace = "namespace",
                Bucket = "bucket",
                Region = "us-ashburn-1",
                Credential = new PantsCloudCredentialOptions
                {
                    Kind = PantsCloudCredentialKind.OciEnvironment
                }
            },
            _ => throw new InvalidOperationException("Unexpected provider kind.")
        };

    static PantsCloudCredentialOptions CreateCredentialOptions(
        PantsCloudCredentialKind kind) => new()
        {
            Kind = kind,
            AccessKey = "access-key",
            SecretKey = "secret-key",
            Profile = "profile",
            CredentialsFile = "credentials",
            ConfigFile = "config",
            AccountKey = "account-key",
            Token = "token",
            ConnectionString = "connection-string",
            TenantId = "tenant-id",
            ClientId = "client-id",
            TokenFile = "token-file",
            AccessId = "access-id",
            Secret = "secret",
            Path = "credentials.json"
        };
}

using Cntryl.Pants.Cloud;
using Cntryl.Pants.DependencyInjection.Options;
using Cntryl.Pants.Storage;

namespace Cntryl.Pants.DependencyInjection.Options.Internal;

static class PantsDatabaseOptionsMapper
{
    public static PantsOpenOptions Create(PantsDatabaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Storage);

        if (options.MemtableSizeLimitBytes is null &&
            options.MemtableFlushThresholdBytes is not null)
        {
            throw new InvalidOperationException(
                "A memtable flush threshold requires a memtable size limit.");
        }

        return PantsOpenOptions.FromSettings(
            CreateStorage(options.Storage),
            options.PerformanceGoal,
            options.MemoryBudgetBytes is { } memoryBudgetBytes
                ? PantsMemoryBudget.FromBytes(memoryBudgetBytes)
                : PantsMemoryBudget.Auto,
            options.WorkloadProfile,
            options.RecoveryPolicy,
            options.BlockCachePolicy,
            CreateCloudWritePolicy(options.CloudWritePolicy),
            options.StorageTimeout,
            options.ShutdownTimeout,
            options.BackgroundCompaction,
            options.MemtableSizeLimitBytes,
            options.MemtableFlushThresholdBytes,
            options.TransactionMemoryPoolBytes,
            options.WalBufferSizeBytes,
            options.LeaseClockSkewTolerance,
            CreateCompaction(options.Compaction),
            options.MinimumEpoch);
    }

    static PantsStorageConfiguration CreateStorage(PantsStorageOptions options) =>
        options.Kind switch
        {
            PantsStorageKind.InMemory => new PantsStorageConfiguration.InMemory(),
            PantsStorageKind.Local => new PantsStorageConfiguration.Local(
                RequireText(options.Path, "The local storage path must not be empty.")),
            PantsStorageKind.SimulatedCloud => CreateSimulatedCloud(options),
            PantsStorageKind.Cloud => CreateCloud(options),
            _ => throw new InvalidOperationException("The storage kind is invalid.")
        };

    static PantsStorageConfiguration.SimulatedCloud CreateSimulatedCloud(
        PantsStorageOptions options)
    {
        var simulated = options.SimulatedCloud ?? throw new InvalidOperationException(
            "Simulated-cloud settings are required for simulated-cloud storage.");
        return new PantsStorageConfiguration.SimulatedCloud(
            RequireText(options.Path, "The simulated-cloud local cache path must not be empty."),
            RequireText(simulated.Bucket, "The simulated-cloud bucket must not be empty."),
            simulated.Prefix ?? throw new InvalidOperationException(
                "The simulated-cloud prefix must not be null."),
            simulated.LocalStorageBudgetBytes);
    }

    static PantsStorageConfiguration.Cloud CreateCloud(PantsStorageOptions options)
    {
        var cloud = options.Cloud ?? throw new InvalidOperationException(
            "Cloud settings are required for cloud storage.");
        PantsCloudStorageLocation? shared = cloud.Shared is null
            ? null
            : CreateLocation(cloud.Shared);
        var wal = cloud.Wal is null ? shared : CreateLocation(cloud.Wal);
        var sst = cloud.Sst is null ? shared : CreateLocation(cloud.Sst);
        var control = cloud.Control is null ? shared : CreateLocation(cloud.Control);
        if (wal is null || sst is null || control is null)
        {
            throw new InvalidOperationException(
                "Cloud storage requires a shared location or explicit WAL, SST, and control locations.");
        }

        return new PantsStorageConfiguration.Cloud(
            RequireText(options.Path, "The cloud local cache path must not be empty."),
            new PantsCloudStorageTopology(wal, sst, control));
    }

    static PantsCloudStorageLocation CreateLocation(PantsCloudLocationOptions options) =>
        new(
            CreateProvider(options.Provider ?? throw new InvalidOperationException(
                "A cloud location provider is required.")),
            options.Prefix ?? throw new InvalidOperationException(
                "A cloud location prefix must not be null."));

    static PantsCloudProviderConfiguration CreateProvider(PantsCloudProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options.Credential);
        return options.Kind switch
        {
            PantsCloudProviderKind.AwsS3 => new PantsCloudProviderConfiguration.AwsS3(
                RequireText(options.Bucket, "The AWS S3 bucket must not be empty."),
                RequireText(options.Region, "The AWS region must not be empty."),
                CreateS3Credential(options.Credential)),
            PantsCloudProviderKind.S3Compatible =>
                new PantsCloudProviderConfiguration.S3Compatible(
                    RequireText(options.Bucket, "The S3-compatible bucket must not be empty."),
                    RequireText(options.Region, "The S3-compatible region must not be empty."),
                    options.Endpoint ?? throw new InvalidOperationException(
                        "The S3-compatible endpoint is required."),
                    options.PathStyle,
                    CreateS3Credential(options.Credential)),
            PantsCloudProviderKind.AzureBlob => new PantsCloudProviderConfiguration.AzureBlob(
                RequireText(options.Account, "The Azure account must not be empty."),
                RequireText(options.Container, "The Azure container must not be empty."),
                options.Endpoint,
                CreateAzureCredential(options.Credential)),
            PantsCloudProviderKind.Gcs => CreateGcsProvider(options),
            _ => throw new InvalidOperationException("The cloud provider kind is invalid.")
        };

        static PantsCloudProviderConfiguration.Gcs CreateGcsProvider(
            PantsCloudProviderOptions options)
        {
            if (options.Credential.Kind == PantsCloudCredentialKind.GcsHmacKey &&
                options.ApiStyle != PantsGcsApiStyle.Xml)
            {
                throw new InvalidOperationException(
                    "GCS HMAC credentials require the XML API style.");
            }

            return new PantsCloudProviderConfiguration.Gcs(
                RequireText(options.Bucket, "The GCS bucket must not be empty."),
                RequireText(options.ProjectId, "The GCS project ID must not be empty."),
                options.Endpoint,
                options.ApiStyle,
                CreateGcsCredential(options.Credential));
        }
    }

    static PantsS3CredentialSource CreateS3Credential(PantsCloudCredentialOptions options) =>
        options.Kind switch
        {
            PantsCloudCredentialKind.Default or PantsCloudCredentialKind.AwsDefaultChain =>
                new PantsS3CredentialSource.AwsDefaultChain(),
            PantsCloudCredentialKind.S3Environment => new PantsS3CredentialSource.Environment(),
            PantsCloudCredentialKind.S3SharedProfile => new PantsS3CredentialSource.SharedProfile(
                options.Profile,
                options.CredentialsFile,
                options.ConfigFile),
            PantsCloudCredentialKind.S3Static => new PantsS3CredentialSource.StaticCredentials(
                RequireText(options.AccessKey, "The S3 access key must not be empty."),
                RequireText(options.SecretKey, "The S3 secret key must not be empty."),
                options.SessionToken),
            _ => throw InvalidCredential(options.Kind, "S3")
        };

    static PantsAzureCredentialSource CreateAzureCredential(PantsCloudCredentialOptions options) =>
        options.Kind switch
        {
            PantsCloudCredentialKind.Default or
                PantsCloudCredentialKind.AzureLightweightDefaultChain =>
                new PantsAzureCredentialSource.LightweightDefaultChain(),
            PantsCloudCredentialKind.AzureSharedKey => new PantsAzureCredentialSource.SharedKey(
                RequireText(options.AccountKey, "The Azure account key must not be empty.")),
            PantsCloudCredentialKind.AzureSasToken => new PantsAzureCredentialSource.SasToken(
                RequireText(options.Token, "The Azure SAS token must not be empty.")),
            PantsCloudCredentialKind.AzureConnectionString =>
                new PantsAzureCredentialSource.ConnectionString(
                    RequireText(
                        options.ConnectionString,
                        "The Azure connection string must not be empty.")),
            PantsCloudCredentialKind.AzureStorageEnvironment =>
                new PantsAzureCredentialSource.StorageEnvironment(),
            PantsCloudCredentialKind.AzureEnvironmentClientSecret =>
                new PantsAzureCredentialSource.EnvironmentClientSecret(),
            PantsCloudCredentialKind.AzureWorkloadIdentity =>
                new PantsAzureCredentialSource.WorkloadIdentity(
                    options.TenantId,
                    options.ClientId,
                    options.TokenFile),
            PantsCloudCredentialKind.AzureManagedIdentity =>
                new PantsAzureCredentialSource.ManagedIdentity(options.ClientId),
            _ => throw InvalidCredential(options.Kind, "Azure Blob")
        };

    static PantsGcsCredentialSource CreateGcsCredential(PantsCloudCredentialOptions options) =>
        options.Kind switch
        {
            PantsCloudCredentialKind.Default or
                PantsCloudCredentialKind.GcsApplicationDefault =>
                new PantsGcsCredentialSource.ApplicationDefault(),
            PantsCloudCredentialKind.GcsBearerToken => new PantsGcsCredentialSource.BearerToken(
                RequireText(options.Token, "The GCS bearer token must not be empty.")),
            PantsCloudCredentialKind.GcsHmacKey => new PantsGcsCredentialSource.HmacKey(
                RequireText(options.AccessId, "The GCS HMAC access ID must not be empty."),
                RequireText(options.Secret, "The GCS HMAC secret must not be empty.")),
            PantsCloudCredentialKind.GcsServiceAccountJsonFile =>
                new PantsGcsCredentialSource.ServiceAccountJsonFile(
                    RequireText(options.Path, "The GCS service-account path must not be empty.")),
            PantsCloudCredentialKind.GcsAuthorizedUserJsonFile =>
                new PantsGcsCredentialSource.AuthorizedUserJsonFile(
                    RequireText(options.Path, "The GCS authorized-user path must not be empty.")),
            PantsCloudCredentialKind.GcsMetadataServer =>
                new PantsGcsCredentialSource.MetadataServer(),
            _ => throw InvalidCredential(options.Kind, "GCS")
        };

    static PantsCloudWritePolicy? CreateCloudWritePolicy(PantsCloudWriteOptions? options) =>
        options is null
            ? null
            : new PantsCloudWritePolicy(
                options.EventualFlushSegmentGap,
                options.WalSealMinimumSegmentBytes,
                options.WalSealMaximumFlushDelay,
                options.WalSealMaximumPendingWrites);

    static PantsCompactionConfiguration? CreateCompaction(PantsCompactionOptions? options) =>
        options is null
            ? null
            : new PantsCompactionConfiguration(
                options.L0SizeTriggerBytes,
                options.L0FileCountTrigger,
                options.MaximumInputFiles,
                options.LevelMultiplier,
                options.L1TargetSizeBytes,
                options.MaximumLevels,
                options.TargetSstSizeBytes);

    static InvalidOperationException InvalidCredential(
        PantsCloudCredentialKind kind,
        string provider) =>
        new($"Credential kind '{kind}' cannot be used with {provider}.");

    static string RequireText(string? value, string message)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(message);
        }

        return value;
    }
}

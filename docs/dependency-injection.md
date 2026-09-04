# Dependency injection configuration

`Cntryl.Pants.DependencyInjection` supports both constructed `PantsOpenOptions` and the standard
`Microsoft.Extensions.Options` configuration pipeline. The options pipeline is useful for .NET
hosts that read configuration from JSON, environment variables, user secrets, or another
configuration provider.

## Bind one database

Register `IConfiguration` through the host, then bind a section:

```csharp
services.AddPants().BindConfiguration("Pants");
```

```json
{
  "Pants": {
    "Storage": {
      "Kind": "Local",
      "Path": "data/catalog"
    },
    "PerformanceGoal": "Latency",
    "MemoryBudgetBytes": 536870912,
    "WorkloadProfile": "ReadMostly",
    "RecoveryPolicy": "Strict",
    "BlockCachePolicy": "Lru",
    "StorageTimeout": "00:00:30",
    "RuntimeResponseTimeout": "00:01:00",
    "ShutdownTimeout": "00:00:30",
    "BackgroundCompaction": true
  }
}
```

`AddPants()` returns `OptionsBuilder<PantsDatabaseOptions>`, so callers can use the standard
`Bind`, `BindConfiguration`, `Configure`, and `PostConfigure` APIs. Pants registers an
`IValidateOptions<PantsDatabaseOptions>` validator and enables `ValidateOnStart` automatically.
Invalid settings therefore fail host startup and also fail before a database can open in a plain
service collection.

Programmatic configuration uses the same model:

```csharp
using Cntryl.Pants.DependencyInjection.Options;

services.AddPants().Configure(options =>
{
    options.Storage.Kind = PantsStorageKind.Local;
    options.Storage.Path = "data/catalog";
    options.PerformanceGoal = PantsPerformanceGoal.Throughput;
});
```

The existing `AddPants(PantsOpenOptions)` and service-provider factory overloads remain available
for applications that construct immutable engine options directly.

`StorageTimeout` bounds an individual storage/provider operation. `RuntimeResponseTimeout` bounds
the caller's wait after the single-threaded runtime has admitted a command and must be strictly
greater than `StorageTimeout`. If omitted, Pants derives it as the larger of 60 seconds and
`StorageTimeout + 30 seconds`. A runtime-response timeout is outcome-unknown: accepted work remains
owned by Pants and can still complete, so callers must not assume a timed-out mutation failed or
retry it blindly.

## Bind multiple databases

Use a string service key. The same string is used as the named-options key:

```csharp
services.AddKeyedPants("catalog").BindConfiguration("Pants:Catalog");
services.AddKeyedPants("sessions").BindConfiguration("Pants:Sessions");
```

Resolve each provider through keyed DI:

```csharp
var catalog = serviceProvider.GetRequiredKeyedService<IPantsDatabaseProvider>("catalog");
var database = await catalog.GetDatabaseAsync(cancellationToken);
```

## Storage settings

`Storage:Kind` accepts `InMemory`, `Local`, `SimulatedCloud`, or `Cloud`.

Local storage requires `Storage:Path`. Simulated cloud also requires a nested section:

```json
{
  "Storage": {
    "Kind": "SimulatedCloud",
    "Path": "data/cache",
    "SimulatedCloud": {
      "Bucket": "catalog-test",
      "Prefix": "database/",
      "LocalStorageBudgetBytes": 1073741824
    }
  }
}
```

Provider-backed cloud storage accepts one shared location or separate `Wal`, `Sst`, and `Control`
locations. Explicit locations override the shared location for that object class:

```json
{
  "Storage": {
    "Kind": "Cloud",
    "Path": "data/cache",
    "Cloud": {
      "Shared": {
        "Prefix": "catalog/",
        "Provider": {
          "Kind": "AwsS3",
          "Bucket": "catalog-production",
          "Region": "us-east-1",
          "Credential": {
            "Kind": "AwsDefaultChain"
          }
        }
      }
    }
  }
}
```

Cloud provider kinds are `AwsS3`, `S3Compatible`, `AzureBlob`, and `Gcs`. Credential kinds are
provider-specific and validated accordingly:

- S3: `S3Static`, `S3Environment`, `S3SharedProfile`, `AwsDefaultChain`.
- Azure Blob: `AzureSharedKey`, `AzureSasToken`, `AzureConnectionString`,
  `AzureStorageEnvironment`, `AzureEnvironmentClientSecret`, `AzureWorkloadIdentity`,
  `AzureManagedIdentity`, `AzureLightweightDefaultChain`.
- GCS: `GcsBearerToken`, `GcsHmacKey`, `GcsApplicationDefault`,
  `GcsServiceAccountJsonFile`, `GcsAuthorizedUserJsonFile`, `GcsMetadataServer`.

`Default` selects `AwsDefaultChain`, `AzureLightweightDefaultChain`, or `GcsApplicationDefault`
according to the provider. Prefer default chains, workload identity, or managed identity over
static secrets. If static credentials are necessary, supply them through a secret-aware
configuration provider rather than a checked-in JSON file.

## Lifetime behavior

The options monitor can observe configuration-source changes, but an open database is not
reconfigured. `IPantsDatabaseProvider` projects the current settings once on its first
`GetDatabaseAsync` call and owns that database for the service provider's lifetime. Storage,
memory, lease, and recovery settings are therefore stable for the database lifetime.

Callbacks and clocks remain part of direct `PantsOpenOptions` construction because they are
runtime collaborators rather than bindable settings.

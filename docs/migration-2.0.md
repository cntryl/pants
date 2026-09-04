# Migrating to Pants 2.0

Pants 2.0 separates the public database contract into focused capability facets and replaces the
closed cloud-provider configuration union with an extensible provider SPI. Persisted Midge FORMAT,
WAL, SST, manifest, and lease formats are unchanged.

## Database capabilities

Operations now live on a facet that describes the capability they require:

| Pants 1.x | Pants 2.0 |
| --- | --- |
| `database.DefaultColumnFamily` | `database.ColumnFamilies.DefaultFamily` |
| `database.CreateColumnFamilyAsync(...)` | `database.ColumnFamilies.CreateAsync(...)` |
| `database.GetColumnFamilyAsync(...)` | `database.ColumnFamilies.GetAsync(...)` |
| `database.ListColumnFamiliesAsync(...)` | `database.ColumnFamilies.ListAsync(...)` |
| `database.DropColumnFamilyAsync(...)` | `database.ColumnFamilies.DropAsync(...)` |
| `database.BeginTransactionAsync(...)` | `database.Transactions.BeginAsync(...)` |
| `database.FlushAsync(...)` | `database.Maintenance.FlushAsync(...)` |
| `database.CompactAsync(...)` | `database.Maintenance.CompactAllAsync(...)` |
| `database.GetRuntimeMetricsAsync(...)` | `database.Diagnostics.GetRuntimeMetricsAsync(...)` |
| `database.VerifyStorageAsync(...)` | `database.PersistentStorage!.VerifyAsync(...)` |
| `database.IsPrimaryLeaseHealthy` | `database.PersistentStorage!.IsPrimaryLeaseHealthy` |

Use `Capabilities`, `PersistentStorage`, and `Cloud` instead of invoking an operation and catching
`PantsNotSupportedException`. In-memory databases return `null` for `PersistentStorage`; local
databases return `null` for `Cloud`.

## Grouped options

`PantsOpenOptions` exposes immutable `Runtime`, `Memory`, `Lease`, and `Compaction` groups. The
existing fluent methods remain convenient for incremental construction. For a single declarative
construction, use `PantsOpenOptions.Create`:

```csharp
var options = PantsOpenOptions.Create(
    new PantsStorageConfiguration.Local("data/catalog"),
    runtime: PantsRuntimeConfiguration.Default with
    {
        PerformanceGoal = PantsPerformanceGoal.Throughput
    },
    memory: PantsMemoryConfiguration.Default with
    {
        Budget = PantsMemoryBudget.FromBytes(512L * 1024 * 1024)
    });

PantsOpenOptionsValidator.Validate(options);
```

Options contain requested configuration only. Core derives memory pools, block sizes, runtime
deadlines, and other executable policy when validating or opening the database.

## Dependency injection namespaces

The dependency-injection package now follows the repository-wide `Cntryl.Pants` root namespace.
Registration extensions, `IPantsDatabaseFactory`, and `IPantsDatabaseProvider` are in
`Cntryl.Pants`; bindable option types moved from `Cntryl.Pants.DependencyInjection.Options` to
`Cntryl.Pants.Options`.

## Cloud providers

Built-in provider records are now exported by `Cntryl.Pants.Core`:

- `PantsAwsS3Provider`
- `PantsS3CompatibleProvider`
- `PantsAzureBlobProvider`
- `PantsGcsProvider`
- `PantsOciObjectStorageProvider`

Each implements `IPantsCloudProvider`. A custom provider can implement that interface and return an
`IPantsCloudObjectStore` from `OpenObjectStoreAsync`. The SPI deliberately exposes conditional
object operations only; provider extensions do not own Pants persistence formats or fencing.

Provider object stores are asynchronously disposed after shutdown and on every failed-open path.
Implementations should make `DisposeAsync` idempotent.

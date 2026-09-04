# Pants

Pants is an embedded transactional database for .NET 10. It provides an idiomatic async API, snapshot reads, atomic
writes, column families, range scans, time-to-live values, and local or cloud-backed persistence.

Pants is a good fit when an application needs to own its data without running a separate database service.

## Install

Add the core package:

```console
dotnet add package Cntryl.Pants.Core
```

Libraries that only need to expose Pants contracts can instead reference
`Cntryl.Pants.Abstractions` without taking a dependency on the storage engine.

For Microsoft dependency injection, also add:

```console
dotnet add package Cntryl.Pants.DependencyInjection
```

Upgrading from 1.x? See the [Pants 2.0 migration guide](docs/migration-2.0.md).

## Quick start

Open a local database, write a value, and read it back:

```csharp
using Cntryl.Pants;
using Cntryl.Pants.Transactions;

await using var database = await PantsDatabase.OpenAsync(
    PantsOpenOptions.Local("data/catalog"));

var products = database.ColumnFamilies.DefaultFamily;

await using (var transaction = await database.Transactions.BeginAsync(
    products,
    PantsTransactionMode.ReadWrite))
{
    transaction.Put("product:42"u8.ToArray(), "Coffee"u8.ToArray());
    await transaction.CommitAsync(PantsWriteOptions.Sync);
}

await using (var transaction = await database.Transactions.BeginAsync(
    products,
    PantsTransactionMode.ReadOnly))
{
    var value = await transaction.GetAsync("product:42"u8.ToArray());
    Console.WriteLine(value is null
        ? "Not found"
        : System.Text.Encoding.UTF8.GetString(value.Value.Span));
}
```

`PantsOpenOptions.Local` reopens the same database from the supplied directory. Use
`PantsOpenOptions.InMemory()` for tests, caches, and short-lived data.

## Transactions

Transactions operate on one column family and provide a consistent snapshot. Dispose every transaction with
`await using`.

```csharp
await using var transaction = await database.Transactions.BeginAsync(
    database.ColumnFamilies.DefaultFamily,
    PantsTransactionMode.ReadWrite,
    cancellationToken);

transaction.Put(key, value);
transaction.Insert(uniqueKey, value);
transaction.Delete(obsoleteKey);
transaction.DeleteRange(startInclusive, endExclusive);

await transaction.CommitAsync(PantsWriteOptions.Sync, cancellationToken);
```

- `Put` adds or replaces a value.
- `Insert` requires the key not to exist.
- `Delete` removes one key.
- `DeleteRange` removes keys in a half-open range.
- `AssertValue` adds a compare-and-set precondition to the transaction.
- `RollbackAsync` ends a transaction explicitly without committing it. Disposal also abandons an uncommitted
  transaction.

By default, concurrent writes use last-write-wins behavior. Request conflict detection when the application should retry
instead:

```csharp
transaction.SetConflictPolicy(PantsConflictPolicy.AbortOnWriteConflict);
```

A conflict raises `PantsWriteConflictException`.

### Durability

Choose durability when committing:

| Option                          | Use when                                                      |
|---------------------------------|---------------------------------------------------------------|
| `PantsWriteOptions.Sync`        | The commit must be durable on local storage before returning. |
| `PantsWriteOptions.Buffered`    | Buffered local durability is sufficient.                      |
| `PantsWriteOptions.BestEffort`  | The data may be lost if the process exits unexpectedly.       |
| `PantsWriteOptions.CloudAsync`  | Local acknowledgement may precede cloud persistence.          |
| `PantsWriteOptions.CloudStrict` | Cloud persistence must complete before returning.             |

Use `Sync` unless the application has made an explicit durability tradeoff. Cloud options require cloud-backed storage.

## Column families

Use column families to keep independent groups of keys in the same database:

```csharp
var sessions = await database.ColumnFamilies.CreateAsync("sessions", cancellationToken);
var existing = await database.ColumnFamilies.GetAsync("sessions", cancellationToken);
var all = await database.ColumnFamilies.ListAsync(cancellationToken);
```

Pass the selected column family to `Transactions.BeginAsync`. Dropping a column family is destructive:

```csharp
await database.ColumnFamilies.DropAsync(sessions, cancellationToken);
```

## Scans

Scans are ordered async streams over a transaction's snapshot:

```csharp
using Cntryl.Pants.Scan;

await using var transaction = await database.Transactions.BeginAsync(
    database.ColumnFamilies.DefaultFamily,
    PantsTransactionMode.ReadOnly,
    cancellationToken);

await using var scan = await transaction.ScanAsync(
    new PantsScanQuery
    {
        Prefix = "product:"u8.ToArray(),
        Direction = PantsScanDirection.Forward,
        Limit = 100
    },
    cancellationToken);

await foreach (var entry in scan.WithCancellation(cancellationToken))
{
    Console.WriteLine(System.Text.Encoding.UTF8.GetString(entry.Key.Span));
}
```

Use `StartInclusive` and `EndExclusive` for a bounded range, `Prefix` for prefix matching,
`Direction` for forward or reverse iteration, and `Limit` to cap the number of results.

## Expiring values

Pass a time-to-live to `Put` or `Insert`:

```csharp
transaction.Put(
    "session:abc"u8.ToArray(),
    sessionBytes,
    timeToLive: TimeSpan.FromHours(1));
```

Expired values are no longer returned by reads or scans.

## Configuration

Start with the defaults. Select a performance goal and workload profile only when they describe a known workload:

```csharp
using Cntryl.Pants.Storage;

var options = PantsOpenOptions.Local("data/catalog")
    .WithPerformanceGoal(PantsPerformanceGoal.Latency)
    .WithWorkloadProfile(PantsWorkloadProfile.ReadMostly)
    .WithMemoryBudget(PantsMemoryBudget.FromBytes(512L * 1024 * 1024));
```

Available performance goals are `Latency`, `Throughput`, and `Economy`. Workload profiles are
`Mixed`, `WriteHeavy`, `ReadMostly`, `RangeScan`, and `TtlHeavy`.

Options are immutable input. `PantsOpenOptionsValidator.Validate` or database open resolves them into a validated
runtime plan. Prefer the high-level goal, profile, and memory budget controls over low-level tuning. New code can
construct grouped options directly with `PantsOpenOptions.Create`.

## Dependency injection

Register a lazily opened database with the Microsoft dependency injection container:

```csharp
using Cntryl.Pants;
using Microsoft.Extensions.DependencyInjection;

services.AddPants(PantsOpenOptions.Local("data/catalog"));
```

Host-based applications can bind and validate settings through the standard options pattern:

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
    "WorkloadProfile": "ReadMostly"
  }
}
```

Bound settings are validated when the host starts and projected once into immutable
`PantsOpenOptions` when the database first opens. Use `AddKeyedPants("name")` for named options and multiple independent
databases. See [dependency injection configuration](docs/dependency-injection.md)
for the complete storage, cloud-provider, and credential shapes.

Resolve `IPantsDatabaseProvider` and open the shared database when it is first needed:

```csharp
public sealed class CatalogStore(IPantsDatabaseProvider databaseProvider)
{
    public ValueTask<IPantsDatabase> GetDatabaseAsync(
        CancellationToken cancellationToken = default) =>
        databaseProvider.GetDatabaseAsync(cancellationToken);
}
```

The service provider owns the database and closes it during asynchronous disposal.

## Cloud-backed storage

Pants supports Amazon S3, S3-compatible services, Azure Blob Storage, Google Cloud Storage, and first-class Oracle Cloud
Infrastructure Object Storage configuration through OCI's S3 Compatibility API. Cloud storage uses a local cache
directory and a provider location:

```csharp
using Cntryl.Pants.Cloud;

var location = new PantsCloudStorageLocation(
    new PantsAwsS3Provider(
        Bucket: "catalog-production",
        Region: "us-east-1",
        Credentials: new PantsS3CredentialSource.AwsDefaultChain()),
    Prefix: "pants/catalog");

var options = PantsOpenOptions.Cloud(
    localCachePath: "data/catalog-cache",
    location: location);

await using var database = await PantsDatabase.OpenAsync(options, cancellationToken);
```

Prefer environment, workload-identity, managed-identity, or default credential chains over static credentials. Use
`PantsOpenOptions.CloudMulti` only when WAL, SST, and control data need separate locations. Custom backends can
implement `IPantsCloudProvider` and the object-store primitive
`IPantsCloudObjectStore`; Pants continues to own its object layout, WAL/SST formats, leases, and fencing. Validate
configuration without I/O and optionally run a read-only provider preflight before opening;
see [cloud validation and preflight](docs/cloud-validation.md).

## Health and verification

Pants exposes snapshots suitable for health endpoints and metrics collection:

```csharp
var runtime = await database.Diagnostics.GetRuntimeMetricsAsync(cancellationToken);
var recovery = await database.Diagnostics.GetRecoveryMetricsAsync(cancellationToken);
var readAmplification = await database.Diagnostics.GetReadAmplificationMetricsAsync(cancellationToken);
```

Verify an open database:

```csharp
var report = await database.PersistentStorage!.VerifyAsync(
    timeout: TimeSpan.FromSeconds(30),
    cancellationToken);
```

Or verify a closed local database path:

```csharp
var report = await PantsDatabase.VerifyPathAsync("data/catalog", cancellationToken);
```

## Shutdown

Prefer `await using`, which closes the database during disposal. For applications with a bounded shutdown phase, call
`ShutdownAsync` explicitly:

```csharp
await database.ShutdownAsync(TimeSpan.FromSeconds(30), cancellationToken);
```

Stop accepting new work before shutdown and allow outstanding transactions to finish or be cancelled.

## Errors and cancellation

Public operations accept `CancellationToken`. Pants reports database failures through
`PantsException` subclasses, including:

- `PantsWriteConflictException` for a rejected concurrent write
- `PantsBusyException` and `PantsWriteStallException` for temporary pressure
- `PantsFencedException` and lease exceptions when a writer no longer owns the database
- `PantsCorruptionException` and `PantsRecoveryFailedException` for storage or recovery failures

Handle only errors the application can meaningfully recover from; let unexpected database failures reach normal
application diagnostics.

## Requirements

- .NET 10 or later
- one application writer per local database path
- asynchronous disposal of databases, transactions, and scans

## Development

Run the standard repository checks with `dotnet build` and `dotnet test`. Cloud provider behavior is qualified against
the pinned Sqrzl emulator; see
[Cloud provider qualification](docs/testing/cloud-provider-qualification.md) for the Compose and test commands.

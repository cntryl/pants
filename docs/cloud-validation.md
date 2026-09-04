# Cloud validation and preflight

Cloud provider records, locations, and topologies expose synchronous `Validate()` methods. The
result is an immutable `PantsCloudValidationReport` with stable provider, role, mode, check,
outcome, severity, and failure-kind values. Structural validation is deterministic: it does not
resolve credentials, read environment variables or files, issue network requests, or mutate any
provider state. `PantsOpenOptions` consumes the same validator and preserves exception-based invalid
configuration behavior.

```csharp
var report = topology.Validate();
if (!report.IsValid)
{
    // Findings contain redacted configuration failures.
}
```

`PreflightAsync` first performs structural validation and then checks each unique physical location
once. Repeated WAL, SST, and control locations share one result whose `Roles` identify every
consumer. One absolute deadline covers provider/credential resolution and all remote calls.

```csharp
var report = await topology.PreflightAsync(
    new PantsCloudPreflightOptions(TimeSpan.FromSeconds(15)),
    cancellationToken);
```

Preflight is read-only. It lists one namespace page, heads the first returned object when one is
available, and reads either one byte or an empty object. It never puts, replaces, deletes, acquires a
lease, or leaves a probe object. An empty namespace can be ready for reads while not fully verified;
HEAD and ranged-read findings are then `NotApplicable`. Failure kinds distinguish timeout,
authentication, authorization, not-found, endpoint/TLS, unsupported, and other provider failures
without copying provider response bodies or credential material into the report.

`IsValid` means only that configuration structure is accepted. `IsReady` means namespace listing
succeeded without an error. `IsFullyVerified` additionally means HEAD and the bounded read passed.
None of these values proves write authorization, conditional-write semantics, durability,
performance, or future availability. Use the Sqrzl provider qualification suite and storage
verification for those separate properties.

OCI is represented by `PantsCloudProviderConfiguration.OciObjectStorage` and
`PantsOciCredentialSource`. When no endpoint override is supplied, Pants derives
`https://{namespace}.compat.objectstorage.{region}.oraclecloud.com` and uses path-style S3 signing.
This is first-class configuration and transport support, not a claim of live OCI qualification;
Pants does not run qualification against live provider accounts.

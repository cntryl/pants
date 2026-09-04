# Cloud validation and preflight

Cloud provider records, locations, and topologies expose synchronous `Validate()` methods. The
result is an immutable `PantsCloudValidationReport` with stable provider, role, mode, check,
outcome, severity, and failure-kind values. Structural validation is deterministic: it does not
resolve credentials, read environment variables or files, issue network requests, or mutate any
provider state. `PantsOpenOptions` consumes the same validator and preserves exception-based invalid
configuration behavior.

Built-in providers implement the same `IPantsCloudProvider` SPI available to third-party
providers. Custom validation findings use a stable `PantsCloudProviderId`; live preflight and
runtime open obtain the provider's `IPantsCloudObjectStore` asynchronously and dispose it on both
success and failed-startup paths.

## Object response contracts

`IPantsCloudObjectStore.GetAsync` and `GetRangeAsync` must return bytes and their conditional
identity from the same read. A provider must not combine GET bytes with an independently observed
HEAD identity, even when both versions have the same length. `null` means the object is absent;
malformed observations must fail rather than masquerade as missing objects.

`PantsCloudObject` rejects null or whitespace-only versions, including record copies. Metadata
uses a supplied generation in preference to ETag, so GCS metadata and media ETags may differ at
one generation. Missing metadata identities fail at construction or when a modified record's
`Version` is consumed. These failures are `PantsIOException`; providers must supply a valid
conditional identity. There is no persisted-format or provider-interface signature change.

Built-in adapters inspect raw GET Content-Length declarations before buffering can normalize
them, then check the buffered body's length. Malformed, conflicting, or mismatched declarations
fail closed; an absent declaration, including on chunked responses, is allowed. Response disposal
and the existing operation deadline still cover buffering.

## Bounded range reads

Built-in adapters require HTTP 206 and a single `Content-Range` matching the requested byte
offset and length. Ignored ranges, wrong offsets, malformed range headers, and inconsistent
declared lengths fail before body consumption. An absent Content-Length and an unknown total
object length in Content-Range are supported.

Range bodies are read into a requested-length buffer, respecting the configured HttpClient
response-buffer limit. The reader consumes at most one extra byte to detect an oversized body;
it rejects truncated bodies and disposes responses on success, failure, or cancellation. The
operation deadline remains active through the end-of-stream check. Error bodies for ranged
requests are discarded without changing status-based failures or bounded retries. These bounds
describe reads from the HTTP response stream, not transport-internal buffering or total process
memory. Full-object GETs and uploads still buffer whole objects; this is not full-transfer streaming.

Third-party `IPantsCloudObjectStore` implementations must implement bounded `GetRangeAsync`
explicitly. Its default implementation now throws `PantsNotSupportedException` without calling
`GetAsync`. This is a behavioral compatibility change for providers that relied on the former
full-object fallback, not an interface-signature or persisted-format change. Preflight reports
unsupported ranged reads as an `Unsupported` failure when it can probe a non-empty object;
an empty namespace still cannot verify this capability. SST reads also fail without silently
downloading the full object.

## Validation and preflight

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

OCI is represented by `PantsOciObjectStorageProvider` and
`PantsOciCredentialSource`. When no endpoint override is supplied, Pants derives
`https://{namespace}.compat.objectstorage.{region}.oraclecloud.com` and uses path-style S3 signing.
This is first-class configuration and transport support, not a claim of live OCI qualification;
Pants does not run qualification against live provider accounts.

# Midge metadata accounting and recovery-progress audit

This engine slice uses Midge 0.5.2
`c684310d9d8ce8ecd1151c47151d4bea98b348cf`, its
`docs/development/invariant-catalog.md`, and the production sources named below.
It was developed from Pants `99b33fd86adef4aa8998129f34bd6a2e0a4ea950` in an
isolated worktree. No persisted format or public diagnostics interface changed.
ACCT-6 campaign qualification and complete behavioral/performance parity remain
separate work.

## Accounting ownership and operation integrity

`Storage/Internal/Accounting` supplies one runtime owner, a counter-only retained
handle, immutable snapshots, and fixed-size operation ledgers. There are eighteen
origin/medium buckets, twenty-five counters, eighty latency bins and sixty-four
committed-SST size bins. Snapshot storage does not grow with operation history.
The owner also holds at most 1,024 pending synchronous publication receipts;
capacity exhaustion invalidates accounting without rejecting storage work. The
retained handle contains no pending receipts, filesystem, callback, runtime,
clock, native handle or writer lease.

An operation captures immutable origin, medium and monotonic start time. Its
single terminal fold records success, failure or abandonment, seals the ledger,
and releases its active gauge once. Delegated payload and namespace/barrier
observations after sealing permanently invalidate the owner's evidence. A held
error return is observed as well as a held successful return. Diagnostic folding
never holds its locks while delegated storage I/O executes.

Arithmetic saturates and records invalidity. Interval subtraction requires the
same owner and fixed bucket identities; it rejects overflow, late/incomplete
observations, decreasing counters and incomplete histogram coverage at either
endpoint. It subtracts bin counts before computing any percentile. A duration
beyond the final finite bin retains its final-bin count and sets accounting
invalidity, matching Midge's accounting-specific
`metadata/accounting.rs::LatencyHistogram::record`. Midge's general operator
histogram has different overflow handling.

The corresponding reference decisions are
`src/metadata/accounting.rs::{Owner, MetricsHandle, Ledger, Operation, Snapshot}`
and `src/common/latency_histogram.rs`. Pants exposes this evidence internally to
its existing friend test/benchmark assemblies, following Midge's
`internal-testing` checkpoint API rather than adding a public campaign API.

## Delegated bytes and semantic durability

`AtomicStagedFile` and `PositionalFile.AppendAndFlush` observe the actual delegated
write boundary. Issued bytes precede the call; successful write-return bytes are
credited only after that call returns successfully. Mutating opens, truncate by
replacement, rename, cleanup, file flush and parent-directory barriers are
observed on entry and completion/error, even for zero-byte payloads. Streamed
metadata writers use the observation's `WriteAsync` helper so each actual chunk
is counted. These are delegated API payloads and barriers, not physical device
I/O or independent power-loss proof.

`LocalDiskStore.DurablyApplyManifestEditCore` credits durable framed journal
bytes only after all required file and newly-created-directory barriers return.
`PersistManifestCheckpoint` distinguishes the snapshot's successful staged
replacement/barrier from the later journal reset/barriers. A durable snapshot
followed by reset failure has positive snapshot-durable evidence, a failed
operation and no checkpoint-complete credit. Recovery preserves that distinction
and the exact file contents. Intent-log writes and metadata repair payloads are
classified separately as other metadata operations; WAL/SST data payloads and
provider object-store network traffic are outside these manifest/intent counters.

Origins are required at the private metadata mutation boundaries. Normal local
flush, cloud flush, recovery, bootstrap, DDL, compaction-before-GC, administration
and newly forced shutdown work remain distinct. A store's medium and ordinary
flush origin are bound once; they are not inferred from an ambient shutdown flag
or provider wrapper. Pants filesystem bootstrap work is truthfully Persistent.
Provider first-generation metadata now has a separate immutable MemoryOnly
staging owner, described in the [bootstrap note](MidgeProviderBootstrap.md).
An in-memory Pants runtime currently has no delegated metadata filesystem writes.

Midge implements these decisions in `src/metadata/accounted_fs.rs`,
`src/metadata/store.rs`, `src/metadata/journal/publication.rs` and
`src/metadata/persistence.rs`. Its separate WAL ledger sync counters are not a
claim that these metadata counters cover all storage/device fsync traffic.

## Matching publication completion

The earlier focused correction removed successful flush credit from a physical
worker's `finally` block. The engine accounting slice additionally carries
publication-start/completion timestamps and immutable flush identity in the
worker result. Initial build/admission failures manufacture no publication
attempt. The first actual publication attempt starts after the initial SST
build. Matching runtime installation records the encoded committed output once,
with the original logical start preserved across accepted retries.

Synchronous cloud flushes retain bounded receipts containing SST identity,
encoded byte count, immutable origin/medium and the original start. Failed
mirroring retains those receipts through retry or shutdown. A newly forced
shutdown generation receives Shutdown origin; retrying an already accepted
CloudFlush retains CloudFlush origin and its clock. Unknown synchronous
publication outcomes invalidate evidence rather than inventing successful cost.

A successful cloud mirror returns `CloudMetadataPublicationReceipt` for its
actual captured, remotely published authoritative manifest. The receipt retains
the original admitted capture until synchronous installation/disposal, validates
the accounting owner, and exposes no escaping payload or iterator. The cloud
worker installs only matching name/family/SST-sequence/size/CRC identities from
that exact cut. Later local generations remain pending for a later cut. No
awaited actor command is introduced into the cloud-worker completion path.
Compaction credit similarly follows the matching successful publication result,
before optional input reclamation.

This follows Midge
`src/runtime/state/accounting.rs::FlushPublicationAccounting` and
`src/runtime/event_loop/flush_pipeline/publication.rs::install_flush_publication`.
The existing flat operator metrics remain distinct from the richer owner ledger;
synchronous flat flush timings retain their prior completed-worker semantics.

## Behavioral evidence and remaining boundaries

The native foundation first reproduced missing issued/returned credit with real
append bytes followed by sync failure, and with a real write followed by a held
delegated error. The engine foundation reproduced zero committed publication
credit for genuine flush/retry/reopen behavior before integration. Independent
cross-review then found a newer-local/older-shared-cut race: provider and
filesystem-simulated regressions each reported two committed SSTs while their
actual shared manifest contained only the older one. Exact-cut receipts correct
that race; the later SST is credited only after its own selected publication.

`Storage/Accounting` behavioral tests cover actual files and barriers, both late
success/error zero-payload returns, streamed writes and deletion, ledger sealing,
capacity/overflow/abandonment validity, histogram intervals, matching receipt
identity, retained owner lifetime through both real shutdowns and same-path
reopen, DDL/recursive compaction attribution, real streaming WAL recovery, and
cloud mirror failure retried during shutdown. Values are read after recovery;
no source/reflection shape tests substitute for behavior. Cloud snapshot and
metadata-admission regressions also cover the retained receipt's existing
shared-budget boundary. Final command/results are recorded with the isolated
patch qualification.

ACCT-6 measurement construction now lives in the existing `bench/Pants.Benches`
project: fixed nine-attempt selection, immutable ordinary-local Persistent
interval gates, warmup/final-query clock boundaries, original capped deadlines,
actual original/reopened-owner settlement, and local source/build/process/archive
provenance with retained native failures. The full nine workload attempts remain
unexecuted; this is an implemented correctness gate, not a performance or hosted
qualification claim. See [checkpoint measurement integrity](../performance/checkpoint-measurement-gate.md)
for exact construction, local trust limits and pending execution.

This pass does not establish every origin/failure schedule, escaped native
callback schedule, cross-process power-loss result, or native provider latency.
It does not treat manifest/intent counters as all filesystem/network/device
traffic. The separate provider bootstrap slice supplies the independently owned
MemoryOnly first-generation staging decision. Comparative performance claims
still require executed campaigns with the native and platform scopes stated.

PROG-1/2 and storage-independent startup ownership/deadlines are separate from
this accounting patch. Phase timing and aggregate WAL counters alone do not
provide completed-work events scoped to the active recovery caller and phase
identity; their implementation/qualification is recorded in the startup and
progress correctness work.

## Isolated qualification

The locked restore and Release solution build used
`--artifacts-path /tmp/pants-accounting-next-artifacts` with one MSBuild node;
the build completed with zero warnings and errors. The final focused accounting,
exact-cut mirror, metadata-admission and existing flush-counter gate passed
39 tests. The held-worker disposal regression first reproduced missing
incomplete evidence, then passed after owned teardown conservatively invalidated
remaining attempted generations. Its accepted worker retained the lease until
physical completion, and the exact value recovered after disposal.

Full formatting verification passed with the isolated MSBuild properties
`ArtifactsPath=/tmp/pants-accounting-next-artifacts Configuration=Release` and
`dotnet format Pants.slnx --verify-no-changes --no-restore`.
The first full run passed 3,244 tests and skipped two; fifteen Sqrzl provider/soak
cases failed because its default port 9000 had no emulator. Those prerequisite
failures are not accounting regressions. Qualification against the task-specific
Sqrzl endpoint uses `SQRZL_API_PORT=19900`; its final result is recorded separately.

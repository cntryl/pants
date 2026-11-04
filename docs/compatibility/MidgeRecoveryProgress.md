# Completed recovery work and caller scopes

This change compares PROG-1/PROG-2 with Midge
`c684310d9d8ce8ecd1151c47151d4bea98b348cf`. The implementation is based on
Pants `99b33fd86adef4aa8998129f34bd6a2e0a4ea950` in an isolated worktree.

## Producer contract

Each `Actor.OpenAsync` owns one internal `RecoveryWorkSink`. The sink is passed
explicitly to that open's local recovery walks and private provider recovery
read views. Closing the open's observation lifetime detaches its callback;
retained stores and remote views hold only an inert sink afterward. Recovery
observation does not own or cancel an engine, worker, lease or I/O request.

`RecoveryWorkRecorder` batches completed work at 64 KiB, 64 frames or 64
operations. A completion after 500 ms can report a smaller batch. There is no
timer or disposal callback: time passing, request admission, error cleanup and
an empty result cannot emit a batch. A successful bounded traversal explicitly
finishes its remaining batch. A tolerated incomplete active WAL tail finishes
the verified prefix; corruption does not finish an unreported batch.

| Stage | Completed unit |
| --- | --- |
| `manifest_snapshot` | A persisted framed snapshot has passed checksum, decode and manifest validation. |
| `family_frontiers` | Frontier rows from that successfully decoded persisted snapshot. New-store default state earns no credit. |
| `manifest_journal` | A complete journal frame passes framing, checksum, envelope and edit/marker validation. |
| `writer_epoch_frontiers` | A verified WAL record is accepted by the writer-epoch frontier walk. |
| `sealed_inspection` | The strict WAL inspection has decoded and observed a checksum-valid record. |
| `wal_replay` | A verified WAL record completes its recovery visitor. |
| `remote_read` | A bounded nonempty remote read returns its exact requested length and required identity. Startup control/catalog reads and WAL pages use private scoped views. |
| `crc_validation` | A successfully returned WAL page has been included in catalog CRC verification. |
| `sst_inventory` | A startup SST HEAD result successfully matches authoritative manifest size. A failed probe retained during salvage is not credited. |

Earlier completed batches remain truthful if a later unit fails. Progress is
evidence of completed bounded work; it does not assert that startup succeeded,
an edit became durable, or a transaction was acknowledged. Different verification
passes have separate stages and may process the same bytes again. These counters
are not unique recovered-byte counts or physical-device accounting.

`PantsDiagnostics.Meter` exposes additive instruments
`pants.recovery.completed_bytes`, `pants.recovery.completed_frames` and
`pants.recovery.completed_operations`. Their only tag is
`pants.recovery.stage`; caller IDs and phase generations are not metric tags.
Existing recovery-record metrics retain their existing meaning. With neither a
scoped observer nor enabled completed-work instruments, no walk recorder is
allocated.

## Consumer contract

The internal benchmark `RecoveryProgressTracker` consumes the same completed-work
observations as the tests. `WalReplayScalingSystemBenchmarks` supplies an explicit
scope callback to its exact open through existing internal runtime dependencies.
No public token or progress-scope API is added.

A scope accepts work only while it is the active recovery caller and its captured
phase generation remains current. Nested workload scopes suppress recovery
credit; restoring an outer scope works only while its generation remains valid.
Completed or stale scopes reject late work. Each scope has its own monotonic
idle clock, so a different caller or newer phase cannot refresh an old watchdog.
Async continuations remain eligible through the explicitly bound sink; ambient
execution context, thread IDs and unrelated background opens do not confer
ownership.

The consumer owns any no-progress timeout policy. Aggregate meter activity,
phase-duration disposal events and request counts must not reset a caller's
watchdog. Progress does not extend the absolute startup deadline. A caller that
stops waiting closes its observation scope while accepted physical work retains
its existing runtime ownership.

## Evidence and boundaries

The first real local WAL/family recovery regression failed because completed-work
measurements were absent. The tolerated active-tail regression independently
failed with zero `wal_replay` frames despite exact value recovery before prefix
finish handling was added.

Behavioral tests exercise real local WAL recovery, framed journal edits and
frontiers, catalog-published remote WAL with bounded reads and no staged local
WAL copy, validated SST inventory, exact recovered values and subsequent reopen.
Held, failed and empty ranges produce no completion for that request. Scope tests
use actual recoveries for unrelated/concurrent callers, nested workload scopes,
stale phases, ended scopes and late physical completions. A controlled clock
checks batching without an idle heartbeat and independent caller clocks.

This is in-process qualification with real filesystem formats and controlled
object-store adapters. It does not qualify a native provider deployment or an
external watchdog executable. Snapshot/frontier credit follows successful typed
snapshot decoding; it is not incremental progress during the underlying DOM
allocation. Not every ledger decode, digest pass, spool operation or startup
publication has a separate completed-work stage. Observer callbacks are internal
diagnostic consumers, and no new public cancellation or recovery-policy contract
is introduced.

## Local validation

Locked restore and Release artifacts used `/tmp/pants-progress-next-artifacts`.
The complete solution Release build passed with zero warnings/errors. The
focused recovery/provider filter passed **101/101** cases, and the final
observability/provider filter passed **167/167** cases after stage-tag assertions.
Full solution formatting verification and `git diff --check` passed. Formatting
used the same Release artifacts through the MSBuild `UseArtifactsOutput` and
`ArtifactsPath` environment properties, rather than unresolved default output
paths. No complete suite, native-provider or external-watchdog qualification is
claimed for this isolated feature patch.

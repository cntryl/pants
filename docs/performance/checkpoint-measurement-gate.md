# Checkpoint campaign measurement integrity

This implements ACCT-6 measurement construction in the existing
`bench/Pants.Benches` executable. It does not change engine checkpoint cadence,
public APIs, or performance policy. **The nine full workload attempts have not
been executed.** Reduced real-engine tests establish named correctness boundaries,
not throughput, latency, release readiness, or complete Midge performance parity.

The source reference is Midge `c684310d9d8ce8ecd1151c47151d4bea98b348cf`:
`benches/tier4_system_checkpoint_write_amplification.rs`,
`benches/bench_support/checkpoint_boundary.rs`, `checkpoint_commit.rs`,
`checkpoint_gate.rs`, and `examples/checkpoint_campaign_readback`.

## Fixed construction

| Cell | Workload | Cycles | Rows/cycle | Families | Warmup | Measured cycles |
|---|---|---:|---:|---:|---:|---:|
| A | `checkpoint_local_256x1mib_1cf` | 256 | 1,024 | 1 | 26 | 230 |
| B | `checkpoint_local_512x256kib_16cf` | 512 | 256 | 16 | 52 | 460 |
| C | `checkpoint_local_1024x64kib_1cf` | 1,024 | 64 | 1 | 103 | 921 |

Each cell has repeats 1, 2 and 3, producing exactly nine attempts. Each attempt
runs in its own process and fresh local database. Values are 1,024 deterministic
pseudorandom bytes; keys encode family and global ordinal. A cycle strictly
commits its exact rows and explicitly flushes that family. Options use Strict
recovery, a 512 MiB memory budget, a 32 MiB transaction pool, 8 MiB memtable limit
and flush threshold, and enabled background compaction. The resolved options,
actual flush latencies and completed compactions are retained.

One original cell deadline is captured before setup: 15 minutes. Only rejected
`WriteStall` commits are rebuilt and retried, within the smaller of the original
cell deadline and one original 30-second logical-commit allowance. Stall waits
are capped at one second and never count as commits. Busy, I/O, timeout,
outcome-unknown and other errors remain actual failures. Accepted operations
retain the engine's existing ownership and caller timeout semantics.

## Measurement boundaries

The sampler queries runtime metrics and then captures that same owner's immutable
metadata snapshot. It accepts only a sample with no active Persistent metadata
operations. This is not global runtime idleness or an atomic runtime barrier.
Each boundary uses the smaller of the original cell deadline and one original
30-second allowance, with pauses capped at one millisecond. Busy samples and
pauses never refresh either deadline. Even an idle response at/after expiry is
rejected. Typed query errors propagate unchanged.

Rejected warmup candidates and their waits are outside measurement. The clock
starts at the accepted warmup-boundary query's start, including that final query
and snapshot. Every end-boundary sample, pause and final capture remains inside
the measured clock. Before/after snapshots, their exact same-owner delta, runtime
samples and flush latencies form an immutable window. Later verification,
shutdown, recovery and reopened-owner costs cannot alter its ratios.

Progress records only acknowledged rows, completed flushes and verified batches
of 64 actual point/scan results. Sampling, status-file activity and stall waits do
not advance progress. The parent observes this genuine progress with a 60-second
watchdog and retains a failed native process outcome on exhaustion. A separate
17-minute process ceiling allows the original cell budget plus owned cleanup;
failed/killed databases are retained. Engine disposal still joins accepted work.

## Integrity gates

Ratios use only the `OrdinaryLocalFlush` / `Persistent` interval. Exact integer
arithmetic tests snapshot-issued bytes >=5% of committed SST bytes, or checkpoint
time >=20% of full publication time with checkpoint p95 lower bound >=5 ms.
Histogram bounds are preserved rather than reported as exact quantiles. A ratio
miss is meaningful only when the measurement is valid; invalid attempts have a
null miss and are never counted as misses.

Validity additionally requires the exact measured flush count, nonzero
publication bytes/time, genuine measured compaction, no unclassified measured
cost, complete histograms, and no Persistent operation failures, abandoned
operations, active boundary operations or checkpoint durability/completion gaps.
Overflow, late writes and incomplete observations invalidate evidence.

After actual owned shutdown, the original counter-only handle remains readable.
A distinct reopened owner verifies all point values and full scan order/count,
then also completes owned shutdown. Both settled owners are checked for metadata
integrity, including forced costs outside the measured interval. A later failure
withdraws provisional acceptance while leaving the measured ratios unchanged.
Diagnostic reduced workloads are explicitly marked and cannot qualify a fixed
campaign.

## Capture and readback

These commands are modes of the existing benchmark executable:

```text
checkpoint-campaign <fresh-artifact-root> <fresh-database-root> <source-root>
checkpoint-readback <artifact-root>
```

The internal `checkpoint-cell` mode selects exactly one registered workload and
repeat; it is launched by the campaign coordinator. All nine rows remain in
readback even when attempts are missing, skipped, failed, killed or malformed.
No invalid attempt can supply a miss or permit a policy change. The local gate
requires all nine valid attempts and at least two misses in one three-repeat cell.

A campaign captures one immutable Git commit, resolves its tree by that commit,
and archives that same commit even if HEAD later changes. A clean worktree and
an assembly version stamp are not treated as a source/build proof: a dirty binary
can still claim a reverted commit's version. Instead the coordinator extracts the
captured archive into a new private source directory, captures its SDK version,
performs a locked restore and optimized Release build from that directory, and
records the actual build process outcomes. The selected commit is explicitly
supplied as the SDK `SourceRevisionId` for consistency, not used as sole proof.

Both fixed-workload children and the authoritative readback executable come from
that fresh archived-source output. Their loaded project assembly hashes/module
IDs must match its build identity. The entire produced runtime file set, including
package/native assets, runtime configuration and debug symbols, is hash-bound;
OS/runtime installations and compiler/package caches are not independently
attested. A stale controller may collect diagnostics but cannot qualify its own
Gate/Readback implementation in place of the captured source's reader. The actual
fresh-reader PID, loaded build and native exit outcome are retained separately.

Capture also retains the package-lock digest, selector/repeat, command, PID,
actual native exit/error/termination outcome, each attempt ZIP digest, and exact
archived/extracted content hashes. Missing or failed archived-source builds,
changed runtime files, dirty selections and missing provenance remain invalid.
A bounded compiler fixture proves the routine dirty-B/revert-A/version-stamp trap:
the old binary executes B with A's stamp, while the fresh archived build executes
A even after HEAD advances to C. This probe is correctness evidence only; it does
not execute any of the nine full performance attempts.

Readback recomputes accounting deltas, histograms, boundary consistency, ratios
and owner settlement from captured runtime observations; it does not trust a
stored pass flag. Actual child failure diagnostics remain visible even when
other evidence is missing. Tests exercise runtime/artifact outcomes, not
repository files, documentation, workflows or reflected type conventions.

The trust class is **local captured source/build/process/archive evidence**.
Pants does not have Midge's `cntryl-stress` native receipt or hosted workflow
attestation. This implementation does not manufacture those receipts, statistical
trust diagnostics, signatures or successful hosted jobs. Artifacts are not an
independent cryptographic attestation of their producer. Companion artifacts
must be placed outside the source/database roots; a separate physical device or
tmpfs is not attested by this implementation. Metadata payload accounting is not
all filesystem, network or device traffic and is not device write amplification.

The full fixed campaign, hosted attestation, platform qualification and comparative
performance execution remain explicit future work. Scheduler/kernel delays and
accepted engine I/O retain their real ownership; watchdog termination is failed
evidence, not successful cancellation or proof of an unapplied mutation.

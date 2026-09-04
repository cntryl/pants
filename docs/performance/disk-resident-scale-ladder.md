# Disk-resident state: proof and optional scale qualification

Issue #219 closure is based on architectural and deterministic resource proofs. The scale-ladder
runner lives in
`bench/Pants.Benches/Tier4/ScaleLadderRunner.cs` and intentionally runs outside
BenchmarkDotNet's iteration machinery: each tier is a single production-shaped ingest,
measurement, reopen, and crash-recovery pass. Those long runs are optional operational
qualification; they are not a merge or issue-closure gate.

## Result status

The earlier 1 million and 10 million rows were removed after review found that they described a
one-entry-per-address workload, called OS-page-cache-warm reads "cold," sampled RSS after open
rather than throughout open, and omitted WAL bytes from write amplification. Those numbers must
not be used as qualification evidence.

No corrected large-tier result is checked in. Generate fresh reports when qualifying a release or
deployment environment, and record the machine, filesystem, provider, and operating-system cache
conditions with the result. Ordinary CI instead proves ownership, bounded buffers, failure
cleanup, differential correctness, and modest N/2N/4N scaling.

## Retained-memory bound

The engine-owned retained-memory bound is:

```text
2 x memtable limit
+ transaction pool
+ block cache
+ scan pool
+ compaction pool
+ bounded cloud buffers
+ manifest and live-reader metadata
```

The two memtables are the active generation and at most one generation being published. A
successful publication removes the covered generation from the current runtime root; an older
active snapshot may keep its immutable root alive until that snapshot is disposed. Transaction,
block-cache, scan, and compaction allocations have explicit capacities. Ranged cloud reads and
complete-file staging use bounded chunks. Manifests, SST indexes/blooms/range-tombstone metadata,
and live reader handles grow with the number of files, but published key/value payloads do not
remain in `RuntimeState.FamilyData`, recovery state, or newly-created snapshots.

Disk bytes and file metadata are expected to grow with the corpus. The proof is that retained
payload and enforced working pools stay within the same configured bounds while that happens.

## Address workload model

One base address creates four entries:

1. A primary `addr-id-*` record with a deterministic 150-byte address-shaped value.
2. A postal lookup index entry mapping to the 8-byte primary identifier.
3. A street lookup index entry mapping to the same identifier.
4. A locality lookup index entry mapping to the same identifier.

The report prints this `4 entries/base record` multiplier. Ingest throughput remains expressed
in base records per second. The write-amplification denominator is the exact logical key plus
value bytes submitted for all four entries, not only the 150-byte primary value.

The generated values are deterministic and have non-trivial entropy so compression does not
turn the corpus into an unrealistically small repeated-byte stream.

The former largest closure tier therefore extrapolates symbolically as:

```text
65.3 million base addresses x 4 entries/base address = 261.2 million entries
```

CI does not allocate those 261.2 million entries. The architecture is corpus-independent for
payload retention, and the N/2N/4N test checks that SST count/bytes grow while the owned-memory
invariants remain fixed.

## Measurement semantics

The runner records:

- Ingest throughput, wall time, final database size, and the address entry multiplier.
- Empty-database startup time/RSS as a baseline.
- Peak process RSS sampled every 5 ms throughout ingest plus background/final compaction. This
  is a conservative upper bound for compaction because it includes the surrounding ingest.
- Peak RSS sampled every 5 ms by the parent while a separate process opens and verifies the
  populated corpus under the same 256 MiB tier memory budget.
- Same-process clean-reopen time/RSS and representative primary-value verification.
- Compaction and scan buffer peak/capacity from their enforced `ResourceBudget` instances.
- Block-cache used/capacity and active/immutable memtable bytes.
- Physical WAL and SST bytes written by the database session. Write amplification is
  `(WAL bytes written + SST bytes written) / logical key+value bytes ingested`; it is not a
  final-directory-size ratio.
- Read amplification, compaction debt/failures, obsolete-file backlog, and write stalls.
- An abrupt child-process crash after acknowledged synchronous WAL commits, followed by lease
  expiry, reopen, WAL replay, and verification of every crash-check record.

### Latency labels

The first point/prefix pass is **block-cache-cold, with the OS page cache explicitly not reset**.
It must not be interpreted as cold-device latency. Portable unprivileged CI cannot reliably
evict the operating system's page cache, so the report uses the precise label rather than making
that stronger claim.

The warm pass replays the exact same point keys and prefix groups as the first pass. Every point
result is compared with its expected value. Prefix measurements use a real
`PantsScanQuery.Prefix` over a 1,000-entry postal group and verify the complete ordered key/value
sequence before retaining the latency sample.

## Correctness and process behavior

- A failed clean-reopen check, populated-corpus probe, or crash-recovery check makes the command
  exit nonzero.
- Reopen and crash children work when the benchmark is launched through either its apphost or
  `dotnet Cntryl.Pants.Benches.dll`.
- The populated reopen child receives the tier's exact memory budget.
- Timed-out children are killed as a process tree and awaited before cleanup proceeds.
- Invalid or non-positive record counts are usage errors and return exit code 2.

## Merge proof obligations

The ordinary test suite provides these closure gates:

- `RuntimeStateFlushReleaseTests` proves exact generation release, snapshot isolation, and that a
  released value payload becomes unreachable to the GC.
- `LocalDiskStoreBoundedRecoveryTests` and `PantsCloudDiskResidentReadTests` prove open reads SST
  metadata plus uncovered WAL without reconstructing published values in runtime snapshots.
- `PantsProviderCloudDiskResidentReadTests` proves provider cold point/prefix reads use ranges,
  reject replaced and timed-out reads, and clean cancelled complete-file staging.
- `PantsCloudDiskResidentFailureTests` proves missing, truncated, metadata-corrupt, and
  data-block-corrupt remote objects fail without local-cache pollution.
- `PantsMemtableAndCacheMetricsContractTests` and `PantsOwnedResourceScalingTests` prove scan and
  compaction `Peak <= Capacity`, `Used == 0` after disposal, and bounded retained payload across
  N/2N/4N corpora while SST partitions and bytes grow.
- `PantsDiskResidentDifferentialTests` compares local and simulated-cloud behavior with a fixed
  in-memory model across puts, inserts, deletes, range deletes, TTL advancement, snapshots,
  flushes, compactions, and clean reopen. Existing crash/WAL recovery, conflict, assertion,
  tombstone, TTL, and column-family-generation suites remain part of the same gate; the same
  fixed model is also checked after abrupt local-WAL and remote-WAL recovery.
- `PantsMemoryBoundedCorpusTests` is only a small subprocess RSS smoke test for gross
  instrumentation regressions. RSS is not the owned-memory proof.

Negative-control worktrees are used during review to selectively restore retained flush state,
whole-SST recovery, uncharged compaction blocks, and incorrect duplicate-version advancement.
Those mutations are never committed; the red/green commands and results belong in the PR.

## Boundaries

- Process RSS includes CLR/JIT/GC overhead and operating-system page cache. Pants does not own or
  cap the OS page cache, and the runner does not claim device-cold latency.
- Manifest file entries and live SST reader metadata grow with file count. Compaction is the
  operational control for excessive file-count/read-amplification growth; this is distinct from
  retaining the corpus's key/value payloads.
- Active snapshots intentionally retain the immutable roots and obsolete SST authority they saw.
  Snapshot age/count and pinned SSTs are observable; disposing the snapshot releases that
  authority.
- Open validates remote existence, length, footer, metadata, index, bloom, trie, and range
  metadata with bounded reads. A complete-object CRC is verified while streaming a whole object
  for cache/compaction admission. Otherwise, data-block CRC is checked when that block is read,
  so corruption isolated to an untouched remote data block is detected on access, not at open.
- An immutable remote object version is captured at source open and compared on every range.
  Replacement, truncation, cancellation, or timeout cannot admit a partial cache file.

## Running an optional tier

Build once, then run a tier:

```bash
dotnet build Pants.slnx --configuration Release
dotnet run --project bench/Pants.Benches/Cntryl.Pants.Benches.csproj \
  --configuration Release --no-build -- \
  scaleladder <base-record-count> docs/performance/scale-ladder-<base-record-count>.md
```

Examples:

```bash
dotnet run --project bench/Pants.Benches/Cntryl.Pants.Benches.csproj -c Release --no-build -- \
  scaleladder 1000000 docs/performance/scale-ladder-1000000.md

dotnet run --project bench/Pants.Benches/Cntryl.Pants.Benches.csproj -c Release --no-build -- \
  scaleladder 10000000 docs/performance/scale-ladder-10000000.md

dotnet run --project bench/Pants.Benches/Cntryl.Pants.Benches.csproj -c Release --no-build -- \
  scaleladder 65300000 docs/performance/scale-ladder-65300000.md
```

Treat single-run latency and RSS numbers as qualification observations, not stable benchmark
means. Compare populated-reopen peak and resource-budget metrics across tiers. Use the owned
resource, reachability, ranged-read, and differential tests above as the deterministic CI gate.

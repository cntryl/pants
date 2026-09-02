# Disk-resident state: scale-ladder qualification

Issue #219 requires scale-ladder evidence that Pants remains memory-bounded as the durable
corpus grows. The qualification runner lives in
`bench/Pants.Benches/Tier4/ScaleLadderRunner.cs` and intentionally runs outside
BenchmarkDotNet's iteration machinery: each tier is a single production-shaped ingest,
measurement, reopen, and crash-recovery pass.

## Result status

The earlier 1 million and 10 million rows were removed after review found that they described a
one-entry-per-address workload, called OS-page-cache-warm reads "cold," sampled RSS after open
rather than throughout open, and omitted WAL bytes from write amplification. Those numbers must
not be used as qualification evidence.

No corrected large-tier result is checked in yet. Generate fresh 1 million, 10 million, and
65.3 million base-record reports with the corrected runner before closing #219. The largest tier
is scheduled/manual; architecture and bounded-resource regressions remain part of ordinary CI.

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

## Running a tier

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
means. Compare the populated-reopen peak and resource-budget metrics across tiers, and use the
dedicated resource regression in `PantsMemoryBoundedCorpusTests` as the deterministic CI guard.

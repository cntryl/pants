# Current Midge engine reads

The current comparison source is Midge 0.5.2 at
`c684310d9d8ce8ecd1151c47151d4bea98b348cf`. Its `Cargo.lock` SHA-256 is
`6a497fab02a0dde008ae9029af4d877e8982bd0fa3113efe3c291bafd091dc88`,
with `cntryl-stress` 0.5.1. The older v0.1.1 aggregate configuration remains a
historical inventory; its pin does not qualify current performance.

## Workload

`EngineSystemBenchmarks.GetAsync` follows Midge's
`benches/tier3_system_engine.rs` for local and simulated-cloud storage:

- 4,096 rotating keys, each 16 bytes: big-endian u64 index followed by eight zeros.
- A separate `cf1` column family, with every value containing 128 bytes of `0x01`.
- Setup commits in batches of 64 puts using BestEffort durability, followed by an
  explicit family flush. Setup is outside the measured method.
- A 2 MiB memtable limit and flush threshold, with background compaction disabled.
- One read-only transaction and one point read per invocation, including checking
  the complete returned value and disposing the transaction.
- Post-run diagnostics require read-only transactions, candidate SST files and
  candidate blocks to have been exercised.

Pants uses its public asynchronous API; Midge uses its synchronous API. Their
default cache policies, runtime coordination and measurement harnesses differ.
BenchmarkDotNet's Dry job includes cold JIT/read effects and cannot be divided by
a Midge duration-sample result. Current measurements do not establish a
cross-engine ratio or complete performance parity.

## Allocation change

Pants previously copied a cached block and materialized all its keys, values and
entry objects for each point lookup. Point reads now borrow the immutable cached
block and use the shared decoder to validate every entry while retaining only
the requested key's versions and block boundaries. A preflight-bounded scratch
key avoids allocating each unrelated key. All retained results own their bytes.

Validation still covers the complete block, including entries after the selected
key, canonical varints and ordering, restart directories and continuation bits,
duplicate sequence content and resource admission. First/last entries remain
available for cross-block checks. Scans and full SST decoding retain every entry.

## Reproduce

From the Pants repository, run the lifecycle check:

```bash
dotnet run --project bench/Pants.Benches/Cntryl.Pants.Benches.csproj \
  --configuration Release --no-restore -- \
  --filter '*EngineSystemBenchmarks*' --job Dry \
  --artifacts BenchmarkDotNet.Artifacts/current-midge-engine-smoke
```

For a bounded warmed diagnostic, add `--job Short --iterationCount 3
--warmupCount 3 --iterationTime 100` instead of `--job Dry`. For qualification,
omit those overrides, use the normal job, and collect at least three clean
independent runs on the same idle host. Save the complete source revision and
working-tree state with the reports.

From a clean checkout of the pinned Midge revision:

```bash
cargo bench --locked --bench tier3_system_engine -- \
  --profile smoke --json --output-dir target/stress-engine-smoke
```

Use its normal or release profile for qualification and inspect the effective
sample count, quality and trust class in the emitted artifact. This benchmark
requests 12 samples explicitly, even when the smoke profile otherwise defaults
to one. Three clean baselines are still required by Midge's own diagnostic row.

## Local evidence, 2026-11-04

Both Pants Dry engine-read rows and both Midge smoke rows completed with value
validation. Midge reported both rows as `noisy`, `diagnostic`, with 41–49% relative
standard deviation in completed-operation samples. These runs occurred while
other repository checks were active on an Apple M5 arm64 host; they are lifecycle
and diagnostic evidence only.

A bounded BenchmarkDotNet diagnostic used the aligned fixture before and after
the point-read change: in-process ShortRun, one launch, three 100 ms measurement
iterations and three warmup iterations, .NET 10.0.14 / SDK 10.0.403. These are
Pants-to-Pants allocation observations, not Midge ratios:

| Storage | Before, managed bytes/read | After, managed bytes/read |
| --- | ---: | ---: |
| Local | 175.29 KiB | 3.88 KiB |
| Simulated cloud | 173.92 KiB | 4.37 KiB |

Ignored raw reports are under
`BenchmarkDotNet.Artifacts/midge-0.5.2-engine-read-diagnostic` (before) and
`BenchmarkDotNet.Artifacts/midge-0.5.2-engine-read-final` (after). The timings are
too abbreviated and environmentally noisy to serve as performance gates.

The focused allocation regression measures a synchronous warmed point read on a
128-entry SST with a 1 MiB block cache and requires fewer than 16 KiB of managed
allocations. Additional tests ensure conflicting value, entry type or expiration
on an unrelated key still rejects synchronous and asynchronous point reads.
Existing V5, cache identity, decoded admission and disk-resident behavior suites
cover the remaining shared-decoder behavior.

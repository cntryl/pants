# Tier 2 subsystem benchmarks

Tier 2 uses fixed-operation workloads to expose subsystem regressions that are too broad for hot-path microbenchmarks. The workload inventory originated from Midge at `c5ffc2d`; that SHA is the historical performance-comparison boundary, not the current compatibility baseline. Current behavior and persisted-format compatibility are pinned separately to `75dcc39f7a9b87df480ed91c3a5c93fe1389ca71`. The inventory covers block-cache rotation and eviction, bloom/SST construction, batched compression, cross-thread event dispatch, multi-run iteration, memtable rotation, warm/cold range-cache access, read amplification, transaction lifecycle latency, and strict durability commit latency.

Run all Tier 2 rows with:

```bash
dotnet run --project bench/Pants.Benches/Cntryl.Pants.Benches.csproj -c Release -- --filter "*Tier2*"
```

Use `--job Dry` only to verify benchmark lifecycle and discovery. It is not performance evidence. Save and compare normal Release reports on the same machine. Investigate a repeated 8–10% regression. Transaction and durability rows additionally report allocations and compare 1-, 16-, and 64-writer shapes; correlate them with `PantsRuntimeMetrics` WAL append, fsync, and fan-out counters when diagnosing a change.

Setup, fixture construction, key generation, and cleanup stay outside measured methods. `OperationsPerInvoke` records the logical unit represented by each fixed batch.

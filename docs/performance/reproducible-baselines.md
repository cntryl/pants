# Reproducible Performance Baselines

Performance changes require evidence from the same idle machine, using exact source revisions and equivalent workload mechanics. A cross-engine number is context, not a ratio, unless the logical unit, storage mode, client count, operation count, and measurement class all match.

## Practical inventory run

From the Pants repository, build and execute every parameterized row once:

```bash
dotnet run --project bench/Pants.Benches/Cntryl.Pants.Benches.csproj \
  --configuration Release --no-restore -- practical BenchmarkDotNet.Artifacts/practical
```

This command fails unless discovery contains exactly 163 unique scenarios and every BenchmarkDotNet report succeeds. It writes source and machine metadata plus the scenario inventory beside the CSV reports. Aggregation matches every CSV identity to that inventory and reports each method's logical operations per invocation. The `Dry` job is a cold lifecycle validation, not statistical performance evidence.

Before promoting an optimization, rerun the affected types with the normal BenchmarkDotNet job at least three times on the same machine. Use the tier-specific regression thresholds documented in the other performance guides. For example:

```bash
dotnet run --project bench/Pants.Benches/Cntryl.Pants.Benches.csproj \
  --configuration Release --no-restore -- \
  --filter '*MvccSystemBenchmarks*' --exporters csv \
  --artifacts BenchmarkDotNet.Artifacts/mvcc-normal
```

## Midge v0.1.1 run

The comparison pin targets the released Midge v0.1.1 tag at commit `7d39f86217fdb07191a83bd885a514dd5ea9723f`. Its committed `Cargo.lock` pins the published registry crate `cntryl-stress` 0.4.0. The tracked [`midge-v0.1.1-pin.json`](../../bench/Pants.Benches/Reporting/midge-v0.1.1-pin.json) records the exact source SHA, lock hash, tool version, and scenario inventory hash. The reader rejects artifacts whose source, tool, lock, or inventory differs from this configuration.

Create a separate worktree from the release tag so the existing Midge checkout stays untouched:

```bash
git -C ../midge worktree add --detach /tmp/midge-bench-v0.1.1 v0.1.1
git -C /tmp/midge-bench-v0.1.1 rev-parse HEAD
```

Run the report-producing targets individually using the release's checked-in lockfile. `tier4_bounded_compaction_qualification` is omitted because it has a qualification-specific CLI and emits no `cntryl-stress` artifact. All other Tier 1 through Tier 4 targets, including strict group commit, emit comparison artifacts.

```bash
cd /tmp/midge-bench-v0.1.1
mkdir -p target/stress
for bench_target in \
  tier1_hotpath_api tier1_hotpath_bloom tier1_hotpath_block_cache \
  tier1_hotpath_iterator tier1_hotpath_memtable tier1_hotpath_sst \
  tier1_hotpath_trie tier1_hotpath_wal tier1_hotpath_tlv_encoding \
  tier1_hotpath_keyed_group_commit tier1_hotpath_compression tier1_hotpath_event_loop \
  tier2_subsystem_block_cache tier2_subsystem_bloom_build tier2_subsystem_compression \
  tier2_subsystem_range_scan_cache tier2_subsystem_iterator_multi_sst \
  tier2_subsystem_memtable_rotate tier2_subsystem_read_amplification \
  tier2_subsystem_event_loop tier2_subsystem_transaction_latency \
  tier2_subsystem_durability_commit_latency \
  tier3_system_engine tier3_system_mvcc tier3_system_sst tier3_system_scan \
  tier3_system_lifecycle \
  tier4_system_local_throughput_regression tier4_system_compression_policy \
  tier4_ycsb_workload_a tier4_system_compaction_backpressure \
  tier4_ycsb_workload_b tier4_ycsb_workload_c tier4_ycsb_workload_d \
  tier4_ycsb_workload_e tier4_ycsb_workload_f tier4_streaming_workload \
  tier4_system_recovery_throughput tier4_system_strict_group_commit
do
  cargo bench --locked --bench "$bench_target" -- \
    --profile smoke --json --output-dir target/stress \
    >> target/stress/bench.log 2>&1 || exit 1
done
cp Cargo.lock target/stress/Cargo.lock
```

The non-production `failpoints` feature is required only by the excluded qualification executable and is intentionally omitted from these reportable runs. No fixture patch or dependency update is used; the release already emits the fixture-size markers consumed by the reader. `--locked` keeps Cargo on the published crate version and exact transitive dependency set.

The smoke profile is an abbreviated lifecycle and inventory run, not statistical performance evidence. Check `run_profile`, sample count, quality, and trust class in the report. Run both engines on the same idle machine. The mapping and remaining workload differences are documented in [cross-engine-scenario-mapping.md](cross-engine-scenario-mapping.md). The checked-in inventory contains 255 scenario IDs emitted by the 39 report-producing targets; it excludes only the qualification executable, which has no comparison artifact.

## Aggregate and compare

After both runs:

```bash
dotnet run --project bench/Pants.Benches/Cntryl.Pants.Benches.csproj \
  --configuration Release --no-restore -- aggregate \
  bench/Pants.Benches/Reporting/midge-v0.1.1-pin.json \
  BenchmarkDotNet.Artifacts/practical \
  /tmp/midge-bench-v0.1.1/target/stress \
  BenchmarkDotNet.Artifacts/comparison.md
```

Aggregation fails closed for incomplete Pants or Midge output, duplicate or unexpected scenario identities, malformed or failed Midge summaries, source or tool mismatch, an unexpected lockfile, a changed scenario inventory, unsupported schemas, missing measured samples, or a disagreement between raw measured-operation counts and summary counts. The report includes the release, exact source SHA, lockfile hash, scenario inventory hash, machine details, Pants operations per invocation, and completed Midge operations across measured samples. It groups rows by tier and workload but deliberately omits ratios. The `Dry` Pants run and Midge smoke run are fresh lifecycle/inventory runs, not performance evidence.

Benchmark artifacts remain ignored. Commit a reviewed fixture under `docs/performance/` only when it is intentionally part of a stable report-schema test.

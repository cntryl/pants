# Reproducible Performance Baselines

Performance changes require evidence from the same idle machine, using exact source revisions and equivalent workload mechanics. A cross-engine number is context, not a ratio, unless the logical unit, storage mode, client count, operation count, and measurement class all match.

## Practical inventory run

From the Pants repository, build and execute every parameterized row once:

```bash
dotnet run --project bench/Pants.Benches/Cntryl.Pants.Benches.csproj \
  --configuration Release --no-restore -- practical BenchmarkDotNet.Artifacts/practical
```

This command fails unless discovery contains exactly 153 unique scenarios and every BenchmarkDotNet report succeeds. It writes source and machine metadata plus the scenario inventory beside the CSV reports. The `Dry` job is a cold lifecycle validation, not statistical performance evidence.

Before promoting an optimization, rerun the affected types with the normal BenchmarkDotNet job at least three times on the same machine. Use the tier-specific regression thresholds documented in the other performance guides. For example:

```bash
dotnet run --project bench/Pants.Benches/Cntryl.Pants.Benches.csproj \
  --configuration Release --no-restore -- \
  --filter '*MvccSystemBenchmarks*' --exporters csv \
  --artifacts BenchmarkDotNet.Artifacts/mvcc-normal
```

## Pinned Midge run

The comparison boundary accepts only Midge commit `c5ffc2d3284c76b6f7cd03444a5b0a38ae8bbc33` and `cntryl-stress.v2` artifacts. That Midge revision declares `cntryl-stress` from a moving branch and does not commit `Cargo.lock`; pin the compatible harness revision `6b7bd34b495f843826eb873e45b7a70b341c74e3` in the benchmark checkout before running:

```bash
git rev-parse HEAD
cargo update -p cntryl-stress --precise 6b7bd34b495f843826eb873e45b7a70b341c74e3
cargo tree -i cntryl-stress
cargo bench --benches -- --profile smoke --json
```

Use `--profile default` or `--profile release` for the Midge suites corresponding to a proposed optimization. Midge writes current artifacts below `target/stress/*/latest.json`. Preserve the generated lockfile with the benchmark artifacts so the resolved harness SHA remains auditable; do not commit it to Midge unless that repository adopts lockfile ownership.

## Aggregate and compare

After both runs:

```bash
dotnet run --project bench/Pants.Benches/Cntryl.Pants.Benches.csproj \
  --configuration Release --no-restore -- aggregate \
  BenchmarkDotNet.Artifacts/practical \
  ../midge/target/stress \
  BenchmarkDotNet.Artifacts/comparison.md
```

Aggregation fails closed for incomplete Pants output, duplicate scenario identities, malformed or failed Midge summaries, a non-pinned Midge SHA, unsupported schemas, and missing required measurements. The report groups Pants and Midge rows by tier and workload but deliberately omits ratios. A reviewer may compare rows only after documenting that their mechanics are equivalent.

Benchmark artifacts remain ignored. Commit a reviewed fixture under `docs/performance/` only when it is intentionally part of a stable report-schema test.

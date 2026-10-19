# Reproducible Performance Baselines

Performance changes require evidence from the same idle machine, using exact source revisions and equivalent workload mechanics. A cross-engine number is context, not a ratio, unless the logical unit, storage mode, client count, operation count, and measurement class all match.

## Practical inventory run

From the Pants repository, build and execute every parameterized row once:

```bash
dotnet run --project bench/Pants.Benches/Cntryl.Pants.Benches.csproj \
  --configuration Release --no-restore -- practical BenchmarkDotNet.Artifacts/practical
```

This command fails unless discovery contains exactly 157 unique scenarios and every BenchmarkDotNet report succeeds. It writes source and machine metadata plus the scenario inventory beside the CSV reports. Aggregation matches every CSV identity to that inventory and reports each method's logical operations per invocation. The `Dry` job is a cold lifecycle validation, not statistical performance evidence.

Before promoting an optimization, rerun the affected types with the normal BenchmarkDotNet job at least three times on the same machine. Use the tier-specific regression thresholds documented in the other performance guides. For example:

```bash
dotnet run --project bench/Pants.Benches/Cntryl.Pants.Benches.csproj \
  --configuration Release --no-restore -- \
  --filter '*MvccSystemBenchmarks*' --exporters csv \
  --artifacts BenchmarkDotNet.Artifacts/mvcc-normal
```

## Historical Midge performance run

The performance comparison reader accepts only Midge commit `c5ffc2d3284c76b6f7cd03444a5b0a38ae8bbc33` and `cntryl-stress.v2` artifacts. This is a historical benchmark boundary and must not be reported as Pants's current Midge compatibility baseline. The current feature and persisted-format compatibility baseline is pinned to `7d39f86217fdb07191a83bd885a514dd5ea9723f` and recorded in [`MidgeCurrentBaselineReview.md`](../compatibility/MidgeCurrentBaselineReview.md). The older benchmark revision declares `cntryl-stress` from a moving branch and does not commit `Cargo.lock`; pin the compatible harness revision `6b7bd34b495f843826eb873e45b7a70b341c74e3` in the benchmark checkout before running.

The pinned Tier 3 engine and SST read fixtures and Tier 4 recovery fixture use small memtables while loading 3,000 to 10,000 keys, which can stall before an explicit flush. Apply the checked-in [smoke fixture patch](midge-smoke-fixtures.patch) to use a 2 MiB engine/recovery fixture and a 4 MiB SST fixture. It emits the selected setup size as a benchmark parameter so the report can verify the patched code produced those rows. The patch leaves measured operations unchanged. Start from a clean Midge worktree at `c5ffc2d3284c76b6f7cd03444a5b0a38ae8bbc33`; confirm it with `git -C ../midge rev-parse HEAD`. This is a zero-context patch for that pinned commit, so apply it with `--unidiff-zero`. From the Pants repository root, with the pinned Midge checkout at `../midge`:

```bash
git -C ../midge apply --unidiff-zero "$PWD/docs/performance/midge-smoke-fixtures.patch"
```

From the Midge checkout, pin the harness and run every registered Tier 1 through Tier 4 benchmark target once:

```bash
cargo update -p cntryl-stress --precise 6b7bd34b495f843826eb873e45b7a70b341c74e3
cargo tree -i cntryl-stress
cargo bench --locked --bench 'tier*' -- --profile smoke --json --output-dir target/stress
cp Cargo.lock target/stress/Cargo.lock
cp ../pants/docs/performance/midge-smoke-fixtures.patch target/stress/midge-smoke-fixtures.patch
```

The smoke profile is abbreviated, and suite-level sample overrides can still apply. The resulting quality may be `untrustworthy`, so treat it as an inventory and lifecycle baseline rather than statistical evidence. The report shows each row's run profile, measured sample count, quality, and trust class. Stable Midge row identities use the pinned `benchmark_id`; runtime diagnostic parameters stay visible without changing identity. Use `--profile default` or `--profile release` for the Midge suites corresponding to a proposed optimization. Midge writes current artifacts below `target/stress/*/latest.json`. Keep the lockfile and setup patch beside those artifacts so the resolved harness and fixture setup remain auditable; the Pants reader checks their pinned hashes and the embedded scenario inventory. Do not commit the generated lockfile to Midge unless that repository adopts lockfile ownership.

## Aggregate and compare

After both runs:

```bash
dotnet run --project bench/Pants.Benches/Cntryl.Pants.Benches.csproj \
  --configuration Release --no-restore -- aggregate \
  BenchmarkDotNet.Artifacts/practical \
  ../midge/target/stress \
  BenchmarkDotNet.Artifacts/comparison.md
```

Aggregation fails closed for incomplete Pants or Midge output, duplicate or unexpected scenario identities, malformed or failed Midge summaries, unpinned Midge source, stress harness, lockfile, fixture patch, or setup markers, unsupported schemas, missing measured samples, or a disagreement between raw measured-operation counts and summary counts. The report includes exact source, harness, lockfile, fixture patch, and inventory hashes, plus Pants operations per invocation and completed Midge operations across measured samples. It groups rows by tier and workload but deliberately omits ratios. A reviewer may compare rows only after documenting that their mechanics are equivalent.

Benchmark artifacts remain ignored. Commit a reviewed fixture under `docs/performance/` only when it is intentionally part of a stable report-schema test.

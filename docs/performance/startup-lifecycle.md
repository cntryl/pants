# Startup lifecycle

Issue #36 adds internal, test-injectable startup measurements for the following phases:

1. cloud control hydration, when applicable;
2. writer lease acquisition;
3. FORMAT validation;
4. manifest snapshot loading;
5. manifest journal replay;
6. intent reconciliation;
7. SST hydration;
8. WAL replay;
9. immutable database-version construction; and
10. runtime service startup.

Measurements contain only the phase, elapsed time, and process allocation delta. They do not
contain paths, object keys, lease holder identifiers, or persisted data. The recorder is disabled
for public opens and performs no timing or allocation reads unless supplied through internal test
dependencies.

## Optimization decision

Clean-reopen diagnostics showed that lease acquisition is dominated by required durable
publication and fencing checks. Those boundaries were not reordered or weakened. A custom lease
parser candidate saved only about 440 bytes and did not improve latency, so it was rejected.

FORMAT validation previously used `File.ReadAllText`, which creates a buffered text reader for a
fixed ASCII marker. Exact binary comparison against the pinned v3 bytes is simpler and reduced the
FORMAT phase from about 8.1 KB to 336 bytes per clean reopen.

The same-machine BenchmarkDotNet comparison used:

```text
dotnet run --project bench/Pants.Benches/Cntryl.Pants.Benches.csproj \
  --configuration Release --no-restore -- \
  --filter '*LifecycleSystemBenchmarks.CleanReopenAsync*' \
  --job short --warmupCount 3 --iterationCount 7
```

| Storage | Before | After | Before allocation | After allocation |
| --- | ---: | ---: | ---: | ---: |
| Local | 40.30 ms | 36.59 ms | 307.09 KB | 299.47 KB |
| Simulated cloud | 51.54 ms | 48.38 ms | 401.77 KB | 394.14 KB |

The approximately 7.6 KB allocation reduction is deterministic. Filesystem latency varies, so the
latency result is treated as directional evidence rather than a reason to add further machinery.

## WAL replay scaling

`WalReplayScalingSystemBenchmarks` holds the replay fixture to 4, 16, or 64 buffered commits and
varies unrelated flushed L0 SSTs between 1 and 64. Setup verifies the SST count, and iteration
cleanup verifies the recovered keys plus the replay metrics: 4 commits produced 4 records / 552
bytes, 16 produced 16 / 2,208, and 64 produced 64 / 8,832 at both SST counts. The timed method
contains only the open and recovery path. Internal phase measurements are written to the temporary
CSV path printed by the benchmark process. Fixture creation, correctness checks, and disposal run
outside the timed method; whole-open timings include the internal phase recorder overhead.

The run used BenchmarkDotNet 0.15.8 on an Apple M5 with .NET 10.0.11. The table shows medians from
the seven measured phase callbacks; the full-open column is the BenchmarkDotNet mean and its
99.9% confidence interval margin.

| Flushed SSTs | WAL records / bytes | WAL replay phase median | SST hydration phase median | Full open mean ± margin |
| ---: | ---: | ---: | ---: | ---: |
| 1 | 4 / 552 B | 142.1 µs | 68.6 µs | 25.15 ± 8.49 ms |
| 1 | 16 / 2,208 B | 381.2 µs | 91.9 µs | 23.73 ± 2.52 ms |
| 1 | 64 / 8,832 B | 602.6 µs | 66.5 µs | 25.22 ± 4.75 ms |
| 64 | 4 / 552 B | 124.0 µs | 1,970.4 µs | 25.24 ± 1.75 ms |
| 64 | 16 / 2,208 B | 189.2 µs | 3,609.2 µs | 28.77 ± 4.81 ms |
| 64 | 64 / 8,832 B | 234.6 µs | 1,742.3 µs | 25.86 ± 4.23 ms |

Replay phase time rose with replay work at both SST counts. Holding replay at 16 or 64 records did
not make the 64-SST WAL phase slower than the 1-SST phase in this run. SST hydration, by contrast,
grew from roughly 67–92 µs at 1 SST to 1.7–3.6 ms at 64 SST, as expected. Whole-open intervals are
wide and overlap, so these timings are directional evidence; the isolated phase measurements and
identical replay counts/bytes establish that unrelated SSTs do not add work to WAL replay.

Reproduce with:

```text
dotnet run --project bench/Pants.Benches/Cntryl.Pants.Benches.csproj --configuration Release --no-build -- --filter '*WalReplayScalingSystemBenchmarks*' --job short --warmupCount 2 --iterationCount 7
```

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

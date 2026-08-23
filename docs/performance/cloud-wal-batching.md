# Cloud WAL batching

Issue #37 batches already sealed, immutable WAL segments at the cloud publication boundary. A batch
may contain only one writer epoch. Object uploads and authoritative readbacks remain per segment;
the publication catalog is updated with one conditional transition and one exact readback for the
batch. Local WAL acknowledgements and deletion happen only after that authoritative boundary.

The drain path splits recovered backlog by writer epoch. Lease authority is checked before remote
publication and again before every local acknowledgement. CloudStrict continues to await the whole
drain, while CloudAsync retains bounded scheduling through the existing queue and deadline controls.

Provider-store tests count requests for three segments:

- three immutable conditional uploads;
- three exact object readbacks;
- one catalog read, conditional write, and exact readback;
- four total PUTs instead of the six required by three individual catalog transitions; and
- five total GETs instead of nine.

The simulated-provider subsystem benchmark publishes eight 4 KiB segments from the same epoch:

```text
dotnet run --project bench/Pants.Benches/Cntryl.Pants.Benches.csproj \
  --configuration Release --no-restore -- \
  --filter '*CloudWalBatchingSubsystemBenchmarks*' \
  --job short --warmupCount 3 --iterationCount 7
```

| Method | Mean | Allocation |
| --- | ---: | ---: |
| Individual publication | 70.09 ms | 85.95 KB |
| Batched publication | 38.73 ms | 26.58 KB |

Batching reduced measured time by 44.7% and managed allocation by 69.1%. No persisted format,
conditional-write rule, object readback, lease fence, recovery validation, or deletion proof changed.

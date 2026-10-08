# Crash/Soak Harness

`test/Pants.Tests/Soak/PantsCrashSoakTests.cs` is a seeded, black-box crash harness in the spirit of
`midge-destroyer`. It lives in the ordinary test assembly and reuses the crash-child pattern: each
cycle launches `dotnet vstest` on the same assembly, runs a ledgered workload in that child, and
kills it with a SIGKILL-equivalent at a seeded point. The parent then reopens the database with
`PantsRecoveryPolicy.Strict` and checks the recovered state against the child's ledger.

## What one seed does

A seed fixes everything except thread interleaving: the engine configuration (lane count, keys per
lane, background compaction, memtable flush threshold, WAL-record flush trigger), every cycle's
operations, and every cycle's kill point.

- **Workload.** One to four lanes run concurrently. Each lane owns a disjoint key range and runs its
  operations one at a time, so a kill leaves at most one operation per lane in flight. Operations
  are `Put`/`Insert`/`Delete`/`DeleteRange` transactions committed with a seeded durability
  (`Sync`, `Buffered`, `BestEffort` locally; `CloudStrict`, `CloudAsync`, `BestEffort` in the cloud),
  plus rollbacks, transactions with an assertion that can never hold, write-write conflict pairs
  under `AbortOnWriteConflict`, and explicit flushes and full compactions.
- **Kill point.** Either the n-th hit of an engine failpoint (commit path, WAL rotation, flush,
  compaction, manifest, intent log, or cloud upload and catalog publication), immediately after the
  n-th commit is dispatched, or after the whole workload. The n-th hit counts from open, so a kill
  can also land inside recovery. For cloud backends a quarter of cycles also discard the local
  cache before the reopen, so recovery must rebuild from cloud objects.
- **Lease takeover.** Every process opens with a lease clock that runs one TTL plus clock-skew
  tolerance ahead of the previous process, so each reopen takes the dead writer's lease over
  through the ordinary expiry path without waiting or editing the lease record.

## The ledger

The child appends one line per event, flushed to the operating system before the call it describes
returns: `D` before a commit is dispatched, `A` after it is acknowledged, `F` when it is rolled back
or rejected (conflict, write stall, busy), and `E` for an exception the workload did not expect. The
verifier regenerates the operations from the seed, so the ledger carries only ids. Each operation is
then:

| Outcome | Meaning | Required after reopen |
|---------|---------|-----------------------|
| acked | acknowledged with a durability the crash cannot undo (`Sync`, `CloudStrict`, and `CloudAsync` unless the cache was discarded) | present, unless a later write to the same key may have replaced it |
| failed | rolled back, rejected, or doomed by construction (impossible assertion, conflict loser) | never visible |
| unknown | dispatched but unanswered, or acknowledged with a durability that promises nothing across a crash | visible entirely or not at all |
| not dispatched | the child never reached it | never visible |

For each key the verifier allows the value of the last crash-durable acknowledged write, or of any
later write that is not known to have failed. It also checks that no unknown transaction is only
partly visible, that a full scan returns exactly the keys and values that point reads return, and
that no key outside the workload appears. The verified state becomes the next cycle's floor; for
cloud backends the verifier first commits an empty `CloudStrict` transaction so that floor is
cloud-durable.

## Smoke run

The default suite runs `ShouldHonorLedgerAcrossCrashesGivenSmokeSeed`: four fixed seeds (two local,
two simulated-cloud), three cycles of about forty operations each, in roughly 15 seconds. It needs
nothing beyond the build. `ShouldHonorLedgerAcrossCrashesGivenSqrzlSmokeSeed` carries
`Category=Sqrzl` and runs wherever the Sqrzl emulator does.

## Soak run

`ShouldHonorLedgerGivenOptInSoak` is skipped unless `PANTS_CRASH_SOAK=1`, and carries
`Category=Soak`; CI never sets the variable.

```sh
PANTS_CRASH_SOAK=1 \
PANTS_CRASH_SOAK_SEEDS=1000-1099 \
PANTS_CRASH_SOAK_BACKENDS=Local,SimulatedCloud \
PANTS_CRASH_SOAK_CYCLES=8 \
PANTS_CRASH_SOAK_OPERATIONS=160 \
dotnet test test/Pants.Tests/Pants.Tests.csproj --configuration Release \
  --filter "FullyQualifiedName=Cntryl.Pants.Soak.PantsCrashSoakTests.ShouldHonorLedgerGivenOptInSoak" \
  --logger "console;verbosity=detailed"
```

| Variable | Default | Meaning |
|----------|---------|---------|
| `PANTS_CRASH_SOAK` | unset | `1` enables the soak test. |
| `PANTS_CRASH_SOAK_SEEDS` | 20 seeds from a time-derived start | `7`, `1,2,3`, or `100-199`. |
| `PANTS_CRASH_SOAK_MINUTES` | `0` | Keep drawing further seeds until this many minutes have passed. |
| `PANTS_CRASH_SOAK_BACKENDS` | `Local,SimulatedCloud` | Any of `Local`, `SimulatedCloud`, `Sqrzl`. |
| `PANTS_CRASH_SOAK_CYCLES` | `8` | Kill/reopen cycles per seed. |
| `PANTS_CRASH_SOAK_OPERATIONS` | `160` | Approximate operations per cycle across all lanes. |
| `PANTS_CRASH_SOAK_KEEP_FAILURES` | unset | `1` copies a failing run, with a `killed-NNN` snapshot of each cycle's database as the kill left it, to the temp directory. Also honored by the smoke run. |

The `Sqrzl` backend needs the emulator: `docker compose up -d sqrzl` (override the endpoint with
`SQRZL_ENDPOINT` or `SQRZL_API_PORT`).

## Replaying a failure

Every failure names its seed, the scenario derived from it, the failing cycle, its kill plan, the
ledger counts, each violated expectation, and a ready-to-run replay command. Replaying the seed
replays the configuration, operations, and kill points exactly; only the interleaving of concurrent
lanes can differ, and the verifier's expectations hold for every interleaving. Seeds with a single
lane are fully sequential.

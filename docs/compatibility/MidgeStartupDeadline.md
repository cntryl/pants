# Aggregate startup timeout and ownership

Pants follows Midge's optional `open_timeout` contract with
`PantsOpenOptions.WithOpenTimeout(TimeSpan?)`. The default is `null`, leaving
aggregate startup unbounded for memory, local, simulated cloud and provider cloud.
The budget is independent of storage, runtime-response and shutdown timeouts; it
may be shorter than an individual storage timeout. Values must be at least one
millisecond and representable by the monotonic clock.

```csharp
var options = PantsOpenOptions.Local(path)
    .WithOpenTimeout(TimeSpan.FromSeconds(30));
await using var database = await PantsDatabase.OpenAsync(options);
```

The deadline covers preparation, recovery, runtime assembly and final admission.
Synchronous local work runs under a preparation owner, so a held syscall or a
provider's blocking cancellation callback does not extend the caller's aggregate
wait. Cancellation callbacks and cleanup remain owned work after the caller
returns. Stores, local exclusion and writer leases are released only when that
work has actually stopped, subject to normal lease-expiry fencing.

A prepared coordinator stays dormant. Recovered flushes, compaction and cloud
retirement begin only after the caller atomically accepts it. Expiry or caller
cancellation prevents late admission and sends the prepared runtime through owned
teardown. Successful admission disarms the startup deadline and original caller
cancellation; they do not become lifetime limits on the returned database.

Blocking authority verification runs on the preparation owner before readiness.
The caller's acceptance checks cached health and monotonic validity without
reading local files, issuing provider calls or running lease-loss callbacks.
The same lock arbitrates cancellation, lease-publication admission and runtime
acceptance, so a cancelled startup cannot submit a later acquisition mutation.

Ordinary expiry returns `PantsTimeoutException`. An admitted, unresolved leader
write or conditional lease acquisition returns `PantsLeaseIndeterminateException`.
Initial reads and discovery do not create acquisition uncertainty. Confirmed
owners and unresolved candidates retain their exact identity for cleanup; cleanup
never expires a successor's lease. Known-state caller cancellation retains
`OperationCanceledException`. Early compatibility and other definite failures
retain their original classification.

Simulated cloud now uses the same bounded shared lease coordinator before cache
hydration or catalog repair/publication. The shared epoch covers the catalog,
local leader and WAL floors; the local lease uses that exact epoch. Heartbeats
start after confirmed acquisition in both cloud modes and remain owned through
startup cleanup. An active foreign shared lease cannot be replaced merely because
the new local cache has a greater proposed epoch.

The local tree is checked immediately after canonicalizing its root and before
shared-lease reads or writes. A planted link beneath the root cannot redirect
lease or native lock creation; a link used as the configured root itself remains
supported. Large valid budgets use capped timer intervals, and timer disposal may
overtake a pending rearm after admission without failing the callback.

The reference boundaries are Midge
`src/engine/startup/deadline.rs`, `src/common/deadline_scope.rs`,
`src/runtime/startup_gate.rs`, and `src/engine/api/options.rs` at the repository's
pinned compatibility revision. This change does not alter FORMAT 9 bytes or add
a migration path.

## Behavioral evidence

The first five actual-engine hold schedules failed before the ownership change:
the caller remained pending while known-owner local work or late service
preparation was held. The blocking-cancellation-callback regression additionally
exposed nested startup timers; startup phases now use the owner's single
cancellation scope instead of composing independent cancellation callbacks.

The focused Release selection passed **40 / 40** cases, with no build
warnings. It covers all four storage modes, the unbounded default, an aggregate
budget spanning metadata and service phases, cancellation and expiry while an
actual prepared runtime is held, successful operation after admission, retained
recovered WAL data without premature background flush, exact FORMAT preservation,
local leader publication uncertainty, conditional provider acquisition with a
held or lost response, successor-preserving cleanup, initial-read expiry without
lease mutation, and explicit provider-startup timeout fixtures.

Two additional path-ordering cases failed before correction: both default and
timed simulated opens rejected a planted `cloud_store` link only after adding
entries outside the configured tree. The corrected order preserves outside bytes
and entries and retains successful root-alias reopen. Another actual-memory-open
schedule failed before correction when acceptance disposed a timer while its
long-budget rearm was held; the released callback now completes safely.

The final frozen-tree Release sweep passed **1,073**, failed **0**, and skipped
**1** live-AWS case, with zero build warnings. Its selection includes Cloud tests
except Sqrzl qualification, file leases, startup deadlines and candidate cleanup,
startup diagnostics, runtime lifecycle, and storage link refusal. Full solution
format verification and `git diff --check` also passed. Crash fixtures confirm
the child has exited before conditionally expiring its exact stopped owner;
they do not force takeover of a live shared lease.

Tests wait for actual cleanup completion before same-path reopen. A timeout reply
alone is not evidence that the preparation owner has stopped or released its
lease. Repository-wide integrated-tree, live provider, exhaustive crash/power-loss
and external cross-engine qualification remain separate evidence from this
selection. Native stripe locking was exercised on the qualification host; this
does not establish the Windows branch's runtime behavior.

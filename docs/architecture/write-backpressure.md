# Write backpressure

`PantsOpenOptions.WithWriteBackpressure` accepts an immutable
`PantsWriteBackpressureConfiguration`. The default leaves caller slowdown and
maintenance rate limits disabled. Dependency injection binds the same six fields
under `Pants:WriteBackpressure`, and `PantsOpenOptionsResolver.Resolve` returns the
effective policy.

Soft limits cover published plus reserved L0 generations, total admitted
memtable bytes, and estimated pending compaction bytes. A mutation touching any
family in a soft band, or entering a coordinator queue at least three quarters
full while soft controls are enabled, waits once for `MaximumAdmissionDelay`.
The wait belongs to the caller and consumes the original response deadline,
including subsequent preflight, queue admission, and response time. Cancellation,
shutdown, fencing, and an exhausted deadline end this wait before commit
submission. Read-only, empty, and assertion-only submissions receive no slowdown;
assertions still undergo serialized validation.

Pending compaction debt is derived from authoritative published files. L0 counts
all its bytes when its count or byte trigger is reached; inner levels contribute
bytes above their targets, excluding the terminal level. The optional hard limit
rejects mutations before WAL allocation. Manifest and family publication refresh
the debt and wake stalled waiters. Dropped families contribute no debt while old
snapshots retain their normal file ownership.

Flushes, compaction outputs, and repair scratch share one monotonic rate schedule
on owned maintenance workers. Encoded writes are charged in chunks of at most
64 KiB. WAL, metadata, provider I/O, and renames receive no charge. Hard write
pressure and shutdown bypass the schedule to preserve maintenance progress.
Caller timeout or cancellation does not release physical work or its staging
reservations; existing worker completion and coordinator publication own them.

Runtime metrics expose `WriteSlowdownCount`, `WriteSlowdownNanosecondsTotal`,
`EstimatedPendingCompactionBytes`, and `BackgroundRateLimitedBytes`. The last
counter includes bytes that passed the rate schedule and excludes urgent or
shutdown bypasses.

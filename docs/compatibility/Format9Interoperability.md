# FORMAT 9 producer/consumer qualification

This is bounded local correctness evidence, not complete interoperability or
performance qualification. The reference is Midge 0.5.2 at
`c684310d9d8ce8ecd1151c47151d4bea98b348cf`, with committed `Cargo.lock` SHA-256
`6a497fab02a0dde008ae9029af4d877e8982bd0fa3113efe3c291bafd091dc88`.
The temporary probe's third-party package names/versions match that lock;
only the probe package is additional. The Midge library build enables
`cloud-aws` to select a supported feature combination; these cases use local
filesystem storage and make no provider calls.

## Workloads

Each engine opens the same directory, verifies all prior phase values, and
optionally commits the next phase before shutdown. Each writing phase commits
one atomic default-family transaction and one separate `side` family transaction
using Sync durability. Transactions never span families, following Midge's
transaction architecture.

The default-family workload covers an empty key with binary value, point
overwrites, retained phase marker keys, point deletion, and a start-inclusive,
end-exclusive range deletion whose end key must remain readable. Side-family
values verify non-default IDs. Every consumer checks the prior point values,
deletions and complete forward scan count.

| Case | Engine order | Publication boundary |
| --- | --- | --- |
| SST handoff, Midge first | Midge 0 → Pants 1 → Midge 2 → Pants 3 → Midge verify 4 | Every writing phase flushes both families and compacts. |
| SST handoff, Pants first | Pants 0 → Midge 1 → Pants 2 → Midge 3 → Pants verify 4 | Same boundary in reverse producer order. |
| WAL handoff, Midge first | Midge 0 → Pants 1 → Midge 2 → Pants 3 → Midge verify 4 | Phases 1 and 2 commit without flushing. The complete SST filename/hash map stays unchanged and WAL payload remains present before each consumer opens. |
| WAL handoff, Pants first | Pants 0 → Midge 1 → Pants 2 → Midge 3 → Pants verify 4 | Same WAL-only middle handoffs in reverse producer order. |
| Spilled non-default WAL | Midge 0 → Pants spill 1 → Midge verify 2 | A 16 KiB Pants transaction pool forces 100 side-family writes through split WAL records. Midge verifies all 100 values and the ordinary phase values without an intervening Pants flush. |

The matrix has 23 engine opens. It exercises normal process shutdown and
successor open; it is not a power-loss or overlapping-writer campaign.

## Failure discovered by the WAL handoff

The first SST-only runs passed. The first WAL-only runs failed in both producer
orders: Midge rejected a Pants non-default-family batch because its outer CF_ID
was zero while its members belonged to family 1. Pants also wrote zero on spilled
begin/commit markers and accepted cross-family batches during replay.

Writers now carry the actual family on batch and split envelopes, validate the
single-family scope before effects, and reject cross-family encoding. Readers
reject members/commit markers outside the declared family before applying them.
`WalTransactionFamilyTests` reproduced eight failures before correction and
covers ordinary envelopes, malformed batches, malformed split units and a real
spilled transaction followed by reopen. Recovery checkpoint crash fixtures now
use legal two-key, single-family transactions across independently progressing
families rather than fabricating an unsupported cross-family transaction.

This changes admission of previously nonconforming bytes, not the WAL version.
Conforming FORMAT 9/V2 records retain their format. A pre-fix Pants non-default
WAL with a mismatched envelope was never Midge-compatible and is now rejected
as corruption; there is no silent rewrite or migration fallback.

## Durable regression input and local artifacts

`Fixtures/Compatibility/Midge/c684310d9d8ce8ecd1151c47151d4bea98b348cf/alternating-local-midge0`
contains unchanged Midge phase-0 writer bytes. The adjacent provenance record
identifies the source, workload and harness. Process-local leader/lock files
are omitted. `CurrentMidgeFormat9WriterTests` reads, extends, flushes, compacts
and reopens this input with binary/empty keys, deletions and both families.
This is a behavioral engine test; fixture metadata is not an integrity gate.

Temporary external probe sources, binaries, databases, SST boundary hashes and
`qualification.json` are retained under
`/tmp/pants-format9-cross-engine-20261104`. No CLI or standalone tooling project
was added to the repository. These probes are separate from the normal test
suite and do not run in CI. The static Midge writer fixture does run in it.

The qualified scope excludes cloud metadata writer alternation, crash/ambiguous
effects, compatibility with pre-FORMAT-9 databases, all compression/TTL/merge
combinations, reverse scans and platform combinations beyond this local host.
Use the separate fault, authority and codec tests for their named evidence;
none of those boundaries closes by association with this matrix.

## Cold simulated-cloud extension

The subsequent isolated startup/native/CAS implementation passes **23** more
engine opens against the same actual Midge library. Both writer orders pass
five-open SST handoffs and five-open WAL handoffs. A final three-open case
consumes 100 spilled non-default-family Pants writes in Midge. Each successor
loses every local-cache entry except `cloud_store` before opening. WAL-only
phases exit immediately after CloudStrict acknowledgement, without engine
shutdown; an unchanged complete shared-SST name/hash map proves those middle
handoffs cannot recover through newly flushed SSTs. This models process exit,
not a device power failure. Earlier graceful-shutdown probes correctly failed
that WAL-only assertion because shutdown flushed committed memtables.

The first cloud probe found that Pants wrote an effectively permanent shared
lease and left it behind after shutdown. Midge refused the successor open.
Simulated storage now uses the bounded coordinator lease and Midge's fixed
native conditional-object lock stripes. Native contention/cancellation tests
pass with that protocol; a separately compiled process-only mutation negative
control fails both tests. Delayed catalog publication additionally uses exact
conditional versions and preserves unknown post-primary outcomes.

These runs use a phase-specific injected lease clock advanced beyond the
previous exited writer's expiry, without editing lease bytes. Temporary probes,
compiled artifact hashes and lock provenance are in
`/tmp/pants-format9-cloud-cross-engine-20261104`. Their third-party Rust package
versions match Midge's committed lock; `internal-testing` enables only the
injected lease clock in addition to `cloud-aws`. The fixture
`cloud-sst-midge0` retains unchanged Midge shared-object bytes, with transient
coordination stripes omitted. `CurrentMidgeCloudWriterTests` reads and extends
those bytes, verifies independent lease acquisition after close and then
recovers from the shared objects after local-cache loss.

The cloud extension was qualified against a compiled isolated slice; the final
integrated-source recheck is separate. Native S3/Azure/GCS/OCI environments,
overlapping writers, arbitrary provider effect ambiguity, reverse scans,
TTL/compression cross-products and other operating systems remain outside this
matrix. The earlier local-only scope above remains its own historical result.

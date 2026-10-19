# Durability Contracts

Pants exposes durability through immutable `PantsWriteOptions` values. Local storage accepts
`Sync`, `Buffered`, and `BestEffort`; cloud-backed storage accepts `CloudStrict`, `CloudAsync`,
and `BestEffort`.

`CloudStrict` acknowledges a commit only after the transaction's epoch-scoped WAL object is
durable and the publication catalog conditionally names that object. Recovery can therefore
hydrate an empty local cache and replay every acknowledged commit. A failed conditional catalog
write, lost lease, ambiguous publication result, or caller deadline before acknowledgement cannot
be reported as durable. A deadline after admission is outcome-unknown rather than an authoritative
rejection: Pants retains the sealed local WAL and continues or retries the accepted publication
obligation under writer-epoch fencing. Callers must reconcile before retrying the mutation.

A read-write `CloudStrict` commit with no mutations also confirms that the current runtime
sequence is cloud-durable. This includes assertion-only and fully empty commits. If that sequence
is already durable, the commit returns without sealing another WAL segment; otherwise it seals
pending WAL work or waits behind an upload already in progress. The confirmation adds no sequence
or WAL record. A failed upload cannot be acknowledged as a successful confirmation.

If Pants cannot establish whether an admitted commit became durable, `CommitAsync` throws
`PantsCommitOutcomeUnknownException` (`PantsErrorCode.CommitOutcomeUnknown`, fatal severity). This
includes a caller response deadline or cancellation after admission, a WAL rollback that cannot
be proven safe, or failure to apply a coalesced commit after WAL append. Local append and sync
failures are also outcome-unknown once WAL I/O has begun and the writer is fenced, even if rollback
restores the previous file length: the rollback itself is not known to be durable. The original
storage failure remains the exception cause for diagnosis, while the public error tells callers not
to retry the mutation before reconciliation. Failures proven to precede all WAL effects keep their
ordinary rejection classification. The runtime may still finish accepted work. For an apply failure
after coalesced WAL append, the current writer is fenced and readers continue to see the previously
published state across column families. Reopen replays the durable WAL group; reconcile its members
before retrying any mutation.

Published WAL is retired from the catalog by background maintenance, never on a commit or flush
path. A turn proves that the *published* manifest covers each candidate segment, reading the
segment frame by frame through version-pinned ranged reads admitted against the shared maintenance
memory pool, and checks only the SSTs whose recorded bounds cover a record. Only the contiguous
oldest prefix of covered segments retires, so the catalog never has a gap. Running out of the
turn's quantum, a provider timeout or busy response, and exhausted maintenance memory leave the
catalog unchanged, raise no persistence anomaly, and resume on a later turn from the last
acknowledged frame. Proof progress is process-local; after a restart retirement revalidates from
the durable catalog and manifest.

`CloudAsync` may acknowledge after the local WAL durability boundary and queues sealed WAL
objects for upload. Runtime metrics expose pending and completed uploads. `BestEffort` provides
no recovery guarantee until an explicit flush publishes an SST.

Every write validates its writer authority before cached or worker write-stall admission can reject
it, then validates again when the runtime prepares the transaction. Coalesced writes repeat that
check for each transaction. If authority expires during preparation, the already-prepared prefix
may commit, while the first transaction prepared after expiry is fenced before it reaches the WAL.

These guarantees describe acknowledgement, not transaction visibility: once a commit is
published to the in-process snapshot, readers observe it according to MVCC rules regardless of
the selected persistence boundary.

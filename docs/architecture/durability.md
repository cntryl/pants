# Durability Contracts

Pants exposes durability through immutable `PantsWriteOptions` values. Local storage accepts
`Sync`, `Buffered`, and `BestEffort`; cloud-backed storage accepts `CloudStrict`, `CloudAsync`,
and `BestEffort`.

`CloudStrict` acknowledges a commit only after the transaction's epoch-scoped WAL object is
durable and the publication catalog conditionally names that object. Recovery can therefore
hydrate an empty local cache and replay every acknowledged commit. A failed conditional catalog
write, lost lease, ambiguous publication result, or caller deadline before acknowledgement fails
the commit closed; it must not be reported as durable.

`CloudAsync` may acknowledge after the local WAL durability boundary and queues sealed WAL
objects for upload. Runtime metrics expose pending and completed uploads. `BestEffort` provides
no recovery guarantee until an explicit flush publishes an SST.

These guarantees describe acknowledgement, not transaction visibility: once a commit is
published to the in-process snapshot, readers observe it according to MVCC rules regardless of
the selected persistence boundary.

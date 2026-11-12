# Entry staging and WAL capacity

This bounded slice follows Midge `c684310d9d8ce8ecd1151c47151d4bea98b348cf`.
It changes admission timing, without changing FORMAT, WAL, SST, manifest, lease
encoding or any persisted size limit.

Midge `runtime/transaction_spill/mod.rs::TransactionWriteSet::push` validates
the decoded SST entry or range size before owning an intent. It does not reject
an operation based on every WAL envelope a later commit might use. Memory-only
and BestEffort work need no WAL; resident batches and streamed transactions
also have different envelopes. Pants now applies that same SST staging bound.
Oversized SST entries still fail immediately and preserve earlier intents.

The selected commit route retains its limits. A resident transaction whose
nested batch exceeds the 64 MiB decoded-value ceiling fails before appending.
Compression cannot bypass that ceiling. A local streamed transaction may have
written an earlier marker when a later member cannot encode: it retains the
existing unknown-outcome classification and writer fence, matching Midge
`runtime/actors/wal/spill.rs` and `common/error.rs::classify_commit_error`.

Hybrid storage has another admission step. Midge
`runtime/actors/wal/spill.rs::admit_spilled_wal` reserves the whole streamed unit
before writing its first marker when an ephemeral SST cache is active. Its
`wal/encoding.rs::record_frame_size_bound` counts actual TLV field presence and
the possible compression tag. `wal/frame.rs::encoded_frame_len` rejects an
oversized raw frame bound with `InvalidArgument`. Pants now uses the same bound
before its first hybrid WAL marker and reserves the resulting complete unit.
Epoch belongs to the segment header, and an absent expiration contributes no
expiration TLV. Existing conservative caller capacity estimates remain bounds;
they do not define the per-record framing limit.

`PantsPublicEntryCapacityTests` proves the native producer's three-byte key and
67,108,806-byte compressible value can commit Sync, flush and reopen. A smaller
control also passes. It verifies SST-legal in-memory tombstones, a larger local
spilled value, safe resident decoded-batch rejection, and local post-marker
ambiguity/recovery. Actual provider and simulated-cloud cases reject an
oversized hybrid bound before any WAL append, keep the database writable, and
reopen the exact prior and later values. The exact 64 MiB hybrid bound is an
accepted control. The legacy largest-delete WAL tests now distinguish the SST
staging maximum from the common committable WAL bound and keep auto-flush out
of the WAL replay fixture; the separate large-key flush test remains.

Actual native engine logs retained outside the repository establish two other
boundaries: a compressible 67,108,830-byte value stages successfully, but its
resident decoded batch fails with `ResourceLimit` and permits a later write.
An incompressible 67,108,806-byte value reaches physical outer-frame encoding,
fails with `InvalidArgument`, and leaves the native writer poisoned: later
Sync and explicit shutdown return `RecoveryFailed`. The distinct accepted
encoder-poison error/lifecycle behavior is handled in the subsequent WAL
encoding slice; this staging patch does not claim to implement it.

Focused validation and source hashes accompany the frozen patch. This is not
a claim that every value entropy, size, durability, cache budget and failure
interleaving has been qualified. Native provider and OS campaigns remain
separate from controlled production-provider HTTP tests.

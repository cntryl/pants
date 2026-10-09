# Current Midge baseline review

This is the historical qualification record for the pin below, not a current
certificate of complete behavior parity. The
[2026-09-04 gap analysis](MidgeBehaviorGapAnalysis.md) identifies stale test
mappings, uncovered scenarios, and concrete compaction/recovery differences.
In particular, retained-payload bounds do not establish indexed read-work or
complete-overlap compaction guarantees.

Pants feature and persisted-format compatibility is pinned to Midge 0.1.1,
commit `7d39f86217fdb07191a83bd885a514dd5ea9723f`. The driver was built with
Midge's committed `Cargo.lock` at that commit, recorded under
`Tooling/7d39f86217fdb07191a83bd885a514dd5ea9723f/Cargo.lock` with SHA-256
`6ce7f8bff009ff908fb8c9ef372b944af597a62fb6261939caac5eeba9cbc623`.

## Reviewed delta 75dcc39..7d39f86

The preceding pin was `75dcc39f7a9b87df480ed91c3a5c93fe1389ca71`. The range
holds 36 commits. No persisted format changed: FORMAT, WAL, SST, manifest,
intent log, DDL, lease, publication-catalog and cloud object-key bytes are the
same at both pins.

The fixtures were regenerated as follows. The removed driver was rebuilt at
both pins: at `75dcc39` with its recorded lock, at `7d39f86` with Midge's lock.
Each build emitted the wire and storage goldens.

- **75dcc39 rebuild vs. committed fixtures:** every deterministic artifact was
  byte-identical.
- **7d39f86 output vs. 75dcc39 output:** the only differences were the five
  semantic artifacts. These are the manifest journal fsync marker times, both
  lease holder identities and times, and the DDL operation identifiers and
  creation times.

Every other artifact is byte-identical. That covers all 14 wire goldens, both
WAL segments, the structured SST, the manifest snapshot, the intent log, the
publication catalog and the generated database tree, whose tree fingerprint is
unchanged. Midge's committed `tests/fixtures/compatibility` is byte-identical.
`pants_compat local-verify` accepts both generated databases at `7d39f86` and
leaves them unchanged.

| Midge change | Byte-affecting? | Pants impact |
| --- | --- | --- |
| `validate_entry_size`: a 64 MiB decoded-block bound on the worst-case entry, applied at staging and in the SST writer (`src/sst/encoding.rs`, `769cc2f`) | No. Admission only | Already matched by `SstCodec.ValidateEntrySize` and `EntryAdmission`; `PantsSstEntryAdmissionTests` covers the same key-length boundaries. |
| `validate_range_tombstone_size`: each bound admitted as a point key, and count, lengths, sequence and bounds (20 bytes plus bounds) within 64 MiB (`src/sst/types.rs`, `769cc2f`) | No. Admission only | Pants charged a 26-byte point header and did not bound each endpoint. It now uses the exact singleton range-block size and the per-endpoint bound. Covered by `ShouldBoundRangeTombstoneAdmissionByCompleteEncodedSize` and an encode/decode round trip at the limit. |
| WAL `record_frame_size_bound` and `decode_txn_batch_payload_bounded` (`src/wal/encoding.rs`, `edd9489`) | No. Admission and replay allocation bounds | `EntryAdmission` already bounds the worst-case WAL record. |
| Decompression capacity no longer floors to `MAX_BLOCK_SIZE` (`src/sst/compression/mod.rs`) | No. Reader allocation only | None. |
| `zstd` 0.13 to 0.14 (`zstd-sys` 2.1.0, still libzstd 1.5.7) | No. The zstd-3 and zstd-9 block goldens are identical | None. |
| Publication catalog streamed with a pre-counted length; `encode` is test-only (`src/runtime/hybrid_persistence/catalog.rs`) | No. Same pretty JSON | None. |
| Recovery reserves SST name ranges by raising `next_sst_seqs` in the manifest snapshot before any upload (`5695a93`) | No. Existing manifest field | Pants already treats `NextSstSeqs` as a high-water mark, so it never reuses a reserved name. |
| Bounded streaming WAL and cloud recovery, remote SST range reads, local-budget admission, and compaction staging (`edd9489`, `268b264`, `baea5ed`, `4b36f61`, `fde3695`, `9f2deb9`) | No | Behavior differences, tracked under #242 (#318, #326 to #330, #334 to #336). |
| WAL durability-transition hardening (`d41ca47`) | No | Runtime ordering only; no new WAL records or tags. |
| New lease tests (`src/lease/cloud/tests/concurrent_renewal.rs`) | No. Lease source unchanged | None. |
| Integration tests consolidated into 13 targets (`1c05baa`) | No | Manifest sources re-resolved. See below. |

The contract manifest keeps its `75dcc39` inventory, with each entry
re-resolved against `7d39f86`:

- **737 integration entries:** these now cite their consolidated file. Each ID
  carries the Rust module path, because two former files can share a test
  name.
- **198 public-symbol entries:** each still resolves.
- **4 entries removed:** Midge deleted these tests. One was a near-duplicate
  fsync test that `durability::durability_wal::should_persist_write_given_fsync_enabled_when_crash_occurs`
  supersedes. The other three were source-inspection checks.
- **4 entries now `revalidate`:** Midge renamed these tests and inverted what
  they assert (`edd9489`). Cloud SSTs stay remote after cache loss and during
  reads. Writes keep running once published SSTs exceed the local budget. A
  failed compaction intent save may leave an unreferenced uploaded orphan,
  which a retry never overwrites. The mapped Pants tests still assert the
  `75dcc39` behavior.

Integration tests that Midge added after `75dcc39` are not inventoried. The
gap analysis tracked in #242 covers them.

## Previous delta c5ffc2d..75dcc39

The preceding compatibility pin was `c5ffc2d3284c76b6f7cd03444a5b0a38ae8bbc33`.
Every compatibility-bearing public symbol and integration contract at the new
pin was re-inventoried. The important changes in that range are:

| Midge change | Pants evidence |
| --- | --- |
| Typed provider configuration and preflight (`1f119aa`) | Typed S3, Azure, GCS, and OCI configuration contracts plus provider preflight tests. OCI is an additional Pants provider; it does not alter Midge bytes. |
| Bounded runtime replies and late-response ownership (`230a1aa`, `ae9cfd8`) | One admitted absolute response deadline, deterministic timeout tests, and late-response metrics. |
| Durable cloud progress after caller deadlines (`0070a79`) | CloudStrict WAL/DDL/flush/compaction tests prove indeterminate timeout classification, retained internal obligations, and reopen recovery. |
| Configured local lease TTL and hardened WAL recovery (`6764d9c`, `3033653`) | Lease-timing tests use an injected clock; recovery tests cover torn catalogs, partial WALs, coverage proofs, fencing, and cache loss. |
| Bounded compaction and partitioned outputs (`64ad111`) | Compaction resource-budget tests prove `Peak <= Capacity` and `Used == 0`; output SSTs partition at the configured target. |
| Indexed candidates and bounded overlap/debt (`fb0dcdb`, `b7e2051`, `2ab9636`) | Point/scan candidate selection and compaction-planning tests bound touched files and overlapping inputs. |
| Cardinality-independent LSM work (`75dcc39`) | The N/2N/4N owned-resource test grows SST partitions and bytes while retained payload and scan/compaction pools stay bounded. The 261.2-million-entry address case is a symbolic extrapolation, not a CI allocation. |

Midge's current WAL encoder may compress the outer value of a sufficiently
large transaction-batch record. Pants now accepts and emits that legal
`COMPRESSION` tag and verifies it with a focused codec regression plus a
current-Midge-generated database fixture.

## Historical closure evidence at 75dcc39

- The machine-readable inventory contains 949 entries: 852 were marked mapped
  and 97 were assigned implementation/tooling or live-account qualification
  `n/a` rationales. Some mapped tests have since been removed and the remaining
  assertions and exclusions require revalidation; no planned entries in this
  old inventory does not mean no current behavior gaps.
- Current Midge regenerates all 31 fixture artifacts. Deterministic bytes are
  compared exactly; time-, identity-, and process-dependent artifacts are
  parsed and validated under documented semantic exceptions.
- Four alternate-process scenarios run local and simulated-cloud databases in
  both producer orders. Each engine reads, extends, flushes, reopens, and
  verifies the other engine's state. Atomic transaction batches cover put,
  insert, TTL, point delete, range delete, and multiple column families.
- FORMAT v3, SST v4, WAL, manifest, intent, DDL, lease, publication-catalog,
  cloud-key, checksum, and compression bytes remain compatible. No persisted
  format revision was introduced.
- The disk-resident retained-memory equation and deterministic ownership/resource
  proofs in [`disk-resident-scale-ladder.md`](../performance/disk-resident-scale-ladder.md)
  establish evidence for retained-memory bounds, not closure of all Midge
  cardinality-dependent work guarantees. Large scale-ladder runs remain
  separate operational qualification.

The older `c5ffc2d` benchmark reader is retained only for reproducible
like-for-like historical performance artifacts. It is not the current feature
or compatibility claim.

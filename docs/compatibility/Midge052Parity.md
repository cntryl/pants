# Midge 0.5.2 parity evidence

The target is matching observable correctness, behavior and performance. On
2026-11-04, fetched upstream main resolved to Midge
`c684310d9d8ce8ecd1151c47151d4bea98b348cf` (0.5.2) and lsm-spec
`2376ba9ddc7a9dc55d5988babc3847089ea8d3e1` (FORMAT 9). The latter has exactly
the same tree as `7748fd1`, whose unchanged fixtures are already in Pants.

The existing worktree contained the FORMAT 9 migration before this follow-up.
Its persisted contracts, compatibility break and fixture scope are documented in
[LsmSpecFormat9.md](LsmSpecFormat9.md). This document records additional gaps
proven against that worktree; it does not attribute the earlier migration to this
follow-up or certify complete parity.

## Source-reviewed changes

| Contract | Midge/spec source | Pants regression and result |
| --- | --- | --- |
| A valid primary WAL catalog selects authority without decoding the mirror; unsupported primary versions fail without repair | `src/runtime/hybrid_persistence/catalog.rs::load_and_repair_catalog_with_authority`; spec `behavior/cloud-storage.md` section 5 | `CloudWalCatalogAuthorityTests`: provider read-only and repair modes plus simulated-cloud hydration. Three cases failed on an unsupported mirror before the fix; primary-version rejection was already correct. |
| Catalog history follows explicit links, permitting skipped allocated segment IDs and a next ID beyond the head | `src/wal/cloud_catalog.rs::validate`; spec `format/wal.md` section 1.2 | `ShouldAcceptLinkedCatalogHistoryWithSkippedAllocatedSegmentIds` failed before removing legacy contiguous-ID/exact-next checks. Hydration also verifies copied segment bytes and retention of the reservation floor. |
| Retained WAL entries may have epoch zero below a positive catalog fencing epoch | `src/wal/cloud_catalog.rs::validate`; spec `schema/wal-catalog.schema.json` | The linked-history decode/hydration tests include epoch-zero predecessors. `PantsCloudObjectLayout.WalSegmentObjectKey` now accepts zero to derive their canonical keys; lease and catalog authority still require positive epochs. This relaxes the public key helper's previous argument rejection. |
| Malformed catalog structures are corruption eligible for mirror repair, rather than leaking framework exceptions; duplicate/unknown fields are rejected | `src/wal/cloud_catalog.rs` typed catalog with `deny_unknown_fields`; spec `schema/wal-catalog.schema.json` | `CloudWalCatalogAuthorityTests`: missing required fields produced two failing repair cases before the fix. Further cases cover null structures/digests and unknown/duplicate fields. |
| WAL ledger edits have one external variant, known fields and required header facts | `src/wal/ledger.rs` and `src/wal/ledger_store.rs`; spec `format/wal.md` section 1.1 | `WalLedgerValidationTests`: five failing cases proved acceptance of malformed edit tags/fields and missing epoch. Optional `SetAside.cut` remains accepted. |
| Complete intents validate captured SST identities and optional runtime metadata field types before recovery | `src/metadata/journal.rs`; spec `format/metadata.md` | `Format9IntentAdmissionTests`: seven malformed complete frames were accepted before the fix, including null/missing captured identities and invalid completeness/checksum/expiration types. |
| Manifest family lifecycle, unique live names and flush-frontier ownership remain consistent | `src/metadata/manifest.rs::validate_persisted`; spec `format/manifest.md` | `Format9ManifestAdmissionTests`: seven invalid lifecycle/identity cases and two orphan allocator/retired-family cases were accepted before the fix. Tests open real databases under Strict and Salvage policies. |
| Same-level overlap repair exceeds configured fan-in through bounded raw scratch runs | `src/compaction/mod.rs` and `src/compaction/repair.rs` | `PantsCompactionRepairTests`: nine overlapping L1 files with maximum input two failed with a resource-limit error before the fix; repair/reopen succeeds after it. `CompactionRepairScratchTests` covers multiple passes, raw history/TTL/ranges, capacity failure and cleanup. Scratch is transient and never manifest authority. |
| Failed scratch cleanup cannot release its admission claim or admit another repair | `src/compaction/repair.rs` and `src/runtime/actors/compaction.rs` | `CompactionRepairScratchRetentionTests` proves stale scratch is preserved/refused, unrelated flush staging is permitted, and retained repair bytes keep the hybrid staging reservation charged. |
| Admitted DDL retains runtime ownership after caller timeout/cancellation; uncertain journal effects fence later mutations until reopen | `75031c3b` and `src/engine/mod.rs` DDL response handling | `PantsLocalDdlAuthorityTests`: six cases failed with definite timeout/cancellation/I/O exceptions before the fix. Regressions cover eventual catalog visibility, fencing of later DDL and BestEffort writes, and recovered committed authority. Cloud DDL commit/adoption uses the same journal ambiguity fence to prevent duplicate edit replay. |
| Cloud family drop wakes writers already parked on memtable pressure | `8b96ce0d` family-drop pressure release | `PantsCloudDropPressureTests`: three individually subthreshold families exceed the aggregate ceiling; after discarding one, a new waiter succeeded but an existing waiter timed out before the fix. Both now proceed after the pressure signal. |
| Point reads avoid materializing unrelated values from a warm cached SST block while validating its complete contents | `src/sst/fs/reader_io` current point-read implementation; spec `format/sst.md` | `SstPointReadAllocationTests` plus corruption/restart regressions. Cache content is borrowed for the read; the returned value remains owned. See [current performance evidence](../performance/current-midge-engine-reads.md) for measurements. |

## Final local validation, 2026-11-04

- `dotnet restore --locked-mode` passed without dependency changes.
- `dotnet format Pants.slnx --no-restore` and the subsequent
  `--verify-no-changes --no-restore` check passed.
- `dotnet build Pants.slnx --configuration Release --no-restore -m:1` passed
  with zero warnings and errors.
- The complete Release test assembly, with Sqrzl enabled, passed **3,163** tests,
  with zero failures and two skips in 8 minutes 35 seconds. The skips were live
  AWS S3 and the opt-in extended soak. All **15** Sqrzl cases passed: 14 provider
  qualification cases and one crash/soak smoke case. The other committed
  deterministic crash/soak smoke cases also ran normally.
- `git diff --check` passed.

The final result is in the ignored local artifact
`test/Pants.Tests/TestResults/midge-052-parity-final.trx`. The emulator used its own
Compose project and ports 19900/19901. No builds replaced test binaries during
the final run. These are local results; no hosted CI or release was performed.

## Evidence boundaries and remaining qualification

The per-case [parity inventory](MidgeTestParityInventory.json) is still pinned to
older source, with thousands of cases not behaviorally reviewed. It has not been
mechanically repointed or counted as current coverage. Current source and invariant
coverage must be refreshed before claiming complete behavioral equivalence.

FORMAT 9 fixture decoding and local regression tests do not prove alternating
Pants/Midge writer interoperability. Whole-database FORMAT 9 producer/consumer
qualification, fault campaigns and all supported platforms still need explicit
evidence. Older wire fixtures retain provenance and rejection checks; they do not
make old FORMAT databases compatible with current writers.

The subsequent [correctness-first pass](MidgeParityExecutionOrder.md) adds
[bounded local writer alternation](Format9Interoperability.md), including
WAL-only handoffs in both producer orders and spilled non-default-family WAL.
That evidence supersedes the absence of local producer/consumer evidence in
this earlier snapshot, within its documented workload and platform scope.

Matching benchmark names does not establish performance parity. The historical
0.1.1 aggregate report remains historical. Current comparisons must align dataset,
storage, transaction, durability and measurement boundaries at the same revisions,
then run repeatedly on an idle host. Allocation reductions and smoke runs establish
only their named workloads, not database-wide latency/throughput equivalence.

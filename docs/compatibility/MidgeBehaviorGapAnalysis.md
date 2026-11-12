# Midge behavior gaps and differences

The following ledger is a historical audit snapshot. Current writers target
[FORMAT 9](LsmSpecFormat9.md) and current source-reviewed fixes are tracked in
[Midge 0.5.2 parity](Midge052Parity.md). Statements below that a feature is absent
must be rechecked against the current worktree before directing implementation.

Updated 2026-10-21. The target is **the same observable database behavior and
system invariants as current Midge, expressed through idiomatic .NET APIs**.
Different implementation languages do not excuse different durability,
recovery, visibility, fencing, resource-limit, or error-classification
contracts.

This is the working ledger for the full semantic comparison and subsequent
test backfill. The prior 2026-09-04 review remains useful evidence, but it did
not audit the current Midge surface. The current inventory contains 4,241
Midge test cases; only 421 are behaviorally reviewed as `verified` or
`verified-adapted`. The full audit and backfill are not complete. An absent
test name is not proof of an absent feature, and a mapped test name is not
proof that its assertions cover the Midge scenario.

## Current audit baseline — 2026-10-21

| Input | Revision / count | Audit meaning |
| --- | --- | --- |
| Midge implementation and test inventory | `d582fd1127741fd69067914423ec76ad6adf0657`; 4,241 cases | Current test source: 3,377 library, 652 integration, 193 fault-injection, 12 doctests, and 7 binary cases. |
| Pants implementation and test inventory | `fc3baa381dc555b60ffad96a7cc87c6320f72419`; 2,902 distinct test names | The current worktree adds seven behavioral test methods for fragmentation, cloud staging, prior-epoch DDL, 10,000-target streaming, and high-cardinality read boundaries; the inventory records 2,909 including these additions. |
| Per-case parity ledger | [`MidgeTestParityInventory.json`](MidgeTestParityInventory.json) | 277 `verified`, 144 `verified-adapted`, 333 `candidate`, and 3,487 `unmapped`. `candidate` is not coverage. The inventory has no `not-applicable` disposition yet; every such disposition needs an explicit reason. |
| Midge system invariant catalog | 63 IDs across authority, transactions, WAL, SST, cloud, lifecycle/backup, resources, progress, accounting, and formats | The catalog's companion evidence JSON says its source revision is `bab5b3ccdee16af9502cba4e032a41b6a0e69947`, not current Midge `d582fd1`. The catalog also says only selected WAL/cloud rows were refreshed. Re-resolve all 63 invariants against current source and tests before using them as closure evidence. |
| Pants persisted-format qualification | Midge `7d39f86217fdb07191a83bd885a514dd5ea9723f` | Historical format/fixture qualification only; it does not establish current behavior parity. |

The 4,241 source cases are not 4,241 independent contracts. The target is to
review every case, group equivalent scenarios by observable invariant, and then
ensure each applicable behavior has executable Pants evidence. Repository
tooling or Rust-only implementation checks may be disposed as not applicable
only with a concrete rationale. Source-level tests that exercise database
behavior, error handling, resource bounds, persistence, recovery, or concurrency
remain in scope.

Current case status by source target:

| Midge test kind | Total | Verified | Adapted | Candidate | Unmapped |
| --- | ---: | ---: | ---: | ---: | ---: |
| Library | 3,377 | 10 | 54 | 14 | 3,299 |
| Integration | 652 | 247 | 74 | 209 | 122 |
| Fault injection | 193 | 20 | 7 | 110 | 56 |
| Doctest | 12 | 0 | 2 | 0 | 10 |
| Binary | 7 | 0 | 7 | 0 | 0 |
| **Total** | **4,241** | **277** | **144** | **333** | **3,487** |

The library-test backlog is concentrated in `runtime` (999 cases), `sst`
(544), `storage` (501), `wal` (320), `engine` (213), and `lease` (192). These
counts prioritize review; they do not imply missing product features. The
previously recorded G/D findings below are specific source-reviewed gaps and
differences. They must be rechecked against the current Midge and Pants heads
as the complete invariant and per-test review proceeds.

## Remediation progress

The gap/difference descriptions below preserve the original audit findings.
This table records remediation demonstrated by the cited regressions. Its
individual claims still need to be rechecked against the current full baseline.

| Item | Status | Behavioral evidence |
| --- | --- | --- |
| D01: conflicting logical compaction versions | Fixed | [Conflict tests](../../test/Pants.Tests/Storage/CompactionVersionConflictTests.cs): all 18 cases failed before the fix and pass afterward. Both merger paths reject differing values/deletion/expiration at one key/sequence, including hidden versions, and collapse identical copies. Streaming validation retains its existing one-entry reservation. |
| D01: partial output cleanup and reopen | Covered | [Recovery regression](../../test/Pants.Tests/Storage/PantsCompactionConflictRecoveryTests.cs) observes a durable output before encountering the conflict, then checks output cleanup, unchanged input bytes/manifest ownership, and successful reopen of unaffected data. |
| D02: complete target overlap above the source input limit | Fixed at current HEAD | [Planner and stream tests](../../test/Pants.Tests/Storage/CompactionStreamsTests.cs): the planner retains all 10,000 overlapping targets with `MaximumInputFiles=1`; a separate 65-target execution check observes at most one source and one chained target reader. The focused Release filter passed 3/3 before tightening the 10,000-target case to limit 1; that exact case then passed 1/1. |
| Fragmentation slice: soft-roll cut continuity, final-key range boundary, and split-budget failure | Covered/adapted at current HEAD | [Merger tests](../../test/Pants.Tests/Storage/StreamingCompactionMergerTests.cs) now compare the full raw point history and exact range placement. [Fragmentation integration tests](../../test/Pants.Tests/Storage/PantsFragmentationParityTests.cs) verify consecutive persisted outputs, endpoint adjacency to an unchanged neighbor, source retention on oversized cuts, and cleanup after invalid outputs. [Cloud staging tests](../../test/Pants.Tests/Cloud/PantsCloudCompactionStagingTests.cs) verify recovery of an earlier drained output after a later partition exceeds the window. The combined focused Release filter passed 51/51. |
| G01/D08: redundant cloud WAL catalog recovery | Covered; stale implementation gap disproved, fail-closed error context aligned | Midge and Pants simulated-cloud reopen tests now assert acknowledged-value recovery after local-cache loss, higher fencing epoch, quiescent copy convergence, typed corruption when both copies are unusable, and no mutation of either damaged copy. Provider tests cover repair, segment recovery, CAS, and lease fencing. The red check exposed that Pants reported only the primary decode error; both provider and simulated-cloud paths now report both copy states and decode causes. Focused Release filter passed 25/25. |
| G02: partial compaction publication and partitioned recovery | Covered/adapted; live Sqrzl execution pending | [Provider partial-publication test](../../test/Pants.Tests/Cloud/ProviderCloudPartialCompactionPublicationTests.cs) fails the second SST PUT, proves no partial manifest authority, removes local SSTs, reopens, cleans the durable orphan, and checks every key. The focused Release test passed 1/1. [Sqrzl qualification](../../test/Pants.Tests/Cloud/SqrzlCloudProviderQualificationTests.cs) covers multi-partition compaction and all-key recovery after local-cache loss; it was not run because the local Docker daemon is unavailable. Midge simulated-cloud cleanup is immediate, while Pants provider cleanup completes through intent replay after reopen. |
| G03: prior-epoch ambiguous DDL prepare | Fixed at current worktree | Pants now persists the prepare writer epoch. Startup fences the registry, checks whether the operation is already authoritative, and discards an absent prepare from an earlier epoch instead of replaying it under the successor. The red regression reproduced an unintended family creation. Simulated-cloud and provider tests cover stale prepare removal, absent operation identity, and unchanged DDL epoch; focused three-test filter passed 3/3. |
| G04: retained WAL after tombstone GC | Covered/adapted; no engine change required | [Reopen regression](../../test/Pants.Tests/Storage/PantsRetainedWalTombstoneRecoveryTests.cs) removes the target from compacted SSTs, restores the retained WAL image, and checks the target stays absent while anchor values survive. The focused Release test passed 1/1. Midge pins the WAL with an idle-family mutation; Pants restores an identical retained WAL image directly. |
| G05: acknowledged data after partial WAL no-space | Covered/adapted; no engine change required | [WAL writer fencing tests](../../test/Pants.Tests/Storage/Wal/PantsWalWriterFencingTests.cs) inject a no-space failure after half a WAL payload is written, verify the physical tail is rolled back, acknowledged rows survive, health degrades, later writes are fenced, and reopen restores write availability. The focused Release class passed 9/9. Pants surfaces `PantsCommitOutcomeUnknownException` with the `PantsNoSpaceException` cause; the storage invariants match. |
| All other G/D items | Re-review required | No closure is inferred from these slices or from broad suite totals; the remaining findings were recorded against an older source snapshot. |

Slice 1 adds 20 behavioral cases. Its focused run, including the existing
merger suites, passes all 36 cases. The red phase was 18 failures and one
already-passing reopen test; the partial-output integration regression was
added as additional verification. No persisted-format revision, public API
change, or integrity test is part of this compaction/recovery slice.

Slice 1 validation: formatting verification passed; the Release build had zero
warnings/errors; all **1,626 non-Sqrzl tests passed** (zero failures or skips).
Sqrzl qualification was not rerun for this local compaction/recovery slice.

Pre-merge validation on 2026-09-04 subsequently passed the complete Release
suite against isolated Compose Sqrzl: **1,639 passed, zero failures/skips**,
including all 13 provider qualification cases. Formatting, the Release build,
and packing the three 2.0.0 packages also passed; no packages were published.

```sh
dotnet format Pants.slnx --verify-no-changes --no-restore
dotnet build --configuration Release --no-restore
dotnet test test/Pants.Tests/Pants.Tests.csproj --configuration Release --no-build --no-restore --filter 'Category!=Sqrzl'
```

## Previous audit inputs — 2026-09-04 (historical)

The following records the first source-reviewed audit and is retained for
provenance. It is superseded as the current comparison baseline by the table
above.

- Supplied Midge inventory: `inventory.md`, generated
  `2026-09-04T20:23:10Z`, SHA-256
  `45deb3bec7fe614e551a3520092bc3f43725db67f4882b98c1a29fa72860053c`.
  It lists **1,902 source tests across 126 source files and 777 integration
  tests**, totaling 2,679 functions, not 2,679 distinct contracts.
- Midge checkout: `68821ee84a2c5d26ad32123905a7d4b84751f571`, with local
  modifications. In particular, `tests/sst_regressions.rs` and
  `src/sst/fs/regression_tests.rs` are untracked additions, and related SST
  and transaction implementation files are modified. Those new expectations
  are provisional, not contracts established by that commit alone.
- Pants: `cleanup` at `4ec8a17e6f41e9a617efe041854af0b8b5eb1947`, including
  the uncommitted cloud identity/range and integrity-test-removal slices.
  No engine fixes were made as part of this analysis.
- Historical [contract manifest](../../test/Pants.Tests/MidgeContractManifest.json):
  pinned to Midge `75dcc39f7a9b87df480ed91c3a5c93fe1389ca71`; 751 integration
  entries and 198 public-symbol entries, with 852 marked mapped and 97 `n/a`.
  It contains no mapping of the 1,902 source tests. All 198 public-symbol
  entries point to a removed shape-checking test; 202 entries in total cite
  removed shape/architecture tests. These are invalid coverage claims, not
  202 missing features. There are also 33 integration test names in the
  supplied inventory absent from that older manifest, including non-engine
  tooling checks and behaviors already tested under different Pants names.

The inventory's “Benches & Stress: (none)” is not evidence of missing Midge
benchmarks: its `Cargo.toml` and `benches/` contain registered benchmarks.
Benchmark inventory completeness needs separate review; it is not counted as
a Pants behavior gap here.

## 1. Previously identified behaviors without confirmed Pants regression coverage

These are candidate gaps from the 2026-09 source review. Reconfirm each
scenario against Midge `d582fd1` and Pants `fc3baa3` before treating it as an
open gap or closing it. A missing scenario test does not mean the underlying
operation is unimplemented. Groups combine related Midge tests; they are not a
test-count parity target.

### G01 — Recover and repair redundant cloud WAL catalogs — first

Midge [fault-injection tests][m-failure]:

- `should_recover_from_valid_catalog_mirror_when_primary_catalog_has_torn_tail`
- `should_fail_closed_when_both_cloud_wal_catalog_copies_are_invalid`

This was an open finding in the 2026-09 review. Current Pants tests cover the
same recovery outcomes in both simulated-cloud reopen and provider hydration;
see the G01/D08 remediation row above. The full-engine scenario advances the
fencing epoch after restoring the primary and checks convergence after shutdown.
Both-invalid recovery now reports the unusable copies and leaves their bytes
unchanged. The cloud-catalog behavior is covered at the current worktree.

### G02 — Recover a partially uploaded compaction partition set — first

Midge [failure injection][m-failure] and [provider engine qualification][m-provider-engine]:

- `failure_injection::should_preserve_remote_inputs_after_partial_compaction_upload`
- `should_rollback_partition_set_after_partial_sqrzl_compaction_upload`
- `should_recover_partitioned_compaction_from_sqrzl_s3_after_local_cache_loss`

Pants [provider partial-publication test](../../test/Pants.Tests/Cloud/ProviderCloudPartialCompactionPublicationTests.cs)
fails the second SST PUT through the Azure provider, verifies that only old
inputs remain manifest-authoritative, removes local SSTs, and checks durable
intent replay removes the first uploaded orphan and preserves all keys. This
adapts Midge's immediate local-simulator cleanup to Pants' durable remote
recovery. [Sqrzl qualification](../../test/Pants.Tests/Cloud/SqrzlCloudProviderQualificationTests.cs)
compacts to multiple S3 partitions, opens with a replacement cache, and checks
every key. The source-level behaviors are covered; the Sqrzl test has not been
run in this worktree because the Docker daemon is unavailable. A live-S3
second-upload failure is the remaining provider-specific validation boundary.

### G03 — Abort prior-epoch ambiguous DDL prepares — fixed at current worktree

Current Midge covers this in the [fault-injection test][m-failure]
`cloud_ddl_two_phase_hardening::should_abort_prior_epoch_ambiguous_ddl_when_reopening_after_crash_before_remote_cas_submission`
and the [DDL reconciliation test][m-ddl-source]
`should_abort_prior_epoch_ambiguous_prepare_after_successor_fence`. The prepare
is durably marked ambiguous before the provider CAS, but the operation is
absent. A successor fences the registry first; it then discards the stale
prepare, leaves the family absent, and does not invent the old edit under its
own lease.

Pants previously redrove every ambiguous startup prepare when the remote DDL
epoch matched, even when a successor lease had already fenced the old writer.
The prepare now records `WriterEpoch`; startup preserves and applies an
operation already present in the registry, but clears an absent prepare from a
prior epoch. [Simulated-cloud and provider regressions](../../test/Pants.Tests/Cloud/CloudDdlTwoPhaseTests.cs)
cover the crash boundary, the successor fence, and an already-advanced DDL
epoch. The fail-before-fix test created the family unexpectedly; the focused
three-test Release filter passed 3/3.

### G04 — Do not resurrect a GC'd value from a retained WAL — covered/adapted

Midge [durability atomicity test][m-atomicity]:
`should_not_resurrect_manifest_covered_value_from_retained_wal_after_tombstone_gc`.

Pants [retained-WAL recovery](../../test/Pants.Tests/Storage/PantsRetainedWalTombstoneRecoveryTests.cs)
now covers the same interaction. It captures a valid WAL before flushing a
value, flushes the tombstone, compacts the value and tombstone out of SSTs,
restores the retained WAL, and reopens. It verifies the target stays absent and
the unrelated anchor keys remain visible. Midge retains the WAL through an
unflushed idle-family write; Pants uses a byte-identical WAL image. The focused
Release test passed 1/1.

### G05 — Preserve acknowledged data after a physical partial-write ENOSPC — covered/adapted

Midge [failure test][m-failure]
`should_preserve_acknowledged_state_when_no_space_tears_wal_frame` seeds
acknowledged values, writes half of a later frame, and returns no-space. It
requires the old WAL length and values to survive, the engine to degrade and
fence follow-up commits, and a reopened engine to accept later durable writes.

Pants [WAL writer fencing tests](../../test/Pants.Tests/Storage/Wal/PantsWalWriterFencingTests.cs)
exercise the same partial-frame boundary. They verify the old file length,
value visibility, absent failed and follow-up values, degraded health, and
successful writes after reopen. Pants wraps the no-space cause in
`PantsCommitOutcomeUnknownException` because the append crossed the ambiguous
I/O boundary; this is the documented exception adaptation. The focused Release
class passed 9/9. This revalidates existing behavior and adds ledger evidence;
no implementation change was needed.

### G06 — Preserve cold-family tombstones while hot-family writes race maintenance — covered

Midge [column-family test][m-cf]:
`should_preserve_cold_tombstone_given_hot_cold_compaction_when_reopening`.

Pants [cold-family tombstone test](../../test/Pants.Tests/Storage/PantsColdFamilyTombstoneTests.cs)
uses deterministic barriers to overlap compaction with phase-B cold-family
writes and tombstones while hot-family maintenance proceeds. It confirms that
at least one tombstone replaces a key already persisted by flush, then checks
every modeled key and a full scan for both families before and after reopen.
The workload differs from Midge's seeded key/operation sequence, so this is a
verified adaptation. Its focused Release test passed 1/1; no implementation
change was needed.

### G07 — Bound compaction and read work by active streams, not total files — partially covered

Midge [compaction tests][m-compaction], [strategy tests][m-strategy], and
[read-view tests][m-read-view]:

- `should_plan_ten_thousand_target_files_with_one_source_stream`
- `should_compact_sixty_five_target_files_with_two_merge_heads`
- `should_keep_compaction_work_bounded_across_ten_thousand_targets`
- `should_prove_read_work_bounds_across_synthetic_manifest_cardinalities`
- `should_select_at_most_two_adjacent_files_per_lower_level_for_point_read`

Pants now covers the exact 10,000-target planner cardinality with a one-source
limit, executes a 65-target merge while tracking a two-reader peak, and executes
a 10,000-target streamed merge while checking all 10,001 entries, a two-cursor
peak, and budget release. Its 100,000-file boundary lookup also returns exactly
the two neighboring files with fewer than 64 probes. The focused Release
`CompactionStreamsTests` class passed 8/8 and the combined compaction/read-view
filter passed 23/23.

The broader synthetic read-work case remains a candidate, not closed. Pants has separate
single-level point/range work bounds (including one million range candidates)
and cache-reuse tests, but has not yet matched Midge's multi-level cardinality
matrix and combined point/range/cursor-bound assertions. Source review also
shows a scan-path difference to qualify: Pants creates one lightweight
`AsyncSstScanSource` per candidate and reads each candidate's SST metadata and
range tombstones before producing the scan; Midge keeps one lazy SST cursor per
ordered lower level and adds tombstones as each file opens. Pants releases each
metadata reader before opening data blocks and admits retained metadata to the
scan budget, but this does not yet prove equivalent startup work or failure
behavior for very large scans.

Pants [owned resource scaling](../../test/Pants.Tests/Storage/PantsOwnedResourceScalingTests.cs)
measures retained payload. The focused compaction and read-view cases above
close the 10,000-target plan, active-reader, and high-cardinality point-boundary
scenarios. The composite multi-level read-work proof and scan metadata startup
behavior remain open under G07. These tests are not a claim of having run a
production-scale performance qualification.

### G08 — Enforce L0 admission and manual debt-drain semantics — backfilled

Midge [backpressure test][m-backpressure]
`should_bound_published_l0_when_background_compaction_is_disabled` and
`runtime::event_loop::tests::should_wait_for_active_compaction_before_declaring_debt_clear`
now map to Pants behavior coverage. Pants checks the `trigger + 10 queued + 1
active` L0 ceiling after every flush, stall recovery without manual compaction,
manual drain of a single L0 file, resumed writes, and a second `CompactAllAsync`
waiting behind a failpoint-held active compaction. The L0 admission test uses a
one-byte L1 target so it also creates deeper debt while critical L0 recovery is
needed. The focused Release filters passed 43/43.

The selector advances critical L0 work across families after each scheduled
plan. Its rotation and wraparound are tested, but the Midge multi-family actor
scenario still needs an end-to-end regression proving that two simultaneously
critical families are drained in round-robin order.

### G09 — Reject filesystem operations through symlinked components — first

Midge [real IO][m-real-io] / [staging IO][m-staging]:

- `should_reject_open_through_symlinked_existing_component_given_real_filesystem_when_opening`
- `should_reject_rename_through_symlinked_parent_given_staging_filesystem_when_renaming`

Related cases in `src/storage/filesystem.rs` and `src/lease/cloud/tests.rs`
reject writes and simulated-lease reads/removals through links. No symbolic-link
or reparse-point coverage was found in Pants. Existing lexical SST-name checks
do not cover resolved paths. Start with staging/open, then lease read/delete;
assert a sibling target is untouched. **Staging is reproduced in D06; the
other operations still need individual validation.**

### G10 — Exercise federated credential edge cases, not only happy-path exchange — next

Midge `src/storage/providers/{gcs,s3,azure}.rs` names specific obligations:

- GCS: `should_read_url_sourced_external_account_subject_token_with_headers`,
  `should_send_workforce_user_project_in_sts_options`,
  `should_follow_external_account_impersonation_contract`,
  `should_reject_empty_json_external_account_subject_token`,
  `should_reject_missing_malformed_or_expired_impersonation_expiry`,
  `should_reject_executable_external_account_credentials`, and
  `should_reject_aws_external_account_before_treating_metadata_url_as_subject_token`.
- S3: `should_accept_bracketed_eks_ipv6_container_credential_endpoint`,
  `should_reject_container_relative_uri_that_can_replace_credential_host`,
  `should_try_every_validated_container_credential_address`, and
  `should_reject_temporary_aws_credentials_without_valid_expiration`.
- Azure: `should_derive_sovereign_authority_when_blob_endpoint_identifies_cloud`,
  `should_preserve_explicit_authority_override_given_sovereign_blob_endpoint`,
  `should_reject_empty_azure_workload_identity_assertion`, and
  `should_reject_missing_or_invalid_managed_identity_expiry`.

Nearest Pants suites: [GCS](../../test/Pants.Tests/Cloud/GcsCredentialSourceTests.cs),
[S3](../../test/Pants.Tests/Cloud/S3CredentialSourceTests.cs),
[Azure](../../test/Pants.Tests/Cloud/AzureCredentialSourceTests.cs).
For example, GCS tests a file-sourced text token exchange but not URL headers,
workforce billing, or impersonation. The
[GCS implementation](../../src/Pants.Core/Cloud/Internal/Providers/Credentials/Gcs/GcsExternalAccountTokenProvider.cs)
already implements several of those paths: this is **not** a claim that
federation is missing. Drive the credential provider through controlled HTTP
responses; assert requests, token validity, errors, and absence of secret leakage.

### G11 — Surface corruption encountered after a spill scan has yielded a row — first

Midge `src/runtime/transaction_spill/tests.rs`:
`should_surface_late_spill_corruption_from_key_cursor_item`.

Pants [spill operation tests](../../test/Pants.Tests/Transactions/Spill/TransactionOperationSourceTests.cs)
cover corrupt indexes, and [spill hardening tests](../../test/Pants.Tests/Transactions/Spill/PantsTransactionSpillHardeningBehaviorTests.cs)
cover commit atomicity after a run read failure. Neither asserts that a cursor
yields the first good key and then reports a corrupt later frame instead of
silently truncating iteration. Exercise forward/reverse transaction scans,
terminal error behavior, and release of the retained spill read view.

### G12 — Isolate cached SST identities when a path/name is reused — first

Midge `src/runtime/read_resources.rs`:
`should_not_reuse_reader_when_same_name_has_different_manifest_identity`;
`src/sst/fs/reader_io/tests.rs`:
`should_isolate_replaced_sst_cache_entries_by_generation_identity` and
`should_finish_scan_from_original_handle_when_sst_path_is_replaced_mid_scan`.

Pants [reader cache tests](../../test/Pants.Tests/Storage/SstReaderCacheTests.cs)
cover retirement and concurrent opens; [streaming scan tests](../../test/Pants.Tests/Storage/PantsStreamingScanHardeningTests.cs)
cover flush/compaction with stable handles; remote SST replacement also has
coverage. No local warm-cache, same-name/different-manifest-identity scenario
was confirmed. Its [reader cache](../../src/Pants.Core/Storage/Internal/Cache/SstReaderCache.cs)
is keyed by filename, so review invalidation guarantees before claiming an
engine bug. New reads must not reuse stale data, while an existing scan must
retain its original view.

### G13 — Cover deep-trie empty keys and exact record-size admission — provisional

The supplied inventory includes these locally added Midge tests:

- `tests/sst_regressions.rs::should_preserve_empty_key_through_engine_flush_with_deep_trie`
- `tests/sst_regressions.rs::should_reject_oversized_value_before_transaction_stages_it`
- `src/sst/fs/regression_tests.rs::should_return_all_prefix_blocks_when_trie_exceeds_256_levels`
- `src/sst/fs/regression_tests.rs::should_roundtrip_maximum_decoded_entry_when_writing_sorted_or_unsorted`

Pants has extended-key encoding and corrupt-length tests, but no confirmed
empty-key + 300-level structured-key + flush/reopen regression, or equivalent
early admission test at the exact decoded-entry limit. Review the Midge changes
before treating their size limit as the new baseline; then cover both resident
and spilled transactions and both sorted/unsorted SST writer paths. **See D10.**

## 2. Previously identified behavior differences requiring current-head review

In the 2026-09 review, “reproduced” meant an isolated executable probe ran
against that Pants Release build. “Source-established” meant a different
algorithm or contract was visible in source without an end-to-end reproduction.
Those labels do not establish the behavior at the current heads; revalidate
each before using it to direct a fix.

### D01 — Conflicting equal-sequence versions are silently resolved

**Reproduced; correctness difference; not language-driven.**
Midge's `should_fail_compaction_when_equal_key_and_sequence_have_conflicting_values`
requires corruption and no published output. Two Pants SSTs containing key
`same`, sequence `7`, values `first` and `second` merged successfully to
`first`. [StreamingCompactionMerger](../../src/Pants.Core/Storage/Internal/Compaction/Compaction/StreamingCompactionMerger.cs)
breaks ties by input order and lacks conflicting-content validation.
The existing differential merger tests compare Pants to another Pants merger;
they do not establish Midge parity. Add independent expected-result assertions
for identical duplicates, conflicting values, and conflicting deletion/TTL metadata.

### D02 — Complete target overlap survives the source-input cap

**Fixed at current Pants HEAD.** Midge's [strategy][m-strategy] plans a broad
source plus all 10,000 overlapping targets with `max_compaction_input_files=1`.
Pants now has the matching [10,000-target planner test](../../test/Pants.Tests/Storage/CompactionStreamsTests.cs),
also with `MaximumInputFiles=1`, and retains all 10,001 inputs. Its planner
shrinks the source set while preserving the complete target span, with one
source as the progress floor. The same file contains an execution test across
65 targets; it observes no more than one source reader plus one chained target
reader. The focused Release filter passed 3/3; after tightening the planner
fixture to the exact Midge source limit, that case passed 1/1.

This closes the earlier `MaximumInputFiles` and reader-fan-in difference for
these scenarios. It does not close D07's separate metadata-search work bound.

### D03 — Ordinary L0 work wins over downstream compaction debt

**Fixed for within-family priority; multi-family behavior is partially covered.**
Pants now checks inner levels from deepest to shallowest before selecting
ordinary L0 work. The actor passes critical slot-debt families separately, and
the disk planner selects their L0 work ahead of inner debt. The backpressure
regression creates a one-byte L1 target to ensure deeper debt exists while L0
pressure clears. A family cursor rotates critical work and the selector test
covers rotation and wraparound; Midge's exact simultaneous-critical-family
actor test still needs end-to-end coverage. Midge's higher priority for
complete-bound layout repair is a separate open contract under D11.

### D04 — Disabling background compaction does not enforce Midge's hard L0 ceiling

**Fixed and covered through the public API.**
Pants now reserves `L0 trigger + 10 immutable generations + 1 active
generation`, stalls admission at the ceiling, runs critical L0 compaction even
when background compaction is disabled, and resumes writes as slots are freed.
The backpressure regression checks the published ceiling after each flush and
accepts twice the ceiling's worth of writes. Its deeper-debt setup also checks
that ordinary inner-level work cannot delay critical L0 recovery.

### D05 — CompactAllAsync can return with L0 files remaining

**Fixed and covered, including single-file L0 and an active worker.**
Pants manual debt drain selects a single soft L0 file when no deeper debt has
priority and waits for an already-running compaction before declaring debt
clear. `ShouldLeaveNoL0FileAfterCompactAllForSingleFlush` covers the former;
`ShouldWaitForActiveCompactionBeforeDeclaringDebtClear` holds publication at a
failpoint while a second drain waits, then checks both completion and zero L0.

### D06 — Staged IO follows symlinked parent directories

**Reproduced on macOS; filesystem-policy difference.**
Pants `AtomicStagedFile.Write(link/published, payload)` succeeded and wrote
inside the link's target directory. Midge's [staging test][m-staging] rejects
the analogous traversal. Pants
[AtomicStagedFile](../../src/Pants.Core/Storage/Internal/IO/AtomicStagedFile.cs)
normalizes the path lexically, then uses ordinary directory/open/rename calls;
normalization is not link resolution. .NET and Rust need different platform
plumbing, but the refusal behavior is portable. Review POSIX symlinks and
Windows reparse points, including races between validation and use.

### D07 — SST read work is not indexed by level in the same way

**Source-established resource difference; not an observed wrong-value result.**
Midge shares an indexed SST read view, bounds adjacent lower-level point
candidates, and uses one sequential cursor per complete lower level.
Pants [SnapshotReadPath](../../src/Pants.Core/Transactions/Internal/SnapshotReadPath.cs)
filters the family's full visible-file array for each query; point candidates
are then sorted. [LocalDiskStore.CreateScanSources](../../src/Pants.Core/Storage/Internal/LocalDiskStore.cs)
opens one source per overlapping SST, in both local and async paths.
Filtering to a few payload reads does not bound metadata search or active scan
sources. This is an indexing/iterator architecture difference, not a managed
runtime requirement. Preserve conservative behavior for untrusted bounds
while adding deterministic work/handle-count regressions.

### D08 — Mirrored cloud WAL catalog recovery — historical finding closed

**The 2026-09 implementation difference was stale at the current Pants
worktree.** Pants provider
[`ProviderWalCatalogStore`](../../src/Pants.Core/Cloud/Internal/ProviderWalCatalogStore.cs)
reads both copies, repairs a torn or missing primary from the valid mirror by
CAS, converges a stale mirror from a valid primary, and rejects unusable copies
without mutating them. Simulated-cloud
[`LoadCatalog`](../../src/Pants.Core/Cloud/Internal/SimulatedCloudPersistence.cs)
has the corresponding recovery path. Midge
[`load_and_repair_catalog_with_authority`][midge-catalog] follows the same
authority and repair rules.

Current Pants regressions cover lost-local-cache reopen, recovered values,
fencing advancement, converged copies after shutdown, both-invalid corruption,
and unchanged damaged bytes. The fail-closed error previously exposed only the
primary decode failure; provider and simulated-cloud paths now report that both
copies are unusable and include each copy's missing/invalid reason. The focused
cloud catalog Release filter passed 25/25. D08 is closed for these scenarios;
the remaining 4,241-case audit is still open.

### D09 — Lease expiry uses a different clock model — needs a targeted reproduction

**Source-established mechanism difference; impact still under review.**
Midge's heartbeat watchdog uses monotonic `Instant` deadlines, including while
renewal blocks. Pants
[CloudLeaseCoordinator](../../src/Pants.Core/Cloud/Internal/Leases/CloudLeaseCoordinator.cs)
uses the high-water mark of `_clock.UtcNow` (`ObserveMonotonicUtcTicks`).
A nondecreasing wall-clock value is not elapsed monotonic time: it can stop
advancing after a backward clock adjustment. Existing Pants tests cover health
expiry and a read crossing the deadline, not independent elapsed-time fencing
with a frozen/backward wall clock and blocked renewal. Reproduce at the engine
boundary before assigning failure severity. .NET monotonic timing is available;
this distinction is not forced by C#.

### D10 — Newly proposed oversized-entry admission — baseline decision needed

**Provisional Midge working-tree expectation, not a confirmed released mismatch.**
The new Midge test rejects a 64 MiB value during `put`/`insert`, leaves no
staged mutation, and permits a later valid commit. Pants
[StagePointWrite](../../src/Pants.Core/Transactions/Internal/TransactionInstance.cs)
copies the key/value and stages by transaction-pool capacity, with no equivalent
decoded-SST-entry precheck there. Later spill/WAL/SST paths have their own
limits. The comparison must resolve error timing, error category, and the
exact representable entry size before porting the assertion. Public input
types or allocation costs may differ; accepting data that cannot later be
flushed is not a justified language adaptation.

### D11 — Complete-bound overlap repair is absent from Pants scheduling

**Source-reviewed scheduling and debt-clear gap.** Midge's
`pick_repair_compaction` finds same-level repair components from complete key
bounds, even when the level is under target or is the bottom level. The
strategy tests cover transitive underfull overlap and the distinction between
three files sharing a boundary and a legal two-file boundary. The actor checks
repair across families before critical L0 work, with a regression for an
underfull repair in one family taking priority over critical L0 debt in another.
Pants validates each file's bounds but has no same-level repair purpose or
repair-component selector in `LeveledCompactionPlanner`; its debt-clear check
also considers level sizes and L0 counts only. Backfill the layout and priority
invariants before treating D03's scheduler order as fully aligned.

## Language adaptations versus semantic deviations

| Difference | Legitimate adaptation | Contract that must remain the same |
| --- | --- | --- |
| Rust `Result` versus .NET exceptions | Typed exception hierarchy and async failure delivery | Error category, atomicity, retryability, and ambiguous-outcome handling |
| Rust iterators versus `IAsyncEnumerable` | Async enumeration, cancellation, and explicit disposal | Row order, bounds, snapshot visibility, late failures, and resource release |
| Rust `Drop`/`Arc` versus .NET GC/disposal | Explicit `Dispose`/`DisposeAsync`, leases, and managed ownership | No prematurely released reader/lease, no lost accepted operation, bounded shutdown |
| Rust durations/integers versus `TimeSpan` and signed APIs | Explicit range conversion and saturation | TTL presence, expiry boundaries, persisted bits, and documented rejection behavior |
| Rust module/features versus .NET assemblies/DI | Different package graph and service registration | Provider capability and runtime behavior; no foreign-company namespace is required |
| CLI and repository tooling | Pants has no product CLI; build/test governance differs | Corresponding database verification/diagnostic behavior still applies |

**None of D01–D08 or D11 is required by the implementation language.** D09 is
also addressable with .NET timing facilities; its end-to-end impact remains to
be tested. D10 first needs a stable Midge baseline. Language adaptation should
be an explicit narrow rationale, never a blanket `n/a` for a difficult
behavior.

## Existing coverage that should not be added to the gap list

These are concrete counterexamples to using inventory-name absence as the audit:

- Shared operation deadlines and expired-before-submission rejection:
  [OperationDeadlineTests](../../test/Pants.Tests/Runtime/OperationDeadlineTests.cs).
- Derived/explicit runtime response timeout and admitted late completion:
  [PantsRuntimeResponseTimeoutTests](../../test/Pants.Tests/Runtime/PantsRuntimeResponseTimeoutTests.cs).
- Distinct provider-store disposal, failed startup cleanup, and provider-open
  cancellation: [disposal](../../test/Pants.Tests/Cloud/ProviderObjectStoreDisposalTests.cs)
  and [initialization](../../test/Pants.Tests/Cloud/PantsCloudProviderInitializationTests.cs) suites.
- Large transactions spill in durable modes; shared pool bounds, rollback,
  reopen, and memory-mode refusal already have
  [spill hardening coverage](../../test/Pants.Tests/Transactions/Spill/PantsTransactionSpillHardeningBehaviorTests.cs).
- Spill sparse/range index corruption and ordinal visibility:
  [TransactionOperationSourceTests](../../test/Pants.Tests/Transactions/Spill/TransactionOperationSourceTests.cs).
- Sticky late SST scan errors and limit-one block reads:
  [streaming scan tests](../../test/Pants.Tests/Storage/PantsStreamingScanHardeningTests.cs).
- Snapshot retention through compaction and reader ownership:
  [disk storage tests](../../test/Pants.Tests/Storage/PantsDiskStorageTests.cs)
  and [reader cache tests](../../test/Pants.Tests/Storage/SstReaderCacheTests.cs).
- Maximum expiration values through resident/spilled transactions and SST:
  [PantsTtlBehaviorTests](../../test/Pants.Tests/PantsTtlBehaviorTests.cs).
- Provider response identities and exact bounded range bodies:
  [response contracts](../../test/Pants.Tests/Cloud/CloudProviderResponseContractTests.cs)
  and [range contracts](../../test/Pants.Tests/Cloud/CloudProviderRangeContractTests.cs).

These statements apply to the named scenarios, not blanket closure of their domains.

## Previous audit evidence and remediation sequence — 2026-09-04

The validation results below belong to the prior audit snapshot. They do not
validate the current `d582fd1` Midge or `fc3baa3` Pants heads.

This audit ran 42 existing Release cases successfully:

```sh
dotnet test test/Pants.Tests/Pants.Tests.csproj --configuration Release --no-build --no-restore --filter 'FullyQualifiedName~LeveledCompactionPlannerTests|FullyQualifiedName~StreamingCompactionMergerTests|FullyQualifiedName~GcsCredentialSourceTests|FullyQualifiedName~PantsStorageIoTests'
```

`dotnet format Pants.slnx --verify-no-changes --no-restore` also passed, and
`dotnet build --configuration Release --no-restore` completed with zero warnings
and errors. Report links and `git diff --check` were checked successfully.

An isolated temporary executable, using the current Release assemblies and
friend-assembly access for internal components, produced:

```text
Conflicting same-key/sequence inputs: returned 1 entry, value=first; no corruption error.
Ordinary L0 and overfull L1: selected source level 0.
Staged write through symlinked parent: succeeded, target=probe.
Background disabled, trigger 3: accepted 16 flushed generations, L0 files=16.
After CompactAllAsync: L0 files=1.
```

The probes are diagnostic evidence, not committed regression tests. The first
three are component probes; the last two use the public database API. No Midge
suite, live-provider qualification, or large-scale performance run was executed
for this report. Midge expectations were checked against test bodies and source.

The following sequence is historical. Re-rank it after the current-head audit;
do not repeat items already closed in the remediation table above.

1. G02 has executable provider and Sqrzl qualification coverage; run the live
   Sqrzl path when its emulator is available.
2. Complete D03 with an end-to-end multi-family critical L0 regression, then
   backfill D11's complete-bound repair planning and priority contracts. D04
   and D05 are covered by exact ceiling, single-file drain, and active
   compaction wait tests. D02 is closed for the planner and active-reader
   scenarios above.
3. Path confinement and lease-clock fencing (G09, D09), then late spill errors
   (G11).
4. Complete the composite multi-level read-work proof and qualify scan metadata
   startup behavior (G07), then review cache identity, credentials, and SST
   boundary cases (G10, G12, G13). G06 and G05 are covered at the current worktree.

For each obligation: write the exact Midge behavioral expectation, show red
against unchanged Pants, make the smallest coherent fix, show green, and run
the relevant reopen/fault/resource tests. If a new test is already green,
record it as a coverage-only improvement; do not invent a failing phase.

The current full backfill replaces the historical 1,902-source-test target:
review all 4,241 entries in the current per-case inventory, resolve every
candidate, disposition every source case, and check every current system
invariant against both implementations. No overall parity percentage is
justified by test-name counts.
Exclude string-based source/project/workflow/doc checks and reflected-shape
checks from behavioral coverage. Keep real corruption/compatibility tests:
reading or altering persisted bytes to exercise recovery is system behavior,
not repository-integrity enforcement.

[m-provider-engine]: https://github.com/cntryl/midge/blob/d582fd1127741fd69067914423ec76ad6adf0657/tests/cloud_provider_engine_qualification.rs
[m-failure]: https://github.com/cntryl/midge/blob/d582fd1127741fd69067914423ec76ad6adf0657/tests/fault_injection.rs
[midge-catalog]: https://github.com/cntryl/midge/blob/d582fd1127741fd69067914423ec76ad6adf0657/src/runtime/hybrid_persistence/catalog.rs
[m-ddl-source]: https://github.com/cntryl/midge/blob/d582fd1127741fd69067914423ec76ad6adf0657/src/runtime/ddl.rs
[m-atomicity]: https://github.com/cntryl/midge/blob/d582fd1127741fd69067914423ec76ad6adf0657/tests/durability.rs
[m-cf]: https://github.com/cntryl/midge/blob/d582fd1127741fd69067914423ec76ad6adf0657/tests/column_families.rs
[m-compaction]: https://github.com/cntryl/midge/blob/d582fd1127741fd69067914423ec76ad6adf0657/src/compaction/mod.rs
[m-strategy]: https://github.com/cntryl/midge/blob/d582fd1127741fd69067914423ec76ad6adf0657/src/compaction/strategy.rs
[m-read-view]: https://github.com/cntryl/midge/blob/d582fd1127741fd69067914423ec76ad6adf0657/src/runtime/sst_read_view.rs
[m-backpressure]: https://github.com/cntryl/midge/blob/d582fd1127741fd69067914423ec76ad6adf0657/tests/backpressure.rs
[m-real-io]: https://github.com/cntryl/midge/blob/d582fd1127741fd69067914423ec76ad6adf0657/src/io/real.rs
[m-staging]: https://github.com/cntryl/midge/blob/d582fd1127741fd69067914423ec76ad6adf0657/src/io/staging.rs

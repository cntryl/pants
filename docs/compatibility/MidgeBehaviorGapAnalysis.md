# Midge behavior gaps and differences

Reviewed 2026-09-04. The target is **the same observable database behavior as
Midge, expressed through idiomatic .NET APIs**. Different implementation
languages do not excuse different durability, recovery, visibility, fencing,
resource-limit, or error-classification contracts.

This report separates missing regression evidence from demonstrated behavior
differences. It is a first source-reviewed backlog, **not a claim that every
one of the 2,679 inventory entries has been semantically audited**. An absent
test name is not proof of an absent feature, and a mapped test name is not
proof that its assertions cover the Midge scenario.

## Remediation progress

The gap/difference descriptions below preserve the original audit findings.
This table records subsequent remediation in the current worktree.

| Item | Status | Behavioral evidence |
| --- | --- | --- |
| D01: conflicting logical compaction versions | Fixed | [Conflict tests](../../test/Pants.Tests/Storage/CompactionVersionConflictTests.cs): all 18 cases failed before the fix and pass afterward. Both merger paths reject differing values/deletion/expiration at one key/sequence, including hidden versions, and collapse identical copies. Streaming validation retains its existing one-entry reservation. |
| D01: partial output cleanup and reopen | Covered | [Recovery regression](../../test/Pants.Tests/Storage/PantsCompactionConflictRecoveryTests.cs) observes a durable output before encountering the conflict, then checks output cleanup, unchanged input bytes/manifest ownership, and successful reopen of unaffected data. |
| G04: retained WAL after tombstone GC | Covered; no engine change required | [Reopen regression](../../test/Pants.Tests/Storage/PantsRetainedWalTombstoneRecoveryTests.cs) passed before the implementation change. It proves the target was removed from compacted SSTs, restores the old WAL, and checks that the deleted value stays absent while anchor values survive. |
| All other G/D items | Open | No closure is inferred from this slice or from broad suite totals. |

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

## Comparison inputs

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

## 1. Known behaviors without confirmed Pants regression coverage

“Gap” below means the exact scenario and assertions were not found after
searching the current suite and inspecting the nearest tests. It does not
mean every underlying operation is unimplemented. Priorities order remediation:
**first** for authority/data safety, **next** for boundedness and fault handling.
Groups combine related Midge tests; they are not a test-count parity target.

### G01 — Recover and repair redundant cloud WAL catalogs — first

Midge [cloud persistence tests][m-cloud-persistence]:

- `should_recover_from_valid_catalog_mirror_when_primary_catalog_has_torn_tail`
- `should_fail_closed_when_both_cloud_wal_catalog_copies_are_invalid`

Pants [provider persistence tests](../../test/Pants.Tests/Cloud/ProviderCloudPersistenceTests.cs)
cover catalog fencing and missing readback, but not primary/mirror corruption
and convergence. Add strict reopen after local WAL loss with one valid copy,
then both corrupt; assert recovered values, repaired authority, and fail-closed
classification. **There is also an implementation difference: D08.**

### G02 — Recover a partially uploaded compaction partition set — first

Midge [failure injection][m-failure] and [provider engine qualification][m-provider-engine]:

- `should_rollback_partition_set_after_partial_remote_compaction_upload`
- `should_rollback_partition_set_after_partial_sqrzl_compaction_upload`
- `should_recover_partitioned_compaction_from_sqrzl_s3_after_local_cache_loss`

Pants [cloud compaction failure tests](../../test/Pants.Tests/Cloud/PantsCloudCompactionFailureTests.cs)
cover failed intent persistence and orphan cleanup, while
[leveled compaction tests](../../test/Pants.Tests/Storage/PantsLeveledCompactionTests.cs)
cover successful partitioned local reopen. Neither establishes rollback after
partition N uploads and N+1 fails through a provider, or complete partitioned
recovery through Sqrzl with the cache removed. Exercise those exact boundaries;
verify every expected key, retained inputs, and absence of partial authority.
Keep the Sqrzl configuration under `SQRZL_*`.

### G03 — Re-drive an ambiguous DDL prepare that never reached CAS — first

Midge [DDL test][m-ddl]:
`should_recover_ambiguous_ddl_once_when_reopening_after_crash_before_remote_cas_submission`.

Pants [DDL tests](../../test/Pants.Tests/Cloud/CloudDdlTwoPhaseTests.cs) cover a
normal torn prepare, failed CAS, and a lost response after remote commit. The
missing case is a durably *ambiguous* prepare with no submitted remote CAS.
Reopen must resolve the same operation exactly once, preserve its operation
identity, and avoid permanently fencing the database. A non-ambiguous prepare
that should be aborted is not equivalent coverage.

### G04 — Do not resurrect a GC'd value from a retained WAL — first

Midge [durability atomicity test][m-atomicity]:
`should_not_resurrect_manifest_covered_value_from_retained_wal_after_tombstone_gc`.

Pants [delete/compaction reopen tests](../../test/Pants.Tests/Storage/PantsLeveledCompactionTests.cs)
and [cloud WAL pruning tests](../../test/Pants.Tests/Cloud/PantsCloudWalPruningTests.cs)
cover neighboring behaviors, not the complete interaction. Preserve an old
valid WAL, compact away a durable point tombstone, restore that retained WAL,
and reopen. Assert the deleted key remains absent and unrelated keys survive.

### G05 — Preserve acknowledged data after a physical partial-write ENOSPC — first

Midge [failure test][m-failure]:
`should_preserve_acknowledged_state_when_no_space_tears_wal_frame`.

Pants [partial WAL test](../../test/Pants.Tests/Runtime/PantsFailureInjectionTests.cs)
starts with an empty database and checks that the failed transaction is absent
after reopen. It does not establish the stronger Midge contract: pre-existing
acknowledged rows survive an attempted overwrite/delete, the physical partial
tail is rolled back, follow-up writes are fenced, health is degraded, and a
reopened engine becomes usable. Inject a short physical append followed by
no-space, not just an exception before any bytes are written.

### G06 — Preserve cold-family tombstones while hot-family writes race maintenance — first

Midge [column-family test][m-cf]:
`should_preserve_cold_tombstone_given_hot_cold_compaction_when_reopening`.

Pants [differential corpus](../../test/Pants.Tests/Storage/PantsDiskResidentDifferentialTests.cs)
and [background flush tests](../../test/Pants.Tests/Runtime/PantsBackgroundFlushPipelineTests.cs)
cover model comparisons and individual publication races. No matching
hot/cold concurrent-write + flush + compaction + reopen oracle was confirmed.
Use deterministic barriers and compare the complete per-family final model,
including deletes; single-key samples are insufficient.

### G07 — Bound compaction and read work by active streams, not total files — next

Midge [compaction tests][m-compaction], [strategy tests][m-strategy], and
[read-view tests][m-read-view]:

- `should_plan_ten_thousand_target_files_with_one_source_stream`
- `should_compact_sixty_five_target_files_with_two_merge_heads`
- `should_keep_compaction_work_bounded_across_ten_thousand_targets`
- `should_prove_read_work_bounds_across_synthetic_manifest_cardinalities`
- `should_select_at_most_two_adjacent_files_per_lower_level_for_point_read`

Pants [owned resource scaling](../../test/Pants.Tests/Storage/PantsOwnedResourceScalingTests.cs)
measures retained payload, and [streaming merge tests](../../test/Pants.Tests/Storage/StreamingCompactionMergerTests.cs)
exercise a small number of simultaneously open inputs. Those are not proofs
of bounded metadata search work, reader handles, or complete target overlap at
10,000 files. Add counters for comparisons, active readers/merge heads, and
reserved bytes over increasing synthetic cardinalities. **D02 and D07 explain
the underlying differences.** These are architectural proofs, not a claim of
having run a production-scale performance qualification.

### G08 — Enforce L0 admission and manual debt-drain semantics — next

Midge [backpressure test][m-backpressure]:
`should_bound_published_l0_when_background_compaction_is_disabled`;
also `should_wait_for_active_compaction_before_declaring_debt_clear` in
`src/runtime/event_loop/tests.rs`.

Pants tests check memtable pressure and bounded compaction input sets, but not
the reserved L0 publication ceiling or a manual drain that leaves no L0 debt.
Test admission at the exact boundary, no WAL mutation for a rejected commit,
manual compaction completion, and resumed writes. **D04 and D05 are reproduced
behavior differences, not merely missing tests.**

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

## 2. Behavior differences requiring review

“Reproduced” means an isolated executable probe ran against the current Pants
Release binaries. “Source-established” identifies a directly different
algorithm/contract, but does not claim an end-to-end failure was reproduced.
The reason column describes the technical cause, not the historical author's intent.

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

### D02 — Complete target overlap is limited by the input-file cap

**Source- and existing-test-established; resource algorithm difference.**
Midge's [strategy][m-strategy] includes a complete target span even when its
file count exceeds the source-stream limit; its executor can traverse 65 target
files using two merge heads. Pants
[LeveledCompactionPlanner](../../src/Pants.Core/Storage/Internal/Compaction/Compaction/LeveledCompactionPlanner.cs)
returns no plan when the complete selected set exceeds `MaximumInputFiles`.
`ShouldSkipFamilyWhenOverlapClosureExceedsInputLimit` explicitly asserts this.
Returning no plan preserves overlap safety but can prevent this family from
making compaction progress. Raising the cap alone does not establish bounded
resource use: Pants currently opens an iterator per input. Port the separation
between complete logical overlap and bounded active streams.

### D03 — Ordinary L0 work wins over downstream compaction debt

**Reproduced; scheduling-policy difference.**
Midge's `should_prioritize_deepest_overfull_level_over_ordinary_l0` expects L1
when both ordinary L0 and L1 are overfull. With that topology, Pants selected
source level **0**. Its planner checks L0 first, then inner levels in ascending
order. Review ordinary versus emergency L0 priority and per-family fairness
together; a different thread/channel implementation does not require a
different debt policy.

### D04 — Disabling background compaction does not enforce Midge's hard L0 ceiling

**Reproduced through the public API; admission-policy difference.**
The Midge [test][m-backpressure] reserves a ceiling of
`L0 trigger + immutable queue capacity (10) + active generation (1)` and
rejects the next write. With trigger **3**, Pants accepted and flushed **16**
generations into **16 L0 files**; the corresponding Midge ceiling is **14**.
There is no matching L0 reservation gate in the inspected Pants path. Review
write admission, queued flush reservations, and emergency recovery compaction
together. Memtable byte limits are not equivalent to a file-count ceiling.

### D05 — CompactAllAsync can return with L0 files remaining

**Reproduced through the public API; maintenance-contract difference.**
After D04's 16 flushes, `CompactAllAsync` returned with **one L0 file**.
Midge's [backpressure test][m-backpressure] requires manual debt drain to
leave **zero L0 files**. Pants also explicitly tests residual L0 files in
`ShouldKeepCompactionInputSetBoundedAndReportMultipleLevels`.
This is not an async naming distinction: the completion condition differs.
Define the Midge-equivalent debt-clear predicate, including single-file L0
work and already-running compactions, and test it independently of batch size.

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

### D08 — Pants has a single cloud WAL catalog, not Midge's mirrored recovery path

**Source-established availability/recovery difference.**
Midge repairs the primary from `publication-catalog.v1.mirror.json` when the
primary is torn, and fails closed when both copies are invalid. Pants
[provider hydration](../../src/Pants.Core/Cloud/Internal/ProviderCloudPersistence.cs)
reads and decodes only `WalCatalogObjectKey`; simulated-cloud
[LoadCatalog](../../src/Pants.Core/Cloud/Internal/SimulatedCloudPersistence.cs)
likewise reads only `publication-catalog.v1.json`. No catalog-mirror path was
found. A valid Midge mirror therefore does not provide the same recovery
fallback in these Pants paths. This is missing recovery protocol behavior,
not a JSON-library limitation. Resolve copy selection, fencing, CAS convergence,
and crash recovery together; do not merely add a second unconditional write.

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

## Language adaptations versus semantic deviations

| Difference | Legitimate adaptation | Contract that must remain the same |
| --- | --- | --- |
| Rust `Result` versus .NET exceptions | Typed exception hierarchy and async failure delivery | Error category, atomicity, retryability, and ambiguous-outcome handling |
| Rust iterators versus `IAsyncEnumerable` | Async enumeration, cancellation, and explicit disposal | Row order, bounds, snapshot visibility, late failures, and resource release |
| Rust `Drop`/`Arc` versus .NET GC/disposal | Explicit `Dispose`/`DisposeAsync`, leases, and managed ownership | No prematurely released reader/lease, no lost accepted operation, bounded shutdown |
| Rust durations/integers versus `TimeSpan` and signed APIs | Explicit range conversion and saturation | TTL presence, expiry boundaries, persisted bits, and documented rejection behavior |
| Rust module/features versus .NET assemblies/DI | Different package graph and service registration | Provider capability and runtime behavior; no foreign-company namespace is required |
| CLI and repository tooling | Pants has no product CLI; build/test governance differs | Corresponding database verification/diagnostic behavior still applies |

**None of D01–D08 is required by the implementation language.** D09 is also
addressable with .NET timing facilities; its end-to-end impact remains to be
tested. D10 first needs a stable Midge baseline. Language adaptation should be
an explicit narrow rationale, never a blanket `n/a` for a difficult behavior.

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

## Evidence and remediation sequence

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

Suggested TDD sequence:

1. Conflicting-version rejection and retained-WAL/tombstone recovery (D01, G04).
2. Catalog recovery, ambiguous DDL, and partial partition publication (G01–G03).
3. Complete compaction overlap, priority, hard L0 admission, and debt drain
   (D02–D05); these are interdependent and should not be patched independently
   by raising limits or weakening assertions.
4. Path confinement and lease-clock fencing (G09, D09), then physical no-space
   and late spill errors (G05, G11).
5. Read-work bounds, cache identity, hot/cold races, credentials, and reviewed
   new SST boundary cases (G06, G07, G10, G12, G13).

For each obligation: write the exact Midge behavioral expectation, show red
against unchanged Pants, make the smallest coherent fix, show green, and run
the relevant reopen/fault/resource tests. If a new test is already green,
record it as a coverage-only improvement; do not invent a failing phase.

Remaining work is explicit: validate the old mapped assertions, classify the
rest of the 1,902 source tests by observable contract, and review unresolved
integration deltas such as the full concurrent-flush follow-up scenario and
tiny-budget admission. No overall parity percentage is justified yet.
Exclude string-based source/project/workflow/doc checks and reflected-shape
checks from behavioral coverage. Keep real corruption/compatibility tests:
reading or altering persisted bytes to exercise recovery is system behavior,
not repository-integrity enforcement.

[m-cloud-persistence]: https://github.com/cntryl/midge/blob/68821ee84a2c5d26ad32123905a7d4b84751f571/tests/cloud_persistence_hardening.rs
[m-provider-engine]: https://github.com/cntryl/midge/blob/68821ee84a2c5d26ad32123905a7d4b84751f571/tests/cloud_provider_engine_qualification.rs
[m-failure]: https://github.com/cntryl/midge/blob/68821ee84a2c5d26ad32123905a7d4b84751f571/tests/failure_injection.rs
[m-ddl]: https://github.com/cntryl/midge/blob/68821ee84a2c5d26ad32123905a7d4b84751f571/tests/cloud_ddl_two_phase_hardening.rs
[m-atomicity]: https://github.com/cntryl/midge/blob/68821ee84a2c5d26ad32123905a7d4b84751f571/tests/durability_atomicity.rs
[m-cf]: https://github.com/cntryl/midge/blob/68821ee84a2c5d26ad32123905a7d4b84751f571/tests/column_families.rs
[m-compaction]: https://github.com/cntryl/midge/blob/68821ee84a2c5d26ad32123905a7d4b84751f571/src/compaction/mod.rs
[m-strategy]: https://github.com/cntryl/midge/blob/68821ee84a2c5d26ad32123905a7d4b84751f571/src/compaction/strategy.rs
[m-read-view]: https://github.com/cntryl/midge/blob/68821ee84a2c5d26ad32123905a7d4b84751f571/src/runtime/sst_read_view.rs
[m-backpressure]: https://github.com/cntryl/midge/blob/68821ee84a2c5d26ad32123905a7d4b84751f571/tests/backpressure.rs
[m-real-io]: https://github.com/cntryl/midge/blob/68821ee84a2c5d26ad32123905a7d4b84751f571/src/io/real.rs
[m-staging]: https://github.com/cntryl/midge/blob/68821ee84a2c5d26ad32123905a7d4b84751f571/src/io/staging.rs

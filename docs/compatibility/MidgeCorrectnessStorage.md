# Midge storage correctness audit

This is a bounded source and behavioral audit of WAL-1–7 and SST-1–10 from
Midge `c684310d9d8ce8ecd1151c47151d4bea98b348cf` (0.5.2), using its
`docs/development/invariant-catalog.md` and corresponding evidence catalog.
The persisted target is lsm-spec `2376ba9ddc7a9dc55d5988babc3847089ea8d3e1`,
FORMAT 9, whose tree matches fixture revision `7748fd1`. The source catalog is
a checklist, not proof that every invariant has been qualified in Pants.

## Observed recovery gaps

- **Missing family authority:** a valid WAL mutation for a family absent from
  the manifest was silently skipped under both recovery policies. Midge's
  `src/wal/replay.rs::ReplaySelection` and
  `src/wal/recovery/streaming.rs` distinguish this authority failure from
  salvageable WAL damage. Pants now validates family authority before record
  processing and validates every complete unit before applying its members.
- **Unsupported payloads:** Salvage treated an unsupported WAL record or SST
  footer version as disposable corruption. Compatibility failures now propagate
  without changing the affected WAL history or manifest. SST startup also
  limits its salvage boundary to typed corruption; I/O and resource failures
  do not authorize removing a published file.
- **Sealed-member admission:** Salvage accepted a missing sealed footer and
  self-consistent replacement bytes that disagreed with ledger sealing facts,
  then replayed younger history. A missing sealed file instead prevented
  prefix recovery. Following Midge's `src/wal/planner.rs::admit_tail`, local
  sealed members are validated against ledger authority before replay. A
  corruption or authoritatively missing member ends the salvage prefix; every
  younger member is set aside. The reserved sequence floor and sealed sequence
  bounds are durable before history is renamed or replaced.
- **Retirement authority:** the coverage evaluator considered an inactive,
  unknown family covered. It now requires the family's durable frontier;
  absence retains the WAL, while the permanent dropped sentinel can prove
  coverage. Runtime family membership is not deletion authority.
- **Batch reservation spans:** only checking the outer commit sequence admitted
  batches whose begin sequence preceded the segment or overlapped a prior
  physical record. Midge's streaming replay checks the complete reserved span
  before selection or application. `WalCodec.ValidateSequenceSpan` now supplies
  the same check to replay and segment scanning, including units whose family
  frontier would skip replay.
- **Grouped family admission:** grouped append reserved durable sequence authority
  before checking every transaction's single-family scope. An invalid later
  transaction changed `LEDGER` even though the group was rejected. Every group
  member now passes family admission before any reservation; Sync, Buffered and
  BestEffort regressions preserve exact WAL/ledger bytes, then verify legal retry
  and reopen.
- **Discovery side effects:** writer-epoch discovery truncated a torn active
  tail before replay discovered missing family authority. Recovery then failed
  after changing WAL bytes. Discovery now observes the verified prefix without
  repairing it; authorized replay owns the later cut and durability boundary.
- **Deferred durability promotion:** the extended crash campaign found that
  opening a deferred-replay store fsynced its unvalidated active tail and advanced
  `LEDGER` before replay. A later torn-tail check then treated discardable bytes
  as acknowledged durable history and refused recovery. Initial durability
  restoration now runs only after immediate replay; the deferred path already
  restores durability after its own validation and replay. Focused regressions
  distinguish complete buffered bytes, a torn unsynced tail and genuine loss
  inside the original synced prefix. The latter still fails without changing
  WAL/ledger authority. The original process-crash reproduction is seed 2929,
  simulated cloud, cycle 3, `MidWalAppend` hit 25; its raw images and client
  acknowledgment ledgers are retained independently of the failed-open image.
- **Decoder admission failures:** an explicit decoded-batch allocation budget
  rejection used an I/O error. It now returns `PantsResourceLimitException`;
  budget exhaustion cannot be classified as corrupt/disposable WAL authority.
- **Remote verification failures:** strict replay wrapped provider timeouts,
  contention and I/O errors as recovery corruption, while Salvage could set
  aside history after the same failures during footer rescanning. Those
  rescan boundaries now classify only malformed bytes as corruption. Six
  injected remote-stream cases preserve the original typed failure and exact
  local ledger bytes under both policies.
- **Publication cleanup and commit replies:** inline obsolete-file collection
  could throw an ordinary I/O error after the complete committed version had
  become visible. Snapshot publication now queues collection through the
  existing owner and GC worker. Cleanup failures are recorded as degraded
  maintenance; they cannot replace the successful committed reply. A regression
  releases a pinned snapshot immediately before publication, injects a journal
  failure during deletion acknowledgment and verifies the committed value before
  and after reopen.
- **Collection queue handoff:** the publication collection claim remained held
  until its response continuation ran, after the owner could publish another
  version. That lost a later snapshot-release request and retained obsolete SSTs.
  The owner now releases each collection's identity before accepting another
  command; delayed response cleanup cannot clear a newer claim. A response
  barrier reproduces the lost deletion and verifies both generations are collected.
  A last-reader release during an existing collection also requests a successor
  pass through a request generation; failed maintenance does not retry itself.
  A second barrier proves that within-command release cannot strand retired files.
- **Drop publication and read pins:** family drop reclaimed SSTs before publishing
  the dropped version. A direct reader admitted after the collector's zero-pin
  check still captured the old family, then its first lazy SST read failed after
  reclamation. Drop now publishes through the existing direct-read admission gate
  before queuing collection. A reclaim-journal barrier proves the failed read;
  another regression preserves an earlier snapshot through drop and reopen.

## Invariant coverage and limits

| Invariant | Pants source and reviewed boundary | Evidence / remaining scope |
| --- | --- | --- |
| WAL-1 | `LocalDiskStore` append, sync and recovered durability restoration | Policy implementation inspected; acknowledgment ownership and actor receipts belong to the separate runtime audit. No new power-loss claim. |
| WAL-2 | `RequireTransactionFitsRecoveryEnvelope`, `RecoveryCheckpointer`, WAL I/O fence, deferred publication cleanup | New decoder-budget and post-publication GC regressions. Existing admission/rollback tests are separate evidence. |
| WAL-3 | `WalRecoveryStateMachine`, `WalCodec`, `ReplayWalStream`, recovery checkpoints | Complete batches and split commits are selected by commit sequence. Abandoned split records are not applied. New batch-span and read-only-discovery regressions. |
| WAL-4 | `AdmitSealedWalReplaySource`, `VisitWalStream`, `PersistWalSalvageFloor` | New reopen regressions exercise a valid prefix followed by a missing footer, changed sealed bytes, or a missing member; strict reopen preserves only the retained prefix. Remote timeout/Busy/I/O rescans preserve typed failures. Active-header corruption and exhaustive crash interleavings remain unreviewed. |
| WAL-5 | Header/record version gates and ledger identities | New unsupported-record tests cover both policies with unchanged WAL/ledger bytes. Lease takeover and delayed cloud workers are separate audits. |
| WAL-6 | `RestoreRecoveredWalDurability`, `WalDurabilityState` | Source inspected; complete runtime frontier mutation review is outside this slice. |
| WAL-7 | `WalReplaySelection`, `WalSegmentCoverage`, paired flush frontier publication | New unknown-family recovery and retention tests. Existing retained-tombstone and recovery-checkpoint suites provide additional focused coverage. Cloud retirement is outside this slice. |
| SST-1 | Manifest file enumeration, `ManifestReadSnapshot`, intent reconciliation | Existing FORMAT 9 publication-authority and unlisted-WAL tests; no inference from raw output presence. |
| SST-2 | SST reader gates, manifest identities, storage verifier | Unsupported authoritative SST regression preserves bytes/manifest. This does not establish complete local external-replacement detection or provider receipt qualification. |
| SST-3 | `SstReadView`, `FileMeta.HasTrustedKeyBounds`, `SstManifestMetadataValidator` | Conservative unknown/incomplete bounds and metadata validation inspected. Read-path/level-order fixture tests remain separate from this recovery audit. |
| SST-4 | Flush output, intent and manifest publication paths | Source reviewed for authority precedence; scheduling, retry ownership and cross-family progress are not exhaustively re-qualified here. |
| SST-5 | `ManifestTransitions`, intent replay, compaction publication | Existing FORMAT 9 phase-only publication tests, retirement/move tests and corruption admission tests. Full failpoint matrix remains separate. |
| SST-6 | Snapshot horizons, retained files and compaction merger | Source inspected; no new claim covering every retention/GC interleaving. |
| SST-7 | Output sequence reservation and immutable staged publication | Reservation/publication source inspected; worker plan completeness has not received an independent full audit here. |
| SST-8 | Journal envelopes, markers, replay and checkpoint horizons | Existing manifest checkpoint/salvage suites; the concurrent-writer horizon protocol in Midge is not inferred from these local exclusive-writer tests. |
| SST-9 | `AtomicStagedFile.CreateDirectoryDurably`, startup storage directory barriers | Existing directory durability regressions; no simulated device power-loss matrix added. |
| SST-10 | Leveled planner, manifest logical moves and overlap repair | Source cross-referenced; no new complete planner qualification in this slice. |

The remaining non-WAL worker shape differs from Midge's AUTH-3 architecture.
Legacy cloud flush requests carry `RuntimeState`, although the production
`LocalDiskStore.Flush` overloads do not use it. Normal local frozen flush workers
publish the durable storage manifest and WAL-retirement effects before the actor
consumes their completion; the actor still owns runtime accounting, generation
release and version publication. Compaction publication itself returns through
`PublishCompactionOnActorAsync`; its worker stages outputs/intents and reserves SST
identities. GC workers read live snapshot counts and change storage while their
owning actor command awaits them. The drop admission race above is a proven
correctness defect; these remaining ownership differences have not independently
established another visibility failure or complete AUTH-3 parity.

## Validation

Use isolated artifacts to avoid modifying another worker's build state:

```sh
dotnet restore Pants.slnx --locked-mode --artifacts-path /tmp/pants-correctness-storage-20261104
dotnet test test/Pants.Tests/Pants.Tests.csproj --configuration Release --no-restore --artifacts-path /tmp/pants-correctness-storage-20261104 --filter 'FullyQualifiedName~PantsWalAuthorityRecoveryTests|FullyQualifiedName~WalSegmentCoverageTests|FullyQualifiedName~PantsSstRecoveryAuthorityTests|FullyQualifiedName~WalCodecTests' -m:1
```

The first four recovery cases showed three failures before correction and then
passed. The next fourteen-case selection showed five additional failures before
correction and then passed all fourteen. Those are focused local results, not
repository-wide, hosted CI, provider, or complete Midge correctness qualification.

The sequence-span, decoder resource classification and read-only discovery
selection showed five failures before correction; the corresponding focused
selection passed all **30** cases with zero build warnings.

An intermediate broader selection (Storage, Compatibility, Decoder Fuzz and
shared cloud WAL coverage/retirement) passed **1,410 / 1,414**. Three failures
were test inputs/expectations incompatible with the corrected Midge contracts:
an overlapping interleaved batch reservation, a helper using a batch commit as
the segment start, and successful salvage without family authority. The fourth
was a concurrently added backup test selecting a sequence number as a segment
id; its fix and qualification belong to the separate backup audit.

After correcting the three storage fixtures and adding the final seven
remote-verification/publication regressions (all seven failed before correction),
the final focused selection passed **56 / 56**, including all tests in
`RecoveryCheckpointTests`, `WalRecoveryStateMachineTests`,
`PantsWalWriterEpochRecoveryTests`, `RemoteWalReplayTests` and
`PantsCommitPublicationMaintenanceTests`. The broad selection was not rerun after
these final changes. Complete latest-tree qualification remains separate.

The grouped family admission selection first failed all three durability cases
at exact ledger-byte comparison, then passed **14 / 14** cases together with the
WAL family-scope and backup-authority suites. The later collection queue regression
also failed before correction at the retired-file deletion assertion; its final
selection, including both queue handoff regressions, publication-failure handling,
snapshot-retention completion, WAL-family admission/scope, backup authority and
fenced completion, passed **20 / 20** with zero build warnings. This result remains
separate from the earlier broad run.

The later drop-admission regression failed on the admitted reader's first SST
read with a missing-file error before correction. The final drop/publication,
column-family reclamation, local DDL authority, cloud two-phase DDL and publication
maintenance selection passed **51 / 51** with zero build warnings. Physical
reclamation is queued after drop publication; its no-pin completion test waits
through the existing reclamation helper. Complete latest-tree qualification
remains the separate final repository run.

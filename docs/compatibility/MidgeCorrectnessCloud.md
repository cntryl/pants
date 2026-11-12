# Midge cloud correctness audit

This audit uses Midge `c684310d9d8ce8ecd1151c47151d4bea98b348cf` (0.5.2),
its `docs/development/invariant-catalog.md` and production source, and the FORMAT 9
lsm-spec main at `2376ba9ddc7a9dc55d5988babc3847089ea8d3e1`, whose tree matches
fixture revision `7748fd1a5e681813a283a65a2578ab07303b3c77`. It reviews authority,
cold recovery and reclamation correctness. It does not establish complete
behavioral or performance parity. Existing dirty work and earlier fixes remain
part of the reviewed Pants working tree.

## Authority and recovery findings

### Cloud reads require current lease authority

Midge AUTH-2 and TX-1 distinguish retained local reads from cloud reads: a local
snapshot pin cannot retain remote sources against a successor. Midge implements
this in `src/engine/tests/cloud_read_fencing.rs` and its read-authority checks.
Cloud reads must return `Fenced` after lease loss, including cached, missing and
own-write reads and scans. A running runtime's diagnostic control path remains
available. Transient provider uncertainty is distinct from proved lease loss.

Pants previously allowed predecessor snapshot reads after actual monotonic
expiry and successor compaction. Its existing cloud GC test explicitly expected
that behavior. Four regression cases failed before the shared read guard was
added: memory/SST retained reads and warm/cold predecessor reads after takeover.
The guard implementation belongs to the runtime/transaction correctness change;
this audit adds provider-level coverage in `CloudReadAuthorityTests` and corrects
`CloudSstGarbageCollectionTests` and `PantsCloudLeaseEngineExpiryTests`.

The physical read race additionally holds a cold provider SST range request,
expires monotonic lease validity, then releases either a successful response or
a provider error. Both point reads and scans must discard that completion and
return `Fenced`.

### A committed metadata generation must cover retired WAL

Midge CLOUD-3/6 checks the selected shared generation against the catalog's
`retired_by` before staging control files. See
`src/runtime/cloud_startup/cloud_recovery/mod.rs::validate_uncommitted_cloud_metadata`
and `hydrate_cloud_metadata_within`. A nil generation cannot justify retired
history, and a generation older than the retirement generation cannot recover
the data after its WAL prefix was removed.

Pants did not check this relationship in either provider or simulated-cloud
hydration. Four arranged cases opened successfully before the fix: provider and
simulated stores, each with an older selected generation or a nil generation,
and a valid catalog containing a retired prefix. The fix captures the validated
catalog first and validates `retired_by` against the pointer-selected generation
before downloading selected control bodies or changing the local cache. It uses
the existing pointer decoder and immutable closure validation.

The preliminary catalog read does not repair either copy and releases its read
reservation before metadata admission. Repair is attempted only after the
selected metadata bodies are admitted and validated, preserving the existing
no-mutation guarantee when metadata cannot fit the shared pool. A changed
retirement generation between that probe and the final catalog capture returns
`Busy` before staging local files; `ShouldRetryChangedRetirementAuthorityBeforeStagingSelectedMetadata`
covers that race.

`CloudRecoveryRetirementAuthorityTests` covers Strict and Salvage rejection,
preservation of existing local control bytes, and successful recovery when the
selected generation reaches the retirement generation. Salvage does not invent
missing retirement authority.

### Nil initialization cannot replace a nonempty simulated cache

The provider path already used `CloudInitializationCacheValidator` when the
committed pointer has no generation. The simulated path instead treated that
empty presence set as permission to remove local control files. A deterministic
provider/simulated test proved the simulated acceptance before the fix. Both
paths now use the same validator: only a verified empty interrupted
initialization may survive a nil pointer, and rejection preserves local bytes.

### Pointer claims compare the complete lease identity

Midge `src/lease/cloud/metadata_pointer.rs::MetadataPointer::owns` compares epoch,
holder ID and owner token. `claim_metadata_pointer` refuses a non-owned pointer
at an equal or greater epoch. Pants checked the token but omitted the holder ID
from its equal-epoch claim condition. A regression with the same token and a
different holder was accepted before the fix; a different-token control was
already rejected. `CloudMetadataClaimAuthorityTests` now requires both cases to
return `Fenced` without any pointer write or byte change. Higher-epoch successor
claims continue to preserve the predecessor's committed descriptor.

## Invariant audit boundary

| Invariant | Reviewed decision and Pants evidence | Remaining boundary |
| --- | --- | --- |
| AUTH-1 | `CloudLeaseCoordinator` combines monotonic and wall-clock validity, checks epoch/owner before renewal, and validates remote ownership before cloud mutations. Existing lease-expiry and takeover regressions exercise stale-writer rejection. | A submitted provider effect may still complete after caller cancellation. No exhaustive native-provider mutation race campaign was performed here. |
| AUTH-2 | Shared runtime/transaction read guards fence cloud reads after loss. New provider tests cover retained reads, lazy scans and in-flight range completion. Diagnostics remain available while the runtime runs. | Native-provider successor reclamation across all cached/read-prefetch schedules remains unqualified. Local retained-read and terminal lifecycle behavior is reviewed in the transaction audit. |
| AUTH-4 | `CloudDdlCoordinator` uses prepared registry operations, epoch fencing and conditional registry updates. Earlier two-phase/ambiguity regressions remain relevant; runtime ownership and local journal ambiguity changes are separate. | This pass does not certify every DDL timeout/restart permutation or simultaneous multi-family operation. |
| CLOUD-1 | `ProviderCloudPersistence.PublishWalBatchAsync` uses epoch-scoped immutable keys and exact byte verification before publishing linked catalog entries. Hydration enumerates validated catalog entries, not arbitrary WAL uploads. | Native delayed uploads and all provider receipt variants were not requalified here. |
| CLOUD-2 | Upload readback, current lease validation, conditional catalog publication and readback precede completion. The runtime owns frontier publication after the worker returns. | Full actor completion-order/frontier proof belongs to the WAL/runtime audit; object existence alone remains insufficient. |
| CLOUD-3 | `CloudMetadataAuthority` selects immutable control bodies through one committed pointer; body lengths/checksums and decoded contents are validated. Claims compare the complete lease identity. New retirement-generation and nil-initialization checks prevent unauthorized replacement of local cache authority. | Alternating whole-database Midge/Pants writers and every version-chain checkpoint/cut schedule remain unqualified. Cold-cache repair with independently corrupted local control files needs explicit provider/simulated parity evidence. |
| CLOUD-4 | Hydration checks each manifest-selected remote SST's exact key and recorded length. Strict failure and explicitly verified local-copy salvage remain distinct. | This metadata inventory does not eagerly validate every SST body/block. SST codec and ranged-reader correctness have their own audit. |
| CLOUD-5 | Recovery follows validated catalog links, retains the sequence/allocation floor and distinguishes published history from cache-only WAL. Generation-floor rejection applies before local control replacement. | Every predecessor cache-continuity and malformed-tail combination was not independently reviewed in this pass. |
| CLOUD-6 | WAL retirement checks committed shared coverage and object identities, conditionally updates the catalog before exact-version deletion, and retains uncertain deletion residue. SST GC revalidates authority guards, local pins and retired identity; it acknowledges retirement only after deletion or confirmed absence. | Inherited SST retirement remains conservatively retained without predecessor reader-liveness proof. All native-provider delayed delete schedules remain unqualified. |
| CLOUD-7 | Control/catalog reads are bounded and identity-checked; providers expose typed conditional-write/delete outcomes. Lease unknown-effect reconciliation uses exact readback and preserves the distinction from definite loss. | This source review is not exhaustive S3/Azure/GCS pagination, authentication, deadline or checksum-receipt qualification. |
| CLOUD-8 | Published catalog authority is required before strict durability completion. Recovered allocation/sequence floors remain separate from visibility and cloud durability frontiers. | Full out-of-order receipt/frontier behavior is assigned to the WAL/runtime audit; this document makes no broader frontier-parity claim. |

The filesystem-backed simulated store's startup catalog-repair concurrency and
multi-process authority timing were not independently fault-qualified in this
pass. The cold-recovery tests use actual temporary local files and conditional
object-store doubles; they do not replace process-crash or live-provider proof.

## Validation

The isolated cloud workspace uses `/tmp/pants-correctness-cloud-20261104` for
restore/build artifacts so concurrent work cannot replace executing test
assemblies. Locked restore passed. An initial combined Release run passed 28
focused authority, retirement, lease-expiry and metadata cases. After fixing the
admission-before-repair regression and adding an exact late-renewal completion
barrier to the takeover fixture, a focused run passed all 23 selected cases.

The subsequent non-Sqrzl cloud namespace checkpoint ran 929 cases: 927 passed,
one live AWS S3 case skipped, and one concurrently introduced simulated shared
read-authority regression failed while its runtime guard was still being
integrated. All provider read, metadata-generation, nil-cache, pointer-owner and
admission-before-repair cases passed. The ignored intermediate report is
`test/Pants.Tests/TestResults/cloud-correctness.trx`. Final repository-wide
validation must supersede that integration checkpoint; it is not final-head,
hosted CI, production-cloud or complete parity evidence.

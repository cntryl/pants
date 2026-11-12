# Midge-to-Pants gap analysis

- **Reviewed:** 2026-11-11
- **Midge reference:** `main`, Midge 0.5.2, `c684310d9d8ce8ecd1151c47151d4bea98b348cf`
- **Pants reference:** `55e89edf11f9bb95a608363537f63631008dc241` plus current .NET 11 and cloud orphan-GC worktree changes
- **Format reference:** LSM spec FORMAT 9, `2376ba9ddc7a9dc55d5988babc3847089ea8d3e1`

The current Pants worktree contains the requested .NET 11 and
`runtime-async=on` changes plus the cloud orphan-GC follow-up described below.
The domain-source comparison is against the Pants HEAD shown above; the .NET
upgrade diff did not modify storage or transaction implementations.

This is a source-and-document comparison across storage/recovery,
transactions/runtime, cloud/providers/leases, public operations, and
qualification. The original comparison was read-only. A follow-up
implementation for the top-ranked cloud orphan-collection gap is now present in
the worktree and described below. This report is not a claim that every
interleaving has been proved or that the change passed hosted qualification.

## Result

Pants has strong current behavioral alignment with Midge for the core database
contracts. The targeted review found no confirmed FORMAT 9, WAL recovery,
manifest authority, SST encoding, transaction isolation, or TTL correctness
regression. The previously confirmed cloud orphan-collection progress gap is
addressed in the current worktree. Remaining differences include cloud upload
throughput, bounded-queue/backpressure behavior, timeout classification for
no-write commits, and transaction cleanup when callers omit disposal.

Complete behavioral and performance parity remains unestablished. Existing
qualification is deliberately bounded to named workloads, local platforms, and
provider schedules.

## Capability and behavior map

| Area | Current assessment |
| --- | --- |
| Persisted format and recovery | Current FORMAT 9 / SST V5 target and WAL/catalog authority rules align in the reviewed paths. Existing producer/consumer qualification is bounded; see open evidence below. |
| Transactions, snapshots, scans, TTL | Sequence allocation, snapshot plus own-write reads, last-write-wins defaults, write-set conflict checks, assertions, binary scan bounds, and TTL snapshot/commit-time handling align in reviewed source. |
| Cloud providers and lease | Built-in AWS S3, S3-compatible, OCI, Azure Blob, and GCS families, object namespaces, topologies, and lease-v4 shape align closely. Pants has additional typed preflight reporting and an active lease watchdog. |
| Public API shape | Deliberately different: Midge exposes synchronous Rust operations; Pants exposes .NET async APIs, `ValueTask`, cancellation, and `IAsyncDisposable`. Some caller-visible differences below need explicit acceptance. |
| Backup and verification | Both have verification and backup/restore paths. Pants supports local and simulated-cloud backup/restore; native cloud restore remains outside the documented scope. |
| CLI | Both have a `verify` CLI. Pants JSON includes the detailed report fields; its plain output and help are less informative than Midge's. An older inventory incorrectly says Pants has no product CLI. |
| Performance | No comparable end-to-end performance parity result is established. The current worktree bounds cloud orphan collection to 32-key pages and eight candidates per sweep; cloud upload controls still differ. |

## Confirmed gaps and intentional differences

### Cloud SST orphan collection: bounded work is implemented in the worktree

The reviewed Pants baseline listed and materialized the entire `sst/` prefix
before orphan collection, with limits of 1,000,000 keys, 256 MiB, and 10,000
pages. The current worktree replaces that path with one 32-key / 64 KiB page per
inventory step. Built-in AWS S3/S3-compatible (including OCI), GCS JSON/XML,
and Azure providers request that page size; the collector rejects an oversized
response before queueing or probing it. The public object-store contract
documents the same response bounds for custom providers. The simulated provider also returns
sorted 32-key pages. The request parameters follow the provider APIs: S3
[`max-keys`](https://docs.aws.amazon.com/AmazonS3/latest/API/API_ListObjectsV2.html),
GCS JSON [`maxResults`](https://docs.cloud.google.com/storage/docs/json_api/v1/objects/list),
GCS XML [`max-keys`](https://docs.cloud.google.com/storage/docs/xml-api/get-bucket-list),
and Azure [`maxresults`](https://learn.microsoft.com/rest/api/storageservices/list-blobs).

The collector retains unprocessed page keys and the page continuation between
calls, and processes at most eight candidates per call across listed and
retired SSTs. It requests the next page only after the current page drains.
Each call captures a fresh retention proof; manifest authority, local pins,
object identity, proof validation, and conditional deletion remain in the
existing path. Cursor advancement occurs only after a successful batch. The
cursors are in-memory, so a process restart starts inventory again from the
first page.

This closes the primary **bounded work and reclamation progress gap** found in
the baseline; it is not a data-loss fix. Custom `IPantsCloudObjectStore`
implementations must follow the documented 32-key / 64 KiB page contract and
4 KiB continuation-token bound or the collector reports incomplete work. HTTP
response bodies are parsed before collector-side validation, and a
nonconforming custom adapter can allocate an oversized result before returning
it. The simulated store still enumerates and sorts the full local inventory
before slicing a page, retired-name selection scans the proof keys to choose
its bounded batch, and a multi-token continuation cycle is not detected.
Cleanup advances when later collection calls occur; this
change does not establish end-to-end cleanup latency or hosted-provider
behavior.

Evidence: Pants `CloudSstGarbageCollector.CollectAsync` and
`CloudObjectStore.MaximumListPageKeys`, native provider LIST request builders,
and `SimulatedCloudObjectStore.ListPageAsync`; Midge
`runtime/hybrid_persistence/orphan_sweep.rs` and
`storage/cloud/list_page.rs`.

### Cloud WAL publication is serial and less configurable in Pants

Midge's `CloudWritePolicy` exposes WAL upload concurrency (default 4, bounded
from 1 to 64) and a WAL upload memory budget (default 256 MiB), and uses a
bounded worker pool. Pants has no equivalent public settings. Its provider path
awaits each WAL segment upload and exact-byte readback before advancing to the
next segment.

This is a **medium throughput/configuration gap**. It can increase cloud flush
and strict-commit latency on high-latency object stores. The size of the effect
depends on the commit/publication path and needs matched workload measurements;
the source difference alone does not establish a numeric performance loss.

Evidence: Midge `storage/cloud/config.rs` and `storage/hybrid/backend.rs`;
Pants `PantsCloudWritePolicy.cs` and `ProviderCloudPersistence.cs`.

### Saturated runtime queues produce different caller behavior

Midge uses nonblocking transaction queue admission and returns `WriteStall` when
the queue is full. Pants waits for channel capacity until the caller's
cancellation or runtime deadline; expiration before admission is reported as a
timeout.

This is a **caller-visible backpressure contract difference**: applications
that retry or alert based on `WriteStall` versus `Timeout` will behave
differently under saturation. It is not evidence of a commit ambiguity because
Pants reports timeout before the command is admitted.

Evidence: Midge `runtime/handle.rs`; Pants `Runtime/Internal/Actor.cs` and
`OperationDeadline.cs`.

### Pants classifies admitted no-write commit timeouts more conservatively

Pants reports `PantsCommitOutcomeUnknownException` for any admitted commit that
times out or is canceled, including empty and assertion-only commits. Midge
tracks whether the submission contains writes and reports an ordinary timeout
when the transaction cannot have changed persisted state.

This affects retry and reconciliation decisions. For empty commits, Pants can
report an unknown outcome even though it admitted no mutation. For
assertion-only commits, the successful assertion result may be uncertain, but
the commit itself does not persist a write. This is a **high-confidence API
classification difference**; deciding whether to narrow Pants's unknown
classification is a product-contract choice.

Evidence: Pants `Runtime/Internal/Actor.cs` commit response handling; Midge
`runtime/handle.rs` and `docs/user-guides/transaction-durability-contract.md`.

### Undisposed transactions have different cleanup semantics

Dropping a Midge transaction releases its snapshot registration; dropping its
engine hands runtime cleanup to a reaper. Pants requires callers to dispose
transactions asynchronously. Its public contract warns that an undisposed
transaction can keep storage pinned and cause shutdown to return Busy.

This is a **deliberate .NET lifecycle adaptation with an operational
consequence**, rather than an isolation mismatch. Applications should use
`await using` for every transaction and scan. The difference is most visible
when an exception or early return skips disposal.

Evidence: Pants `IPantsTransaction.cs`, `TransactionInstance.cs`, and
`Runtime/Internal/Actor.cs`; Midge `engine/api/transaction.rs` and
`engine/mod.rs`.

### Pants conservatively retains inherited cloud SST objects

Pants preserves predecessor SST identities after takeover until it can prove
that predecessor readers are gone. Midge restores retired SST names to its
reclamation queue and deletes only after checking manifest retirement,
authority, and pins.

Pants's policy is documented safety conservatism. The tradeoff is **additional
cloud storage cost and potentially incomplete reclamation**, not a correctness
regression. It should remain an explicit operational tradeoff until Pants has
reader-liveness evidence sufficient to safely reclaim those objects.

Evidence: `LsmSpecFormat9.md` and `Storage/Internal/LocalDiskStore.cs`; Midge
`runtime/actors/gc.rs`.

### Verification CLI output is less detailed in plain mode

Both projects ship `verify` commands. Pants's JSON output includes detailed
verification counts, byte totals, and WAL recovery boundaries. In plain mode,
Pants currently prints only health, authority, and manifest epoch; Midge prints
additional manifest/SST counts, verified bytes/blocks, and WAL recovery counts.
Midge help also documents exit codes while Pants help only lists usage.

This is a **low-severity operator diagnostics gap**. The source code shows
matching JSON schema-version and verification-scope fields. The historical
`MidgeBehaviorInventory.md` statement that Pants does not ship a product CLI is
stale and should not guide current scope decisions.

Evidence: `src/Pants.Cli/Program.cs`, `src/Pants.Cli/CommandLineParser.cs`, and
Midge `src/bin/midge.rs`.

### Smaller provider configuration and performance differences

- **Retry tuning:** Midge exposes retry count and base delay and uses randomized
  exponential backoff by default. Pants uses a fixed retry count and
  deterministic read backoff. Both avoid generic replay of uncertain
  mutations. This is a low-severity tuning difference.
- **Checksum receipts:** Midge can trust provider CRC32C receipts where
  configured and supported. Pants instead verifies uploaded SST/WAL bytes by
  readback. Pants keeps byte-level verification but gives up the provider-side
  checksum optimization.
- **OCI session credentials:** Midge's OCI credential path can carry a static
  session token; Pants's explicit OCI access-key type cannot. Pants does retain
  session tokens from environment credentials. The operational relevance of
  this edge was not established, so treat it as a possible auth-shape gap, not
  a demonstrated provider failure.

## Confirmed parity and Pants advantages

- Current Pants format markers and Midge target the same FORMAT 9 / SST V5
  profile. The reviewed WAL/catalog and lease-version-4 shapes align. The
  current compatibility baseline is FORMAT 9; older database formats require
  logical export/import into fresh storage rather than an in-place upgrade.
- The storage/recovery review found no additional current mismatch in manifest
  and journal admission, WAL/catalog mirrored recovery, SST encoding,
  compaction repair, or the reviewed crash-recovery paths.
- Transaction review found alignment in contiguous sequence allocation,
  family-scoped transactions, snapshot reads plus own intents, default
  last-write-wins, opt-in write-set conflict checks, explicit assertions,
  half-open binary scan bounds, and TTL timing.
- Both sides expose the same main cloud provider families and shared/split
  WAL/SST/control layouts. Current lease fields, conditional mutation checks,
  and object-key namespaces align. Pants's typed cloud failure reporting and
  active lease watchdog are additional capabilities.
- Pants has stricter byte-level readback verification for uploaded provider
  data where Midge can use validated checksum receipts.

## Qualification and inventory gaps

1. **The per-test parity inventory is stale.**
   `MidgeTestParityInventory.md` and its JSON are pinned to Midge
   `d582fd...`, Pants `fc3baa...`, and an older spec, not the current reviewed
   commits. Its 4,241-case count, thousands of unmapped cases, and mapping
   statuses are historical leads, not current parity evidence. The current
   Midge compiled discovery lists 4,689 cases, but discovery is not execution
   and does not establish equivalent Pants assertions.
2. **Current local correctness evidence is bounded.**
   `MidgeCorrectness.md` and `MidgeBehaviorAlignment.md` record a supported
   local checkpoint on macOS arm64, including a Release suite, crash campaign,
   and native FORMAT 9 handoffs. The evidence does not establish arbitrary
   worker completion orderings, independent-process power loss, every
   provider deletion/takeover schedule, every codec/TTL combination, Linux or
   Windows behavior, hosted CI, or live AWS operation.
3. **FORMAT 9 interop does not cover all schedules.**
   The producer/consumer matrix records 23 local engine opens across named
   handoffs. It does not qualify overlapping writers, power-loss at every
   publication cut, every provider, or all platforms.
4. **Performance parity is open.**
   Current Midge engine-read measurements were reported as noisy diagnostics,
   not a cross-engine ratio. The broader benchmark mapping includes historical
   and merely related rows with different operation boundaries. The
   comparative performance gate requires matched data, storage, durability,
   concurrency, cache state, and measurement windows, then repeated runs on
   an idle host.
5. **Native-provider qualification is incomplete.**
   Source similarities do not establish live S3/Azure/GCS/OCI behavior. Current
   cloud correctness docs leave provider ambiguity, takeover, deletion, and
   some cache/recovery schedules open.

Several entries in the older `MidgeBehaviorGapAnalysis.md` are also stale at
these heads. The current source now has symlink staging protection, indexed
SST reads, early entry admission, overlap repair, and manifest-identity cache
keys for areas that historical notes marked as open.

## Jev-assisted implementation order

I sent one request to Jev 1.14.0 with summarized candidate descriptions and
source-review evidence; no source code, credentials, or raw repository files
were sent. The request asked Jev to score implementation priority from 0
(preserve/defer) to 4 (urgent) and choose the candidate with the greatest
operational impact. Jev's ranking is advisory; the source-backed contract and
safety constraints below determine the actual sequence.

Jev chose resumable cloud SST orphan collection first, with probability 0.73
and confidence 0.70. Its score was 2.88/4 (0.89 probability on score 3). The
next operational candidate was runtime queue backpressure (choice probability
0.19), with a score of 1.88/4. Scores and confidence for the remaining
candidates are listed below.

After the implementation, Jev assessed the 32-key / 64 KiB collector guard as
present with probability 0.94. Its final residual choices were multi-token
continuation cycles (0.50 probability, 0.37 confidence) and full retired-map
selection scanning (0.45). These are advisory signals; source review and
focused regression tests remain the qualification evidence.

### Source implementation candidates

1. **Make cloud orphan collection bounded and resumable.** Implemented in the
   current worktree as the first source change. Jev rated it high; Release tests
   cover page continuation, eight-candidate sweeps, retired-proof
   acknowledgement, provider page sizing, and oversized-page rejection.
   Hosted-provider qualification and end-to-end cleanup latency remain open.
2. **Settle queue-saturation behavior, then implement only if Midge parity is
   the chosen contract.** Jev rated this moderate (1.88/4, confidence 0.84)
   and placed it second in the top-choice distribution. Pants currently waits
   for admission until timeout/cancellation; changing that to immediate
   `WriteStall` is a public behavior change. Decide the contract before code.
3. **Measure cloud WAL publication, then add bounded concurrency if the
   workload justifies it.** Jev rated this moderate (1.78/4, confidence 0.81).
   The source gap is clear, but its user impact is performance-dependent. Run
   matched provider workloads before choosing pool size and memory limits.
4. **Settle no-write timeout semantics.** Jev rated this low-to-moderate
   (1.53/4, confidence 0.62, split between scores 1 and 2). If the intended
   API contract matches Midge, classify admitted empty/no-write commit timeout
   separately from a write-bearing unknown outcome. Retain uncertainty where
   assertion success versus conflict is unknown.
5. **Add retry/checksum options only after provider measurements.** Jev rated
   this low (1.01/4, confidence 0.96). Current byte readback is stronger
   verification than a provider receipt; do not weaken it without a validated
   provider contract and a measured benefit.
6. **Improve plain `verify` output if operator feedback supports it.** Jev
   rated this low (0.96/4, confidence 0.97). It is a contained diagnostics
   change, after progress and API-contract items.
7. **Confirm the OCI session-token use case before changing the API.** Jev
   rated this low (0.92/4, confidence 0.93); the review did not establish that
   a supported OCI deployment requires this credential shape.

### Preserve current safety and API decisions

- **Do not add implicit transaction cleanup now.** Jev scored this 0.08/4
  (confidence 0.95). Keep the documented .NET `IAsyncDisposable` contract and
  `await using` guidance unless a concrete application requirement justifies
  a separate reaper design.
- **Do not relax predecessor SST retention now.** Jev scored this 0.16/4
  (confidence 0.87). Keep the conservative safety policy until reader-liveness
  proof supports reclamation.

### Qualification workstream

This work is separate from source implementation and remains required before
claiming full parity:

1. Refresh the current-source behavioral inventory and review assertions by
   contract, not by test name or aggregate test count.
2. Complete matched performance comparisons, including cloud WAL publication,
   with aligned workload boundaries and repeated runs.
3. Expand provider, platform, power-loss, and publication-cut qualification
   with exact source and dependency-lock provenance.

The request returned 3,154 input tokens and 300 output tokens. The raw response
is not stored in the repository; its selection and scores above are transcribed
from the live response. No Jev score is treated as proof of a defect or as a
substitute for source review or executable qualification.

No tests were run for this analysis. The validation evidence above is quoted
from the dated compatibility records and must be refreshed after future
storage/runtime changes.

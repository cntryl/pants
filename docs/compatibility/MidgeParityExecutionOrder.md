# Midge parity execution order

The implementation order is correctness, then behavior, then performance.
The reference is upstream Midge main `c684310d9d8ce8ecd1151c47151d4bea98b348cf`
(0.5.2) and lsm-spec main `2376ba9ddc7a9dc55d5988babc3847089ea8d3e1`
(FORMAT 9), fetched on 2026-11-04. Existing working-tree changes are preserved.

The [correctness checkpoint](MidgeCorrectness.md) and subsequent
[behavior integration checkpoint](MidgeBehaviorAlignment.md) have passed their
supported local gates, including the full Release suite, extended crash campaign
and native unsigned WAL/SST readback. Comparative performance qualification is
now active. These checkpoints retain their explicit platform and coverage limits.

## Correctness comes first

Review current invariants against implementation and executable assertions,
starting with authority, atomic visibility, acknowledged durability, authorized
recovery prefixes, immutable publication and safe reclamation. Midge's invariant
catalog is an index into source, not proof that an implementation satisfies it.
Passing the historical Pants suite does not close an unreviewed invariant.

For each concrete gap, retain a failing behavioral regression, implement the
corresponding Midge decision, and exercise reopen, uncertainty and resource
failure boundaries as appropriate. Record already-passing scenarios honestly as
coverage rather than inventing a red phase. Temporary isolated build outputs
prevent concurrent reviews from replacing crash-test child binaries.

Architectural decisions to preserve:

- The coordinator owns mutable runtime state and ordered publication. Workers
  receive immutable requests and return typed receipts; they do not advance a
  shared runtime sequence or publish health before coordinator acceptance.
- WAL replay selects an authorized chain/prefix from explicit ledger/catalog
  facts. Decoding, selection and mutation are separate decisions. Unsupported
  versions, uncertain I/O and exhausted resources never authorize salvage.
- Catalogs and committed metadata pointers establish cloud authority. LIST,
  uploads, cache presence and newer-looking recovery copies do not select it.
  Metadata must cover the shared generation recorded by WAL retirement.
- Local retained reads and cloud reads have different authority rules. Cloud
  transactions and every scan step require current lease authority, including
  cache, memtable, own-intent and empty-result paths.
- Manifest publication selects the complete immutable output set before input
  retirement. Intents bridge interrupted publication; phase labels alone are
  not proof of commitment. Pins and exact identities constrain reclamation.
- Admission precedes effects that must remain unapplied on rejection. Resource
  claims remain charged until cleanup or transfer of ownership is proved.
- Once an admitted effect may have happened, loss of its response is an unknown
  outcome. Recovery reconciles authority; timeout is not evidence of rollback.

Keep idiomatic .NET interfaces and explicit disposal where they preserve these
decisions. Any architectural divergence needs a named contract and reason;
implementation language alone is not a correctness exception.

Current audit evidence is split into [storage](MidgeCorrectnessStorage.md),
[transactions and publication](MidgeCorrectnessTransactions.md),
[cloud authority](MidgeCorrectnessCloud.md) and the
[FORMAT 9 producer/consumer matrix](Format9Interoperability.md).

Correctness qualification requires current invariant coverage, relevant fault
and recovery tests, FORMAT 9 producer/consumer evidence, formatting, a clean
Release build and the complete supported local suite. Record the exact scope
of platform, provider and fault campaigns that remain unexecuted. Do not move
to performance merely because a focused slice is green.

## Behavior follows correctness

Refresh the old per-case Midge inventory against current source. Review actual
assertions, group equivalent contracts, and distinguish covered behavior from
name candidates and explicit implementation-only exclusions. Cover observable
admission, conflict, TTL, scan, timeout, cancellation, lifecycle, configuration
and error-category contracts. Preserve the corrected authority/recovery design.

## Performance follows behavior

Use matched current datasets, storage, durability, transaction boundaries,
concurrency, cache state and measurement windows. Retain exact source and lock
provenance and collect repeated normal runs on an idle host. Historical pins,
allocation improvements and noisy smoke runs are not throughput/latency parity.
Follow Midge's indexed read views, bounded streams, pure planning and ownership
decisions before introducing a different optimization architecture.

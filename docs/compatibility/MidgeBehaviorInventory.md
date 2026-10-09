# Midge contract inventory

Pants is baselined against Midge commit
`7d39f86217fdb07191a83bd885a514dd5ea9723f`.

The committed, machine-readable inventory is
[`MidgeContractManifest.json`](../../test/Pants.Tests/MidgeContractManifest.json).
It records compatibility-bearing public source symbols and integration tests,
their observable behavior, the expected Midge error when statically
discoverable, the mapped Pants test, and coverage status. The inventory was
discovered at `75dcc39` and its sources were re-resolved at `7d39f86`: entries
for tests Midge consolidated into its 13 integration targets now cite the new
file and Rust module path, entries for tests Midge deleted were removed, and
tests Midge renamed with a changed assertion are marked `revalidate`. Its 945
entries are a historical mapping, not a current coverage certificate: some
mappings reference removed shape/architecture tests, and it omits integration
tests added after `75dcc39` and the source-test inventory. See the
[behavior gap analysis](MidgeBehaviorGapAnalysis.md) for known uncovered
scenarios, demonstrated differences, and unresolved baseline questions.
Public exports and public integration tests are canonical. Source-level
tests also establish observable behavior and resource guarantees; genuinely
Rust-specific implementation checks do not override public contracts.

Runtime decomposition is intentionally compatibility-neutral: coordinator,
WAL, flush, compaction, immutable-flush, and commit-coalescing responsibilities
are separate internal types, but the public contracts and persisted Midge wire
formats remain unchanged. See [`runtime.md`](../architecture/runtime.md).

The removed compatibility harness originally generated this inventory and the
golden fixtures from a clean checkout at the pinned commit. Normal restore,
build, and test commands remain self-contained and consume only committed
artifacts. See [`MidgeQualification.md`](MidgeQualification.md) for the current
baseline-maintenance and fixture policy.

The delta from the preceding baseline is reviewed in
[`MidgeCurrentBaselineReview.md`](MidgeCurrentBaselineReview.md). In addition to
the static inventory, the committed fixtures preserve the verified local and
simulated-cloud interchange formats from that qualification.

Midge CLI-only verification behaviors are explicitly not applicable because
Pants exposes verification through its async database interfaces and does not
ship a product CLI. Rust-specific module dependency checks and Midge's
benchmark, mutation, coverage-analysis, and pull-request scripts are also not
applicable: they govern Midge's private implementation and repository tooling
rather than observable database behavior.

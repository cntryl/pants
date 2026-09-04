# Midge contract inventory

Pants is baselined against Midge commit
`75dcc39f7a9b87df480ed91c3a5c93fe1389ca71`.

The committed, machine-readable inventory is
[`MidgeContractManifest.json`](../../test/Pants.Tests/MidgeContractManifest.json).
It records the compatibility-bearing public source symbols and integration
tests discovered at that exact revision, their observable behavior, the
expected Midge error when statically discoverable, the mapped Pants test, and
coverage status. All 949 current entries are either mapped to an executable
Pants test or carry a reviewed `n/a` rationale; none are planned. Public exports
and public integration tests are canonical. Private Rust-only implementation
tests do not override those contracts.

Runtime decomposition is intentionally compatibility-neutral: coordinator,
WAL, flush, compaction, immutable-flush, and commit-coalescing responsibilities
are separate internal types, but the public contracts and persisted Midge wire
formats remain unchanged. See [`runtime.md`](../architecture/runtime.md).

The release-only compatibility harness refreshes this inventory and the golden
fixtures together from a clean checkout at the pinned commit. It also runs the
alternate-process, bidirectional qualification. Normal restore, build, and test
commands remain self-contained and consume only committed artifacts. See
[`MidgeQualification.md`](MidgeQualification.md) for the exact commands and
fixture policy.

The delta from the preceding baseline is reviewed in
[`MidgeCurrentBaselineReview.md`](MidgeCurrentBaselineReview.md). In addition to
the static inventory, the release-only qualification hands live databases
between current Midge and Pants in both directions for local and simulated-cloud
storage.

Midge CLI-only verification behaviors are explicitly not applicable because
Pants exposes verification through its async database interfaces and does not
ship a product CLI. Rust-specific module dependency checks and Midge's
benchmark, mutation, coverage-analysis, and pull-request scripts are also not
applicable: they govern Midge's private implementation and repository tooling
rather than observable database behavior.

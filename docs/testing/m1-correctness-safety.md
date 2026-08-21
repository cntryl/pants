# M1 Correctness and Safety

M1 closes the behavior groups assigned to issues #1, #2, and #3 in the
committed Midge contract manifest. The source baseline remains Midge commit
`c5ffc2d3284c76b6f7cd03444a5b0a38ae8bbc33`.

The executable coverage is organized by invariant rather than by mechanically
copying Rust test structure. Transaction suites cover atomic intent ordering,
snapshot isolation, strict conflicts, assertions, spill accounting, terminal
states, and concurrent commits. Lifecycle suites cover immutable option
derivation, column-family identity and persistence, single-writer exclusion,
lease loss, and strict/salvage recovery. Persistence suites exercise WAL,
flush, compaction, manifest, intent-log, checkpoint, and no-space boundaries.

The M1 completion test fails while any contract assigned to issues #1-#3 is
still `planned`. Entries marked `mapped` name one or more executable Pants
tests. Entries marked `n/a` are limited to Rust Cargo/workflow checks or Midge
subprocess-driver plumbing; their observable recovery invariants remain mapped
to deterministic Pants failpoint tests.

Six cloud-specific failure-injection contracts are assigned to issue #7 and
remain planned for the cloud milestone. M1 does not claim those behaviors.

# M1 Correctness and Safety

M1 closes the behavior groups assigned to issues #1, #2, and #3 in the
committed Midge contract manifest. The source baseline remains Midge commit
`75dcc39f7a9b87df480ed91c3a5c93fe1389ca71`.

The executable coverage is organized by invariant rather than by mechanically
copying Rust test structure. Transaction suites cover atomic intent ordering,
snapshot isolation, strict conflicts, assertions, spill accounting, terminal
states, and concurrent commits. Lifecycle suites cover immutable option
derivation, column-family identity and persistence, single-writer exclusion,
lease loss, and strict/salvage recovery. Persistence suites exercise WAL,
flush, compaction, manifest, intent-log, checkpoint, and no-space boundaries.

The manifest is a review inventory, not an executable completion gate. Entries
marked `mapped` identify behavioral Pants tests; tests do not validate manifest
status strings or method names. Entries marked `n/a` identify Rust Cargo/workflow
checks or Midge subprocess-driver plumbing; their observable recovery invariants
remain covered by deterministic Pants failpoint tests.

Cloud-specific failure-injection contracts remain assigned to their cloud
milestones, but are now mapped to executable Pants tests. M1 itself claims only
the contracts assigned to issues #1-#3.

# Metadata-only SST promotion

This slice matches the pinned Midge `c684310d9d8ce8ecd1151c47151d4bea98b348cf`
whole-file promotion decision. It closes the small local-budget maintenance case
recorded in the read-resource audit; it does not establish throughput or platform
qualification.

Midge's `src/runtime/actors/compaction/moves.rs:4` permits a merge to move existing
SSTs when all accepted inputs belong to the source level and family, none belongs
to the target level, each fits the accepted output target, and complete key bounds
prove a legal disjoint placement. Its grandparent overlap check also applies.
`src/runtime/actors/compaction.rs:735` caps the hybrid target at one eighth of the
local storage allowance and avoids output-storage admission for an accepted move.
The event loop records captured identities and revalidates them before publishing
(`src/runtime/event_loop/compaction/moves.rs:5`); reused inputs are not prepared
outputs or rollback-owned files.

Pants now uses its existing FORMAT 9 `MoveSsts` edit and `MoveSstsPublish` intent
for those plans. The canonical manifest transition validates exact captured
identities, family and level, complete trusted bounds, disjointness, and the target
span both at selection and publication. Existing unknown bounds, overlap,
oversized files, and non-merge plans still use the physical rewrite path. The
whole-file grandparent allowance is the pinned public Midge default of 320 MiB.

An accepted move retains filenames, physical bytes, sequence bounds and proofs.
It needs no SST read, decode, new output, or hybrid staging reservation. The
intent and manifest publication still cross the existing local/provider authority
checks; the provider publishes the metadata intent even with no new SST outputs.
Cleanup and supersession never claim reused inputs as removable output files.
Physical compaction counters, rewritten bytes, and committed SST accounting do
not increase for a metadata move. Manifest checkpoints and placement do advance.

The native reference run used a 64 KiB local memory allowance, 64 unique 1 KiB
values, a flush every ten writes plus a final flush, and manual compaction. It
finished with the same eight SST identities in L1 and no physical compaction.
Pants exercises that exact public schedule, then reopens with a larger read
allowance and verifies all 64 values. Separate public tests cover local and
simulated-cloud identity and SHA-256 preservation, retained snapshots, a hybrid
move while unrelated disk capacity is occupied, a cold Azure-backed move without
SST reads, and empty-cache provider recovery. Pre- and post-journal failures must
retain the original bytes and recover or retry safely. Overlap, accepted-target
size, and incomplete-bound controls must still rewrite.

Existing fault tests that require a physical output now seed genuine overlapping
inputs, or an SST larger than the accepted move target for single-input admission
races. Their row, fault, cleanup and reopen assertions remain in place. Tests of
disjoint layouts instead observe placement changes without physical-write credit.

The retained `/tmp/pants-behavior-sst-promotion-results-20261104` artifacts contain
the initial public failures, focused controls, expanded gate results, source
snapshots, hashes and patch. This is bounded local engine/provider-handler
evidence. Remote service, multi-platform and performance qualification remain
separate.

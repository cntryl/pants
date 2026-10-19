# Compaction Policy

## L0 source selection

L0 compaction selects the oldest SST sequences first, up to the configured
source-file bound. Selection is deterministic and does not use read heat. The
read-amplification trigger remains an independent reason to run a forced
compaction; it does not change which L0 files the planner selects. The planner
regression `ShouldPickBoundedL0InputsAndEveryOverlappingL1File` asserts the
oldest-first order and complete target overlap.

This keeps L0 drainage deterministic and independent of a moving heat signal.
Heat-aware selection remains a performance experiment for the M06 workload
qualification, where candidate-selection gains can be measured against any
increase in read amplification or retention time.

## Hybrid pass resources

The actor protects the exact input names of each compaction plan before the
merge opens them and holds those protections through manifest publication.
Continued and debt-drain rounds request a fresh protection lease for their
current inputs. The focused regression verifies that each forced pass retains
its lease until publication and releases it afterward.

Hybrid compaction reserves one fixed local staging window for the whole
serialized compaction request. Each output partition is published and its
local file removed before the next partition is staged, so later passes reuse
the same bounded window. The reservation is released when the request ends;
reserving the identical window again between sequential passes would not lower
the high-water mark and could let another writer consume space between passes.

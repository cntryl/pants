# Current behavior alignment

The supported local behavior integration checkpoint passed on 2026-11-04.
Behavior follows the [qualified correctness checkpoint](MidgeCorrectness.md).
The reference remains Midge `c684310d9d8ce8ecd1151c47151d4bea98b348cf` and
lsm-spec `2376ba9ddc7a9dc55d5988babc3847089ea8d3e1`. Comparative performance
qualification follows behavior. Historical name mappings are not current proof.
The [current compiled inventory](MidgeCurrentBehaviorDiscovery.md) contains 4,689
cases in the recorded macOS feature matrix; discovery is separate from execution.

| Contract | Current status and executable evidence |
| --- | --- |
| Scan cache policy | Integrated. [Normal/Bypass](MidgeScanCachePolicy.md) follows the limit-64 default and explicit override, including existing hits under bypass and remote range reads. Four failing cases plus four controls preceded 31 new cases; the related gate passes 153 cases. |
| In-memory global pressure | Integrated. Three failing engine cases now prove stalls, unchanged sequence on refusal, unrelated-drop control, existing-waiter wake and successful retry after debtor discard. The pressure/flush/L0 gate passes 70 cases. |
| Safe family drop | Integrated. `PantsUnflushedDataPresentException` carries family/bytes and is the sole discard licence; active data is checked before definite Busy for immutable work. Seven failing cases preceded 178 passing family/DDL/drop/flush/recovery cases. |
| Cloud namespace aliases | Integrated. Provider location construction and record copies trim boundary slashes. Six failures and two dot-segment controls preceded 24 passing namespace/validation cases, including shared authority and cold-cache SST recovery. |
| Write backpressure | Integrated. [Six-field policy](../architecture/write-backpressure.md), pending debt, original caller deadline and shared encoded-output limiter. The original slice passes 110 cases; the root combination passes 198. The 153-case supplement (14 new cases, three demonstrated defects) also passes queue, soft spill/coalescing, shared repair rate and held-mirror schedules. Its combined promotion gate initially exposed two physical-output fixtures; both pass after retaining explicit overlapping inputs. |
| Lease policy | Integrated. [Derived skew and renewal](../architecture/lease-renewal.md) follows the provider margin, shared renewal window, definite retry and immediate ambiguous fencing with retained physical ownership. The original isolated gate passes 172 cases and the root combination passes 196. The separate long-duration timer slice passes 174 cases, including six demonstrated failures and a retained native-lock control; all seven new cases pass in the combined promotion/timer gate. |
| Transaction receipts, TTL and cloud target waits | Integrated after 1,471 passes and one expected live-AWS skip in the isolated transaction gate. It covers serialized TTL observation, equal-range rejection, durability categories, exact sequence receipts, no-op/assertion distinction, explicit cloud waits and authority-derived recovery frontiers. Crash regressions cover both a lost retired anchor and unused reservation gaps. The first combined gate passed 2,145 cases with one expected skip and two invalid short-lease fixtures; their corrected fencing schedules pass in the 140-case follow-up. |
| Shared read resources | Integrated. [Shared read resources](MidgeBehaviorReadResources.md) follows Midge's 75/25 content/metadata split, shared reader/open ownership, lazy trusted runs and transaction-owned reverse spill admission. The root combination passes 842 cases after valid-lease fixture updates. Native controls distinguish startup validation, runtime metadata refusal and format-bounded public payloads. Metadata-only progress has its separate promotion qualification below. |
| Metadata-only promotion | Integrated after 338 isolated passes, including Sqrzl. [Move publication](MidgeBehaviorSstPromotion.md) reuses FORMAT 9 identities and intent phases without reading, rewriting, retiring or claiming physical output bytes for eligible inputs. The combined root gate passes 508 cases; its two physical-output fixture corrections pass separately. Four existing failed-freeze cases also retain real rewritten output by selecting a target below the seeded SST size and pass in the 35-case entry/L0 gate. |
| Public query assertions | Strengthened exact ordered vectors, inclusive/exclusive boundaries, missing seeks, concurrent scans, range survivors, preexisting SST/memtable merges, retained snapshots, and genuine consumed-input compaction. The 16-case query matrix passes; the 140-case related gate includes strengthened TTL/reopen boundaries and separate-process filesystem observation with a persistent control. |
| Cloud wait outcome routing | Integrated after 129 isolated passes and independent reproduction of both ACK/shutdown winner orders. Actual covering segments determine eligibility; accepted shutdown ends pending observers before physical join; caller cancellation and deadlines cannot overwrite an already routed result. The invented live-observer cap is removed. The root combination passes 128 cases, followed by 89 combined WAL-capacity/transaction/wait cases. |
| Entry staging and WAL capacity | Staging and streamed hybrid bounds are integrated after 32 focused and 1,194 broader isolated passes; the root entry/L0 gate passes 35 cases. [WAL failure phases](MidgeWalEncodingFailureBehavior.md) distinguish configured cloud replay admission, allocated-but-rejected hybrid units, and local accepted-encoder poisoning. All 27 new cases and the 89-case root combination pass. The final isolated writer union passes 1,479 cases with one expected live-AWS skip, including 14 Sqrzl cases. |
| Configured cloud startup replay | Integrated. [Replay admission](MidgeCloudReplayBudgetBehavior.md) checks frame and declared decoded sizes before large allocations, bounds batch expansion and shared pending split transactions, and preserves resource errors under Strict and Salvage. Actual native and pinned-Pants comparisons demonstrated the mismatch. Both policies now reject the small-budget image and recover its exact acknowledged rows from the same failed cache with the larger budget. The related gate passed 664 cases and exposed two stale compatibility fixtures; corrected fixtures and all 11 new cases pass. Local format-bounded decoding remains an explicit control. |
| Reader-cache diagnostics | Integrated. Each async cache lookup returns its own immutable reader/hit receipt. Initial openers and concurrent joiners remain misses; a later cached lookup is a hit. The query and aggregate diagnostics consume that same receipt. Actual provider tests prove the warm query makes zero requests, with an existing block hit and zero decoded blocks. The related ownership/read-resource gate passes 68 cases. Exact storage layout distinguishes 13 unrelated preserved SSTs from the prior rewrite-dependent expectation of 12. |

These are finite local gates, not complete behavior equivalence. Their counts
overlap and must not be added. Public receipt APIs, unsigned diagnostics, the
typed unflushed-data outcome, normalized namespace values and nullable DI skew
intent have API compatibility implications. Persisted FORMAT 9/SST V5 is unchanged.

Current source/assertion inputs group 62 query/read cases, 153 transaction cases,
98 family/lifecycle/lease cases and the options/configuration assertions. The
historical complete inventory still requires reconciliation against current
assertions; source presence and unchanged names are not execution certification.
Native provider transports, Linux/Windows behavior and comparative benchmarks
remain explicit qualification dimensions.

## Integrated local qualification, 2026-11-04

Locked restore, formatting verification and a forced Release solution build
passed with zero warnings/errors. The frozen complete Release suite passed
**3,676** cases with zero failures and two expected skips in 9 minutes 10 seconds.
All **15** Sqrzl provider/crash-smoke cases passed. Live AWS remains unexecuted;
the skipped opt-in extended campaign was subsequently run explicitly.

The extended run passed nine seed/backend combinations: seeds 3131, 7717 and
2929 across Local, simulated cloud and Sqrzl, with four cycles and 96 planned
operations per cycle. All 36 cycles passed their recovery-model checks. Eleven
planned endpoints fell back to workload completion; the actual endpoints remain
in the TRX rather than being credited as reached failpoints. Both simulated
cloud and Sqrzl reached seed 2929's `MidWalAppend` hit 25.

Fresh Pants WAL-only and flushed-SST continuations at sequence
`9223372036857921555` were consumed by native current Midge, which verified all
three exact rows across two opens. FORMAT 9/SST V5 remains unchanged.

The retained results are `behavior-final-combined-v2.trx` and
`behavior-final-extended-crash.trx` under the ignored test result directory.
The 1,450 source/build/fixture inputs and 232 DLL/JSON output candidates remained
unchanged through qualification. Their manifest and hashes are retained in
`/tmp/pants-final-behavior-20261104-v2.qualification.json`; actual crash endpoints
are in `/tmp/pants-final-behavior-20261104-v2.crash-details.json`, and native readback is in
`/tmp/pants-behavior-u64-interop-20261104/qualification.json`.

The first frozen full run is retained separately: 3,665 passes, nine failures
and two expected skips. It exposed eight fixture mismatches with the aligned
contracts and the actual warm reader-cache attribution defect. Each correction
keeps the original behavioral oracle or strengthens it; physical-work fixtures
prove actual rewrites rather than crediting metadata moves. No source or binary
was replaced during either full run or the extended crash run.

This qualifies the named integration checkpoint on macOS arm64. The complete
historical per-case mapping, other native platforms, live provider environments
and comparative performance remain separate evidence. Performance work now
follows this checkpoint in the requested order.

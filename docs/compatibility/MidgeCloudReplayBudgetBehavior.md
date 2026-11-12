# Cloud WAL replay budgets

Cloud startup uses the writer's configured replay envelope: the smaller of one sixteenth of total memory and one half of the local storage budget. Reopening with a smaller budget can therefore report `ResourceLimit` for a valid transaction acknowledged by a larger-budget writer. Strict and Salvage both preserve that transaction; reopening the same cache with a sufficient budget can recover it.

The reader checks a frame's size before allocating its body, and checks the record's declared decompressed size before copying fields or decompressing values. Atomic batches additionally use Midge's 64-bit decoded allocation bound (`2 * payload bytes + 416 * operation count`). The record preflight charges 144 bytes plus its key, range end, and declared decoded value. Open split transactions share a pending limit: 128 bytes per open transaction and 288 bytes plus key, value, and range-end lengths per member. Charges are released at commit or a writer-epoch transition. These are compatibility admission bounds, not measurements of .NET heap allocation.

Local storage keeps its existing replay policy. The persisted format and public API are unchanged. FORMAT 9 stores the writer epoch in the segment header, so startup's epoch-floor discovery reads that header without decoding record values before recovery admission.

The regression captures a live, acknowledged cloud WAL image with no SSTs, attempts recovery with an insufficient budget under each policy, then reopens the same failed cache with the original larger budget and verifies the acknowledged prefix and complete transaction. Component cases cover frame-read ordering, LZ4 and Zstd preflight, batch bounds, and pending-unit reclamation.

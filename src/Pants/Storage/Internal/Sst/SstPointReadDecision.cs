namespace Pants;

internal readonly record struct SstPointReadDecision(
    int BloomChecks,
    int CandidateBlocks,
    int BlocksRead,
    bool Rejected,
    int CandidateBlockIndex,
    int CandidateBlockSizeBytes);

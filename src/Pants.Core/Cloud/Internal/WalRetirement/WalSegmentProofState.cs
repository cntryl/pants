namespace Cntryl.Pants.Cloud.Internal.WalRetirement;

enum WalSegmentProofState
{
    /// <summary>Every byte was read and checked and every record is covered.</summary>
    Covered,

    /// <summary>A record is not covered by the published manifest; the segment keeps authority.</summary>
    Uncovered,

    /// <summary>The turn's quantum ran out at a frame boundary; the proof resumes there.</summary>
    Yielded,

    /// <summary>The WAL object is no longer the pinned version; the proof must restart.</summary>
    IdentityChanged
}

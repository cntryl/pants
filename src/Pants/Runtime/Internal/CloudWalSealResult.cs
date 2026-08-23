namespace Cntryl.Pants;

sealed record CloudWalSealResult(
    SealedWalSegment? Segment,
    Exception? PostRotationFailure);

namespace Cntryl.Pants.Runtime.Internal;

sealed record CloudWalSealResult(
    SealedWalSegment? Segment,
    Exception? PostRotationFailure);

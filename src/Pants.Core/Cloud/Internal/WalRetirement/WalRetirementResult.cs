namespace Cntryl.Pants.Cloud.Internal.WalRetirement;

/// <param name="Outcome">How the turn ended.</param>
/// <param name="RetiredSegments">How many catalog segments the turn retired.</param>
readonly record struct WalRetirementResult(WalRetirementOutcome Outcome, int RetiredSegments)
{
    public static WalRetirementResult Settled { get; } = new(WalRetirementOutcome.Settled, 0);
}

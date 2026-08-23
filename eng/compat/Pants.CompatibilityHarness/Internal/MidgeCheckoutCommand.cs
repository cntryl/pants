namespace Pants.CompatibilityHarness.Internal;

internal sealed record MidgeCheckoutCommand(
    MidgeCheckoutOperation Operation,
    string CheckoutPath,
    bool ForceRefresh,
    bool CheckBaseline);

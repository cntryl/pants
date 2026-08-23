namespace Pants.CompatibilityHarness.Internal;

internal sealed record MidgeDriverBuild(
    string CheckoutPath,
    string ExecutablePath,
    TimeSpan BuildTime);

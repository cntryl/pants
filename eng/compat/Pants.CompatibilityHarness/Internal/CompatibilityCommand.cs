namespace Pants.CompatibilityHarness.Internal;

internal sealed record CompatibilityCommand(
    CompatibilityStorageMode StorageMode,
    CompatibilityOperation Operation,
    string DatabasePath,
    IReadOnlyList<string> Producers);

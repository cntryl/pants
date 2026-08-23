namespace Pants;

internal sealed record KeyStructureProfile(
    float AverageSharedPrefix,
    int MaximumSharedPrefix,
    int PrefixDivergence,
    float Entropy,
    int CommonPrefixLength,
    float KeyLengthStandardDeviation,
    IReadOnlyList<KeyStructurePrefixHeat> PrefixHeat,
    int KeyCount);

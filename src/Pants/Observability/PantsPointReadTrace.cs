using System.Collections.Immutable;

namespace Cntryl.Pants;

public sealed record PantsPointReadTrace(
    int KeyRangeRejects,
    IReadOnlyList<PantsSstReadTrace> Ssts)
{
    public IReadOnlyList<PantsSstReadTrace> Ssts { get; } =
        ImmutableArray.CreateRange(Ssts ?? throw new ArgumentNullException(nameof(Ssts)));
}

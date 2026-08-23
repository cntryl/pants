using System.Text.Json.Serialization;

namespace Cntryl.Pants.CompatibilityHarness.Internal;

internal sealed record CompatibilityFixtureArtifact(
    string Id,
    string Structure,
    string Producer,
    string Coverage,
    string Path,
    string Sha256,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Rationale);

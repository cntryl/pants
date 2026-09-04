namespace Cntryl.Pants.Cloud.Internal;

static class CloudObjectIdentity
{
    public static string RequireVersion(string? version) =>
        string.IsNullOrWhiteSpace(version)
            ? throw new PantsIOException("Cloud object response did not include a non-empty version.")
            : version;
}

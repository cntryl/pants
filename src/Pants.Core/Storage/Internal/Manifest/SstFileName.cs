namespace Cntryl.Pants.Storage.Internal.Manifest;

/// <summary>
///     Decides whether a manifest-recorded SST name is safe to join onto the SST directory. A name
///     must be a bare file name, so it can neither escape the directory nor address another drive,
///     stream or NUL-terminated path.
/// </summary>
static class SstFileName
{
    public static bool IsSafe(string name) =>
        !string.IsNullOrEmpty(name) &&
        name == Path.GetFileName(name) &&
        name.EndsWith(".sst", StringComparison.Ordinal) &&
        !name.Contains(':') &&
        !name.Contains('\\') &&
        !name.Contains('\0');
}

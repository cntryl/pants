namespace Cntryl.Pants.Storage.Internal.IO;

/// <summary>
///     Renames a file over its destination even while another process holds the destination open
///     for reading.
/// </summary>
/// <remarks>
///     POSIX rename already lets readers keep the replaced file. Windows <c>MoveFileEx</c> refuses
///     with access denied while any handle is open on the destination, even one that shares
///     delete, so that one refusal falls back to a POSIX-semantics rename: existing readers keep
///     the old file and new opens see the replacement. If the fallback is unavailable the original
///     refusal surfaces, so callers' transient-sharing handling still applies.
/// </remarks>
static class ReplacingFileMove
{
    public static void Move(string source, string destination, bool overwrite) =>
        Move(
            source,
            destination,
            overwrite,
            OperatingSystem.IsWindows(),
            File.Move,
            WindowsPosixRename.TryReplace);

    internal static void Move(
        string source,
        string destination,
        bool overwrite,
        bool isWindows,
        Action<string, string, bool> move,
        Func<string, string, bool> posixReplace)
    {
        try
        {
            move(source, destination, overwrite);
        }
        catch (UnauthorizedAccessException) when (overwrite && isWindows)
        {
            if (!posixReplace(source, destination))
            {
                throw;
            }
        }
    }
}

namespace Cntryl.Pants.Storage.Internal;

/// <summary>
///     Distinguishes a missing, inaccessible, non-directory or malformed storage path, and
///     unreadable storage files, before verification reads any contents. Absent storage files are
///     left to verification, which classifies them as corruption.
/// </summary>
static class StoragePathPreflight
{
    public static PantsStorageVerificationOutcome? Check(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Fail(PantsStoragePathFailure.Invalid, "The storage path is empty.");
        }

        string root;
        try
        {
            root = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException
                                              or PathTooLongException)
        {
            return Fail(PantsStoragePathFailure.Invalid, $"The storage path is invalid: {exception.Message}");
        }

        if (File.Exists(root))
        {
            return Fail(PantsStoragePathFailure.NotADirectory, $"Storage path '{root}' is not a directory.");
        }

        try
        {
            using var entries = Directory.EnumerateFileSystemEntries(root).GetEnumerator();
            _ = entries.MoveNext();
        }
        catch (DirectoryNotFoundException)
        {
            return Fail(PantsStoragePathFailure.Missing, $"Storage path '{root}' does not exist.");
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return Fail(
                PantsStoragePathFailure.Inaccessible,
                $"Storage path '{root}' is inaccessible: {exception.Message}");
        }

        var snapshot = Path.Combine(root, "manifest.snapshot.json");
        string[] storageFiles =
        [
            Path.Combine(root, "FORMAT"),
            File.Exists(snapshot) ? snapshot : Path.Combine(root, "manifest.json"),
            Path.Combine(root, "manifest.journal"),
            Path.Combine(root, "intent_log.json")
        ];
        foreach (var storageFile in storageFiles)
        {
            if (CheckReadable(storageFile) is { } failure)
            {
                return failure;
            }
        }

        return null;
    }

    static PantsStorageVerificationOutcome? CheckReadable(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return null;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return Fail(
                PantsStoragePathFailure.Inaccessible,
                $"Storage file '{path}' is inaccessible: {exception.Message}");
        }
    }

    static PantsStorageVerificationOutcome Fail(PantsStoragePathFailure failure, string message) =>
        PantsStorageVerificationOutcome.FromPathFailure(failure, message);
}

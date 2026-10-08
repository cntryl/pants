namespace Cntryl.Pants.Storage;

/// <summary>Why a storage path could not be verified before its contents were read.</summary>
public enum PantsStoragePathFailure
{
    /// <summary>The path does not exist.</summary>
    Missing,

    /// <summary>The path, or a storage file under it, exists but cannot be read.</summary>
    Inaccessible,

    /// <summary>The path exists but is not a directory.</summary>
    NotADirectory,

    /// <summary>The path is empty or malformed for this platform.</summary>
    Invalid
}

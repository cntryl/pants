namespace Cntryl.Pants.Transactions.Internal.Spill;

/// <summary>
///     Removes one spill file from disk. Failures surface as <see cref="IOException" /> or
///     <see cref="UnauthorizedAccessException" />; a file that is already absent is not a failure.
/// </summary>
interface ISpillFileRemover
{
    void Remove(string path);
}

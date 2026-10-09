namespace Cntryl.Pants.Transactions.Internal.Spill;

sealed class FileSystemSpillFileRemover : ISpillFileRemover
{
    public void Remove(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}

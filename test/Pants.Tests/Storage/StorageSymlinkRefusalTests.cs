using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage;

public sealed class StorageSymlinkRefusalTests
{
    [Fact]
    public async Task ShouldRefuseOpenWhenSstDirectoryIsLinkedOutsideStore()
    {
        using var directory = new TemporaryDirectory();
        using var outside = new TemporaryDirectory();
        await CreateStoreAsync(directory.Path);
        var sst = Path.Combine(directory.Path, "sst");
        Directory.Delete(sst, true);
        if (!TryCreateDirectoryLink(sst, outside.Path))
        {
            return;
        }

        await Assert.ThrowsAsync<PantsInvalidPathException>(
            async () => await OpenAsync(directory.Path));
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside.Path));
    }

    [Fact]
    public async Task ShouldRefuseOpenWhenManifestJournalIsLinkedFile()
    {
        using var directory = new TemporaryDirectory();
        using var outside = new TemporaryDirectory();
        await CreateStoreAsync(directory.Path);
        var journal = Path.Combine(directory.Path, "manifest.journal");
        var target = Path.Combine(outside.Path, "journal");
        File.Copy(journal, target);
        File.Delete(journal);
        if (!TryCreateFileLink(journal, target))
        {
            return;
        }

        await Assert.ThrowsAsync<PantsInvalidPathException>(
            async () => await OpenAsync(directory.Path));
    }

    [Fact]
    public async Task ShouldOpenWhenStoreRootItselfIsLinked()
    {
        using var directory = new TemporaryDirectory();
        using var real = new TemporaryDirectory();
        await CreateStoreAsync(real.Path);
        var link = Path.Combine(directory.Path, "root-link");
        if (!TryCreateDirectoryLink(link, real.Path))
        {
            return;
        }

        await using var database = await OpenAsync(link);
    }

    [Fact]
    public async Task ShouldRefuseRuntimeStagedWriteThroughLinkedParent()
    {
        using var directory = new TemporaryDirectory();
        using var outside = new TemporaryDirectory();
        var link = Path.Combine(directory.Path, "link");
        if (!TryCreateDirectoryLink(link, outside.Path))
        {
            return;
        }

        Assert.Throws<PantsInvalidPathException>(
            () => Internal.IO.AtomicStagedFile.Write(Path.Combine(link, "file"), "x"u8));
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside.Path));
    }

    static async Task CreateStoreAsync(string path)
    {
        await using var database = await OpenAsync(path);
    }

    static ValueTask<IPantsDatabase> OpenAsync(string path) =>
        PantsDatabase.OpenAsync(PantsOpenOptions.Local(path));

    static bool TryCreateDirectoryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    static bool TryCreateFileLink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

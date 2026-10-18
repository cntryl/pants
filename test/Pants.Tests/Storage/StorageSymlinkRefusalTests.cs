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
    public async Task ShouldRefuseStorageVerificationWhenSstDirectoryIsLinkedOutsideStore()
    {
        using var directory = new TemporaryDirectory();
        using var outside = new TemporaryDirectory();
        await CreateStoreAsync(directory.Path);
        var sstDirectory = Path.Combine(directory.Path, "sst");
        Directory.Delete(sstDirectory, recursive: true);
        if (!TryCreateDirectoryLink(sstDirectory, outside.Path))
        {
            return;
        }

        await Assert.ThrowsAsync<PantsInvalidPathException>(async () =>
            await PantsDatabase.VerifyPathAsync(directory.Path));
    }

    [Fact]
    public async Task ShouldFailHybridResidueAccountingWhenAnSstEntryIsLinkedOutsideStore()
    {
        using var directory = new TemporaryDirectory();
        using var outside = new TemporaryDirectory();
        var root = Internal.IO.StoragePathGuard.CanonicalizeRoot(directory.Path);
        var options = PantsOpenOptions.SimulatedCloud(root, "pants-tests", "path-guard/")
            .WithSimulatedCloudLocalStorageBudget(128 * 1024)
            .WithBackgroundCompaction(false);
        await using var database = await PantsDatabase.OpenAsync(options);

        var outsideFile = Path.Combine(outside.Path, "outside.sst");
        File.WriteAllBytes(outsideFile, new byte[1024]);
        var linkedSst = Path.Combine(root, "sst", "outside.sst");
        if (!TryCreateFileLink(linkedSst, outsideFile))
        {
            return;
        }

        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put("key"u8.ToArray(), "value"u8.ToArray());
        var exception = await Assert.ThrowsAsync<PantsRecoveryFailedException>(() =>
            transaction.CommitAsync(PantsWriteOptions.CloudStrict).AsTask());
        Assert.Equal(PantsErrorCode.RecoveryFailed, exception.Code);
        Assert.Equal(1024, new FileInfo(outsideFile).Length);
    }

    [Fact]
    public async Task ShouldRefuseStaleLeaseMutationLockRecoveryWhenTheLockIsLinkedOutsideStore()
    {
        using var directory = new TemporaryDirectory();
        using var outside = new TemporaryDirectory();
        var root = Internal.IO.StoragePathGuard.CanonicalizeRoot(directory.Path);
        var outsideFile = Path.Combine(outside.Path, "lock-target");
        File.WriteAllText(outsideFile, "owner_token=outside\n");
        var lockPath = Path.Combine(root, ".midge_leader.lock");
        if (!TryCreateFileLink(lockPath, outsideFile))
        {
            return;
        }

        await Assert.ThrowsAsync<PantsInvalidPathException>(() =>
            PantsDatabase.RecoverStaleLeaseMutationLockAsync(root).AsTask());
        Assert.Equal("owner_token=outside\n", File.ReadAllText(outsideFile));
        Assert.NotNull(new FileInfo(lockPath).LinkTarget);
    }

    [Fact]
    public async Task ShouldRefuseSimulatedCloudOpenWhenTheLeaderRecordIsLinkedOutsideStore()
    {
        using var directory = new TemporaryDirectory();
        using var outside = new TemporaryDirectory();
        var root = Internal.IO.StoragePathGuard.CanonicalizeRoot(directory.Path);
        var options = PantsOpenOptions.SimulatedCloud(root, "pants-tests", "path-guard/")
            .WithBackgroundCompaction(false);
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
        }

        var leaderPath = Path.Combine(root, ".midge_leader");
        var outsideFile = Path.Combine(outside.Path, "leader");
        File.Copy(leaderPath, outsideFile);
        File.Delete(leaderPath);
        if (!TryCreateFileLink(leaderPath, outsideFile))
        {
            return;
        }

        await Assert.ThrowsAsync<PantsInvalidPathException>(async () =>
            await PantsDatabase.OpenAsync(options));
        Assert.True(File.Exists(outsideFile));
        Assert.NotNull(new FileInfo(leaderPath).LinkTarget);
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

    [Fact]
    public async Task ShouldRefuseRuntimeStagedWriteThroughLinkedAncestorBelowStoreRoot()
    {
        using var directory = new TemporaryDirectory();
        using var outside = new TemporaryDirectory();
        var root = Internal.IO.StoragePathGuard.CanonicalizeRoot(directory.Path);
        await using var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(root).WithBackgroundCompaction(false));

        var sstDirectory = Path.Combine(root, "sst");
        Directory.Delete(sstDirectory, recursive: true);
        var outsideNestedDirectory = Path.Combine(outside.Path, "nested");
        Directory.CreateDirectory(outsideNestedDirectory);
        if (!TryCreateDirectoryLink(sstDirectory, outside.Path))
        {
            return;
        }

        Assert.Throws<PantsInvalidPathException>(() => Internal.IO.AtomicStagedFile.Write(
            Path.Combine(sstDirectory, "nested", "file"),
            "x"u8));
        Assert.Empty(Directory.EnumerateFileSystemEntries(outsideNestedDirectory));
    }

    [Fact]
    public async Task ShouldRefuseRuntimeReadThroughLinkedAncestorBelowStoreRoot()
    {
        using var directory = new TemporaryDirectory();
        using var outside = new TemporaryDirectory();
        var root = Internal.IO.StoragePathGuard.CanonicalizeRoot(directory.Path);
        await using var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(root).WithBackgroundCompaction(false));

        var sstDirectory = Path.Combine(root, "sst");
        Directory.Delete(sstDirectory, recursive: true);
        var outsideNestedDirectory = Path.Combine(outside.Path, "nested");
        Directory.CreateDirectory(outsideNestedDirectory);
        var target = Path.Combine(outsideNestedDirectory, "file");
        File.WriteAllBytes(target, "outside"u8.ToArray());
        if (!TryCreateDirectoryLink(sstDirectory, outside.Path))
        {
            return;
        }

        var linkedPath = Path.Combine(sstDirectory, "nested", "file");
        Assert.Throws<PantsInvalidPathException>(() => Internal.Sst.SstReader.Open(linkedPath));
        Assert.Throws<PantsInvalidPathException>(() => Internal.Sst.LocalAsyncSstSource.Open(linkedPath));
        Assert.Equal("outside", File.ReadAllText(target));
    }

    [Fact]
    public async Task ShouldKeepAnOpenStoreRootBoundToItsResolvedTargetWhenAnAncestorLinkChanges()
    {
        using var directory = new TemporaryDirectory();
        using var firstTarget = new TemporaryDirectory();
        using var secondTarget = new TemporaryDirectory();
        var parentLink = Path.Combine(directory.Path, "linked-parent");
        if (!TryCreateDirectoryLink(parentLink, firstTarget.Path))
        {
            return;
        }

        var linkedRoot = Path.Combine(parentLink, "database");
        var resolvedRoot = Path.Combine(firstTarget.Path, "database");
        await using (var database = await PantsDatabase.OpenAsync(
                         PantsOpenOptions.Local(linkedRoot).WithBackgroundCompaction(false)))
        {
            Directory.Delete(parentLink);
            Assert.True(TryCreateDirectoryLink(parentLink, secondTarget.Path));

            await using var transaction = await database.Transactions.BeginAsync(
                database.ColumnFamilies.DefaultFamily,
                PantsTransactionMode.ReadWrite);
            transaction.Put("root-target"u8.ToArray(), "first"u8.ToArray());
            await transaction.CommitAsync(PantsWriteOptions.Sync);
        }

        await using var reopened = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(resolvedRoot).WithBackgroundCompaction(false));
        await using var reader = await reopened.Transactions.BeginAsync(
            reopened.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Equal(
            "first",
            TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(
                await reader.GetAsync("root-target"u8.ToArray()))));
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

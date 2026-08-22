namespace Pants.Tests;

public sealed class PantsStorageIoTests
{
    [Fact]
    public void ShouldFlushWindowsDirectoryWithWritableBackupHandle()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TemporaryDirectory();

        AtomicStagedFile.FlushDirectory(directory.Path);
    }

    [Fact]
    public void ShouldLeavePreviousFileIntactWhenStagedWriteFailsBeforePublish()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "manifest.json");
        AtomicStagedFile.Write(path, "old"u8);

        Assert.Throws<IOException>(() => AtomicStagedFile.Write(
            path,
            "new"u8,
            beforePublish: static () => throw new IOException("Injected pre-publish failure.")));

        Assert.Equal("old"u8.ToArray(), File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public void ShouldAppendVectoredBuffersWithoutSharingAFileCursor()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "journal");

        PositionalFile.AppendAndFlush(path, ["one"u8.ToArray(), "two"u8.ToArray()]);
        PositionalFile.AppendAndFlush(path, ["three"u8.ToArray()]);

        Assert.Equal("onetwothree"u8.ToArray(), PositionalFile.ReadAllBytes(path));
    }
}

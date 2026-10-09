using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage;

public sealed class ReplacingFileMoveTests
{
    [Fact]
    public void ShouldFallBackToPosixReplacementGivenWindowsDeniesReplacingAnOpenTarget()
    {
        var replaced = new List<(string Source, string Destination)>();

        ReplacingFileMove.Move(
            "source.tmp",
            "target",
            true,
            true,
            static (_, _, _) => throw new UnauthorizedAccessException("Injected access denied."),
            (source, destination) =>
            {
                replaced.Add((source, destination));
                return true;
            });

        Assert.Equal([("source.tmp", "target")], replaced);
    }

    [Fact]
    public void ShouldSurfaceOriginalDenialGivenPosixReplacementIsUnavailable()
    {
        var denial = new UnauthorizedAccessException("Injected access denied.");

        var thrown = Assert.Throws<UnauthorizedAccessException>(() => ReplacingFileMove.Move(
            "source.tmp",
            "target",
            true,
            true,
            (_, _, _) => throw denial,
            static (_, _) => false));

        Assert.Same(denial, thrown);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void ShouldNotUsePosixReplacementOffWindowsOrWithoutOverwrite(bool isWindows, bool overwrite)
    {
        var fallbacks = 0;

        Assert.Throws<UnauthorizedAccessException>(() => ReplacingFileMove.Move(
            "source.tmp",
            "target",
            overwrite,
            isWindows,
            static (_, _, _) => throw new UnauthorizedAccessException("Injected access denied."),
            (_, _) =>
            {
                fallbacks++;
                return true;
            }));

        Assert.Equal(0, fallbacks);
    }

    [Fact]
    public void ShouldNotUsePosixReplacementForFailuresOtherThanAccessDenied()
    {
        var fallbacks = 0;

        Assert.Throws<IOException>(() => ReplacingFileMove.Move(
            "source.tmp",
            "target",
            true,
            true,
            static (_, _, _) => throw new IOException("Injected disk failure."),
            (_, _) =>
            {
                fallbacks++;
                return true;
            }));

        Assert.Equal(0, fallbacks);
    }

    [Fact]
    public void ShouldPreserveOpenReaderWhenAtomicallyReplacingTarget()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "leader-記録");
        File.WriteAllBytes(path, "old leader"u8.ToArray());
        using var reader = SharedReadFile.Open(path)!;

        foreach (var replacement in new[] { "new leader with longer contents"u8.ToArray(), "latest"u8.ToArray() })
        {
            AtomicStagedFile.Write(path, replacement);

            var buffer = new byte[reader.Length];
            reader.Position = 0;
            reader.ReadExactly(buffer);
            Assert.Equal("old leader"u8.ToArray(), buffer);
            Assert.Equal(replacement, File.ReadAllBytes(path));
            Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
        }
    }

    [Fact]
    public void ShouldReadMissingFileAsAbsent()
    {
        using var directory = new TemporaryDirectory();

        Assert.Null(SharedReadFile.TryReadAllBytes(Path.Combine(directory.Path, "missing")));
    }
}

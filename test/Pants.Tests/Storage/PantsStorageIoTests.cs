using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage;

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
        var path = Path.Combine(directory.Path, "manifest.json");
        AtomicStagedFile.Write(path, "old"u8);

        Assert.Throws<IOException>(() => AtomicStagedFile.Write(
            path,
            "new"u8,
            beforePublish: static () => throw new IOException("Injected pre-publish failure.")));

        Assert.Equal("old"u8.ToArray(), File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public async Task ShouldReadOldManifestBeforeConcurrentReplacement()
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "manifest.snapshot.json");
        AtomicStagedFile.Write(path, "old-generation"u8);
        using var publishing = new ManualResetEventSlim();
        Task? replacement = null;

        // Act
        var captured = PositionalFile.ReadAllBytes(path, (handle, buffer, offset) =>
        {
            replacement = Task.Run(() => AtomicStagedFile.Write(
                path,
                "new-generation"u8,
                beforePublish: publishing.Set));
            Assert.True(publishing.Wait(TestTimeouts.Expected));
            Assert.False(replacement.IsCompleted);

            return RandomAccess.Read(handle, buffer, offset);
        });
        await replacement!;

        // Assert
        Assert.Equal("old-generation"u8.ToArray(), captured);
        Assert.Equal("new-generation"u8.ToArray(), File.ReadAllBytes(path));
    }

    [Fact]
    public void ShouldAppendVectoredBuffersWithoutSharingAFileCursor()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "journal");

        PositionalFile.AppendAndFlush(path, ["one"u8.ToArray(), "two"u8.ToArray()]);
        PositionalFile.AppendAndFlush(path, ["three"u8.ToArray()]);

        Assert.Equal("onetwothree"u8.ToArray(), PositionalFile.ReadAllBytes(path));
    }

    [Fact]
    public void ShouldFlushParentDirectoryAfterCreatingAppendTarget()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "journal");
        string? flushedDirectory = null;

        PositionalFile.AppendAndFlush(
            path,
            ["record"u8.ToArray()],
            flushDirectory: value => flushedDirectory = value);

        Assert.Equal(Path.GetFullPath(directory.Path), flushedDirectory);
        Assert.Equal("record"u8.ToArray(), File.ReadAllBytes(path));
    }

    [Fact]
    public void ShouldNotFlushParentDirectoryWhenAppendingExistingTarget()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "journal");
        File.WriteAllBytes(path, "one"u8);
        var flushCount = 0;

        PositionalFile.AppendAndFlush(
            path,
            ["two"u8.ToArray()],
            flushDirectory: _ => flushCount++);

        Assert.Equal(0, flushCount);
        Assert.Equal("onetwo"u8.ToArray(), File.ReadAllBytes(path));
    }

    [Fact]
    public void ShouldExposePublishedContentWhenDirectoryFlushFails()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "manifest.json");
        AtomicStagedFile.Write(path, "old"u8);

        Assert.Throws<IOException>(() => AtomicStagedFile.Write(
            path,
            "new"u8,
            afterPublish: static () => throw new IOException("Injected directory-flush failure.")));

        Assert.Equal("new"u8.ToArray(), File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public void ShouldReportTemporaryCleanupFailureWithoutReplacingPrimaryFailure()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "manifest.json");
        Exception? observed = null;

        var thrown = Assert.Throws<InvalidOperationException>(() => AtomicStagedFile.Write(
            path,
            "new"u8,
            beforePublish: static () => throw new InvalidOperationException("Primary failure."),
            deleteTemporary: static _ => throw new IOException("Injected cleanup failure."),
            cleanupFailure: exception => observed = exception));

        Assert.Equal("Primary failure.", thrown.Message);
        Assert.IsType<IOException>(observed);
    }

    [Fact]
    public void ShouldPreserveExistingTargetWhenCreateOnlyPublishCollides()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "manifest.json");
        File.WriteAllBytes(path, "old"u8);

        Assert.Throws<IOException>(() => AtomicStagedFile.Write(path, "new"u8, false));

        Assert.Equal("old"u8.ToArray(), File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public async Task ShouldPublishOnlyCompletePayloadsGivenConcurrentWriters()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "manifest.json");
        var payloads = Enumerable.Range(1, 8)
            .Select(index => Enumerable.Repeat(checked((byte)index), 4_096 + index).ToArray())
            .ToArray();
        AtomicStagedFile.Write(path, payloads[0]);
        using var stop = new CancellationTokenSource();
        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                var observed = PositionalFile.ReadAllBytes(path);
                Assert.Contains(payloads, payload => payload.AsSpan().SequenceEqual(observed));
            }
        });

        try
        {
            await Task.WhenAll(payloads.Select(payload => Task.Run(() => AtomicStagedFile.Write(path, payload))));
        }
        finally
        {
            stop.Cancel();
            await reader;
        }

        var final = File.ReadAllBytes(path);
        Assert.Contains(payloads, payload => payload.AsSpan().SequenceEqual(final));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public void ShouldRetryDeterministicallyShortPositionalReads()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "data");
        File.WriteAllBytes(path, "complete-payload"u8);
        var reads = 0;

        var bytes = PositionalFile.ReadAllBytes(
            path,
            (handle, destination, offset) =>
            {
                reads++;
                return RandomAccess.Read(handle, destination[..Math.Min(3, destination.Length)], offset);
            });

        Assert.True(reads > 1);
        Assert.Equal("complete-payload"u8.ToArray(), bytes);
    }

    [Fact]
    public void ShouldThrowWhenPositionalReadEndsBeforeRequestedLength()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "data");
        File.WriteAllBytes(path, "short"u8);
        using var handle =
            File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        Assert.Throws<EndOfStreamException>(() => PositionalFile.ReadExactly(handle, 0, 10));
    }

    [Fact]
    public void ShouldRetryOperationGivenTransientWindowsSharingFailures()
    {
        var attempts = 0;
        var delays = new List<TimeSpan>();

        TransientSharingRetry.Run(
            () =>
            {
                attempts++;
                switch (attempts)
                {
                    case 1:
                        throw new UnauthorizedAccessException("Injected access denied.");
                    case 2:
                        throw new IOException("Injected sharing violation.", unchecked((int)0x80070020));
                    case 3:
                        throw new IOException("Injected lock violation.", unchecked((int)0x80070021));
                }
            },
            true,
            delays.Add);

        Assert.Equal(4, attempts);
        Assert.Equal(3, delays.Count);
        Assert.All(delays, delay => Assert.True(delay > TimeSpan.Zero));
    }

    [Fact]
    public void ShouldNotRetryGivenNonTransientFailureOrNonWindowsPlatform()
    {
        var existsAttempts = 0;
        Assert.Throws<IOException>(() => TransientSharingRetry.Run(
            () =>
            {
                existsAttempts++;
                throw new IOException("Injected file exists.", unchecked((int)0x80070050));
            },
            true,
            static _ => { }));

        var deniedAttempts = 0;
        Assert.Throws<UnauthorizedAccessException>(() => TransientSharingRetry.Run(
            () =>
            {
                deniedAttempts++;
                throw new UnauthorizedAccessException("Injected access denied.");
            },
            false,
            static _ => { }));

        Assert.Equal(1, existsAttempts);
        Assert.Equal(1, deniedAttempts);
    }

    [Fact]
    public void ShouldSurfaceSharingFailureGivenItPersistsPastTheRetryBound()
    {
        var attempts = 0;

        Assert.Throws<UnauthorizedAccessException>(() => TransientSharingRetry.Run(
            () =>
            {
                attempts++;
                throw new UnauthorizedAccessException("Injected access denied.");
            },
            true,
            static _ => { }));

        Assert.Equal(TransientSharingRetry.MaximumAttempts, attempts);
    }

    [Fact]
    public async Task ShouldPublishGivenTargetIsBrieflyHeldOpenWithoutDeleteSharing()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "manifest.json");
        AtomicStagedFile.Write(path, "old"u8);
        using var opened = new ManualResetEventSlim();
        var holder = Task.Run(() =>
        {
            // Stands in for an antivirus or indexing scan of the freshly published file.
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            opened.Set();
            Thread.Sleep(TimeSpan.FromMilliseconds(150));
        });
        opened.Wait(TimeSpan.FromSeconds(5));

        AtomicStagedFile.Write(path, "new"u8);

        await holder;
        Assert.Equal("new"u8.ToArray(), File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }
}

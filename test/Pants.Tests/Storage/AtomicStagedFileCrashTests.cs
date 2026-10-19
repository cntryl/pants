using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage;

[Collection(CrashProcessTestGroup.Name)]
public sealed class AtomicStagedFileCrashTests
{
    const string TargetEnvironmentVariable = "PANTS_STAGED_WRITE_CRASH_TARGET";
    const string BoundaryEnvironmentVariable = "PANTS_STAGED_WRITE_CRASH_BOUNDARY";

    [Fact]
    public async Task ShouldRetainPreviousDurableFileWhenChildCrashesBeforePublish()
    {
        using var directory = new TemporaryDirectory();
        var targetPath = Path.Combine(directory.Path, "manifest.json");
        var boundaryPath = Path.Combine(directory.Path, "before-publish.reached");
        AtomicStagedFile.Write(targetPath, "previous-durable-value"u8);

        var start = CrashChildProcess.CreateStartInfo(
            typeof(AtomicStagedFileCrashTests),
            nameof(ShouldCrashAfterStagedPayloadSyncBeforePublishInChild));
        start.Environment[TargetEnvironmentVariable] = targetPath;
        start.Environment[BoundaryEnvironmentVariable] = boundaryPath;

        using var child = await CrashChildProcess.RunToExitAsync(
            start,
            "atomic staged-write crash child",
            TestTimeouts.Expected);

        Assert.NotEqual(0, child.ExitCode);
        Assert.Equal("reached", File.ReadAllText(boundaryPath));
        Assert.Equal("previous-durable-value"u8.ToArray(), File.ReadAllBytes(targetPath));
        Assert.Contains(
            Directory.EnumerateFiles(directory.Path),
            path => path.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public void ShouldCrashAfterStagedPayloadSyncBeforePublishInChild()
    {
        var targetPath = Environment.GetEnvironmentVariable(TargetEnvironmentVariable);
        if (targetPath is null)
        {
            return;
        }

        var boundaryPath = Assert.IsType<string>(
            Environment.GetEnvironmentVariable(BoundaryEnvironmentVariable));
        AtomicStagedFile.Write(
            targetPath,
            "replacement-value"u8,
            beforePublish: () =>
            {
                AtomicStagedFile.Write(boundaryPath, "reached"u8, overwrite: false);
                Console.WriteLine("Terminating after staged payload sync and before publish.");
                Console.Out.Flush();
                Environment.FailFast("Injected process crash before staged-file publish.");
            });

        Assert.Fail("The staged-write child returned without crashing.");
    }
}

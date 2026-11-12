using System.Diagnostics;
using System.IO.Compression;

namespace Cntryl.Pants.Checkpoint;

static class CheckpointSourceBuild
{
    public const string BenchmarkProject = "bench/Pants.Benches/Cntryl.Pants.Benches.csproj";

    public static async Task<Archive> CaptureAsync(string sourceRoot, string commit, string archivePath)
    {
        if (commit.Length != 40 || !commit.All(char.IsAsciiHexDigit))
        {
            throw new InvalidOperationException("An immutable source commit is required.");
        }
        var tree = await GitAsync(sourceRoot, "rev-parse", commit + "^{tree}").ConfigureAwait(false);
        _ = await GitAsync(sourceRoot, "archive", "--format=zip", "--output", archivePath, commit).ConfigureAwait(false);
        return new Archive(commit, tree, Path.GetFileName(archivePath), CheckpointProvenance.HashFile(archivePath));
    }

    public static async Task<Build> BuildAsync(string campaignRoot, Archive archive, string project = BenchmarkProject)
    {
        var source = Path.Combine(campaignRoot, "source");
        var artifacts = Path.Combine(campaignRoot, "build");
        if (Directory.Exists(source) || Directory.Exists(artifacts))
        {
            throw new InvalidOperationException("An archived-source build cannot reuse extracted source or compiled outputs.");
        }
        var archivePath = CheckpointArchive.Resolve(campaignRoot, archive.Path);
        if (CheckpointProvenance.HashFile(archivePath) != archive.Sha256)
        {
            throw new InvalidOperationException("Captured source archive changed before compilation.");
        }
        ZipFile.ExtractToDirectory(archivePath, source);
        var projectPath = CheckpointArchive.Resolve(source, project);
        var sdk = await DotnetInDirectoryAsync(source, TimeSpan.FromSeconds(30), "--version").ConfigureAwait(false);
        var restore = await DotnetInDirectoryAsync(source, TimeSpan.FromMinutes(2), "restore", projectPath, "--locked-mode",
            "--artifacts-path", artifacts, "-p:SourceRevisionId=" + archive.Commit).ConfigureAwait(false);
        CheckpointEvidence.ProcessOutcome? compile = null;
        string? executable = null;
        if (restore.Success)
        {
            compile = await DotnetInDirectoryAsync(source, TimeSpan.FromMinutes(5), "build", projectPath, "--configuration", "Release",
                "--no-restore", "--artifacts-path", artifacts, "-p:SourceRevisionId=" + archive.Commit, "-m:1").ConfigureAwait(false);
            if (compile.Success)
            {
                var name = Path.GetFileNameWithoutExtension(project) + ".dll";
                executable = Path.GetRelativePath(campaignRoot,
                    Directory.EnumerateFiles(Path.Combine(artifacts, "bin"), name, SearchOption.AllDirectories).Single())
                    .Replace(Path.DirectorySeparatorChar, '/');
            }
        }
        return new Build(archive.Commit, archive.Sha256, project, executable, restore, compile)
        {
            SdkVersion = sdk
        };
    }

    public static async Task<string> GitAsync(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        var result = await CheckpointProcess.RunAsync(start, "git", TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new InvalidOperationException($"Git provenance capture failed: {result.StandardError} {result.StartError} {result.TerminationReason}");
        }
        return result.StandardOutput.Trim();
    }

    public static Task<CheckpointEvidence.ProcessOutcome> DotnetAsync(TimeSpan allowance, params string[] arguments) =>
        DotnetInDirectoryAsync(Environment.CurrentDirectory, allowance, arguments);

    static Task<CheckpointEvidence.ProcessOutcome> DotnetInDirectoryAsync(string workingDirectory, TimeSpan allowance,
        params string[] arguments)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = workingDirectory
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        return CheckpointProcess.RunAsync(start, arguments[0], allowance);
    }

    public sealed record Archive(string Commit, string Tree, string Path, string Sha256);

    public sealed record Build(string Commit, string SourceArchiveSha256, string Project, string? Executable,
        CheckpointEvidence.ProcessOutcome Restore, CheckpointEvidence.ProcessOutcome? Compilation)
    {
        public CheckpointEvidence.ProcessOutcome? SdkVersion { get; init; }
        public bool Success => SdkVersion?.Success == true && Restore.Success && Compilation?.Success == true && Executable is not null;
    }
}

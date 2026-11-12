using Cntryl.Pants.Checkpoint;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Performance.Checkpoint;

public sealed class CheckpointArchivedBuildTests
{
    [Fact]
    public async Task ShouldExecuteCapturedSourceAfterDirtyBuildRevertAndSubsequentHeadChange()
    {
        // An isolated compiler fixture tests executed behavior, not repository source conventions.
        using var directory = new TemporaryDirectory();
        var source = Path.Combine(directory.Path, "producer");
        var dirty = Path.Combine(directory.Path, "dirty-output");
        var fresh = Path.Combine(directory.Path, "captured-output");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(fresh);
        var project = Path.Combine(source, "Probe.csproj");
        var program = Path.Combine(source, "Program.cs");
        await File.WriteAllTextAsync(project, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net11.0</TargetFramework>
                <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
              </PropertyGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(program, ProgramText("A"));
        var initial = await CheckpointSourceBuild.DotnetAsync(TimeSpan.FromMinutes(1), "restore", project,
            "--use-lock-file", "--artifacts-path", dirty);
        Assert.True(initial.Success, initial.StandardError);
        await CheckpointSourceBuild.GitAsync(source, "init");
        await CheckpointSourceBuild.GitAsync(source, "add", ".");
        await CommitAsync(source, "record source A");
        var selected = await CheckpointSourceBuild.GitAsync(source, "rev-parse", "HEAD");

        // A routine dirty build keeps A's source version stamp even though it executes B.
        await File.WriteAllTextAsync(program, ProgramText("B"));
        var dirtyBuild = await CheckpointSourceBuild.DotnetAsync(TimeSpan.FromMinutes(1), "build", project,
            "--configuration", "Release", "--no-restore", "--artifacts-path", dirty, "-p:SourceRevisionId=" + selected);
        Assert.True(dirtyBuild.Success, dirtyBuild.StandardError);
        await File.WriteAllTextAsync(program, ProgramText("A"));
        Assert.Empty(await CheckpointSourceBuild.GitAsync(source, "status", "--porcelain"));
        var dirtyExecutable = Directory.EnumerateFiles(Path.Combine(dirty, "bin"), "Probe.dll", SearchOption.AllDirectories).Single();
        var actualDirty = await CheckpointSourceBuild.DotnetAsync(TimeSpan.FromSeconds(10), dirtyExecutable);
        Assert.True(actualDirty.Success);
        Assert.StartsWith("B:", actualDirty.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(selected, actualDirty.StandardOutput, StringComparison.Ordinal);

        // HEAD changes again after selection. Both tree/archive and compilation must stay on A.
        await File.WriteAllTextAsync(program, ProgramText("C"));
        await CheckpointSourceBuild.GitAsync(source, "add", ".");
        await CommitAsync(source, "advance source to C");
        var archive = await CheckpointSourceBuild.CaptureAsync(source, selected, Path.Combine(fresh, "source.zip"));
        var archivedBuild = await CheckpointSourceBuild.BuildAsync(fresh, archive, "Probe.csproj");
        Assert.True(archivedBuild.Success, archivedBuild.Compilation?.StandardError ?? archivedBuild.Restore.StandardError);

        var actual = await CheckpointSourceBuild.DotnetAsync(TimeSpan.FromSeconds(10),
            CheckpointArchive.Resolve(fresh, archivedBuild.Executable!));

        Assert.True(actual.Success);
        Assert.StartsWith("A:", actual.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(selected, actual.StandardOutput, StringComparison.Ordinal);
        Assert.NotEqual(actualDirty.StandardOutput, actual.StandardOutput);
    }

    static Task<string> CommitAsync(string source, string message) =>
        CheckpointSourceBuild.GitAsync(source, "-c", "commit.gpgsign=false", "-c", "core.hooksPath=" + Path.Combine(source, ".no-hooks"),
            "-c", "user.name=Pants checkpoint fixture", "-c",
            "user.email=checkpoint-fixture@example.invalid", "commit", "-m", message);

    static string ProgramText(string value) => $$"""
        using System;
        using System.Reflection;
        Console.WriteLine("{{value}}:" + typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
        """;
}

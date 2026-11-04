using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Cntryl.Pants.Checkpoint;

static class CheckpointProvenance
{
    public static async Task<CheckpointEvidence.Provenance> CaptureAsync(string sourceRoot, string artifactRoot)
    {
        sourceRoot = Path.GetFullPath(sourceRoot);
        var campaign = Guid.NewGuid().ToString("D");
        var commit = await CheckpointSourceBuild.GitAsync(sourceRoot, "rev-parse", "HEAD").ConfigureAwait(false);
        var clean = string.IsNullOrWhiteSpace(await CheckpointSourceBuild.GitAsync(sourceRoot,
            "status", "--porcelain", "--untracked-files=all").ConfigureAwait(false));
        var archive = await CheckpointSourceBuild.CaptureAsync(sourceRoot, commit, Path.Combine(artifactRoot, "source.zip")).ConfigureAwait(false);
        var reasons = ImmutableArray.CreateBuilder<string>();
        if (!clean)
        {
            reasons.Add("Source worktree is dirty; commit selection must be explicit and clean before workload launch.");
        }
        var lockHash = "";
        using (var source = System.IO.Compression.ZipFile.OpenRead(Path.Combine(artifactRoot, archive.Path)))
        {
            using var input = (source.GetEntry("bench/Pants.Benches/packages.lock.json") ??
                throw new InvalidOperationException("The captured source archive lacks its package lock.")).Open();
            lockHash = Convert.ToHexString(SHA256.HashData(input));
        }
        CheckpointSourceBuild.Build? build = null;
        CheckpointEvidence.ProcessOutcome? identityOutcome = null;
        var files = ImmutableArray<CheckpointEvidence.BuildFile>.Empty;
        var runtime = ImmutableDictionary<string, string>.Empty;
        if (clean)
        {
            build = await CheckpointSourceBuild.BuildAsync(artifactRoot, archive).ConfigureAwait(false);
            if (!build.Success)
            {
                reasons.Add("Actual locked restore or archived-source compilation failed.");
            }
            else
            {
                var executable = CheckpointArchive.Resolve(artifactRoot, build.Executable!);
                var identityPath = Path.Combine(artifactRoot, "built-module-identity.json");
                identityOutcome = await CheckpointSourceBuild.DotnetAsync(TimeSpan.FromSeconds(30), executable,
                    "checkpoint-build-identity", identityPath).ConfigureAwait(false);
                if (!identityOutcome.Success || !File.Exists(identityPath))
                {
                    reasons.Add("The fresh archived-source executable did not supply its actual loaded build identity.");
                }
                else
                {
                    files = await CheckpointEvidence.ReadAsync<ImmutableArray<CheckpointEvidence.BuildFile>>(identityPath).ConfigureAwait(false);
                    if (files.Length != 3 || files.Any(file => file.Configuration != "Release" || file.OptimizerDisabled ||
                        !file.InformationalVersion.Contains(commit, StringComparison.OrdinalIgnoreCase)))
                    {
                        reasons.Add("The fresh archived-source build has a different revision or optimization profile.");
                    }
                    var runtimeRoot = Path.GetDirectoryName(executable)!;
                    runtime = Directory.EnumerateFiles(runtimeRoot, "*", SearchOption.AllDirectories).ToImmutableDictionary(
                        path => Path.GetRelativePath(runtimeRoot, path).Replace(Path.DirectorySeparatorChar, '/'),
                        CheckpointProvenance.HashFile, StringComparer.Ordinal);
                }
            }
        }
        return new CheckpointEvidence.Provenance(campaign, sourceRoot, commit, archive.Tree, clean,
            archive.Path, archive.Sha256, lockHash, files,
            RuntimeInformation.FrameworkDescription, RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(), Environment.CommandLine, reasons.ToImmutable())
        {
            ArchivedSourceBuild = build,
            BuildIdentityOutcome = identityOutcome,
            RuntimeFilesSha256 = runtime
        };
    }

    public static ImmutableArray<CheckpointEvidence.BuildFile> ObserveLoadedBuild()
    {
        var result = ImmutableArray.CreateBuilder<CheckpointEvidence.BuildFile>(3);
        foreach (var assembly in new[] { typeof(CheckpointProvenance).Assembly, typeof(PantsDatabase).Assembly, typeof(IPantsDatabase).Assembly }.Distinct())
        {
            using var input = new FileStream(assembly.Location, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var hash = Convert.ToHexString(SHA256.HashData(input));
            input.Position = 0;
            using var pe = new PEReader(input);
            var metadata = pe.GetMetadataReader();
            var fileModule = metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
            var loadedModule = assembly.ManifestModule.ModuleVersionId;
            if (loadedModule != fileModule)
            {
                throw new InvalidOperationException("The loaded benchmark/dependency changed at its binary path.");
            }
            result.Add(new CheckpointEvidence.BuildFile(Path.GetFileName(assembly.Location), hash,
                assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "",
                assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "",
                assembly.GetCustomAttribute<DebuggableAttribute>()?.IsJITOptimizerDisabled ?? true, loadedModule));
        }
        return result.ToImmutable();
    }

    public static string HashFile(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input));
    }

}

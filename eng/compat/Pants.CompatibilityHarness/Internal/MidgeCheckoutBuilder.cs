using System.Security.Cryptography;

namespace Cntryl.Pants.CompatibilityHarness.Internal;

internal static class MidgeCheckoutBuilder
{
    internal const string RequiredCommit = "75dcc39f7a9b87df480ed91c3a5c93fe1389ca71";
    const string RequiredDependencyLockSha256 =
        "1fe29024e1789245b1ca8b20274aea17573380d5e33cf8f1811b59a65f85f937";

    public static async Task<MidgeDriverBuild> BuildAsync(
        string checkoutPath,
        string temporaryRoot,
        MidgeDriverBuildMode mode,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkoutPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryRoot);

        var fullCheckoutPath = Path.GetFullPath(checkoutPath, Environment.CurrentDirectory);
        if (!Directory.Exists(fullCheckoutPath))
        {
            throw new DirectoryNotFoundException(
                $"Midge checkout '{fullCheckoutPath}' does not exist.");
        }

        var status = await ProcessRunner.RunAsync(
            "git",
            ["-C", fullCheckoutPath, "status", "--porcelain=v1", "--untracked-files=all"],
            Environment.CurrentDirectory,
            cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(status.StandardOutput))
        {
            throw new InvalidOperationException(
                status.FormatEvidence("Midge checkout is not clean."));
        }

        var revision = await ProcessRunner.RunAsync(
            "git",
            ["-C", fullCheckoutPath, "rev-parse", "--verify", "HEAD"],
            Environment.CurrentDirectory,
            cancellationToken).ConfigureAwait(false);
        var actualCommit = revision.StandardOutput.Trim();
        if (!StringComparer.Ordinal.Equals(actualCommit, RequiredCommit))
        {
            throw new InvalidOperationException(
                revision.FormatEvidence(
                    $"Midge HEAD '{actualCommit}' does not match required commit '{RequiredCommit}'."));
        }

        var driverSourcePath = FindDriverSourcePath();
        var sourceLockPath = Path.Combine(
            Path.GetDirectoryName(driverSourcePath)!,
            "Cargo.lock");
        if (!File.Exists(sourceLockPath))
        {
            throw new FileNotFoundException(
                $"The pinned Midge resolver lock is missing at '{sourceLockPath}'.",
                sourceLockPath);
        }

        var lockHash = Convert.ToHexStringLower(
            SHA256.HashData(File.ReadAllBytes(sourceLockPath)));
        if (!StringComparer.Ordinal.Equals(lockHash, RequiredDependencyLockSha256))
        {
            throw new InvalidDataException(
                $"The Midge driver Cargo lock hash '{lockHash}' does not match the pinned hash "
                + $"'{RequiredDependencyLockSha256}'.");
        }

        var clonedCheckoutPath = Path.Combine(temporaryRoot, "midge");
        _ = await ProcessRunner.RunAsync(
            "git",
            ["clone", "--no-hardlinks", "--quiet", fullCheckoutPath, clonedCheckoutPath],
            temporaryRoot,
            cancellationToken).ConfigureAwait(false);

        var clonedRevision = await ProcessRunner.RunAsync(
            "git",
            ["-C", clonedCheckoutPath, "rev-parse", "--verify", "HEAD"],
            temporaryRoot,
            cancellationToken).ConfigureAwait(false);
        var clonedCommit = clonedRevision.StandardOutput.Trim();
        if (!StringComparer.Ordinal.Equals(clonedCommit, RequiredCommit))
        {
            throw new InvalidOperationException(
                clonedRevision.FormatEvidence(
                    $"Cloned Midge HEAD '{clonedCommit}' does not match required commit "
                    + $"'{RequiredCommit}'."));
        }

        File.Copy(sourceLockPath, Path.Combine(clonedCheckoutPath, "Cargo.lock"), overwrite: true);
        var clonedBinarySourceDirectory = Path.Combine(clonedCheckoutPath, "src", "bin");
        _ = Directory.CreateDirectory(clonedBinarySourceDirectory);
        File.Copy(
            driverSourcePath,
            Path.Combine(clonedBinarySourceDirectory, "pants_compat.rs"),
            overwrite: true);

        if (mode == MidgeDriverBuildMode.FixtureRefresh)
        {
            _ = await ProcessRunner.RunAsync(
                "cargo",
                ["fmt", "--all", "--", "--check"],
                clonedCheckoutPath,
                cancellationToken).ConfigureAwait(false);
            await RunClippyAsync(
                clonedCheckoutPath,
                enableFailpoints: false,
                cancellationToken).ConfigureAwait(false);
            await RunClippyAsync(
                clonedCheckoutPath,
                enableFailpoints: true,
                cancellationToken).ConfigureAwait(false);
        }

        var buildArguments = new List<string>
        {
            "build",
            "--release",
            "--locked",
            "--no-default-features"
        };
        if (mode == MidgeDriverBuildMode.FixtureRefresh)
        {
            buildArguments.Add("--features");
            buildArguments.Add("failpoints");
        }

        buildArguments.Add("--bin");
        buildArguments.Add("pants_compat");
        var build = await ProcessRunner.RunAsync(
            "cargo",
            buildArguments,
            clonedCheckoutPath,
            cancellationToken).ConfigureAwait(false);
        var executableName = OperatingSystem.IsWindows() ? "pants_compat.exe" : "pants_compat";
        var executablePath = Path.Combine(
            clonedCheckoutPath,
            "target",
            "release",
            executableName);
        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException(
                build.FormatEvidence(
                    $"Cargo succeeded but did not produce '{executablePath}'."),
                executablePath);
        }

        return new MidgeDriverBuild(clonedCheckoutPath, executablePath, build.Elapsed);
    }

    static async Task RunClippyAsync(
        string checkoutPath,
        bool enableFailpoints,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            "clippy",
            "--release",
            "--locked",
            "--no-default-features"
        };
        if (enableFailpoints)
        {
            arguments.Add("--features");
            arguments.Add("failpoints");
        }

        arguments.Add("--bin");
        arguments.Add("pants_compat");
        arguments.Add("--");
        arguments.Add("-D");
        arguments.Add("warnings");
        _ = await ProcessRunner.RunAsync(
            "cargo",
            arguments,
            checkoutPath,
            cancellationToken).ConfigureAwait(false);
    }

    static string FindDriverSourcePath()
    {
        foreach (var startingPath in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(Path.GetFullPath(startingPath));
                 directory is not null;
                 directory = directory.Parent)
            {
                var candidate = Path.Combine(
                    directory.FullName,
                    "eng",
                    "compat",
                    "MidgeDriver",
                    "pants_compat.rs");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        throw new FileNotFoundException(
            "Could not locate eng/compat/MidgeDriver/pants_compat.rs from the current directory "
            + "or compatibility harness assembly path.");
    }
}

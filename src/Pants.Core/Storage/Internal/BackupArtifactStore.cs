using System.Buffers;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cntryl.Pants.Storage.Internal;

static class BackupArtifactStore
{
    const uint BackupVersion = 1;
    const string ManifestFileName = "backup.json";
    const string ObjectsDirectoryName = "objects";
    const long MaximumManifestBytes = 16 * 1024 * 1024;
    const int BufferSize = 64 * 1024;
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    public static CaptureTarget PrepareCapture(string sourceRoot, string destination)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        var canonicalSource = StoragePathGuard.CanonicalizeRoot(sourceRoot);
        var target = PrepareTarget(canonicalSource, destination);
        var stage = CreateStage(target, "midge-backup");
        return new CaptureTarget(canonicalSource, target, stage);
    }

    public static async ValueTask<PinnedBackupFiles> PinFilesAsync(
        string sourceRoot,
        IReadOnlyList<string> sourcePaths,
        string objectsPath,
        CancellationToken cancellationToken)
    {
        var files = new List<PinnedBackupFile>(sourcePaths.Count);
        try
        {
            foreach (var sourcePath in sourcePaths.Order(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                StoragePathGuard.EnsureNotLink(sourcePath);
                var relative = Path.GetRelativePath(sourceRoot, sourcePath)
                    .Replace(Path.DirectorySeparatorChar, '/')
                    .Replace(Path.AltDirectorySeparatorChar, '/');
                ValidateInventoryPath(relative);
                FileStream? input = new FileStream(
                    sourcePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    BufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                try
                {
                    var length = input.Length;
                    if (IsMutableWal(relative) || IsMutableManifestJournal(relative))
                    {
                        var outputPath = GetObjectPath(objectsPath, relative);
                        AtomicStagedFile.CreateDirectoryDurably(Path.GetDirectoryName(outputPath)!);
                        await using var output = new FileStream(
                            outputPath,
                            FileMode.CreateNew,
                            FileAccess.Write,
                            FileShare.None,
                            BufferSize,
                            FileOptions.Asynchronous | FileOptions.SequentialScan);
                        var checksum = await CopyExactlyAsync(
                                input,
                                output,
                                length,
                                cancellationToken)
                            .ConfigureAwait(false);
                        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                        output.Flush(true);
                        AtomicStagedFile.FlushDirectory(Path.GetDirectoryName(outputPath)!);
                        input.Dispose();
                        input = null;
                        files.Add(new PinnedBackupFile(relative, length, null, checksum.Crc32C));
                    }
                    else
                    {
                        files.Add(new PinnedBackupFile(relative, length, input, null));
                        input = null;
                    }
                }
                finally
                {
                    input?.Dispose();
                }
            }

            return new PinnedBackupFiles(files);
        }
        catch
        {
            foreach (var file in files)
            {
                file.Dispose();
            }

            throw;
        }
    }

    public static async ValueTask<PantsBackupManifest> CompleteCaptureAsync(
        CaptureTarget target,
        PinnedBackupFiles pinnedFiles,
        ulong durabilityFrontier,
        PantsBackupStorageKind storageKind,
        DateTimeOffset capturedAt,
        IFailpointHandler failpoints,
        CancellationToken cancellationToken)
    {
        var objects = new List<PantsBackupObject>(pinnedFiles.Files.Count);
        var first = true;
        foreach (var file in pinnedFiles.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (first)
            {
                failpoints.Hit(Failpoint.BeforeBackupObjectCopy);
                first = false;
            }

            var objectPath = GetObjectPath(target.ObjectsPath, file.RelativePath);
            AtomicStagedFile.CreateDirectoryDurably(Path.GetDirectoryName(objectPath)!);
            uint checksum;
            if (file.Source is null)
            {
                checksum = file.PinnedChecksum ?? throw new PantsInternalException(
                    "A frozen WAL backup object is missing its checksum.");
            }
            else
            {
                await using var output = new FileStream(
                    objectPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    BufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var result = await CopyExactlyAsync(
                        file.Source,
                        output,
                        file.SizeBytes,
                        cancellationToken)
                    .ConfigureAwait(false);
                checksum = result.Crc32C;
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(true);
                AtomicStagedFile.FlushDirectory(Path.GetDirectoryName(objectPath)!);
            }

            objects.Add(new PantsBackupObject(
                file.RelativePath,
                checked((ulong)file.SizeBytes),
                checksum));
            failpoints.Hit(Failpoint.AfterBackupObjectCopy);
        }

        var formatVersion = ReadFormatVersion(Path.Combine(target.ObjectsPath, "FORMAT"));
        var manifest = new PantsBackupManifest(
            BackupVersion,
            Guid.NewGuid().ToString(),
            capturedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            durabilityFrontier,
            formatVersion,
            typeof(PantsDatabase).Assembly.GetName().Version?.ToString() ?? "unknown",
            storageKind,
            objects.OrderBy(static item => item.Path, StringComparer.Ordinal).ToArray());

        var manifestPath = Path.Combine(target.StagePath, ManifestFileName);
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        await using (var output = new FileStream(
                         manifestPath,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         BufferSize,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await output.WriteAsync(manifestBytes, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(true);
        }

        await ValidateObjectsAsync(target.StagePath, manifest, cancellationToken)
            .ConfigureAwait(false);
        await VerifyDatabaseAsync(target.ObjectsPath, storageKind, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        AtomicStagedFile.FlushDirectory(target.ObjectsPath);
        AtomicStagedFile.FlushDirectory(target.StagePath);
        failpoints.Hit(Failpoint.BeforeBackupPublish);
        PublishNoReplace(target.StagePath, target.DestinationPath);
        target.MarkPublished();
        AtomicStagedFile.FlushDirectory(target.ParentPath);
        return manifest;
    }

    public static async ValueTask<PantsBackupManifest> RestoreAsync(
        string artifact,
        PantsOpenOptions options,
        IFailpointHandler failpoints,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifact);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(failpoints);
        cancellationToken.ThrowIfCancellationRequested();

        string artifactRoot;
        try
        {
            artifactRoot = StoragePathGuard.CanonicalizeRoot(artifact);
            if (!Directory.Exists(artifactRoot))
            {
                throw new PantsCorruptionException("Backup artifact directory is missing.");
            }

            StoragePathGuard.EnsureTreeHasNoLinks(artifactRoot);
        }
        catch (PantsException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
                NotSupportedException)
        {
            throw new PantsCorruptionException("Backup artifact path is invalid.", exception);
        }

        var manifest = await ReadManifestAsync(artifactRoot, cancellationToken).ConfigureAwait(false);
        await ValidateObjectsAsync(artifactRoot, manifest, cancellationToken).ConfigureAwait(false);
        await VerifyDatabaseAsync(
                Path.Combine(artifactRoot, ObjectsDirectoryName),
                manifest.StorageKind,
                cancellationToken)
            .ConfigureAwait(false);

        var (destination, destinationKind) = options.Storage switch
        {
            PantsStorageConfiguration.Local local =>
                (local.Path, PantsBackupStorageKind.Local),
            PantsStorageConfiguration.SimulatedCloud simulated =>
                (simulated.LocalCachePath, PantsBackupStorageKind.CloudSimulated),
            _ => throw new PantsNotSupportedException(
                "Backup restore supports only local and simulated-cloud storage.")
        };
        if (destinationKind != manifest.StorageKind)
        {
            throw new PantsInvalidArgumentException(
                $"Backup storage kind '{manifest.StorageKind}' cannot restore to '{destinationKind}'.");
        }

        var target = PrepareTarget(artifactRoot, destination);
        using var restoreTarget = new RestoreTarget(target);
        var first = true;
        foreach (var item in manifest.Objects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (first)
            {
                failpoints.Hit(Failpoint.BeforeRestoreObjectCopy);
                first = false;
            }

            var sourcePath = GetObjectPath(Path.Combine(artifactRoot, ObjectsDirectoryName), item.Path);
            var destinationPath = GetDatabaseObjectPath(restoreTarget.StagePath, item.Path);
            AtomicStagedFile.CreateDirectoryDurably(Path.GetDirectoryName(destinationPath)!);
            await CopyVerifiedObjectAsync(sourcePath, destinationPath, item, cancellationToken)
                .ConfigureAwait(false);
            AtomicStagedFile.FlushDirectory(Path.GetDirectoryName(destinationPath)!);
            failpoints.Hit(Failpoint.AfterRestoreObjectCopy);
        }

        await VerifyDatabaseAsync(restoreTarget.StagePath, destinationKind, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        AtomicStagedFile.FlushDirectory(restoreTarget.StagePath);
        failpoints.Hit(Failpoint.BeforeRestorePublish);
        PublishNoReplace(restoreTarget.StagePath, restoreTarget.DestinationPath);
        restoreTarget.MarkPublished();
        AtomicStagedFile.FlushDirectory(restoreTarget.ParentPath);
        return manifest;
    }

    static async ValueTask<PantsBackupManifest> ReadManifestAsync(
        string artifactRoot,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(artifactRoot, ManifestFileName);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= 0 || info.Length > MaximumManifestBytes)
            {
                throw new PantsCorruptionException("Backup manifest is missing or exceeds its size limit.");
            }

            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            var manifest = JsonSerializer.Deserialize<PantsBackupManifest>(bytes, JsonOptions) ??
                           throw new PantsCorruptionException("Backup manifest is empty.");
            ValidateManifest(manifest);
            return manifest;
        }
        catch (PantsException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new PantsCorruptionException("Backup manifest cannot be decoded.", exception);
        }
        catch (FileNotFoundException exception)
        {
            throw new PantsCorruptionException("Backup manifest is missing.", exception);
        }
    }

    static void ValidateManifest(PantsBackupManifest manifest)
    {
        if (manifest.BackupVersion != BackupVersion ||
            !Guid.TryParse(manifest.BackupId, out _) ||
            !DateTimeOffset.TryParse(
                manifest.CapturedAt,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out _) ||
            string.IsNullOrWhiteSpace(manifest.EngineVersion) ||
            !Enum.IsDefined(manifest.StorageKind) ||
            manifest.Objects is null)
        {
            throw new PantsCorruptionException("Backup manifest metadata is invalid.");
        }

        var seen = new HashSet<string>(GetPathComparer());
        foreach (var item in manifest.Objects)
        {
            if (item is null || item.SizeBytes > long.MaxValue ||
                !seen.Add(ValidateInventoryPath(item.Path)))
            {
                throw new PantsCorruptionException("Backup inventory contains an invalid or duplicate object.");
            }
        }

        if (!seen.Contains("FORMAT"))
        {
            throw new PantsCorruptionException("Backup inventory does not contain the FORMAT marker.");
        }
    }

    static async ValueTask ValidateObjectsAsync(
        string artifactRoot,
        PantsBackupManifest manifest,
        CancellationToken cancellationToken)
    {
        var objectsRoot = Path.Combine(artifactRoot, ObjectsDirectoryName);
        if (!Directory.Exists(objectsRoot))
        {
            throw new PantsCorruptionException("Backup objects directory is missing.");
        }

        try
        {
            StoragePathGuard.EnsureTreeHasNoLinks(objectsRoot);
        }
        catch (PantsInvalidPathException exception)
        {
            throw new PantsCorruptionException("Backup objects contain a link.", exception);
        }

        var expected = manifest.Objects.ToDictionary(
            static item => item.Path,
            GetPathComparer());
        var actual = new HashSet<string>(GetPathComparer());
        foreach (var path in Directory.EnumerateFiles(objectsRoot, "*", SearchOption.AllDirectories))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            {
                throw new PantsCorruptionException("Backup contains a non-regular object.");
            }

            var relative = Path.GetRelativePath(objectsRoot, path)
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/');
            ValidateInventoryPath(relative);
            if (!actual.Add(relative))
            {
                throw new PantsCorruptionException("Backup contains duplicate object paths.");
            }
        }

        if (actual.Count != expected.Count || actual.Any(path => !expected.ContainsKey(path)))
        {
            throw new PantsCorruptionException("Backup objects differ from the recorded inventory.");
        }

        foreach (var item in manifest.Objects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = GetObjectPath(objectsRoot, item.Path);
            await using var input = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (checked((ulong)input.Length) != item.SizeBytes)
            {
                throw new PantsCorruptionException($"Backup object '{item.Path}' has an invalid length.");
            }

            var checksum = await ComputeChecksumAsync(input, input.Length, cancellationToken)
                .ConfigureAwait(false);
            if (checksum != item.Crc32C)
            {
                throw new PantsCorruptionException($"Backup object '{item.Path}' failed its checksum.");
            }
        }

        var formatVersion = ReadFormatVersion(GetObjectPath(objectsRoot, "FORMAT"));
        if (formatVersion != manifest.DatabaseFormatVersion)
        {
            throw new PantsCorruptionException("Backup FORMAT version differs from its inventory.");
        }
    }

    static async ValueTask VerifyDatabaseAsync(
        string databaseRoot,
        PantsBackupStorageKind storageKind,
        CancellationToken cancellationToken)
    {
        IAsyncSstSourceFactory? sourceFactory = storageKind == PantsBackupStorageKind.CloudSimulated
            ? new SimulatedCloudSstSourceFactory(databaseRoot)
            : null;
        var report = await StorageVerifier.VerifyPathAsync(
                databaseRoot,
                sourceFactory,
                cancellationToken)
            .ConfigureAwait(false);
        if (report.Health != PantsEngineHealth.Healthy)
        {
            throw new PantsCorruptionException("Backup database files are not healthy.");
        }
    }

    static async ValueTask CopyVerifiedObjectAsync(
        string sourcePath,
        string destinationPath,
        PantsBackupObject expected,
        CancellationToken cancellationToken)
    {
        StoragePathGuard.EnsureNotLink(sourcePath);
        await using var input = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (checked((ulong)input.Length) != expected.SizeBytes)
        {
            throw new PantsCorruptionException($"Backup object '{expected.Path}' changed during restore.");
        }

        await using var output = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var result = await CopyExactlyAsync(input, output, input.Length, cancellationToken)
            .ConfigureAwait(false);
        if (result.Crc32C != expected.Crc32C)
        {
            throw new PantsCorruptionException($"Backup object '{expected.Path}' changed during restore.");
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        output.Flush(true);
    }

    static async ValueTask<(long SizeBytes, uint Crc32C)> CopyExactlyAsync(
        Stream source,
        Stream destination,
        long sizeBytes,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            var remaining = sizeBytes;
            var checksum = 0U;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var requested = checked((int)Math.Min(buffer.Length, remaining));
                var read = await source.ReadAsync(buffer.AsMemory(0, requested), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    throw new PantsCorruptionException("A backup source object changed during capture.");
                }

                checksum = DiskFormat.Crc32CAppend(checksum, buffer.AsSpan(0, read));
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
                remaining -= read;
            }

            return (sizeBytes, checksum);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    static async ValueTask<uint> ComputeChecksumAsync(
        Stream source,
        long sizeBytes,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            var remaining = sizeBytes;
            var checksum = 0U;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var requested = checked((int)Math.Min(buffer.Length, remaining));
                var read = await source.ReadAsync(buffer.AsMemory(0, requested), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    throw new PantsCorruptionException("A backup object changed during verification.");
                }

                checksum = DiskFormat.Crc32CAppend(checksum, buffer.AsSpan(0, read));
                remaining -= read;
            }

            return checksum;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    static uint ReadFormatVersion(string path)
    {
        var marker = File.ReadAllText(path, Encoding.UTF8).Trim();
        const string prefix = "midge-format-version=";
        if (!marker.StartsWith(prefix, StringComparison.Ordinal) ||
            !uint.TryParse(marker[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var version))
        {
            throw new PantsCorruptionException("Database FORMAT marker is invalid.");
        }

        return version;
    }

    static string ValidateInventoryPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || path.Contains(':') ||
            path[0] == '/' ||
            path.Split('/').Any(static component => component.Length == 0 || component is "." or ".."))
        {
            throw new PantsCorruptionException($"Backup inventory path '{path}' is unsafe.");
        }

        return path;
    }

    static bool IsMutableWal(string relativePath) =>
        relativePath.Equals("wal/wal.log", StringComparison.Ordinal) ||
        relativePath.Equals("wal/active/wal.log", StringComparison.Ordinal);

    static bool IsMutableManifestJournal(string relativePath) =>
        relativePath.Equals("manifest.journal", StringComparison.Ordinal);

    static string GetObjectPath(string objectsRoot, string inventoryPath) =>
        Path.Combine(objectsRoot, inventoryPath.Replace('/', Path.DirectorySeparatorChar));

    static string GetDatabaseObjectPath(string databaseRoot, string inventoryPath) =>
        Path.Combine(databaseRoot, inventoryPath.Replace('/', Path.DirectorySeparatorChar));

    static string PrepareTarget(string sourceRoot, string destination)
    {
        var resolved = ResolvePath(destination);
        EnsureDisjoint(sourceRoot, resolved);
        if (PathEntryExists(resolved))
        {
            throw new PantsInvalidArgumentException($"Backup/restore target '{resolved}' already exists.");
        }

        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(resolved));
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(resolved));
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(parent))
        {
            throw new PantsInvalidArgumentException("Backup/restore target must have a parent and name.");
        }

        Directory.CreateDirectory(parent);
        parent = ResolvePath(parent);
        resolved = Path.Combine(parent, name);
        EnsureDisjoint(sourceRoot, resolved);
        if (PathEntryExists(resolved))
        {
            throw new PantsInvalidArgumentException($"Backup/restore target '{resolved}' already exists.");
        }

        return resolved;
    }

    static string ResolvePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var absolute = Path.IsPathRooted(path) ? path : Path.Combine(Environment.CurrentDirectory, path);
        var root = Path.GetPathRoot(absolute) ??
                   throw new PantsInvalidArgumentException("Backup/restore path has no filesystem root.");
        var current = root;
        var components = absolute[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        var missingAncestor = false;
        for (var index = 0; index < components.Length; index++)
        {
            var component = components[index];
            if (component == ".")
            {
                continue;
            }

            if (component == "..")
            {
                if (missingAncestor)
                {
                    throw new PantsInvalidArgumentException(
                        "Parent traversal through a missing backup/restore path ancestor is ambiguous.");
                }

                current = Directory.GetParent(current)?.FullName ?? root;
                continue;
            }

            var candidate = Path.Combine(current, component);
            FileSystemInfo info = new DirectoryInfo(candidate);
            string? linkTarget;
            try
            {
                linkTarget = info.LinkTarget;
            }
            catch (Exception exception) when (
                exception is FileNotFoundException or DirectoryNotFoundException)
            {
                info = new FileInfo(candidate);
                try
                {
                    linkTarget = info.LinkTarget;
                }
                catch (Exception inner) when (
                    inner is FileNotFoundException or DirectoryNotFoundException)
                {
                    linkTarget = null;
                }
            }

            if (linkTarget is not null)
            {
                var link = info.ResolveLinkTarget(true) ??
                           throw new PantsInvalidArgumentException(
                               $"Backup/restore path link '{candidate}' cannot be resolved.");
                current = Path.GetFullPath(link.FullName);
            }
            else if (Directory.Exists(candidate) || File.Exists(candidate))
            {
                current = Path.GetFullPath(candidate);
                if (index + 1 < components.Length && !Directory.Exists(candidate))
                {
                    throw new PantsInvalidArgumentException(
                        $"Backup/restore path ancestor '{candidate}' is not a directory.");
                }
            }
            else
            {
                current = candidate;
                missingAncestor = true;
            }
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));
    }

    static bool PathEntryExists(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
        {
            return true;
        }

        try
        {
            return new FileInfo(path).LinkTarget is not null ||
                   new DirectoryInfo(path).LinkTarget is not null;
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    static void EnsureDisjoint(string left, string right)
    {
        if (IsSameOrDescendant(left, right) || IsSameOrDescendant(right, left))
        {
            throw new PantsInvalidArgumentException(
                $"Backup/restore roots '{left}' and '{right}' overlap.");
        }
    }

    static bool IsSameOrDescendant(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative == "." ||
               (!Path.IsPathRooted(relative) &&
                relative != ".." &&
                !relative.StartsWith($"..{Path.DirectorySeparatorChar}", PathComparison) &&
                !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", PathComparison));
    }

    static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    static StringComparer GetPathComparer() =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    static string CreateStage(string target, string prefix)
    {
        var parent = Path.GetDirectoryName(target) ??
                     throw new PantsInvalidArgumentException("Backup/restore target requires a parent.");
        var stage = Path.Combine(parent, $".{prefix}-{Guid.NewGuid():N}.tmp");
        AtomicStagedFile.CreateDirectoryDurably(stage);
        return stage;
    }

    static void PublishNoReplace(string stage, string target)
    {
        int error;
        if (OperatingSystem.IsWindows())
        {
            if (MoveFileExW(stage, target, 0))
            {
                return;
            }

            error = Marshal.GetLastPInvokeError();
            if (error is 80 or 183)
            {
                throw new PantsInvalidArgumentException($"Backup/restore target '{target}' already exists.");
            }

            throw new PantsIOException(
                "Atomic backup/restore directory publication failed.",
                new Win32Exception(error));
        }

        if (OperatingSystem.IsLinux())
        {
            try
            {
                if (RenameAt2(AtCurrentWorkingDirectory, stage, AtCurrentWorkingDirectory, target, RenameNoReplace) == 0)
                {
                    return;
                }
            }
            catch (EntryPointNotFoundException exception)
            {
                throw new PantsNotSupportedException(
                    "Atomic directory publication without replacement is unavailable.",
                    exception);
            }

            error = Marshal.GetLastPInvokeError();
        }
        else if (OperatingSystem.IsMacOS())
        {
            try
            {
                if (RenameAtxNp(AtCurrentWorkingDirectory, stage, AtCurrentWorkingDirectory, target, RenameExclusive) == 0)
                {
                    return;
                }
            }
            catch (EntryPointNotFoundException exception)
            {
                throw new PantsNotSupportedException(
                    "Atomic directory publication without replacement is unavailable.",
                    exception);
            }

            error = Marshal.GetLastPInvokeError();
        }
        else
        {
            throw new PantsNotSupportedException(
                "Atomic directory publication without replacement is unavailable on this platform.");
        }

        if (error == 17)
        {
            throw new PantsInvalidArgumentException($"Backup/restore target '{target}' already exists.");
        }

        if (error is 22 or 38 or 45 or 78 or 95)
        {
            throw new PantsNotSupportedException(
                "Atomic directory publication without replacement is unavailable.",
                new Win32Exception(error));
        }

        throw new PantsIOException(
            "Atomic backup/restore directory publication failed.",
            new Win32Exception(error));
    }

    const int AtCurrentWorkingDirectory = -100;
    const uint RenameNoReplace = 1;
    const uint RenameExclusive = 4;

    [DllImport("libc", EntryPoint = "renameat2", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    static extern int RenameAt2(
        int oldDirectory,
        string oldPath,
        int newDirectory,
        string newPath,
        uint flags);

    [DllImport("libSystem.B.dylib", EntryPoint = "renameatx_np", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    static extern int RenameAtxNp(
        int oldDirectory,
        string oldPath,
        int newDirectory,
        string newPath,
        uint flags);

    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool MoveFileExW(string existingFileName, string newFileName, uint flags);

    internal sealed class CaptureTarget(
        string sourceRoot,
        string destinationPath,
        string stagePath) : IDisposable
    {
        int _published;

        public string SourceRoot { get; } = sourceRoot;

        public string DestinationPath { get; } = destinationPath;

        public string ParentPath { get; } = Path.GetDirectoryName(destinationPath)!;

        public string StagePath { get; } = stagePath;

        public string ObjectsPath { get; } = Path.Combine(stagePath, ObjectsDirectoryName);

        public void MarkPublished() => Interlocked.Exchange(ref _published, 1);

        public void Dispose()
        {
            if (Volatile.Read(ref _published) == 0 && Directory.Exists(StagePath))
            {
                try
                {
                    Directory.Delete(StagePath, true);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    // An unpublished stage is safer than deleting an uncertain artifact.
                }
            }
        }
    }

    internal sealed class RestoreTarget(string destinationPath) : IDisposable
    {
        int _published;

        public string DestinationPath { get; } = destinationPath;

        public string ParentPath { get; } = Path.GetDirectoryName(destinationPath)!;

        public string StagePath { get; } = CreateStage(destinationPath, "midge-restore");

        public void MarkPublished() => Interlocked.Exchange(ref _published, 1);

        public void Dispose()
        {
            if (Volatile.Read(ref _published) == 0 && Directory.Exists(StagePath))
            {
                try
                {
                    Directory.Delete(StagePath, true);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    // An unpublished stage is safer than deleting an uncertain target.
                }
            }
        }
    }

    internal sealed class PinnedBackupFiles(IReadOnlyList<PinnedBackupFile> files) : IDisposable
    {
        public IReadOnlyList<PinnedBackupFile> Files { get; } = files;

        public void Dispose()
        {
            foreach (var file in Files)
            {
                file.Dispose();
            }
        }
    }

    internal sealed class PinnedBackupFile(
        string relativePath,
        long sizeBytes,
        FileStream? source,
        uint? pinnedChecksum) : IDisposable
    {
        public string RelativePath { get; } = relativePath;

        public long SizeBytes { get; } = sizeBytes;

        public FileStream? Source { get; } = source;

        public uint? PinnedChecksum { get; } = pinnedChecksum;

        public void Dispose() => Source?.Dispose();
    }
}

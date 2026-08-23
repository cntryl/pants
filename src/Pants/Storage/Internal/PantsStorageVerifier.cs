using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cntryl.Pants.Storage.Internal;

static class PantsStorageVerifier
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static async ValueTask<PantsStorageVerificationReport> VerifyPathAsync(
        string path,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        return await Task.Run(
            () => VerifyPath(path, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    static PantsStorageVerificationReport VerifyPath(
        string path,
        CancellationToken cancellationToken)
    {
        string root;
        try
        {
            root = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException
                                              or PathTooLongException)
        {
            throw PantsException.Create(PantsErrorCode.InvalidPath, "The database path is invalid.", exception);
        }

        if (!Directory.Exists(root))
        {
            throw PantsException.Create(PantsErrorCode.InvalidPath, $"Database path '{root}' does not exist.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var formatPath = Path.Combine(root, "FORMAT");
        if (!File.Exists(formatPath) || File.ReadAllText(formatPath) != "midge-format-version=3\n")
        {
            throw PantsException.Create(
                PantsErrorCode.CompatibilityError,
                "The path does not contain a valid Midge FORMAT v3 marker.");
        }

        var manifestPath = File.Exists(Path.Combine(root, "manifest.snapshot.json"))
            ? Path.Combine(root, "manifest.snapshot.json")
            : Path.Combine(root, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            throw PantsException.Create(PantsErrorCode.Corruption, "The manifest snapshot is missing.");
        }

        MidgeManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<MidgeManifest>(
                File.ReadAllBytes(manifestPath),
                JsonOptions) ?? throw new JsonException("Manifest is empty.");
        }
        catch (JsonException exception)
        {
            throw PantsException.Create(PantsErrorCode.Corruption, "The manifest cannot be decoded.", exception);
        }

        long bytesVerified = 0;
        long dataBlocksVerified = 0;
        var sstFilesVerified = 0;
        var warnings = new List<string>();
        var ownedSsts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var safeName = ValidateFileName(file.Name);
            var sstPath = Path.Combine(root, "sst", safeName);
            if (!File.Exists(sstPath))
            {
                throw PantsException.Create(
                    PantsErrorCode.Corruption,
                    $"Manifest SST '{safeName}' is missing.");
            }

            var bytes = File.ReadAllBytes(sstPath);
            if (checked((ulong)bytes.Length) != file.SizeBytes)
            {
                throw PantsException.Create(
                    PantsErrorCode.Corruption,
                    $"Manifest SST '{safeName}' has an unexpected length.");
            }

            if (file.ContentCrc32C is not { } expectedCrc)
            {
                throw PantsException.Create(
                    PantsErrorCode.Corruption,
                    $"Manifest SST '{safeName}' is missing its content checksum.");
            }

            if (MidgeDiskFormat.Crc32C(bytes) != expectedCrc)
            {
                throw PantsException.Create(
                    PantsErrorCode.Corruption,
                    $"Manifest SST '{safeName}' content checksum does not match.");
            }

            MidgeSstContents contents;
            try
            {
                contents = MidgeSstCodec.Decode(bytes);
                SstManifestMetadataValidator.Validate(contents, file, "Manifest SST");
            }
            catch (PantsException exception)
            {
                throw PantsException.Create(
                    PantsErrorCode.Corruption,
                    $"Manifest SST '{safeName}' is structurally corrupt.",
                    exception);
            }

            ownedSsts.Add(safeName);
            bytesVerified = checked(bytesVerified + bytes.Length);
            dataBlocksVerified = checked(dataBlocksVerified + contents.DataBlockCount);
            sstFilesVerified++;
        }

        var sstDirectory = Path.Combine(root, "sst");
        if (Directory.Exists(sstDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(sstDirectory, "*.sst"))
            {
                if (!ownedSsts.Contains(Path.GetFileName(file)))
                {
                    warnings.Add($"Unowned SST retained conservatively: {Path.GetFileName(file)}");
                }
            }
        }

        var (walRecords, walBytes, walBoundary) = VerifyWal(root, cancellationToken);
        bytesVerified = checked(bytesVerified + walBytes);

        var intentEntries = 0;
        var intentPath = Path.Combine(root, "intent_log.json");
        if (File.Exists(intentPath))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllBytes(intentPath));
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                {
                    throw new JsonException("The intent log root is not an array.");
                }

                ValidateIntentLogSstNames(document.RootElement);
                intentEntries = document.RootElement.GetArrayLength();
                if (intentEntries != 0)
                {
                    warnings.Add($"Intent log retains {intentEntries} publication entries.");
                }
            }
            catch (JsonException exception)
            {
                throw PantsException.Create(
                    PantsErrorCode.RecoveryFailed,
                    "The intent log cannot be decoded.",
                    exception);
            }
        }

        var journalPath = Path.Combine(root, "manifest.journal");
        if (!File.Exists(journalPath))
        {
            throw PantsException.Create(PantsErrorCode.Corruption, "The manifest journal is missing.");
        }

        try
        {
            LocalDiskStore.ValidateManifestJournal(File.ReadAllBytes(journalPath));
        }
        catch (PantsException exception)
        {
            throw PantsException.Create(
                PantsErrorCode.RecoveryFailed,
                "The manifest journal is corrupt.",
                exception);
        }

        var retainedWalDirectory = Path.Combine(root, "wal");
        foreach (var retained in Directory.Exists(retainedWalDirectory)
                     ? Directory.EnumerateFiles(
                         retainedWalDirectory,
                         "*.salvage-retained*",
                         SearchOption.TopDirectoryOnly)
                     : [])
        {
            warnings.Add($"Salvage-retained WAL preserved: {Path.GetFileName(retained)}");
        }

        var health = warnings.Count == 0
            ? PantsEngineHealth.Healthy
            : PantsEngineHealth.Degraded;
        return new PantsStorageVerificationReport(
            checked((long)manifest.EditCheckpointId),
            manifest.Files.Count,
            sstFilesVerified,
            bytesVerified,
            dataBlocksVerified,
            walBoundary,
            walRecords,
            walBytes,
            intentEntries,
            true,
            health,
            warnings);
    }

    static (long Records, long Bytes, long? Boundary) VerifyWal(
        string root,
        CancellationToken cancellationToken)
    {
        var walDirectory = Path.Combine(root, "wal");
        if (!Directory.Exists(walDirectory))
        {
            return (0, 0, null);
        }

        var records = 0L;
        var decodedBytes = 0L;
        long? boundary = null;
        var sealedPaths = Directory
            .EnumerateFiles(walDirectory, "*.wal", SearchOption.TopDirectoryOnly)
            .OrderBy(static candidate => Path.GetFileName(candidate), StringComparer.Ordinal)
            .ToArray();
        var walPath = Path.Combine(root, "wal", "wal.log");
        var replayPaths = File.Exists(walPath)
            ? [.. sealedPaths, walPath]
            : sealedPaths;
        var writerEpochFrontiers = DiscoverWriterEpochFrontiers(
            replayPaths,
            cancellationToken);
        var replayOrdinal = 0UL;
        using var recovery = new MidgeWalRecoveryStateMachine();
        var recoveredVersions = new MidgeWalRecoveredVersionTracker();
        foreach (var sealedPath in sealedPaths)
        {
            ValidateSealedWalName(Path.GetFileName(sealedPath));
            var (fileRecords, fileBytes, fileBoundary) = VerifyWalFile(
                sealedPath,
                MidgeWalTailPolicy.Strict,
                boundary,
                recovery,
                recoveredVersions,
                writerEpochFrontiers,
                ref replayOrdinal,
                cancellationToken);
            records = checked(records + fileRecords);
            decodedBytes = checked(decodedBytes + fileBytes);
            boundary = fileBoundary ?? boundary;
        }

        if (File.Exists(walPath))
        {
            var (fileRecords, fileBytes, fileBoundary) = VerifyWalFile(
                walPath,
                MidgeWalTailPolicy.AllowIncompleteFinalTail,
                boundary,
                recovery,
                recoveredVersions,
                writerEpochFrontiers,
                ref replayOrdinal,
                cancellationToken);
            records = checked(records + fileRecords);
            decodedBytes = checked(decodedBytes + fileBytes);
            boundary = fileBoundary ?? boundary;
        }

        return (records, decodedBytes, boundary);
    }

    static MidgeWalWriterEpochFrontiers DiscoverWriterEpochFrontiers(
        IReadOnlyList<string> replayPaths,
        CancellationToken cancellationToken)
    {
        var frontiers = new MidgeWalWriterEpochFrontiers();
        var ordinal = 0UL;
        foreach (var replayPath in replayPaths)
        {
            using var stream = new FileStream(
                replayPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            try
            {
                MidgeWalFrameReader.Visit(
                    stream,
                    (record, _) =>
                    {
                        frontiers.Record(record, ordinal);
                        if (ordinal != ulong.MaxValue)
                        {
                            ordinal++;
                        }
                    },
                    StringComparer.Ordinal.Equals(Path.GetFileName(replayPath), "wal.log")
                        ? MidgeWalTailPolicy.AllowIncompleteFinalTail
                        : MidgeWalTailPolicy.Strict,
                    cancellationToken);
            }
            catch (PantsException exception) when (exception.Code != PantsErrorCode.Corruption)
            {
                throw PantsException.Create(
                    PantsErrorCode.Corruption,
                    $"WAL '{Path.GetFileName(replayPath)}' is malformed.",
                    exception);
            }
            catch (OverflowException exception)
            {
                throw PantsException.Create(
                    PantsErrorCode.Corruption,
                    $"WAL '{Path.GetFileName(replayPath)}' contains values outside supported limits.",
                    exception);
            }
        }

        return frontiers;
    }

    static (long Records, long Bytes, long? Boundary) VerifyWalFile(
        string walPath,
        MidgeWalTailPolicy tailPolicy,
        long? precedingBoundary,
        MidgeWalRecoveryStateMachine recovery,
        MidgeWalRecoveredVersionTracker recoveredVersions,
        MidgeWalWriterEpochFrontiers writerEpochFrontiers,
        ref ulong replayOrdinal,
        CancellationToken cancellationToken)
    {
        using var stream = new FileStream(
            walPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var records = 0L;
        var decodedBytes = 0L;
        var boundary = precedingBoundary;
        var currentOrdinal = replayOrdinal;
        try
        {
            MidgeWalFrameReader.Visit(
                stream,
                (record, _) =>
                {
                    var recordOrdinal = currentOrdinal;
                    if (currentOrdinal != ulong.MaxValue)
                    {
                        currentOrdinal++;
                    }

                    if (writerEpochFrontiers.IsStale(record, recordOrdinal))
                    {
                        return;
                    }

                    if (record.Sequence > long.MaxValue)
                    {
                        throw new PantsStorageException(
                            "A WAL sequence exceeds Pants' supported range.");
                    }

                    var decodedBoundary = (long)record.Sequence;
                    var applicableMutations = new List<MidgeWalMutation>();
                    recovery.Accept(
                        record,
                        (mutation, _) => applicableMutations.Add(mutation));
                    recoveredVersions.ValidateAndRecord(applicableMutations);
                    boundary = boundary.HasValue
                        ? Math.Max(boundary.Value, decodedBoundary)
                        : decodedBoundary;
                    records++;
                    decodedBytes = checked(
                        decodedBytes + MidgeWalRecordMetrics.GetLogicalByteCount(record));
                },
                tailPolicy,
                cancellationToken);
            replayOrdinal = currentOrdinal;
        }
        catch (PantsException exception) when (exception.Code != PantsErrorCode.Corruption)
        {
            throw PantsException.Create(
                PantsErrorCode.Corruption,
                $"WAL '{Path.GetFileName(walPath)}' is malformed.",
                exception);
        }
        catch (OverflowException exception)
        {
            throw PantsException.Create(
                PantsErrorCode.Corruption,
                $"WAL '{Path.GetFileName(walPath)}' contains values outside supported limits.",
                exception);
        }

        return (records, decodedBytes, boundary);
    }

    static void ValidateSealedWalName(string name)
    {
        ReadOnlySpan<char> stem = Path.GetFileNameWithoutExtension(name);
        if (stem.Length != 20 || stem.ContainsAnyExceptInRange('0', '9'))
        {
            throw PantsException.Create(
                PantsErrorCode.Corruption,
                $"Sealed WAL name '{name}' is invalid.");
        }
    }

    static string ValidateFileName(string name)
    {
        if (string.IsNullOrEmpty(name) ||
            name != Path.GetFileName(name) ||
            !name.EndsWith(".sst", StringComparison.Ordinal) ||
            name.IndexOfAny(['/', '\\', ':', '\0']) >= 0)
        {
            throw PantsException.Create(
                PantsErrorCode.Corruption,
                $"Manifest SST name '{name}' is unsafe.");
        }

        return name;
    }

    static void ValidateIntentLogSstNames(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    ValidateIntentLogSstNames(property.Value);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    ValidateIntentLogSstNames(item);
                }

                break;
            case JsonValueKind.String:
                var value = element.GetString();
                if (value is not null && value.EndsWith(".sst", StringComparison.Ordinal))
                {
                    _ = ValidateFileName(value);
                }

                break;
        }
    }
}

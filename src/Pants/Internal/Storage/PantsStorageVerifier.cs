using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pants;

internal static class PantsStorageVerifier
{
    private static readonly JsonSerializerOptions JsonOptions = new()
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

    private static PantsStorageVerificationReport VerifyPath(
        string path,
        CancellationToken cancellationToken)
    {
        string root;
        try
        {
            root = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw PantsException.Create(PantsErrorCode.InvalidPath, "The database path is invalid.", exception);
        }

        if (!Directory.Exists(root))
        {
            throw PantsException.Create(PantsErrorCode.InvalidPath, $"Database path '{root}' does not exist.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        string formatPath = Path.Combine(root, "FORMAT");
        if (!File.Exists(formatPath) || File.ReadAllText(formatPath) != "midge-format-version=3\n")
        {
            throw PantsException.Create(
                PantsErrorCode.CompatibilityError,
                "The path does not contain a valid Midge FORMAT v3 marker.");
        }

        string manifestPath = File.Exists(Path.Combine(root, "manifest.snapshot.json"))
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
        int sstFilesVerified = 0;
        var warnings = new List<string>();
        var ownedSsts = new HashSet<string>(StringComparer.Ordinal);
        foreach (MidgeFileMeta file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string safeName = ValidateFileName(file.Name);
            string sstPath = Path.Combine(root, "sst", safeName);
            if (!File.Exists(sstPath))
            {
                throw PantsException.Create(
                    PantsErrorCode.Corruption,
                    $"Manifest SST '{safeName}' is missing.");
            }

            byte[] bytes = File.ReadAllBytes(sstPath);
            if (checked((ulong)bytes.Length) != file.SizeBytes)
            {
                throw PantsException.Create(
                    PantsErrorCode.Corruption,
                    $"Manifest SST '{safeName}' has an unexpected length.");
            }

            if (file.ContentCrc32C is { } expectedCrc && MidgeDiskFormat.Crc32C(bytes) != expectedCrc)
            {
                throw PantsException.Create(
                    PantsErrorCode.Corruption,
                    $"Manifest SST '{safeName}' content checksum does not match.");
            }

            MidgeSstContents contents;
            try
            {
                contents = MidgeSstCodec.Decode(bytes);
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

        string sstDirectory = Path.Combine(root, "sst");
        if (Directory.Exists(sstDirectory))
        {
            foreach (string file in Directory.EnumerateFiles(sstDirectory, "*.sst"))
            {
                if (!ownedSsts.Contains(Path.GetFileName(file)))
                {
                    warnings.Add($"Unowned SST retained conservatively: {Path.GetFileName(file)}");
                }
            }
        }

        (long walRecords, long walBytes, long? walBoundary) = VerifyWal(root, cancellationToken);
        bytesVerified = checked(bytesVerified + walBytes);

        int intentEntries = 0;
        string intentPath = Path.Combine(root, "intent_log.json");
        if (File.Exists(intentPath))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(intentPath));
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                {
                    throw new JsonException("The intent log root is not an array.");
                }

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

        string journalPath = Path.Combine(root, "manifest.journal");
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

        foreach (string retained in Directory.EnumerateFiles(
                     Path.Combine(root, "wal"),
                     "*.salvage-retained*",
                     SearchOption.TopDirectoryOnly))
        {
            warnings.Add($"Salvage-retained WAL preserved: {Path.GetFileName(retained)}");
        }

        PantsEngineHealth health = warnings.Count == 0
            ? PantsEngineHealth.Healthy
            : PantsEngineHealth.Degraded;
        return new PantsStorageVerificationReport(
            checked((long)manifest.EditCheckpointId),
            1,
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

    private static (long Records, long Bytes, long? Boundary) VerifyWal(
        string root,
        CancellationToken cancellationToken)
    {
        string walDirectory = Path.Combine(root, "wal");
        if (!Directory.Exists(walDirectory))
        {
            return (0, 0, null);
        }

        long records = 0;
        long decodedBytes = 0;
        long? boundary = null;
        foreach (string sealedPath in Directory
                     .EnumerateFiles(walDirectory, "*.wal", SearchOption.TopDirectoryOnly)
                     .OrderBy(static candidate => Path.GetFileName(candidate), StringComparer.Ordinal))
        {
            ValidateSealedWalName(Path.GetFileName(sealedPath));
            (long fileRecords, long fileBytes, long? fileBoundary) = VerifyWalFile(
                sealedPath,
                boundary,
                cancellationToken);
            records = checked(records + fileRecords);
            decodedBytes = checked(decodedBytes + fileBytes);
            boundary = fileBoundary ?? boundary;
        }

        string walPath = Path.Combine(root, "wal", "wal.log");
        if (File.Exists(walPath))
        {
            (long fileRecords, long fileBytes, long? fileBoundary) = VerifyWalFile(
                walPath,
                boundary,
                cancellationToken);
            records = checked(records + fileRecords);
            decodedBytes = checked(decodedBytes + fileBytes);
            boundary = fileBoundary ?? boundary;
        }

        return (records, decodedBytes, boundary);
    }

    private static (long Records, long Bytes, long? Boundary) VerifyWalFile(
        string walPath,
        long? precedingBoundary,
        CancellationToken cancellationToken)
    {
        using var stream = new FileStream(
            walPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        long records = 0;
        long decodedBytes = 0;
        long? boundary = precedingBoundary;
        Span<byte> header = stackalloc byte[8];
        while (stream.Position < stream.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!MidgeDiskFormat.ReadExactly(stream, header))
            {
                throw PantsException.Create(PantsErrorCode.Corruption, "The WAL has a torn frame header.");
            }

            uint length = BinaryPrimitives.ReadUInt32LittleEndian(header);
            if (length > MidgeDiskFormat.WalMaximumRecordBytes)
            {
                throw PantsException.Create(PantsErrorCode.Corruption, "A WAL frame exceeds the 64 MiB limit.");
            }

            byte[] payload = new byte[length];
            if (!MidgeDiskFormat.ReadExactly(stream, payload))
            {
                throw PantsException.Create(PantsErrorCode.Corruption, "The WAL has a torn frame payload.");
            }

            if (MidgeDiskFormat.Crc32C(payload) != BinaryPrimitives.ReadUInt32LittleEndian(header[4..]))
            {
                throw PantsException.Create(PantsErrorCode.Corruption, "A WAL frame checksum does not match.");
            }

            _ = MidgeWalCodec.DecodeTransactionBatch(payload, out ulong commitSequence);
            long decodedBoundary = checked((long)commitSequence);
            if (boundary.HasValue && decodedBoundary <= boundary.Value)
            {
                throw PantsException.Create(
                    PantsErrorCode.Corruption,
                    "WAL commit sequence boundaries are not strictly increasing.");
            }

            boundary = decodedBoundary;
            records++;
            decodedBytes = checked(decodedBytes + payload.Length);
        }

        return (records, decodedBytes, boundary);
    }

    private static void ValidateSealedWalName(string name)
    {
        ReadOnlySpan<char> stem = Path.GetFileNameWithoutExtension(name);
        if (stem.Length != 20 || stem.ContainsAnyExceptInRange('0', '9'))
        {
            throw PantsException.Create(
                PantsErrorCode.Corruption,
                $"Sealed WAL name '{name}' is invalid.");
        }
    }

    private static string ValidateFileName(string name)
    {
        if (string.IsNullOrEmpty(name) ||
            name != Path.GetFileName(name) ||
            !name.EndsWith(".sst", StringComparison.Ordinal) ||
            name.Contains(':'))
        {
            throw PantsException.Create(
                PantsErrorCode.Corruption,
                $"Manifest SST name '{name}' is unsafe.");
        }

        return name;
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;
using Cntryl.Pants.Storage.Internal.Sst;

namespace Cntryl.Pants.Storage.Internal;

static class StorageVerifier
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static async ValueTask<PantsStorageVerificationReport> VerifyPathAsync(
        string path,
        CancellationToken cancellationToken) =>
        await VerifyPathAsync(path, null, cancellationToken).ConfigureAwait(false);

    public static async ValueTask<PantsStorageVerificationReport> VerifyPathAsync(
        string path,
        IAsyncSstSourceFactory? remoteSstSourceFactory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        return await Task.Run(
            () => VerifyPath(path, remoteSstSourceFactory, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    static PantsStorageVerificationReport VerifyPath(
        string path,
        IAsyncSstSourceFactory? remoteSstSourceFactory,
        CancellationToken cancellationToken)
    {
        string root;
        try
        {
            root = StoragePathGuard.CanonicalizeRoot(path);
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

        StoragePathGuard.EnsureTreeHasNoLinks(root);

        cancellationToken.ThrowIfCancellationRequested();
        var formatPath = Path.Combine(root, "FORMAT");
        if (!File.Exists(formatPath) || File.ReadAllText(formatPath) != "midge-format-version=9\n")
        {
            throw PantsException.Create(
                PantsErrorCode.CompatibilityError,
                "The path does not contain a valid Midge FORMAT v9 marker.");
        }

        var manifestPath = File.Exists(Path.Combine(root, "manifest.snapshot.json"))
            ? Path.Combine(root, "manifest.snapshot.json")
            : Path.Combine(root, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            throw PantsException.Create(PantsErrorCode.Corruption, "The manifest snapshot is missing.");
        }

        ManifestState manifest;
        try
        {
            manifest = ManifestCodec.Decode(PositionalFile.ReadAllBytes(manifestPath));
            var journal = Path.Combine(root, "manifest.journal");
            if (File.Exists(journal))
            {
                LocalDiskStore.ReplayManifestMetadata(manifest, File.ReadAllBytes(journal));
            }
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
            if (remoteSstSourceFactory is not null)
            {
                var verified = VerifyRemoteSst(remoteSstSourceFactory, file, cancellationToken);
                bytesVerified = checked(bytesVerified + verified.Bytes);
                dataBlocksVerified = checked(dataBlocksVerified + verified.DataBlocks);
            }
            else
            {
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

                if (DiskFormat.Crc32C(bytes) != expectedCrc)
                {
                    throw PantsException.Create(
                        PantsErrorCode.Corruption,
                        $"Manifest SST '{safeName}' content checksum does not match.");
                }

                SstContents contents;
                try
                {
                    contents = SstCodec.Decode(bytes);
                    SstManifestMetadataValidator.Validate(contents, file, "Manifest SST");
                }
                catch (PantsException exception)
                {
                    throw PantsException.Create(
                        PantsErrorCode.Corruption,
                        $"Manifest SST '{safeName}' is structurally corrupt.",
                        exception);
                }

                bytesVerified = checked(bytesVerified + bytes.Length);
                dataBlocksVerified = checked(dataBlocksVerified + contents.DataBlockCount);
            }

            ownedSsts.Add(safeName);
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
                var decoded = IntentLogCodec.Read(File.ReadAllBytes(intentPath));
                using var document = IntentLogCodec.ToDocument(decoded.Entries);
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
        try
        {
            if (File.Exists(journalPath))
            {
                LocalDiskStore.ValidateManifestJournal(File.ReadAllBytes(journalPath));
            }
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
            manifest.EditCheckpointId,
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

    static (long Bytes, int DataBlocks) VerifyRemoteSst(
        IAsyncSstSourceFactory sourceFactory,
        FileMeta file,
        CancellationToken cancellationToken)
    {
        if (file.ContentCrc32C is not { } expectedCrc)
        {
            throw PantsException.Create(
                PantsErrorCode.Corruption,
                $"Manifest SST '{file.Name}' is missing its content checksum.");
        }

        IAsyncSstSource? source = sourceFactory.OpenAsync(file, cancellationToken)
            .AsTask().GetAwaiter().GetResult();
        if (source is null)
        {
            throw PantsException.Create(
                PantsErrorCode.Corruption,
                $"Manifest SST '{file.Name}' is missing.");
        }

        try
        {
            var length = source.Length;
            if (length < 0 || checked((ulong)length) != file.SizeBytes)
            {
                throw PantsException.Create(
                    PantsErrorCode.Corruption,
                    $"Manifest SST '{file.Name}' has an unexpected length.");
            }

            const int verificationRangeBytes = 64 * 1024;
            uint checksum = 0;
            for (long offset = 0; offset < length;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rangeLength = checked((int)Math.Min(verificationRangeBytes, length - offset));
                var bytes = source.ReadExactlyAsync(offset, rangeLength, cancellationToken)
                    .AsTask().GetAwaiter().GetResult();
                if (bytes.Length != rangeLength)
                {
                    throw PantsException.Create(
                        PantsErrorCode.Corruption,
                        $"Manifest SST '{file.Name}' returned a short verification range.");
                }

                checksum = DiskFormat.Crc32CAppend(checksum, bytes);
                offset += rangeLength;
            }

            if (checksum != expectedCrc)
            {
                throw PantsException.Create(
                    PantsErrorCode.Corruption,
                    $"Manifest SST '{file.Name}' content checksum does not match.");
            }

            var opening = AsyncSstReader.OpenAsync(source, file, cancellationToken).AsTask();
            source = null;
            var reader = opening.GetAwaiter().GetResult();
            try
            {
                byte[]? smallestKey = null;
                byte[]? largestKey = null;
                SstEntry? previousEntry = null;
                ulong? smallestSequence = null;
                ulong? largestSequence = null;

                void ObserveKey(byte[] key)
                {
                    if (smallestKey is null || ByteArrayComparer.Instance.Compare(key, smallestKey) < 0)
                    {
                        smallestKey = key;
                    }

                    if (largestKey is null || ByteArrayComparer.Instance.Compare(key, largestKey) > 0)
                    {
                        largestKey = key;
                    }
                }

                void ObserveSequence(ulong sequence)
                {
                    smallestSequence = smallestSequence is null
                        ? sequence
                        : Math.Min(smallestSequence.Value, sequence);
                    largestSequence = largestSequence is null
                        ? sequence
                        : Math.Max(largestSequence.Value, sequence);
                }

                foreach (var tombstone in reader.RangeTombstones)
                {
                    ObserveKey(tombstone.Start);
                    ObserveKey(tombstone.End);
                    ObserveSequence(tombstone.Sequence);
                }

                for (var blockIndex = 0; blockIndex < reader.DataBlockCount; blockIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var block = reader.ReadDataBlockAsync(blockIndex, cancellationToken)
                        .AsTask().GetAwaiter().GetResult();
                    var entries = SstCodec.DecodeDataBlock(block);
                    if (entries.Count == 0 ||
                        !entries[0].Key.AsSpan().SequenceEqual(reader.GetFirstKey(blockIndex)))
                    {
                        throw new PantsCorruptionException(
                            "SST index first key does not match its data block.");
                    }

                    foreach (var entry in entries)
                    {
                        if (previousEntry is not null)
                        {
                            SstCodec.ValidateAdjacentEntries(previousEntry, entry);
                        }

                        ObserveKey(entry.Key);
                        ObserveSequence(entry.Sequence);
                        previousEntry = entry;
                    }
                }

                SstManifestMetadataValidator.ValidateRemoteContents(
                    file,
                    reader.SmallestKey,
                    reader.LargestKey,
                    smallestKey,
                    largestKey,
                    smallestSequence,
                    largestSequence,
                    "Manifest SST");
                return (length, reader.DataBlockCount);
            }
            finally
            {
                reader.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
        finally
        {
            source?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    static (long Records, long Bytes, ulong? Boundary) VerifyWal(
        string root,
        CancellationToken cancellationToken)
    {
        var walDirectory = Path.Combine(root, "wal");
        if (!Directory.Exists(walDirectory))
        {
            return (0, 0, null);
        }

        var ledger = WalLedgerStore.LoadExisting(walDirectory);
        var replayPaths = ledger.Members.Select(member =>
            Path.Combine(walDirectory, $"{member.Open.Id:00000000000000000000}.wal")).ToArray();
        for (var index = 0; index < ledger.Members.Count; index++)
        {
            ValidateLedgerMember(replayPaths[index], ledger.Holder, ledger.Members[index], cancellationToken);
        }

        var records = 0L;
        var decodedBytes = 0L;
        ulong? boundary = null;
        var activePath = replayPaths[^1];
        var writerEpochFrontiers = DiscoverWriterEpochFrontiers(replayPaths, activePath, cancellationToken);
        var replayOrdinal = 0UL;
        var scratchDirectory = Path.Combine(Path.GetTempPath(), $"pants-verification-{Guid.NewGuid():N}");
        try
        {
            using var recovery = new WalRecoveryStateMachine(scratchDirectory);
            var recoveredVersions = new WalRecoveredVersionTracker();
            foreach (var replayPath in replayPaths)
            {
                var (fileRecords, fileBytes, fileBoundary) = VerifyWalFile(
                    replayPath,
                    StringComparer.Ordinal.Equals(replayPath, activePath)
                        ? WalTailPolicy.AllowIncompleteFinalTail
                        : WalTailPolicy.Strict,
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
        finally
        {
            if (Directory.Exists(scratchDirectory))
            {
                Directory.Delete(scratchDirectory, recursive: true);
            }
        }
    }

    internal static void ValidateLedgerMember(string path, Guid holder, WalLedgerMember member,
        CancellationToken cancellationToken) => ValidateLedgerMember(path, holder, member, null, cancellationToken);

    internal static void ValidateLedgerMember(string path, Guid holder, WalLedgerMember member,
        long? maximumReplayableTransactionBytes, CancellationToken cancellationToken)
    {
        try
        {
            ValidateLedgerMemberCore(path, holder, member, maximumReplayableTransactionBytes, cancellationToken);
        }
        catch (StorageException exception)
        {
            throw new PantsCorruptionException("Ledger-owned WAL bytes are corrupt.", exception);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new PantsCorruptionException($"WAL ledger member '{Path.GetFileName(path)}' is missing.", exception);
        }
    }

    static void ValidateLedgerMemberCore(string path, Guid holder, WalLedgerMember member,
        long? maximumReplayableTransactionBytes, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var header = new byte[WalSegmentCodec.HeaderSize];
        if (!DiskFormat.ReadExactly(stream, header))
        {
            throw new PantsCorruptionException("WAL ledger member header is truncated.");
        }

        var (open, actualHolder) = WalSegmentCodec.DecodeHeader(header);
        if (actualHolder != holder || open.Id != member.Open.Id || open.Epoch != member.Open.Epoch ||
            open.FirstSeq != member.Open.FirstSeq || !open.Prev.Matches(member.Open.Prev) ||
            member.DurableBytes > (ulong)stream.Length)
        {
            throw new PantsCorruptionException("WAL segment identity or durable boundary differs from its ledger.");
        }

        stream.Position = 0;
        if (member.Sealed is null)
        {
            if (member.DurableBytes is { } durableBytes)
            {
                WalFrameReader.ValidateDurablePrefix(stream, checked((long)durableBytes), maximumReplayableTransactionBytes, cancellationToken);
            }

            return;
        }

        var facts = member.Sealed;
        var decoded = WalFrameReader.ScanSegment(stream, maximumReplayableTransactionBytes, cancellationToken).Footer
            ?? throw new PantsCorruptionException("A sealed WAL ledger member has no complete footer.");
        if (!facts.Link.Matches(decoded.Link) || facts.RecordCount != decoded.RecordCount ||
            facts.DataLen != decoded.DataLen || facts.MaxSeqPerCf.Length != decoded.MaxSeqPerCf.Length ||
            facts.MaxSeqPerCf.Where((pair, index) => !pair.AsSpan().SequenceEqual(decoded.MaxSeqPerCf[index])).Any())
        {
            throw new PantsCorruptionException("Sealed WAL facts differ from their authoritative ledger.");
        }
    }

    static WalWriterEpochFrontiers DiscoverWriterEpochFrontiers(
        IReadOnlyList<string> replayPaths,
        string activePath,
        CancellationToken cancellationToken)
    {
        var frontiers = new WalWriterEpochFrontiers();
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
                WalFrameReader.Visit(
                    stream,
                    (record, _) =>
                    {
                        frontiers.Record(record, ordinal);
                        if (ordinal != ulong.MaxValue)
                        {
                            ordinal++;
                        }
                    },
                    StringComparer.Ordinal.Equals(replayPath, activePath)
                        ? WalTailPolicy.AllowIncompleteFinalTail
                        : WalTailPolicy.Strict,
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

    static (long Records, long Bytes, ulong? Boundary) VerifyWalFile(
        string walPath,
        WalTailPolicy tailPolicy,
        ulong? precedingBoundary,
        WalRecoveryStateMachine recovery,
        WalRecoveredVersionTracker recoveredVersions,
        WalWriterEpochFrontiers writerEpochFrontiers,
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
            WalFrameReader.Visit(
                stream,
                (record, frameBytes) =>
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

                    var decodedBoundary = record.Sequence;
                    var applicableMutations = new List<WalMutation>();
                    recovery.Accept(
                        record,
                        (mutation, commitSequence) => applicableMutations.Add(mutation));
                    _ = recoveredVersions.ValidateAndRecord(applicableMutations, Path.GetFileName(walPath));
                    boundary = boundary.HasValue
                        ? Math.Max(boundary.Value, decodedBoundary)
                        : decodedBoundary;
                    records++;
                    decodedBytes = checked(
                        decodedBytes + WalRecordMetrics.GetLogicalByteCount(record));
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

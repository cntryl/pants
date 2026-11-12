namespace Cntryl.Pants.Cloud.Internal;

/// <summary>Only an empty, verified interrupted initialization may survive a nil committed pointer.</summary>
static class CloudInitializationCacheValidator
{
    public static void Validate(string root)
    {
        StoragePathGuard.EnsureTreeHasNoLinks(root);
        var formatPath = Path.Combine(root, "FORMAT");
        if (File.Exists(formatPath) && !LocalDiskStore.IsSupportedFormatMarker(File.ReadAllBytes(formatPath)))
        {
            throw new PantsCompatibilityException("An interrupted cloud initialization has an incompatible local FORMAT marker.");
        }

        var manifest = CloudManifestReader.ReadManifest(root);
        if (manifest is not null && (manifest.Files.Count != 0 || manifest.ColumnFamilies.Count != 0 ||
            manifest.RetiredSsts.Count != 0 || manifest.EditCheckpointId != 0 ||
            manifest.FlushedThrough.Any(static pair => pair.Value != 0)))
        {
            throw new PantsCorruptionException("A nil committed cloud pointer cannot authorize a nonempty local manifest.");
        }

        var sstDirectory = Path.Combine(root, "sst");
        if (Directory.Exists(sstDirectory) && Directory.EnumerateFiles(sstDirectory, "*.sst").Any())
        {
            throw new PantsCorruptionException("A nil committed cloud pointer cannot authorize local SST files.");
        }

        var intentPath = Path.Combine(root, "intent_log.json");
        if (File.Exists(intentPath))
        {
            var bytes = File.ReadAllBytes(intentPath);
            var intent = IntentLogCodec.Read(bytes);
            if (intent.Entries.Count != 0 || intent.ValidLength != bytes.Length)
            {
                throw new PantsCorruptionException("A nil committed cloud pointer cannot authorize local intent history.");
            }
        }

        var walDirectory = Path.Combine(root, "wal");
        if (!Directory.Exists(walDirectory))
        {
            return;
        }

        if (File.Exists(Path.Combine(walDirectory, "LEDGER")))
        {
            var ledger = WalLedgerStore.LoadExisting(walDirectory);
            if (ledger.ReservedThrough != 0 || !ledger.Anchor.Matches(WalChainLink.Genesis) ||
                ledger.Members.Any(static member => member.Open.Prev.Id != 0 || member.Open.Prev.LastSeq != 0 ||
                    member.Sealed is { RecordCount: > 0 }))
            {
                throw new PantsCorruptionException("A nil committed cloud pointer cannot authorize allocated local WAL history.");
            }
        }

        Span<byte> header = stackalloc byte[WalSegmentCodec.HeaderSize];
        foreach (var path in Directory.EnumerateFiles(walDirectory, "*.wal"))
        {
            using var stream = File.OpenRead(path);
            if (!DiskFormat.ReadExactly(stream, header))
            {
                throw new PantsCorruptionException("An interrupted cloud initialization has an incomplete WAL header.");
            }

            _ = WalSegmentCodec.DecodeHeader(header);
            stream.Position = 0;
            WalFrameReader.Visit(stream, static (_, _) =>
                throw new PantsCorruptionException("A nil committed cloud pointer cannot authorize local WAL records."));
        }

        if (File.Exists(Path.Combine(walDirectory, "wal.log")))
        {
            throw new PantsCompatibilityException("An interrupted cloud initialization contains legacy WAL history.");
        }
    }
}

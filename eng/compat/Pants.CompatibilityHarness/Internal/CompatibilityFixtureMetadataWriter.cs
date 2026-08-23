using System.Security.Cryptography;
using System.Text.Json;

namespace Pants.CompatibilityHarness.Internal;

internal static class CompatibilityFixtureMetadataWriter
{
    const string SemanticJournalRationale =
        "Midge fsync markers embed wall-clock milliseconds; Pants validates the framed edit "
        + "and marker semantics rather than reproducing time-dependent bytes.";
    const string SemanticLocalLeaseRationale =
        "The holder identity and acquisition timestamp are process- and host-dependent.";
    const string SemanticCloudLeaseRationale =
        "The holder identity, owner token, and timestamps are generated for each lease acquisition.";
    const string SemanticDdlRationale =
        "DDL operation identifiers and creation timestamps are generated for each operation.";

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static void Write(string fixtureRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fixtureRoot);

        var sha = MidgeCheckoutBuilder.RequiredCommit;
        var artifacts = new List<CompatibilityFixtureArtifact>();
        Add(artifacts, fixtureRoot, "format-v3", "format-v3", "midge", "exact", $"Wire/{sha}/FORMAT");
        Add(artifacts, fixtureRoot, "wal-tlv-put", "wal-tlv", "midge", "exact", $"Wire/{sha}/wal-tlv-put-v1.bin");
        Add(artifacts, fixtureRoot, "wal-tlv-insert", "wal-tlv", "midge", "exact", $"Wire/{sha}/wal-tlv-insert-v1.bin");
        Add(artifacts, fixtureRoot, "wal-tlv-delete", "wal-tlv", "midge", "exact", $"Wire/{sha}/wal-tlv-delete-v1.bin");
        Add(artifacts, fixtureRoot, "wal-tlv-delete-range", "wal-tlv", "midge", "exact", $"Wire/{sha}/wal-tlv-delete-range-v1.bin");
        Add(artifacts, fixtureRoot, "wal-tlv-empty-value", "wal-tlv", "midge", "exact", $"Wire/{sha}/wal-tlv-empty-value-v1.bin");
        Add(artifacts, fixtureRoot, "wal-transaction-batch", "wal-transaction-batch", "midge", "exact", $"Wire/{sha}/wal-txn-batch-v1.bin");
        Add(artifacts, fixtureRoot, "wal-frame-put", "wal-frame", "midge", "exact", $"Wire/{sha}/wal-frame-put-v1.bin");
        Add(artifacts, fixtureRoot, "wal-active", "wal-active-segment", "midge", "exact", $"Storage/{sha}/wal/active/wal.log");
        Add(artifacts, fixtureRoot, "wal-sealed", "wal-sealed-epoch-segment", "midge", "exact", $"Storage/{sha}/wal/sealed/00000000000000000001.wal");
        Add(artifacts, fixtureRoot, "sst-block-raw", "sst-v4-raw-block", "midge", "exact", $"Wire/{sha}/sst-block-none-v1.bin");
        Add(artifacts, fixtureRoot, "sst-block-lz4", "sst-v4-lz4-block", "midge", "exact", $"Wire/{sha}/sst-block-lz4-v1.bin");
        Add(artifacts, fixtureRoot, "sst-block-zstd3", "sst-v4-zstd3-block", "midge", "exact", $"Wire/{sha}/sst-block-zstd3-v1.bin");
        Add(artifacts, fixtureRoot, "sst-block-zstd9", "sst-v4-zstd9-block", "midge", "exact", $"Wire/{sha}/sst-block-zstd9-v1.bin");
        Add(artifacts, fixtureRoot, "sst-block-input", "sst-compression-input", "midge", "exact", $"Wire/{sha}/sst-block-input-v1.bin");
        AddSstStructures(artifacts, fixtureRoot, sha);
        Add(artifacts, fixtureRoot, "manifest-snapshot", "manifest-snapshot", "midge", "exact", $"Storage/{sha}/metadata/manifest.snapshot.json");
        Add(artifacts, fixtureRoot, "manifest-journal", "manifest-journal", "midge", "semantic", $"Storage/{sha}/metadata/manifest.journal", SemanticJournalRationale);
        Add(artifacts, fixtureRoot, "intent-log", "intent-log", "midge", "exact", $"Storage/{sha}/metadata/intent_log.json");
        Add(artifacts, fixtureRoot, "local-lease", "local-lease", "midge", "semantic", $"Storage/{sha}/leases/local.midge_leader", SemanticLocalLeaseRationale);
        Add(artifacts, fixtureRoot, "cloud-lease", "cloud-lease", "midge", "semantic", $"Storage/{sha}/leases/cloud.midge_primary_lease", SemanticCloudLeaseRationale);
        Add(artifacts, fixtureRoot, "ddl-registry", "ddl-registry", "midge", "semantic", $"Storage/{sha}/ddl/ddl.registry.json", SemanticDdlRationale);
        Add(artifacts, fixtureRoot, "ddl-prepare", "ddl-prepare", "midge", "semantic", $"Storage/{sha}/ddl/ddl.prepare.json", SemanticDdlRationale);
        Add(artifacts, fixtureRoot, "cloud-wal-catalog", "cloud-wal-catalog", "midge", "exact", $"Storage/{sha}/cloud/publication-catalog.v1.json");
        Add(artifacts, fixtureRoot, "cloud-object-keys", "cloud-object-keys", "midge", "exact", $"Wire/{sha}/cloud-object-keys-v1.txt");
        Add(artifacts, fixtureRoot, "generated-database-tree", "generated-database-tree", "canonical", "exact", $"Storage/{sha}/databases/midge-structured-v4-db.fixture.json");
        Add(artifacts, fixtureRoot, "midge-driver-cargo-lock", "midge-driver-dependency-lock", "canonical", "exact", $"Tooling/{sha}/Cargo.lock");

        var metadata = new CompatibilityFixtureMetadata(1, sha, artifacts);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(metadata, JsonOptions);
        File.WriteAllBytes(Path.Combine(fixtureRoot, "fixture-metadata.json"), [.. bytes, (byte)'\n']);
    }

    static void AddSstStructures(
        List<CompatibilityFixtureArtifact> artifacts,
        string fixtureRoot,
        string sha)
    {
        var path = $"Storage/{sha}/sst/structured-v4.sst";
        foreach (var structure in new[]
                 {
                     "sst-v4-index",
                     "sst-v4-bloom",
                     "sst-v4-trie",
                     "sst-v4-range-tombstone",
                     "sst-v4-footer"
                 })
        {
            Add(artifacts, fixtureRoot, structure, structure, "midge", "exact", path);
        }
    }

    static void Add(
        List<CompatibilityFixtureArtifact> artifacts,
        string fixtureRoot,
        string id,
        string structure,
        string producer,
        string coverage,
        string relativePath,
        string? rationale = null)
    {
        var fullPath = Path.Combine(
            fixtureRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException(
                $"Required compatibility fixture '{relativePath}' was not emitted.",
                fullPath);
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(fullPath)));
        artifacts.Add(new CompatibilityFixtureArtifact(
            id,
            structure,
            producer,
            coverage,
            relativePath,
            hash,
            rationale));
    }
}

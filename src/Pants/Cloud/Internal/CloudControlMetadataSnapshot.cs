using System.Collections.Frozen;

namespace Cntryl.Pants.Cloud.Internal;

sealed class CloudControlMetadataSnapshot
{
    CloudControlMetadataSnapshot(
        FrozenDictionary<string, ReadOnlyMemory<byte>> files,
        MidgeManifest[] manifests,
        MidgeManifest? authoritativeManifest)
    {
        Files = files;
        Manifests = manifests;
        AuthoritativeManifest = authoritativeManifest;
        ReferencedSsts = manifests
            .SelectMany(static manifest => manifest.Files)
            .Select(static file => file.Clone())
            .ToArray();
        MaximumManifestSequence = manifests.Length == 0
            ? 0
            : manifests.Max(static manifest => manifest.LastPersistedSequence);
    }

    public FrozenDictionary<string, ReadOnlyMemory<byte>> Files { get; }

    public MidgeManifest[] Manifests { get; }

    public MidgeManifest? AuthoritativeManifest { get; }

    public MidgeFileMeta[] ReferencedSsts { get; }

    public ulong MaximumManifestSequence { get; }

    public static CloudControlMetadataSnapshot Capture(string root, string[] fileNames)
    {
        var files = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
        foreach (var fileName in fileNames)
        {
            var path = Path.Combine(root, fileName);
            if (File.Exists(path))
            {
                files.Add(fileName, File.ReadAllBytes(path));
            }
        }

        var manifests = new List<MidgeManifest>(2);
        var referencedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fileName in new[] { "manifest.snapshot.json", "manifest.json" })
        {
            if (files.TryGetValue(fileName, out var bytes))
            {
                var manifest = CloudManifestReader.DecodeManifest(bytes.Span);
                CloudSstReferenceReader.AddManifestNames(manifest, referencedNames);
                manifests.Add(manifest);
            }
        }

        var authoritativeManifest = manifests.FirstOrDefault();
        return new CloudControlMetadataSnapshot(
            files.ToFrozenDictionary(StringComparer.Ordinal),
            manifests.ToArray(),
            authoritativeManifest);
    }
}

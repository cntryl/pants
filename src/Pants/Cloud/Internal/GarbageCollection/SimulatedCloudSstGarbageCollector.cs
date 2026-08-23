using System.Security.Cryptography;

namespace Cntryl.Pants.Cloud.Internal.GarbageCollection;

static class SimulatedCloudSstGarbageCollector
{
    static readonly string[] ManifestFileNames =
    [
        "manifest.snapshot.json",
        "manifest.json"
    ];

    public static bool Collect(
        string localRoot,
        string cloudRoot,
        IPantsFailpointHandler failpoints)
    {
        try
        {
            var proof = CaptureProof(localRoot, cloudRoot);
            var cloudSstDirectory = Path.Combine(cloudRoot, "sst");
            Directory.CreateDirectory(cloudSstDirectory);
            foreach (var path in Directory.EnumerateFiles(
                         cloudSstDirectory,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                var objectKey = PantsCloudObjectLayout.SstPrefix + Path.GetFileName(path);
                if (!CloudSstObjectKey.TryGetName(objectKey, out var name))
                {
                    return false;
                }

                if (proof.ProtectedNames.Contains(name) ||
                    CloudSstReferenceReader.ReadLocalProtectedNames(localRoot).Contains(name))
                {
                    continue;
                }

                var expectedVersion = CreateVersion(File.ReadAllBytes(path));
                if (!VerifyProof(proof) ||
                    CloudSstReferenceReader.ReadLocalProtectedNames(localRoot).Contains(name))
                {
                    return false;
                }

                failpoints.Hit(PantsFailpoint.BeforeCloudSstGarbageCollectionDelete);
                if (!QuarantineAndDelete(path, expectedVersion))
                {
                    return false;
                }
            }

            return true;
        }
        catch (PantsRecoveryFailedException)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    static (IReadOnlySet<string> ProtectedNames, IReadOnlyList<SimulatedCloudObjectGuard> Guards)
        CaptureProof(string localRoot, string cloudRoot)
    {
        var names = new HashSet<string>(
            CloudSstReferenceReader.ReadLocalProtectedNames(localRoot),
            StringComparer.Ordinal);
        var guards = new List<SimulatedCloudObjectGuard>();
        foreach (var fileName in ManifestFileNames)
        {
            var path = Path.Combine(cloudRoot, "metadata", fileName);
            if (!File.Exists(path))
            {
                continue;
            }

            var bytes = File.ReadAllBytes(path);
            CloudSstReferenceReader.AddManifestNames(
                CloudManifestReader.DecodeManifest(bytes),
                names);
            guards.Add(new SimulatedCloudObjectGuard(path, CreateVersion(bytes)));
        }

        return (names, guards);
    }

    static bool VerifyProof(
        (IReadOnlySet<string> ProtectedNames, IReadOnlyList<SimulatedCloudObjectGuard> Guards) proof)
    {
        foreach (var guard in proof.Guards)
        {
            if (!File.Exists(guard.Path) ||
                !StringComparer.Ordinal.Equals(
                    CreateVersion(File.ReadAllBytes(guard.Path)),
                    guard.Version))
            {
                return false;
            }
        }

        return true;
    }

    static bool QuarantineAndDelete(string path, string expectedVersion)
    {
        var quarantinePath = $"{path}.gc-{Guid.NewGuid():N}";
        try
        {
            File.Move(path, quarantinePath, false);
            if (!StringComparer.Ordinal.Equals(
                    CreateVersion(File.ReadAllBytes(quarantinePath)),
                    expectedVersion))
            {
                RestoreQuarantine(path, quarantinePath);
                throw new PantsRecoveryFailedException(
                    $"Simulated-cloud SST garbage collection proof for '{path}' became stale before deletion.");
            }

            File.Delete(quarantinePath);
            return true;
        }
        catch (IOException)
        {
            RestoreQuarantine(path, quarantinePath);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            RestoreQuarantine(path, quarantinePath);
            return false;
        }
    }

    static void RestoreQuarantine(string path, string quarantinePath)
    {
        if (!File.Exists(quarantinePath) || File.Exists(path))
        {
            return;
        }

        try
        {
            File.Move(quarantinePath, path, false);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    static string CreateVersion(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));
}

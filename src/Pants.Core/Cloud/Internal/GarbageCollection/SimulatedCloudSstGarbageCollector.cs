using System.Security.Cryptography;

namespace Cntryl.Pants.Cloud.Internal.GarbageCollection;

static class SimulatedCloudSstGarbageCollector
{
    public static bool Collect(
        string localRoot,
        string cloudRoot,
        IFailpointHandler failpoints,
        Func<IReadOnlyDictionary<string, FileMeta>>? readDeletionCandidates = null,
        Action<FileMeta>? acknowledgeDeletion = null,
        Action? ensureAuthority = null)
    {
        try
        {
            ensureAuthority?.Invoke();
            var proof = CaptureProof(localRoot, cloudRoot, readDeletionCandidates?.Invoke(), acknowledgeDeletion is not null);
            var cloudSstDirectory = Path.Combine(cloudRoot, "sst");
            Directory.CreateDirectory(cloudSstDirectory);
            ReconcileQuarantines(cloudSstDirectory);
            var encounteredAnomaly = false;
            foreach (var retired in proof.ReleasedRetired.Values)
            {
                var path = Path.Combine(cloudSstDirectory, retired.Name);
                if (!File.Exists(path) && !CloudSstReferenceReader.ReadLocalProtectedNames(localRoot).Contains(retired.Name))
                {
                    ensureAuthority?.Invoke();
                    if (!VerifyProof(proof))
                    {
                        return false;
                    }

                    AtomicStagedFile.FlushDirectory(cloudSstDirectory);
                    acknowledgeDeletion!(retired);
                }
            }

            foreach (var path in Directory.EnumerateFiles(
                         cloudSstDirectory,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                var objectKey = PantsCloudObjectLayout.SstPrefix + Path.GetFileName(path);
                if (!CloudSstObjectKey.TryGetName(objectKey, out var name))
                {
                    encounteredAnomaly = true;
                    continue;
                }

                if (proof.ProtectedNames.Contains(name) ||
                    CloudSstReferenceReader.ReadLocalProtectedNames(localRoot).Contains(name))
                {
                    continue;
                }

                var bytes = File.ReadAllBytes(path);
                proof.ReleasedRetired.TryGetValue(name, out var retired);
                if (retired is not null && (!retired.ContentCrc32C.HasValue || retired.SizeBytes != (ulong)bytes.Length || retired.ContentCrc32C.Value != DiskFormat.Crc32C(bytes)))
                {
                    encounteredAnomaly = true;
                    continue;
                }

                var expectedVersion = CreateVersion(bytes);
                if (!VerifyProof(proof) ||
                    CloudSstReferenceReader.ReadLocalProtectedNames(localRoot).Contains(name))
                {
                    return false;
                }

                failpoints.Hit(Failpoint.BeforeCloudSstGarbageCollectionDelete);
                ensureAuthority?.Invoke();
                if (!VerifyProof(proof) || !QuarantineAndDelete(path, expectedVersion))
                {
                    return false;
                }

                if (retired is not null)
                {
                    AtomicStagedFile.FlushDirectory(cloudSstDirectory);
                    acknowledgeDeletion!(retired);
                }
            }

            return !encounteredAnomaly;
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

    static void ReconcileQuarantines(string cloudSstDirectory)
    {
        foreach (var quarantinePath in Directory.EnumerateFiles(
                     cloudSstDirectory,
                     "*.gc-*",
                     SearchOption.TopDirectoryOnly))
        {
            var marker = quarantinePath.LastIndexOf(".gc-", StringComparison.Ordinal);
            if (marker < 0)
            {
                continue;
            }

            var originalPath = quarantinePath[..marker];
            if (File.Exists(originalPath))
            {
                File.Delete(quarantinePath);
            }
            else
            {
                File.Move(quarantinePath, originalPath, false);
            }
        }
    }

    static (IReadOnlySet<string> ProtectedNames, IReadOnlyList<SimulatedCloudObjectGuard> Guards, IReadOnlyDictionary<string, FileMeta> ReleasedRetired)
        CaptureProof(string localRoot, string cloudRoot, IReadOnlyDictionary<string, FileMeta>? candidates, bool canAcknowledge)
    {
        var names = new HashSet<string>(
            CloudSstReferenceReader.ReadLocalProtectedNames(localRoot),
            StringComparer.Ordinal);
        var guards = new List<SimulatedCloudObjectGuard>();
        var pointerPath = Path.Combine(cloudRoot, CloudMetadataAuthority.PointerKey.Replace('/', Path.DirectorySeparatorChar));
        var pointerBytes = File.ReadAllBytes(pointerPath);
        var selected = CloudMetadataAuthority.ReadCommittedFilesAsync(new SimulatedCloudObjectStore(cloudRoot), CancellationToken.None)
            .AsTask().GetAwaiter().GetResult() ?? throw new PantsRecoveryFailedException("Simulated cloud has no committed metadata authority.");
        var manifest = CloudManifestReader.DecodeMetadata(selected);
        CloudSstReferenceReader.AddManifestNames(manifest, names);
        var released = new Dictionary<string, FileMeta>(StringComparer.Ordinal);
        foreach (var retired in manifest.RetiredSsts)
        {
            if (canAcknowledge && candidates?.TryGetValue(retired.Key, out var candidate) == true && ManifestTransitions.SameIdentity(retired.Value, candidate))
            {
                released.Add(retired.Key, retired.Value.Clone());
            }
            else
            {
                names.Add(retired.Key);
            }
        }
        if (selected.TryGetValue("intent_log.json", out var intentBytes))
        {
            CloudSstReferenceReader.AddIntentNames(intentBytes, names);
        }

        guards.Add(new SimulatedCloudObjectGuard(pointerPath, CreateVersion(pointerBytes)));

        return (names, guards, released);
    }

    static bool VerifyProof(
        (IReadOnlySet<string> ProtectedNames, IReadOnlyList<SimulatedCloudObjectGuard> Guards, IReadOnlyDictionary<string, FileMeta> ReleasedRetired) proof)
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

using System.Collections.Frozen;
using Cntryl.Pants.Storage.Internal;
using Cntryl.Pants.Storage.Internal.IO;
using Microsoft.Win32.SafeHandles;

namespace Cntryl.Pants.Cloud.Internal;

sealed class CloudControlMetadataSnapshot : IDisposable
{
    const int WorkspaceMultiplier = 16;
    const long FixedWorkspaceBytes = 256;

    IDisposable? _reservation;

    CloudControlMetadataSnapshot(
        FrozenDictionary<string, ReadOnlyMemory<byte>> files,
        ManifestState[] manifests,
        ManifestState? authoritativeManifest,
        IDisposable reservation)
    {
        _reservation = reservation;
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

    public ManifestState[] Manifests { get; }

    public ManifestState? AuthoritativeManifest { get; }

    public FileMeta[] ReferencedSsts { get; }

    public ulong MaximumManifestSequence { get; }

    public void Dispose()
    {
        Interlocked.Exchange(ref _reservation, null)?.Dispose();
        GC.SuppressFinalize(this);
    }

    public static CloudControlMetadataSnapshot Capture(
        string root,
        string[] fileNames,
        ResourceBudget budget)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(fileNames);
        ArgumentNullException.ThrowIfNull(budget);

        var openedFiles = new List<(string Name, SafeFileHandle Handle, int Length)>(fileNames.Length);
        IDisposable? reservation = null;
        try
        {
            long totalBytes = 0;
            foreach (var fileName in fileNames)
            {
                var path = Path.Combine(root, fileName);
                if (!File.Exists(path))
                {
                    continue;
                }

                StoragePathGuard.EnsureNotLink(path);
                SafeFileHandle? handle = File.OpenHandle(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    FileOptions.RandomAccess);
                try
                {
                    var length = RandomAccess.GetLength(handle);
                    if (length > Array.MaxLength)
                    {
                        throw new PantsResourceLimitException(
                            $"Cloud control metadata file '{fileName}' exceeds the supported byte-array size.");
                    }

                    totalBytes = checked(totalBytes + length);
                    openedFiles.Add((fileName, handle, checked((int)length)));
                    handle = null;
                }
                finally
                {
                    handle?.Dispose();
                }
            }

            reservation = budget.Reserve(GetWorkspaceBytes(totalBytes));

            var files = new Dictionary<string, ReadOnlyMemory<byte>>(openedFiles.Count, StringComparer.Ordinal);
            foreach (var (name, handle, length) in openedFiles)
            {
                files.Add(name, PositionalFile.ReadExactly(handle, 0, length));
            }

            var manifests = new List<ManifestState>(2);
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
            var snapshot = new CloudControlMetadataSnapshot(
                files.ToFrozenDictionary(StringComparer.Ordinal),
                manifests.ToArray(),
                authoritativeManifest,
                reservation);
            reservation = null;
            return snapshot;
        }
        catch (OverflowException exception)
        {
            throw new PantsResourceLimitException(
                "Cloud control metadata snapshot exceeds the maintenance memory accounting range.",
                exception);
        }
        finally
        {
            reservation?.Dispose();
            foreach (var (_, handle, _) in openedFiles)
            {
                handle.Dispose();
            }
        }
    }

    static long GetWorkspaceBytes(long encodedBytes)
    {
        try
        {
            return checked((encodedBytes * WorkspaceMultiplier) + FixedWorkspaceBytes);
        }
        catch (OverflowException exception)
        {
            throw new PantsResourceLimitException(
                "Cloud control metadata snapshot exceeds the maintenance memory accounting range.",
                exception);
        }
    }
}

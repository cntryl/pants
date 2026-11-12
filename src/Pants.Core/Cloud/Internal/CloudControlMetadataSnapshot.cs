using System.Collections.Frozen;
using System.Text.Json;
using Cntryl.Pants.Storage.Internal;
using Cntryl.Pants.Storage.Internal.Accounting;
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
        IDisposable reservation,
        MetadataAccountingHandle? bootstrapAccounting = null)
    {
        _reservation = reservation;
        BootstrapAccounting = bootstrapAccounting;
        Files = files;
        Manifests = manifests;
        AuthoritativeManifest = authoritativeManifest;
        ReferencedSsts = manifests
            .SelectMany(static manifest => manifest.Files)
            .Select(static file => file.Clone())
            .ToArray();
        MaximumManifestSequence = manifests.Length == 0
            ? 0
            : manifests.Max(static manifest => manifest.EditCheckpointId);
    }

    public FrozenDictionary<string, ReadOnlyMemory<byte>> Files { get; }

    public ManifestState[] Manifests { get; }

    public ManifestState? AuthoritativeManifest { get; }

    public FileMeta[] ReferencedSsts { get; }

    public ulong MaximumManifestSequence { get; }

    public MetadataAccountingHandle? BootstrapAccounting { get; }

    public void Dispose()
    {
        Interlocked.Exchange(ref _reservation, null)?.Dispose();
        GC.SuppressFinalize(this);
    }

    public static CloudControlMetadataSnapshot CreateBootstrapCandidate(ResourceBudget budget) =>
        CreateBootstrapCandidate(budget, out _);

    internal static CloudControlMetadataSnapshot CreateBootstrapCandidate(ResourceBudget budget,
        out MetadataAccountingHandle accounting)
    {
        // FORMAT 9's empty manifest has a fixed, small shape. Admit its complete encoded/decoded
        // envelope before creating any payload, independently of the later persistent runtime.
        const int maximumEncodedBytes = 1024;
        var owner = new MetadataAccountingOwner();
        accounting = owner.Handle;
        using var checkpoint = owner.Begin(MetadataOperationKind.Checkpoint, MetadataOrigin.Bootstrap,
            MetadataMedium.MemoryOnly);
        IDisposable? reservation = null;
        try
        {
            reservation = budget.Reserve(GetWorkspaceBytes(maximumEncodedBytes));
            var manifest = ManifestState.CreateInitial();
            var encoded = ManifestCodec.Encode(manifest,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
            var format = "midge-format-version=9\n"u8.ToArray();
            if (encoded.Length + format.Length > maximumEncodedBytes)
            {
                throw new PantsResourceLimitException("The empty bootstrap manifest exceeds its fixed admission envelope.");
            }

            using var formatWrite = owner.Begin(MetadataOperationKind.OtherMetadata, MetadataOrigin.Bootstrap,
                MetadataMedium.MemoryOnly);
            format = StageMemoryPayload(format, formatWrite.Observe(MetadataPayload.OtherMetadata));
            formatWrite.Finish(true);
            encoded = StageMemoryPayload(encoded, checkpoint.Observe(MetadataPayload.Snapshot));

            var files = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal)
            {
                ["FORMAT"] = format,
                ["manifest.snapshot.json"] = encoded,
                ["manifest.journal"] = ReadOnlyMemory<byte>.Empty
            };
            var candidate = new CloudControlMetadataSnapshot(files.ToFrozenDictionary(StringComparer.Ordinal),
                [manifest], manifest, reservation, accounting);
            // This checkpoint installs only volatile immutable memory. No native filesystem
            // barrier or Persistent cost is claimed by its independent MemoryOnly owner.
            checkpoint.SnapshotDurable(checked((ulong)encoded.Length));
            checkpoint.CheckpointComplete();
            checkpoint.Finish(true);
            reservation = null;
            return candidate;
        }
        catch
        {
            checkpoint.Finish(false);
            throw;
        }
        finally
        {
            reservation?.Dispose();
        }
    }

    static byte[] StageMemoryPayload(ReadOnlySpan<byte> bytes, MetadataIoObservation observation)
    {
        using var staging = new MemoryStream(bytes.Length);
        observation.Issued(checked((ulong)bytes.Length));
        try
        {
            staging.Write(bytes);
            observation.Returned(checked((ulong)bytes.Length));
        }
        catch
        {
            observation.Mutation();
            throw;
        }
        return staging.ToArray();
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

            var manifests = new List<ManifestState>(1);
            if (files.ContainsKey("manifest.snapshot.json"))
            {
                manifests.Add(CloudManifestReader.DecodeMetadata(files));
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

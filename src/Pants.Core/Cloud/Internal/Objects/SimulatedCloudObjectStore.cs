using System.Security.Cryptography;
using System.Text;
using Cntryl.Pants.Storage.Internal;
using Cntryl.Pants.Storage.Internal.IO;

namespace Cntryl.Pants.Cloud.Internal.Objects;

/// <summary>Conditional filesystem object observations for the simulated cloud authority.</summary>
sealed class SimulatedCloudObjectStore : CloudObjectStore
{
    static readonly object[] Gates = Enumerable.Range(0, 64).Select(static _ => new object()).ToArray();
    readonly string _root;
    readonly object _gate;
    readonly Action? _onProcessLockContention;

    public SimulatedCloudObjectStore(string root, Action? onProcessLockContention = null)
    {
        _root = Path.GetFullPath(root);
        _gate = Gates[DiskFormat.Crc32C(Encoding.UTF8.GetBytes(_root)) % 64];
        _onProcessLockContention = onProcessLockContention;
    }

    public override ValueTask<CloudObject?> GetAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var path = Resolve(objectKey);
            using var processLock = SimulatedCloudMutationLock.Acquire(
                _root, path, cancellationToken, _onProcessLockContention);
            if (!File.Exists(path))
            {
                return ValueTask.FromResult<CloudObject?>(null);
            }

            var bytes = File.ReadAllBytes(path);
            return ValueTask.FromResult<CloudObject?>(new CloudObject(bytes, Version(bytes)));
        }
    }

    internal CloudObject? ReadControlMetadata(
        string objectKey,
        Action<ulong> admit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var path = Resolve(objectKey);
            using var processLock = SimulatedCloudMutationLock.Acquire(
                _root, path, cancellationToken, _onProcessLockContention);
            if (!File.Exists(path))
            {
                return null;
            }

            using var stream = File.OpenRead(path);
            var sizeBytes = stream.Length;
            admit(checked((ulong)sizeBytes));
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = GC.AllocateUninitializedArray<byte>(checked((int)sizeBytes));
            for (var offset = 0; offset < bytes.Length;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var length = Math.Min(ProviderCloudControlMetadataReadSet.ReadRangeBytes, bytes.Length - offset);
                stream.ReadExactly(bytes.AsSpan(offset, length));
                offset += length;
            }
            return new CloudObject(bytes, Version(bytes));
        }
    }

    public override ValueTask<CloudObject?> GetRangeAsync(string objectKey, ulong offset, int length, CancellationToken cancellationToken = default) =>
        GetRangeCore(objectKey, offset, length, null, cancellationToken);

    internal override ValueTask<CloudObject?> GetRangeAsync(string objectKey, ulong offset, int length, CloudObjectMetadata expectedMetadata, CancellationToken cancellationToken = default) =>
        GetRangeCore(objectKey, offset, length, expectedMetadata, cancellationToken);

    ValueTask<CloudObject?> GetRangeCore(string objectKey, ulong offset, int length, CloudObjectMetadata? expected, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        lock (_gate)
        {
            var path = Resolve(objectKey);
            using var processLock = SimulatedCloudMutationLock.Acquire(
                _root, path, cancellationToken, _onProcessLockContention);
            if (!File.Exists(path))
            {
                return ValueTask.FromResult<CloudObject?>(null);
            }

            using var stream = File.OpenRead(path);
            var version = Convert.ToHexString(SHA256.HashData(stream));
            if (expected is not null && (expected.Version != version || expected.SizeBytes != (ulong)stream.Length))
            {
                throw new PantsCorruptionException("Simulated cloud range identity changed.");
            }

            if (offset > (ulong)stream.Length || (ulong)length > (ulong)stream.Length - offset)
            {
                throw new PantsCorruptionException("Simulated cloud range exceeds its object.");
            }

            stream.Position = checked((long)offset);
            var bytes = new byte[length];
            stream.ReadExactly(bytes);
            return ValueTask.FromResult<CloudObject?>(new CloudObject(bytes, version));
        }
    }

    public override ValueTask<CloudObjectMetadata?> HeadAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var path = Resolve(objectKey);
            using var processLock = SimulatedCloudMutationLock.Acquire(
                _root, path, cancellationToken, _onProcessLockContention);
            if (!File.Exists(path))
            {
                return ValueTask.FromResult<CloudObjectMetadata?>(null);
            }

            using var stream = File.OpenRead(path);
            return ValueTask.FromResult<CloudObjectMetadata?>(new CloudObjectMetadata((ulong)stream.Length, Convert.ToHexString(SHA256.HashData(stream)), File.GetLastWriteTimeUtc(path)));
        }
    }

    public override ValueTask<bool> PutAsync(string objectKey, ReadOnlyMemory<byte> data, CloudObjectWriteCondition condition, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var path = Resolve(objectKey);
            using var processLock = SimulatedCloudMutationLock.Acquire(
                _root, path, cancellationToken, _onProcessLockContention);
            var exists = File.Exists(path);
            if (condition is CloudObjectWriteCondition.IfAbsent && exists ||
                condition is CloudObjectWriteCondition.IfVersion expected && (!exists || Version(File.ReadAllBytes(path)) != expected.Version))
            {
                return ValueTask.FromResult(false);
            }

            AtomicStagedFile.Write(path, data.Span);
            return ValueTask.FromResult(true);
        }
    }

    public override ValueTask<CloudObjectListPage> ListPageAsync(string prefix, string? continuationToken, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var keys = Directory.Exists(_root) ? Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(_root, path).Replace(Path.DirectorySeparatorChar, '/'))
                .Where(key => !key.StartsWith(".midge-locks/", StringComparison.Ordinal) && key.StartsWith(prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray() : [];
            var continuationIndex = 0;
            if (continuationToken is not null)
            {
                var index = Array.BinarySearch(keys, continuationToken, StringComparer.Ordinal);
                continuationIndex = index >= 0 ? index + 1 : ~index;
            }

            var pageKeys = keys
                .Skip(continuationIndex)
                .Take(CloudObjectStore.MaximumListPageKeys)
                .ToArray();
            var nextContinuation = continuationIndex + pageKeys.Length < keys.Length && pageKeys.Length > 0
                ? pageKeys[^1]
                : null;
            return ValueTask.FromResult(new CloudObjectListPage(pageKeys, nextContinuation));
        }
    }

    public override ValueTask<CloudObjectDeleteOutcome> DeleteAsync(string objectKey, CloudObjectDeleteCondition condition, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var path = Resolve(objectKey);
            using var processLock = SimulatedCloudMutationLock.Acquire(
                _root, path, cancellationToken, _onProcessLockContention);
            if (!File.Exists(path))
            {
                return ValueTask.FromResult(CloudObjectDeleteOutcome.NotFound);
            }

            if (condition is CloudObjectDeleteCondition.IfVersion expected && Version(File.ReadAllBytes(path)) != expected.Version)
            {
                return ValueTask.FromResult(CloudObjectDeleteOutcome.ConditionNotMet);
            }

            File.Delete(path);
            return ValueTask.FromResult(CloudObjectDeleteOutcome.Deleted);
        }
    }

    string Resolve(string key)
    {
        if (Path.IsPathRooted(key) || key.Split('/').Any(static part => part is "" or "." or "..") || key.Contains('\\') || key.Contains(':'))
        {
            throw new PantsInvalidArgumentException("Simulated object key is unsafe.");
        }

        return Path.Combine(_root, key.Replace('/', Path.DirectorySeparatorChar));
    }

    static string Version(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}

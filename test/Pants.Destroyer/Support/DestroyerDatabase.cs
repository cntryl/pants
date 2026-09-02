using System.Text;
using Cntryl.Pants.Transactions;

namespace Cntryl.Pants.Destroyer.Support;

/// <summary>
/// String-keyed convenience wrappers around <see cref="IPantsDatabase"/> so
/// individual scenario tests read as the mutation/assertion they're
/// checking, not byte-array plumbing.
/// </summary>
public static class DestroyerDatabase
{
    public static async ValueTask PutAsync(
        IPantsDatabase database,
        IPantsColumnFamily columnFamily,
        string key,
        string value,
        PantsWriteOptions options)
    {
        await using var writer = await database.BeginTransactionAsync(columnFamily, PantsTransactionMode.ReadWrite);
        writer.Put(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(value));
        await writer.CommitAsync(options);
    }

    public static async ValueTask DeleteAsync(
        IPantsDatabase database,
        IPantsColumnFamily columnFamily,
        string key,
        PantsWriteOptions options)
    {
        await using var writer = await database.BeginTransactionAsync(columnFamily, PantsTransactionMode.ReadWrite);
        writer.Delete(Encoding.UTF8.GetBytes(key));
        await writer.CommitAsync(options);
    }

    public static async ValueTask PutBytesAsync(
        IPantsDatabase database,
        IPantsColumnFamily columnFamily,
        string key,
        ReadOnlyMemory<byte> value,
        PantsWriteOptions options)
    {
        await using var writer = await database.BeginTransactionAsync(columnFamily, PantsTransactionMode.ReadWrite);
        writer.Put(Encoding.UTF8.GetBytes(key), value);
        await writer.CommitAsync(options);
    }

    public static async ValueTask<ReadOnlyMemory<byte>?> GetBytesAsync(
        IPantsDatabase database,
        IPantsColumnFamily columnFamily,
        string key)
    {
        await using var reader = await database.BeginTransactionAsync(columnFamily, PantsTransactionMode.ReadOnly);
        return await reader.GetAsync(Encoding.UTF8.GetBytes(key));
    }

    public static async ValueTask<string?> GetAsync(
        IPantsDatabase database,
        IPantsColumnFamily columnFamily,
        string key)
    {
        await using var reader = await database.BeginTransactionAsync(columnFamily, PantsTransactionMode.ReadOnly);
        var value = await reader.GetAsync(Encoding.UTF8.GetBytes(key));
        return value is null ? null : Encoding.UTF8.GetString(value.Value.Span);
    }

    /// <summary>A temp directory that deletes itself (recursively) on disposal.</summary>
    public static TempDirectory CreateTempDirectory(string prefix) =>
        new(Directory.CreateTempSubdirectory(prefix).FullName);

    public sealed class TempDirectory(string path) : IDisposable
    {
        public string Path { get; } = path;

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}

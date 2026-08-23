using System.Text;

namespace Cntryl.Pants.Cloud.Internal.Objects;

static class CloudObjectStoreExtensions
{
    const int MaximumListPages = 10_000;
    const int MaximumListItems = 1_000_000;
    const int MaximumListBytes = 256 * 1024 * 1024;

    public static async ValueTask<IReadOnlyList<string>> ListAllAsync(
        this ICloudObjectStore objectStore,
        string prefix,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(objectStore);
        ArgumentNullException.ThrowIfNull(prefix);
        var objectKeys = new List<string>();
        var continuationTokens = new HashSet<string>(StringComparer.Ordinal);
        var aggregateBytes = 0;
        var continuationToken = default(string);

        for (var pageCount = 1; ; pageCount++)
        {
            if (pageCount > MaximumListPages)
            {
                throw new PantsInternalException(
                    $"Cloud LIST page count exceeded limit of {MaximumListPages}.");
            }

            var page = await objectStore.ListPageAsync(
                prefix,
                continuationToken,
                cancellationToken).ConfigureAwait(false);
            if (page.ObjectKeys.Count > MaximumListItems - objectKeys.Count)
            {
                throw new PantsInternalException(
                    $"Cloud LIST item count exceeded limit of {MaximumListItems}.");
            }

            foreach (var objectKey in page.ObjectKeys)
            {
                aggregateBytes = AddBytes(aggregateBytes, Encoding.UTF8.GetByteCount(objectKey));
                objectKeys.Add(objectKey);
            }

            continuationToken = string.IsNullOrEmpty(page.ContinuationToken)
                ? null
                : page.ContinuationToken;
            if (continuationToken is null)
            {
                return objectKeys.ToArray();
            }

            aggregateBytes = AddBytes(
                aggregateBytes,
                Encoding.UTF8.GetByteCount(continuationToken));
            if (!continuationTokens.Add(continuationToken))
            {
                throw new PantsInternalException(
                    "Cloud LIST repeated continuation token.");
            }
        }
    }

    static int AddBytes(int current, int additional)
    {
        var combined = 0;
        try
        {
            combined = checked(current + additional);
        }
        catch (OverflowException exception)
        {
            throw new PantsInternalException(
                "Cloud LIST aggregate byte count overflowed its safety counter.",
                exception);
        }

        return combined <= MaximumListBytes
            ? combined
            : throw new PantsInternalException(
                $"Cloud LIST aggregate byte count exceeded limit of {MaximumListBytes}.");
    }
}

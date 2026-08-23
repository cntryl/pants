using System.Globalization;
using System.Text;

namespace Cntryl.Pants;

internal sealed class CloudObjectLeaseStore(
    ICloudObjectStore objectStore,
    string objectKey) : ICloudLeaseStore
{
    private readonly ICloudObjectStore _objectStore = objectStore ??
        throw new ArgumentNullException(nameof(objectStore));
    private readonly string _objectKey = string.IsNullOrWhiteSpace(objectKey)
        ? throw new ArgumentException("A lease object key is required.", nameof(objectKey))
        : objectKey;

    public async ValueTask<CloudLeaseSnapshot?> ReadAsync(CancellationToken cancellationToken)
    {
        CloudObject? value = await _objectStore.GetAsync(_objectKey, cancellationToken)
            .ConfigureAwait(false);
        return value is null
            ? null
            : new CloudLeaseSnapshot(Parse(value.Data.Span), value.Version);
    }

    public ValueTask<bool> TryCreateAsync(
        CloudLeaseRecord lease,
        CancellationToken cancellationToken) =>
        _objectStore.PutAsync(
            _objectKey,
            Serialize(lease),
            new CloudObjectWriteCondition.IfAbsent(),
            cancellationToken);

    public ValueTask<bool> TryReplaceAsync(
        string expectedVersion,
        CloudLeaseRecord lease,
        CancellationToken cancellationToken) =>
        _objectStore.PutAsync(
            _objectKey,
            Serialize(lease),
            new CloudObjectWriteCondition.IfVersion(expectedVersion),
            cancellationToken);

    private static byte[] Serialize(CloudLeaseRecord lease)
    {
        ValidateSingleLine(lease.HolderId, nameof(lease.HolderId));
        ValidateSingleLine(lease.OwnerToken, nameof(lease.OwnerToken));
        string acquiredAt = lease.AcquiredAtUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        string expiresAt = lease.ExpiresAtUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        string value = string.Create(
            CultureInfo.InvariantCulture,
            $"epoch: {lease.Epoch}\nholder_id: {lease.HolderId}\nowner_token: {lease.OwnerToken}\nacquired_at: {acquiredAt}\nexpires_at: {expiresAt}\n");
        return Encoding.UTF8.GetBytes(value);
    }

    private static CloudLeaseRecord Parse(ReadOnlySpan<byte> bytes)
    {
        string text = Encoding.UTF8.GetString(bytes);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = line.IndexOf(": ", StringComparison.Ordinal);
            if (separator <= 0 || !fields.TryAdd(line[..separator], line[(separator + 2)..]))
            {
                throw new PantsCorruptionException("The cloud primary lease document is malformed.");
            }
        }

        if (!fields.TryGetValue("epoch", out string? epochText) ||
            !ulong.TryParse(epochText, NumberStyles.None, CultureInfo.InvariantCulture, out ulong epoch) ||
            epoch == 0 ||
            !fields.TryGetValue("holder_id", out string? holderId) ||
            !fields.TryGetValue("owner_token", out string? ownerToken) ||
            !fields.TryGetValue("acquired_at", out string? acquiredText) ||
            !DateTimeOffset.TryParse(
                acquiredText,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset acquiredAt) ||
            !fields.TryGetValue("expires_at", out string? expiresText) ||
            !DateTimeOffset.TryParse(
                expiresText,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset expiresAt))
        {
            throw new PantsCorruptionException("The cloud primary lease document is invalid.");
        }

        ValidateSingleLine(holderId, nameof(holderId));
        ValidateSingleLine(ownerToken, nameof(ownerToken));
        return new CloudLeaseRecord(holderId, epoch, ownerToken, acquiredAt, expiresAt);
    }

    private static void ValidateSingleLine(string value, string description)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\n') || value.Contains('\r'))
        {
            throw new PantsCorruptionException($"Cloud lease {description} is invalid.");
        }
    }
}

using System.Globalization;
using System.Text;

namespace Cntryl.Pants.Cloud.Internal.Leases;

sealed class CloudObjectLeaseStore(
    ICloudObjectStore objectStore,
    string objectKey) : ICloudLeaseStore
{
    readonly string _objectKey = string.IsNullOrWhiteSpace(objectKey)
        ? throw new ArgumentException("A lease object key is required.", nameof(objectKey))
        : objectKey;

    readonly ICloudObjectStore _objectStore = objectStore ??
                                              throw new ArgumentNullException(nameof(objectStore));

    public async ValueTask<CloudLeaseSnapshot?> ReadAsync(CancellationToken cancellationToken)
    {
        var value = await _objectStore.GetAsync(_objectKey, cancellationToken)
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

    static byte[] Serialize(CloudLeaseRecord lease)
    {
        ValidateSingleLine(lease.HolderId, nameof(lease.HolderId));
        ValidateSingleLine(lease.OwnerToken, nameof(lease.OwnerToken));
        var acquiredAt = lease.AcquiredAtUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        var expiresAt = lease.ExpiresAtUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        var value = string.Create(
            CultureInfo.InvariantCulture,
            $"epoch: {lease.Epoch}\nholder_id: {lease.HolderId}\nowner_token: {lease.OwnerToken}\nacquired_at: {acquiredAt}\nexpires_at: {expiresAt}\n");
        return Encoding.UTF8.GetBytes(value);
    }

    static CloudLeaseRecord Parse(ReadOnlySpan<byte> bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf(": ", StringComparison.Ordinal);
            if (separator <= 0 || !fields.TryAdd(line[..separator], line[(separator + 2)..]))
            {
                throw new PantsCorruptionException("The cloud primary lease document is malformed.");
            }
        }

        if (!fields.TryGetValue("epoch", out var epochText) ||
            !ulong.TryParse(epochText, NumberStyles.None, CultureInfo.InvariantCulture, out var epoch) ||
            epoch == 0 ||
            !fields.TryGetValue("holder_id", out var holderId) ||
            !fields.TryGetValue("owner_token", out var ownerToken) ||
            !fields.TryGetValue("acquired_at", out var acquiredText) ||
            !DateTimeOffset.TryParse(
                acquiredText,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var acquiredAt) ||
            !fields.TryGetValue("expires_at", out var expiresText) ||
            !DateTimeOffset.TryParse(
                expiresText,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var expiresAt))
        {
            throw new PantsCorruptionException("The cloud primary lease document is invalid.");
        }

        ValidateSingleLine(holderId, nameof(holderId));
        ValidateSingleLine(ownerToken, nameof(ownerToken));
        return new CloudLeaseRecord(holderId, epoch, ownerToken, acquiredAt, expiresAt);
    }

    static void ValidateSingleLine(string value, string description)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\n') || value.Contains('\r'))
        {
            throw new PantsCorruptionException($"Cloud lease {description} is invalid.");
        }
    }
}

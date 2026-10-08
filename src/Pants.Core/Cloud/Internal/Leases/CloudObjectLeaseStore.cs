using System.Globalization;
using System.Text;

namespace Cntryl.Pants.Cloud.Internal.Leases;

sealed class CloudObjectLeaseStore(
    ICloudObjectStore objectStore,
    string objectKey) : ICloudLeaseStore
{
    static readonly UTF8Encoding StrictUtf8 = new(false, true);

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
            new PantsCloudObjectWriteCondition.IfAbsent(),
            cancellationToken);

    public ValueTask<bool> TryReplaceAsync(
        string expectedVersion,
        CloudLeaseRecord lease,
        CancellationToken cancellationToken) =>
        _objectStore.PutAsync(
            _objectKey,
            Serialize(lease),
            new PantsCloudObjectWriteCondition.IfVersion(expectedVersion),
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

    /// <summary>
    ///     Parses a lease document the way the reference engine does: the last value of a repeated
    ///     field wins, unknown and unrecognizable lines are ignored, and a document that is not valid
    ///     UTF-8, lacks a holder, acquisition time or expiry, or carries an unparseable epoch is
    ///     indeterminate. The epoch and owner token are optional so a legacy document written before
    ///     they existed still reads: it parses as epoch 0 with no owner token, nobody owns it, and it
    ///     is respected until its expiry and then taken over like any other. A present but
    ///     unparseable expiry is kept as such, because it makes takeover indeterminate.
    /// </summary>
    static CloudLeaseRecord Parse(ReadOnlySpan<byte> bytes)
    {
        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new PantsLeaseIndeterminateException(
                "The cloud primary lease document is not valid UTF-8.",
                exception);
        }

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf(": ", StringComparison.Ordinal);
            if (separator > 0)
            {
                fields[line[..separator]] = line[(separator + 2)..];
            }
        }

        if (!fields.TryGetValue("holder_id", out var holderId) ||
            !fields.TryGetValue("acquired_at", out var acquiredText) ||
            !fields.TryGetValue("expires_at", out var expiresText))
        {
            throw new PantsLeaseIndeterminateException(
                "The cloud primary lease document is missing a required field; ownership is ambiguous.");
        }

        ulong epoch = 0;
        if (fields.TryGetValue("epoch", out var epochText) &&
            !ulong.TryParse(epochText, NumberStyles.None, CultureInfo.InvariantCulture, out epoch))
        {
            throw new PantsLeaseIndeterminateException(
                "The cloud primary lease epoch is unparseable; ownership is ambiguous.");
        }

        var ownerToken = fields.GetValueOrDefault("owner_token", string.Empty);
        _ = DateTimeOffset.TryParse(
            acquiredText,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var acquiredAt);
        var expiryParsed = DateTimeOffset.TryParse(
            expiresText,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var expiresAt);
        return new CloudLeaseRecord(
            holderId,
            epoch,
            ownerToken,
            acquiredAt,
            expiresAt,
            !expiryParsed);
    }

    static void ValidateSingleLine(string value, string description)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\n') || value.Contains('\r'))
        {
            throw new PantsCorruptionException($"Cloud lease {description} is invalid.");
        }
    }
}

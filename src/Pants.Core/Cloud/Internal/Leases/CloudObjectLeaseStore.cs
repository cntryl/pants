using System.Globalization;
using System.Text;

namespace Cntryl.Pants.Cloud.Internal.Leases;

sealed class CloudObjectLeaseStore(
    ICloudObjectStore objectStore,
    string objectKey) : ICloudLeaseStore
{
    internal const int MaximumBackupLeaseBytes = 4096;
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
        if (value is not null)
        {
            return new CloudLeaseSnapshot(Parse(value.Data.Span), value.Version);
        }

        var sentinel = await _objectStore.GetAsync(CloudMetadataAuthority.SentinelKey, cancellationToken).ConfigureAwait(false);
        if (sentinel?.Data.Span.SequenceEqual("midge cloud metadata authority v3 initialized\n"u8) == true)
        {
            throw new PantsCorruptionException("Initialized cloud metadata has lost its primary lease.");
        }

        return null;
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

    internal static byte[] CreateExpiredBackupLease(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaximumBackupLeaseBytes)
        {
            throw new PantsLeaseIndeterminateException("The cloud primary lease exceeds its backup size limit.");
        }

        _ = Parse(bytes);
        // Keep the exact producer text for every ownership field, including RFC3339 precision.
        // Only the private backup/restore copy loses the source's active expiry.
        var lines = StrictUtf8.GetString(bytes).Split('\n');
        return Encoding.UTF8.GetBytes(string.Join('\n', lines.Select(static line =>
            line.StartsWith("expires: ", StringComparison.Ordinal)
                ? "expires: 1970-01-01T00:00:00Z"
                : line)));
    }

    static byte[] Serialize(CloudLeaseRecord lease)
    {
        ArgumentOutOfRangeException.ThrowIfZero(lease.Epoch);
        ValidateSingleLine(lease.HolderId, nameof(lease.HolderId));
        ValidateSingleLine(lease.OwnerToken, nameof(lease.OwnerToken));
        var acquiredAt = lease.AcquiredAtUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        var expiresAt = lease.ExpiresAtUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        var value = string.Create(
            CultureInfo.InvariantCulture,
            $"lease_version: 4\nfencing_epoch: {lease.Epoch}\nholder: {lease.HolderId}\nowner: {lease.OwnerToken}\nacquired: {acquiredAt}\nexpires: {expiresAt}\n");
        return Encoding.UTF8.GetBytes(value);
    }

    static CloudLeaseRecord Parse(ReadOnlySpan<byte> bytes)
    {
        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new PantsLeaseIndeterminateException("The cloud primary lease is not valid UTF-8.", exception);
        }

        if (!text.EndsWith('\n') || text.Contains('\r'))
        {
            throw new PantsLeaseIndeterminateException("The cloud primary lease requires LF-terminated fields.");
        }

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var known = new HashSet<string>(["lease_version", "fencing_epoch", "holder", "owner", "acquired", "expires"], StringComparer.Ordinal);
        foreach (var line in text[..^1].Split('\n'))
        {
            var separator = line.IndexOf(": ", StringComparison.Ordinal);
            if (separator <= 0 || !known.Contains(line[..separator]) ||
                !fields.TryAdd(line[..separator], line[(separator + 2)..]))
            {
                throw new PantsLeaseIndeterminateException("The cloud primary lease has an unknown or duplicate field.");
            }
        }

        if (fields.Count != known.Count)
        {
            throw new PantsLeaseIndeterminateException("The cloud primary lease is missing required fields.");
        }

        if (fields.GetValueOrDefault("lease_version") != "4")
        {
            throw new PantsCompatibilityException("The cloud primary lease requires lease version 4.");
        }

        if (!ulong.TryParse(fields["fencing_epoch"], NumberStyles.None, CultureInfo.InvariantCulture, out var epoch) || epoch == 0 ||
            string.IsNullOrWhiteSpace(fields["holder"]) || string.IsNullOrWhiteSpace(fields["owner"]) ||
            !TryParseRfc3339(fields["acquired"], out var acquiredAt) ||
            !TryParseRfc3339(fields["expires"], out var expiresAt))
        {
            throw new PantsLeaseIndeterminateException("The cloud primary lease contains invalid ownership or timestamps.");
        }

        return new CloudLeaseRecord(fields["holder"], epoch, fields["owner"], acquiredAt, expiresAt);
    }

    static bool TryParseRfc3339(string text, out DateTimeOffset timestamp)
    {
        timestamp = default;
        var value = text.AsSpan();
        if (value.Length < 20 || value[4] != '-' || value[7] != '-' || value[10] is not ('T' or 't' or ' ') ||
            value[13] != ':' || value[16] != ':')
        {
            return false;
        }

        for (var index = 0; index < 19; index++)
        {
            if (index is not (4 or 7 or 10 or 13 or 16) && !char.IsAsciiDigit(value[index]))
            {
                return false;
            }
        }

        var cursor = 19;
        if (value[cursor] == '.')
        {
            var firstFractionDigit = ++cursor;
            while (cursor < value.Length && char.IsAsciiDigit(value[cursor]))
            {
                cursor++;
            }

            if (cursor == firstFractionDigit)
            {
                return false;
            }
        }

        var offsetMinutes = 0;
        if (!(cursor == value.Length - 1 && value[cursor] is 'Z' or 'z'))
        {
            if (cursor != value.Length - 6 || value[cursor] is not ('+' or '-') || value[cursor + 3] != ':' ||
                !char.IsAsciiDigit(value[cursor + 1]) || !char.IsAsciiDigit(value[cursor + 2]) ||
                !char.IsAsciiDigit(value[cursor + 4]) || !char.IsAsciiDigit(value[cursor + 5]))
            {
                return false;
            }

            var hours = (value[cursor + 1] - '0') * 10 + value[cursor + 2] - '0';
            var minutes = (value[cursor + 4] - '0') * 10 + value[cursor + 5] - '0';
            if (hours > 23 || minutes > 59)
            {
                return false;
            }

            offsetMinutes = (hours * 60 + minutes) * (value[cursor] == '+' ? 1 : -1);
        }

        // Parse the calendar/fraction as UTC, then apply the RFC offset. This retains nanosecond
        // fractional syntax and offsets beyond DateTimeOffset's native fourteen-hour range.
        var leapSecond = value[17] == '6' && value[18] == '0';
        var calendar = leapSecond
            ? string.Concat(value[..17], "59", value.Slice(19, cursor - 19), "Z")
            : string.Concat(value[..cursor], "Z");
        if (!DateTimeOffset.TryParse(calendar, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            return false;
        }

        try
        {
            timestamp = parsed.AddMinutes(-offsetMinutes);
            if (leapSecond)
            {
                timestamp = timestamp.AddSeconds(1);
            }
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    static void ValidateSingleLine(string value, string description)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\n') || value.Contains('\r'))
        {
            throw new PantsCorruptionException($"Cloud lease {description} is invalid.");
        }
    }
}

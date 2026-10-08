using System.Globalization;
using System.Numerics;
using System.Text;

namespace Cntryl.Pants.Storage.Internal.Lease;

/// <summary>
///     Encodes and strictly decodes the local leader record, format/lease.md §3 and §7.
/// </summary>
static class LeaderRecordCodec
{
    const string EpochField = "epoch";
    const string HolderIdField = "holder_id";
    const string AcquiredAtField = "acquired_at";
    const string ChecksumField = "checksum";

    static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static byte[] Encode(LeaderRecord record) =>
        Encoding.UTF8.GetBytes($"{Body(record)}{ChecksumField}: {Checksum(record)}\n");

    /// <summary>
    ///     Decodes a record. Anything that is not exactly the documented format makes ownership
    ///     ambiguous and raises <see cref="PantsLeaseIndeterminateException" />.
    /// </summary>
    public static LeaderRecord Decode(ReadOnlySpan<byte> bytes)
    {
        string content;
        try
        {
            // No BOM stripping and no replacement characters: the record is exactly UTF-8.
            content = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new PantsLeaseIndeterminateException(
                "Midge leader record is not UTF-8; ownership is ambiguous.",
                exception);
        }

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.EndsWith('\r') ? rawLine[..^1] : rawLine;
            var separator = line.IndexOf(": ", StringComparison.Ordinal);
            if (separator >= 0)
            {
                // Last-occurrence-wins per format/lease.md §7: a duplicate field is resolved,
                // not treated as an indeterminate/corrupt record.
                fields[line[..separator]] = line[(separator + 2)..];
            }
        }

        if (!(fields.TryGetValue(EpochField, out var epochRaw) &&
              TryParseDecimal(epochRaw, out ulong epoch) &&
              fields.TryGetValue(HolderIdField, out var holderId) &&
              fields.TryGetValue(AcquiredAtField, out var acquiredAt)))
        {
            throw new PantsLeaseIndeterminateException(
                "Midge leader record is invalid; ownership is ambiguous.");
        }

        var record = new LeaderRecord(epoch, holderId, acquiredAt);

        // A record may carry a CRC32C over its three-field body. One that is present but wrong or
        // unparseable is a damaged record; one that is absent is an older, unchecked record.
        if (fields.TryGetValue(ChecksumField, out var checksumRaw) &&
            (!TryParseDecimal(checksumRaw, out uint expected) || expected != Checksum(record)))
        {
            throw new PantsLeaseIndeterminateException(
                "Midge leader record checksum does not verify; ownership is ambiguous.");
        }

        return record;
    }

    /// <summary>Plain ASCII decimal digits only: no sign, whitespace, separators, or hex.</summary>
    static bool TryParseDecimal<T>(string value, out T result)
        where T : IBinaryInteger<T> =>
        T.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result!);

    static uint Checksum(LeaderRecord record) =>
        DiskFormat.Crc32C(Encoding.UTF8.GetBytes(Body(record)));

    static string Body(LeaderRecord record) =>
        $"{EpochField}: {record.Epoch}\n{HolderIdField}: {record.HolderId}\n{AcquiredAtField}: {record.AcquiredAt}\n";
}

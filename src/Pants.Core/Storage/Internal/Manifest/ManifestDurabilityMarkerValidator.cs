using System.Text.Json;

namespace Cntryl.Pants.Storage.Internal.Manifest;

/// <summary>
///     Checks the payload of a manifest journal durability marker (record type 9). The marker must be
///     a JSON object carrying unsigned integer <c>last_persisted_sequence</c> and <c>ts_millis</c>,
///     the shape Midge writes and reads. Replay trusts a durability marker to decide which preceding
///     edits are durable, so an unreadable marker is journal corruption rather than an ignorable record.
/// </summary>
static class ManifestDurabilityMarkerValidator
{
    /// <summary>Throws <see cref="JsonException" /> for invalid JSON and <see cref="PantsException" /> for a wrong shape.</summary>
    public static void Validate(byte[] payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !IsUnsignedInteger(root, "last_persisted_sequence") ||
            !IsUnsignedInteger(root, "ts_millis"))
        {
            throw PantsException.Create(
                PantsErrorCode.Corruption,
                "The manifest journal durability marker is malformed.");
        }
    }

    static bool IsUnsignedInteger(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetUInt64(out _);
}

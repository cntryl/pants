using System.Text.Json;

namespace Cntryl.Pants.Storage.Internal.Manifest;

/// <summary>
///     Reads a manifest journal record payload without building a DOM, so replaying an edit that
///     carries a large key bound costs a pass over its bytes rather than a row per number.
/// </summary>
static class ManifestJournalPayload
{
    /// <summary>
    ///     Finds the edit within a payload. Current writers wrap it as <c>{"edit_id": n, "edit": ...}</c>;
    ///     older writers journaled the bare edit, in which case <paramref name="editId" /> is null.
    /// </summary>
    public static int Locate(ReadOnlySpan<byte> payload, out ulong? editId)
    {
        editId = null;
        var reader = new Utf8JsonReader(payload);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            return 0;
        }

        ulong? id = null;
        var editStart = -1;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("edit_id"u8))
            {
                _ = reader.Read();
                id = reader.TokenType == JsonTokenType.Number && reader.TryGetUInt64(out var value)
                    ? value
                    : throw PantsException.Create(
                        PantsErrorCode.Corruption,
                        "The manifest journal edit id is invalid.");
            }
            else if (reader.ValueTextEquals("edit"u8))
            {
                _ = reader.Read();
                editStart = checked((int)reader.TokenStartIndex);
                reader.Skip();
            }
            else
            {
                _ = reader.Read();
                reader.Skip();
            }
        }

        if (id is null || editStart < 0)
        {
            return 0;
        }

        editId = id;
        return editStart;
    }

    public static ManifestEdit ReadEdit(
        ReadOnlySpan<byte> payload,
        int editStart,
        JsonSerializerOptions options)
    {
        var reader = new Utf8JsonReader(payload[editStart..]);
        _ = reader.Read();
        return ManifestEdit.Read(ref reader, options, true);
    }

    /// <summary>Throws <see cref="JsonException" /> unless the payload is exactly one JSON value.</summary>
    public static void Validate(ReadOnlySpan<byte> payload)
    {
        var reader = new Utf8JsonReader(payload);
        if (!reader.Read())
        {
            throw new JsonException("The manifest journal payload is empty.");
        }

        reader.Skip();
        if (reader.Read())
        {
            throw new JsonException("The manifest journal payload has trailing content.");
        }
    }
}

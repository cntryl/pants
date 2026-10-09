using System.Text.Json;

namespace Cntryl.Pants.Storage.Internal;

/// <summary>
///     A manifest key bound that is not an array of byte values. Raised by
///     <see cref="KeyBoundJsonConverter" /> so a load can tell a malformed key bound apart from other
///     malformed manifest JSON.
/// </summary>
sealed class ManifestKeyBoundFormatException(string message) : JsonException(message);

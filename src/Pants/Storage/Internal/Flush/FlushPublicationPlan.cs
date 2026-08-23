using System.Text.Json;

namespace Cntryl.Pants.Storage.Internal.Flush;

sealed record FlushPublicationPlan(
    List<JsonElement> Edits,
    List<JsonElement> Intents,
    List<StagedSstOutput> Outputs);

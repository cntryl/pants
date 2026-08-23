using System.Text.Json;

namespace Pants;

sealed record FlushPublicationPlan(
    List<JsonElement> Edits,
    List<JsonElement> Intents,
    List<StagedSstOutput> Outputs);

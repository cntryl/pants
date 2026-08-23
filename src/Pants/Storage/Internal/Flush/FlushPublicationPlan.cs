using System.Text.Json;

namespace Cntryl.Pants;

sealed record FlushPublicationPlan(
    List<JsonElement> Edits,
    List<JsonElement> Intents,
    List<StagedSstOutput> Outputs);

using Cntryl.Pants.Storage.Internal.Manifest;
using Cntryl.Pants.Storage.Internal.Recovery;

namespace Cntryl.Pants.Storage.Internal.Flush;

sealed record FlushPublicationPlan(
    List<ManifestEdit> Edits,
    List<IntentEntry> Intents,
    List<StagedSstOutput> Outputs);

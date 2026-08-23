namespace Cntryl.Pants.Storage.Internal.Flush;

sealed record StagedSstOutput(
    FileMeta Metadata,
    string StagingPath,
    string FinalPath);

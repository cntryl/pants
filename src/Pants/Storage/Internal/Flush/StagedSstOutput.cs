namespace Cntryl.Pants.Storage.Internal.Flush;

sealed record StagedSstOutput(
    MidgeFileMeta Metadata,
    string StagingPath,
    string FinalPath);

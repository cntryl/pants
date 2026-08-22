namespace Pants;

sealed record StagedSstOutput(
    MidgeFileMeta Metadata,
    string StagingPath,
    string FinalPath);

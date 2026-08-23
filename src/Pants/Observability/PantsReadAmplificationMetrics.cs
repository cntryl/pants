namespace Cntryl.Pants.Observability;

public sealed record PantsReadAmplificationMetrics
{
    public long ReadsTotal { get; init; }

    public long SstsTouchedTotal { get; init; }

    public long L0SstsTouchedTotal { get; init; }

    public long BlocksReadTotal { get; init; }

    public double AverageSstsPerRead { get; init; }

    public double AverageL0SstsPerRead { get; init; }

    public double AverageBlocksPerRead { get; init; }

    public double L0OverlapRate { get; init; }

    public long SstBudgetViolationsTotal { get; init; }

    public long BlockBudgetViolationsTotal { get; init; }

    public double SstBudgetViolationRate { get; init; }

    public double BlockBudgetViolationRate { get; init; }

    public long ReaderCacheHitsTotal { get; init; }

    public long ReaderCacheMissesTotal { get; init; }

    public long BlockCacheHitsTotal { get; init; }

    public long BlockCacheMissesTotal { get; init; }

    public long KeyRangeRejectsTotal { get; init; }

    public long BloomChecksTotal { get; init; }

    public long BloomTruePositivesTotal { get; init; }

    public long BloomFalsePositivesTotal { get; init; }

    public long BloomTrueNegativesTotal { get; init; }

    public long DataBlocksReadTotal { get; init; }

    public long CompactionTriggersTotal { get; init; }
}

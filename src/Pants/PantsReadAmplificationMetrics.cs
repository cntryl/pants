namespace Pants;

public sealed record PantsReadAmplificationMetrics(
    long ReadsTotal,
    long SstsTouchedTotal,
    long L0SstsTouchedTotal,
    long BlocksReadTotal,
    double AverageSstsPerRead,
    double AverageL0SstsPerRead,
    double AverageBlocksPerRead,
    double L0OverlapRate,
    double SstBudgetViolationRate,
    double BlockBudgetViolationRate);

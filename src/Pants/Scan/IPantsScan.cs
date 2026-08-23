namespace Cntryl.Pants.Scan;

public interface IPantsScan : IAsyncEnumerable<PantsEntry>, IAsyncDisposable
{
    PantsScanDirection Direction { get; }

    PantsIteratorState State { get; }

    bool IsActive { get; }

    bool IsExhausted { get; }

    bool IsFailed { get; }
}

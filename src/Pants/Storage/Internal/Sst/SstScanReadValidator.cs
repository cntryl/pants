namespace Cntryl.Pants;

internal sealed class SstScanReadValidator : IScanReadValidator
{
    private readonly RuntimeTelemetry _telemetry;
    private readonly IReadOnlyList<MidgeSstReader> _readers;
    private readonly IReadOnlyList<SstScanBlock> _blocks;
    private readonly int _candidateSsts;
    private int _dataBlocksRead;
    private int _disposed;

    public SstScanReadValidator(
        RuntimeTelemetry telemetry,
        IReadOnlyList<MidgeSstReader> readers,
        IReadOnlyList<SstScanBlock> blocks,
        int candidateSsts)
    {
        _telemetry = telemetry;
        _readers = readers;
        _blocks = blocks;
        _candidateSsts = candidateSsts;
    }

    public void ValidateKey(ReadOnlySpan<byte> key)
    {
        foreach (SstScanBlock block in _blocks)
        {
            if (!block.IsValidated && block.Contains(key))
            {
                Validate(block);
            }
        }
    }

    public void Complete()
    {
        foreach (SstScanBlock block in _blocks)
        {
            if (!block.IsValidated)
            {
                Validate(block);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (MidgeSstReader reader in _readers)
        {
            reader.Dispose();
        }

        _telemetry.RecordSstScan(
            _candidateSsts,
            _blocks.Count,
            _dataBlocksRead,
            readerCacheHits: 0,
            readerCacheMisses: _candidateSsts,
            rangeTombstoneScans: _candidateSsts);
    }

    private void Validate(SstScanBlock block)
    {
        _ = block.Reader.ReadDataBlock(block.BlockIndex);
        block.IsValidated = true;
        _dataBlocksRead = checked(_dataBlocksRead + 1);
    }
}

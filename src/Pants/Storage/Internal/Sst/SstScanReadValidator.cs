namespace Cntryl.Pants.Storage.Internal.Sst;

sealed class SstScanReadValidator : IScanReadValidator
{
    readonly IReadOnlyList<SstScanBlock> _blocks;
    readonly int _candidateSsts;
    readonly IReadOnlyList<MidgeSstReader> _readers;
    readonly RuntimeTelemetry _telemetry;
    int _dataBlocksRead;
    int _disposed;

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
        foreach (var block in _blocks)
        {
            if (!block.IsValidated && block.Contains(key))
            {
                Validate(block);
            }
        }
    }

    public void Complete()
    {
        foreach (var block in _blocks)
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

        foreach (var reader in _readers)
        {
            reader.Dispose();
        }

        _telemetry.RecordSstScan(
            _candidateSsts,
            _blocks.Count,
            _dataBlocksRead,
            0,
            _candidateSsts,
            _candidateSsts);
    }

    void Validate(SstScanBlock block)
    {
        _ = block.Reader.ReadDataBlock(block.BlockIndex);
        block.IsValidated = true;
        _dataBlocksRead = checked(_dataBlocksRead + 1);
    }
}

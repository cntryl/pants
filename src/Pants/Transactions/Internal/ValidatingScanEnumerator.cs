namespace Cntryl.Pants.Transactions.Internal;

sealed class ValidatingScanEnumerator : IAsyncEnumerator<PantsEntry>
{
    readonly IAsyncEnumerator<PantsEntry> _entries;
    readonly IScanReadValidator _validator;
    int _disposed;

    public ValidatingScanEnumerator(
        IAsyncEnumerator<PantsEntry> entries,
        IScanReadValidator validator)
    {
        _entries = entries;
        _validator = validator;
    }

    public PantsEntry Current => _entries.Current;

    public async ValueTask<bool> MoveNextAsync()
    {
        if (!await _entries.MoveNextAsync().ConfigureAwait(false))
        {
            _validator.Complete();
            return false;
        }

        _validator.ValidateKey(_entries.Current.Key.Span);
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            await _entries.DisposeAsync().ConfigureAwait(false);
            _validator.Dispose();
        }
    }
}

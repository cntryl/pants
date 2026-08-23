using System.Collections;

namespace Cntryl.Pants.Transactions.Internal;

sealed class ValidatingScanEnumerator : IEnumerator<PantsEntry>
{
    readonly IEnumerator<PantsEntry> _entries;
    readonly IScanReadValidator _validator;
    int _disposed;

    public ValidatingScanEnumerator(
        IEnumerator<PantsEntry> entries,
        IScanReadValidator validator)
    {
        _entries = entries;
        _validator = validator;
    }

    public PantsEntry Current => _entries.Current;

    object IEnumerator.Current => Current;

    public bool MoveNext()
    {
        if (!_entries.MoveNext())
        {
            _validator.Complete();
            return false;
        }

        _validator.ValidateKey(_entries.Current.Key.Span);
        return true;
    }

    public void Reset() => throw new NotSupportedException();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _entries.Dispose();
            _validator.Dispose();
        }
    }
}

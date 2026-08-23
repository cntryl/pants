namespace Cntryl.Pants;

internal sealed class ValidatingScanEnumerator : IEnumerator<PantsEntry>
{
    private readonly IEnumerator<PantsEntry> _entries;
    private readonly IScanReadValidator _validator;
    private int _disposed;

    public ValidatingScanEnumerator(
        IEnumerator<PantsEntry> entries,
        IScanReadValidator validator)
    {
        _entries = entries;
        _validator = validator;
    }

    public PantsEntry Current => _entries.Current;

    object System.Collections.IEnumerator.Current => Current;

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

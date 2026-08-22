namespace Pants.Tests;

internal sealed class TestCloudLeaseStore : ICloudLeaseStore
{
    CloudLeaseRecord? _lease;
    int _version;

    public Action? AfterNextRead { get; set; }

    public Action? AfterNextReplace { get; set; }

    public bool ApplyIndeterminateReplace { get; set; }

    public Func<CancellationToken, ValueTask>? BeforeNextReplaceAsync { get; set; }

    public bool IndeterminateRead { get; set; }

    public bool IndeterminateReplace { get; set; }

    public CloudLeaseRecord? Lease => _lease;

    public int ReplaceAttempts { get; set; }

    public ValueTask<CloudLeaseSnapshot?> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IndeterminateRead)
        {
            throw new PantsLeaseIndeterminateException(
                "The conditional lease read outcome is unknown.");
        }

        var snapshot = _lease is null
            ? null
            : new CloudLeaseSnapshot(
                _lease,
                _version.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var afterRead = AfterNextRead;
        AfterNextRead = null;
        afterRead?.Invoke();
        return ValueTask.FromResult(snapshot);
    }

    public ValueTask<bool> TryCreateAsync(
        CloudLeaseRecord lease,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_lease is not null)
        {
            return ValueTask.FromResult(false);
        }

        _lease = lease;
        _version++;
        return ValueTask.FromResult(true);
    }

    public async ValueTask<bool> TryReplaceAsync(
        string expectedVersion,
        CloudLeaseRecord lease,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReplaceAttempts++;
        if (!StringComparer.Ordinal.Equals(
                expectedVersion,
                _version.ToString(System.Globalization.CultureInfo.InvariantCulture)))
        {
            return false;
        }

        var beforeReplace = BeforeNextReplaceAsync;
        BeforeNextReplaceAsync = null;
        if (beforeReplace is not null)
        {
            await beforeReplace(cancellationToken).ConfigureAwait(false);
        }

        var indeterminate = IndeterminateReplace;
        IndeterminateReplace = false;
        if (!indeterminate || ApplyIndeterminateReplace)
        {
            _lease = lease;
            _version++;
        }

        var afterReplace = AfterNextReplace;
        AfterNextReplace = null;
        afterReplace?.Invoke();
        if (indeterminate)
        {
            throw new PantsLeaseIndeterminateException(
                "The conditional lease replacement outcome is unknown.");
        }

        return true;
    }
}

using System.Globalization;

namespace Cntryl.Pants.Support.TestDoubles;

sealed class TestCloudLeaseStore : ICloudLeaseStore
{
    int _version;

    public Action? AfterNextRead { get; set; }

    public Action? AfterNextReplace { get; set; }

    public Func<CancellationToken, ValueTask>? BeforeNextCreateAsync { get; set; }

    public bool ApplyIndeterminateReplace { get; set; }

    public bool ApplyReplaceBeforeException { get; set; }

    public Func<CancellationToken, ValueTask>? BeforeNextReplaceAsync { get; set; }

    public bool IndeterminateRead { get; set; }

    public bool IndeterminateReplace { get; set; }

    public PantsException? NextReplaceException { get; set; }

    public CloudLeaseRecord? Lease { get; private set; }

    public int ReplaceAttempts { get; set; }

    public ValueTask<CloudLeaseSnapshot?> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IndeterminateRead)
        {
            throw new PantsLeaseIndeterminateException(
                "The conditional lease read outcome is unknown.");
        }

        var snapshot = Lease is null
            ? null
            : new CloudLeaseSnapshot(
                Lease,
                _version.ToString(CultureInfo.InvariantCulture));
        var afterRead = AfterNextRead;
        AfterNextRead = null;
        afterRead?.Invoke();
        return ValueTask.FromResult(snapshot);
    }

    public async ValueTask<bool> TryCreateAsync(
        CloudLeaseRecord lease,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var beforeCreate = BeforeNextCreateAsync;
        BeforeNextCreateAsync = null;
        if (beforeCreate is not null)
        {
            await beforeCreate(cancellationToken).ConfigureAwait(false);
        }

        if (Lease is not null)
        {
            return false;
        }

        Lease = lease;
        _version++;
        return true;
    }

    public async ValueTask<bool> TryReplaceAsync(
        string expectedVersion,
        CloudLeaseRecord lease,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReplaceAttempts++;
        var beforeReplace = BeforeNextReplaceAsync;
        BeforeNextReplaceAsync = null;
        if (beforeReplace is not null)
        {
            await beforeReplace(cancellationToken).ConfigureAwait(false);
        }

        if (!StringComparer.Ordinal.Equals(
                expectedVersion,
                _version.ToString(CultureInfo.InvariantCulture)))
        {
            return false;
        }

        var indeterminate = IndeterminateReplace;
        IndeterminateReplace = false;
        var replacementException = NextReplaceException;
        NextReplaceException = null;
        if ((!indeterminate && replacementException is null) ||
            ApplyIndeterminateReplace ||
            ApplyReplaceBeforeException)
        {
            Lease = lease;
            _version++;
        }

        var afterReplace = AfterNextReplace;
        AfterNextReplace = null;
        afterReplace?.Invoke();
        if (replacementException is not null)
        {
            throw replacementException;
        }

        if (indeterminate)
        {
            throw new PantsLeaseIndeterminateException(
                "The conditional lease replacement outcome is unknown.");
        }

        return true;
    }

    public void Seed(CloudLeaseRecord lease)
    {
        Lease = lease;
        _version++;
    }
}

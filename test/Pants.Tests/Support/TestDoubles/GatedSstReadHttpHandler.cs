namespace Cntryl.Pants.Support.TestDoubles;

sealed class GatedSstReadHttpHandler : DelegatingHandler
{
    readonly TaskCompletionSource _release =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    readonly TaskCompletionSource _requestStarted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    int _armed;

    public GatedSstReadHttpHandler(HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
    }

    public void Arm() => Volatile.Write(ref _armed, 1);

    public Task WaitUntilRequestStartsAsync(TimeSpan timeout) =>
        _requestStarted.Task.WaitAsync(timeout);

    public void Release() => _release.TrySetResult();

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Get &&
            request.RequestUri?.AbsolutePath.Contains("/sst/", StringComparison.Ordinal) == true &&
            Interlocked.Exchange(ref _armed, 0) != 0)
        {
            _requestStarted.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

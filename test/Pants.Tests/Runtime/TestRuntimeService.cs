namespace Cntryl.Pants.Tests.Runtime;

sealed class TestRuntimeService(
    int capacity,
    TaskCompletionSource? started = null,
    TaskCompletionSource? release = null)
    : ChannelRuntimeService<TestRuntimeRequest, int>(capacity)
{
    readonly List<int> _executedRequests = [];

    public IReadOnlyList<int> ExecutedRequests => _executedRequests;

    protected override async ValueTask<int> DispatchAsync(
        TestRuntimeRequest request,
        CancellationToken cancellationToken)
    {
        _executedRequests.Add(request.Sequence);
        if (request.ShouldFail)
        {
            throw new InvalidOperationException($"Request {request.Sequence} failed.");
        }

        if (request.ShouldWait)
        {
            started?.TrySetResult();
            if (release is not null)
            {
                await release.Task.WaitAsync(cancellationToken);
            }
        }

        return request.Sequence;
    }
}

namespace Cntryl.Pants.Support.TestDoubles;

/// <summary>Records every provider request that targets one SST object, then forwards it.</summary>
sealed class SstRequestRecordingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    readonly Lock _gate = new();
    readonly List<SstRequest> _requests = [];

    public IReadOnlyList<SstRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return _requests.ToArray();
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _requests.Clear();
        }
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        var marker = path.LastIndexOf("/sst/", StringComparison.Ordinal);
        if (marker >= 0 && path.EndsWith(".sst", StringComparison.Ordinal))
        {
            long? rangeBytes = request.Headers.Range?.Ranges.SingleOrDefault() is { } range
                ? (range.To ?? long.MaxValue) - (range.From ?? 0) + 1
                : null;
            lock (_gate)
            {
                _requests.Add(new SstRequest(
                    request.Method.Method,
                    path[(marker + "/sst/".Length)..],
                    rangeBytes));
            }
        }

        return base.SendAsync(request, cancellationToken);
    }

    public sealed record SstRequest(string Method, string Name, long? RangeBytes)
    {
        public bool IsWholeGet => Method == "GET" && RangeBytes is null;

        public bool IsRangedGet => Method == "GET" && RangeBytes is not null;
    }
}

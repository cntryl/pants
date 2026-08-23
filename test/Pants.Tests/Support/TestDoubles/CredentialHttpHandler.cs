namespace Pants.Tests;

internal sealed class CredentialHttpHandler(
    Func<RecordedCredentialRequest, int, HttpResponseMessage> responseFactory) : HttpMessageHandler
{
    readonly Func<RecordedCredentialRequest, int, HttpResponseMessage> _responseFactory =
        responseFactory;
    int _requestCount;

    public List<RecordedCredentialRequest> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var contentHeaders = request.Content is null
            ? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>()
            : request.Content.Headers;
        var headers = request.Headers
            .Concat(contentHeaders)
            .ToDictionary(
                static header => header.Key,
                static header => string.Join(',', header.Value),
                StringComparer.OrdinalIgnoreCase);
        var recorded = new RecordedCredentialRequest(
            request.Method,
            request.RequestUri!,
            headers,
            body);
        Requests.Add(recorded);
        return _responseFactory(recorded, Interlocked.Increment(ref _requestCount));
    }
}

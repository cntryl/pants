using System.Net;

namespace Cntryl.Pants.Support.TestDoubles;

/// <summary>
///     Failed-precondition responses as each provider really sends them: a bare 412 is not
///     enough, because only the provider's own condition code means a write lost its condition.
/// </summary>
static class ProviderPreconditionResponses
{
    public static HttpResponseMessage S3() => new(HttpStatusCode.PreconditionFailed)
    {
        Content = new StringContent("<Error><Code>PreconditionFailed</Code></Error>")
    };

    public static HttpResponseMessage Azure()
    {
        var response = new HttpResponseMessage(HttpStatusCode.PreconditionFailed);
        response.Headers.TryAddWithoutValidation("x-ms-error-code", "ConditionNotMet");
        return response;
    }

    public static HttpResponseMessage GcsJson() => new(HttpStatusCode.PreconditionFailed)
    {
        Content = new StringContent(
            "{\"error\":{\"errors\":[{\"reason\":\"conditionNotMet\"}]}}")
    };
}

using System.Collections.Immutable;
using System.Net;
using System.Security.Authentication;

namespace Cntryl.Pants.Cloud.Internal;

static class CloudConfigurationPreflight
{
    internal delegate ICloudObjectStore StoreFactory(
        PantsCloudStorageLocation location,
        TimeSpan remaining);

    public static ValueTask<PantsCloudValidationReport> RunAsync(
        PantsCloudStorageLocation location,
        PantsCloudPreflightOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(location);
        return RunLocationsAsync(
            [new CloudStorageLocations.Item(location, [PantsCloudStorageRole.Standalone])],
            options ?? PantsCloudPreflightOptions.Default,
            static (candidate, remaining) => CloudObjectStoreFactory.Create(candidate, remaining),
            cancellationToken);
    }

    public static ValueTask<PantsCloudValidationReport> RunAsync(
        PantsCloudStorageTopology topology,
        PantsCloudPreflightOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(topology);
        return RunLocationsAsync(
            CloudStorageLocations.Unique(topology),
            options ?? PantsCloudPreflightOptions.Default,
            static (location, remaining) => CloudObjectStoreFactory.Create(location, remaining),
            cancellationToken);
    }

    internal static ValueTask<PantsCloudValidationReport> RunAsync(
        PantsCloudStorageTopology topology,
        PantsCloudPreflightOptions options,
        StoreFactory factory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(factory);
        return RunLocationsAsync(
            CloudStorageLocations.Unique(topology),
            options,
            factory,
            cancellationToken);
    }

    static async ValueTask<PantsCloudValidationReport> RunLocationsAsync(
        IReadOnlyList<CloudStorageLocations.Item> locations,
        PantsCloudPreflightOptions options,
        StoreFactory factory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.Deadline);
        var started = TimeProvider.System.GetTimestamp();
        var tasks = locations.Select(item => RunLocationAsync(
                item,
                options.Deadline,
                started,
                factory,
                cancellationToken,
                deadline.Token))
            .ToArray();

        try
        {
            var reports = await Task.WhenAll(tasks)
                .WaitAsync(options.Deadline, cancellationToken)
                .ConfigureAwait(false);
            return Combine(reports);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException)
        {
            await deadline.CancelAsync().ConfigureAwait(false);
            return Combine(tasks.Select((task, index) =>
                task.IsCompletedSuccessfully
                    ? task.Result
                    : Timeout(locations[index], "Cloud preflight absolute deadline expired.")));
        }
    }

    static async Task<PantsCloudValidationReport> RunLocationAsync(
        CloudStorageLocations.Item item,
        TimeSpan totalBudget,
        long started,
        StoreFactory factory,
        CancellationToken callerToken,
        CancellationToken deadlineToken)
    {
        var structural = CloudConfigurationValidator.Validate(item.Location, item.Roles);
        if (!structural.IsValid)
        {
            return structural;
        }

        ICloudObjectStore store;
        try
        {
            store = factory(item.Location, Remaining(totalBudget, started));
        }
        catch (Exception exception) when (!callerToken.IsCancellationRequested)
        {
            var failure = Classify(exception, deadlineToken.IsCancellationRequested);
            return Append(
                structural,
                Failure(
                    item,
                    PantsCloudCheckCode.BackendResolution,
                    failure,
                    Message(PantsCloudCheckCode.BackendResolution, failure)),
                Unverified(item, PantsCloudCheckCode.NamespaceList, "Provider backend did not resolve."),
                Unverified(item, PantsCloudCheckCode.ObjectHead, "Provider backend did not resolve."),
                Unverified(item, PantsCloudCheckCode.RangedRead, "Provider backend did not resolve."));
        }

        var findings = new List<PantsCloudValidationFinding>(structural.Findings)
        {
            Passed(item, PantsCloudCheckCode.BackendResolution, "Provider backend resolved.")
        };

        CloudObjectListPage page;
        try
        {
            page = await store.ListPageAsync("", null, deadlineToken).ConfigureAwait(false);
            findings.Add(Passed(item, PantsCloudCheckCode.NamespaceList, "Namespace LIST passed."));
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var failure = Classify(exception, deadlineToken.IsCancellationRequested);
            findings.Add(Failure(
                item,
                PantsCloudCheckCode.NamespaceList,
                failure,
                Message(PantsCloudCheckCode.NamespaceList, failure)));
            findings.Add(Unverified(item, PantsCloudCheckCode.ObjectHead, "Object HEAD depends on LIST."));
            findings.Add(Unverified(item, PantsCloudCheckCode.RangedRead, "Ranged read depends on LIST."));
            return new PantsCloudValidationReport(findings);
        }

        var objectKey = page.ObjectKeys.Count == 0 ? null : page.ObjectKeys[0];
        if (objectKey is null)
        {
            findings.Add(NotApplicable(
                item,
                PantsCloudCheckCode.ObjectHead,
                "Namespace is empty; object HEAD is not applicable."));
            findings.Add(NotApplicable(
                item,
                PantsCloudCheckCode.RangedRead,
                "Namespace is empty; object read is not applicable."));
            return new PantsCloudValidationReport(findings);
        }

        CloudObjectMetadata? metadata;
        try
        {
            metadata = await store.HeadAsync(objectKey, deadlineToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var failure = Classify(exception, deadlineToken.IsCancellationRequested);
            findings.Add(Failure(
                item,
                PantsCloudCheckCode.ObjectHead,
                failure,
                Message(PantsCloudCheckCode.ObjectHead, failure)));
            findings.Add(Unverified(item, PantsCloudCheckCode.RangedRead, "Ranged read depends on HEAD."));
            return new PantsCloudValidationReport(findings);
        }

        if (metadata is null)
        {
            findings.Add(Failure(
                item,
                PantsCloudCheckCode.ObjectHead,
                PantsCloudFailureKind.NotFound,
                Message(PantsCloudCheckCode.ObjectHead, PantsCloudFailureKind.NotFound)));
            findings.Add(Unverified(item, PantsCloudCheckCode.RangedRead, "Ranged read depends on HEAD."));
            return new PantsCloudValidationReport(findings);
        }

        findings.Add(Passed(item, PantsCloudCheckCode.ObjectHead, "Object HEAD passed."));
        try
        {
            var value = metadata.SizeBytes == 0
                ? await store.GetAsync(objectKey, deadlineToken).ConfigureAwait(false)
                : await store.GetRangeAsync(objectKey, 0, 1, deadlineToken).ConfigureAwait(false);
            if (value is null)
            {
                findings.Add(Failure(
                    item,
                    PantsCloudCheckCode.RangedRead,
                    PantsCloudFailureKind.NotFound,
                    Message(PantsCloudCheckCode.RangedRead, PantsCloudFailureKind.NotFound)));
            }
            else if (value.Data.Length != (metadata.SizeBytes == 0 ? 0 : 1))
            {
                findings.Add(Failure(
                    item,
                    PantsCloudCheckCode.RangedRead,
                    PantsCloudFailureKind.Provider,
                    "Bounded object read returned an unexpected byte count."));
            }
            else
            {
                findings.Add(Passed(item, PantsCloudCheckCode.RangedRead, "Bounded object read passed."));
            }
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var failure = Classify(exception, deadlineToken.IsCancellationRequested);
            findings.Add(Failure(
                item,
                PantsCloudCheckCode.RangedRead,
                failure,
                Message(PantsCloudCheckCode.RangedRead, failure)));
        }

        return new PantsCloudValidationReport(findings);
    }

    static PantsCloudValidationReport Timeout(CloudStorageLocations.Item item, string message)
    {
        var structural = CloudConfigurationValidator.Validate(item.Location, item.Roles);
        return Append(
            structural,
            Failure(
                item,
                PantsCloudCheckCode.BackendResolution,
                PantsCloudFailureKind.Timeout,
                message),
            Unverified(item, PantsCloudCheckCode.NamespaceList, "Preflight deadline expired."),
            Unverified(item, PantsCloudCheckCode.ObjectHead, "Preflight deadline expired."),
            Unverified(item, PantsCloudCheckCode.RangedRead, "Preflight deadline expired."));
    }

    static PantsCloudValidationReport Combine(IEnumerable<PantsCloudValidationReport> reports) =>
        new(reports.SelectMany(static report => report.Findings));

    static PantsCloudValidationReport Append(
        PantsCloudValidationReport report,
        params PantsCloudValidationFinding[] findings) =>
        new(report.Findings.Concat(findings));

    static PantsCloudValidationFinding Passed(
        CloudStorageLocations.Item item,
        PantsCloudCheckCode code,
        string message) => Finding(
            item,
            code,
            PantsCloudCheckOutcome.Passed,
            PantsCloudCheckSeverity.Information,
            PantsCloudFailureKind.None,
            message);

    static PantsCloudValidationFinding Failure(
        CloudStorageLocations.Item item,
        PantsCloudCheckCode code,
        PantsCloudFailureKind failureKind,
        string message) => Finding(
            item,
            code,
            PantsCloudCheckOutcome.Failed,
            PantsCloudCheckSeverity.Error,
            failureKind,
            message);

    static PantsCloudValidationFinding Unverified(
        CloudStorageLocations.Item item,
        PantsCloudCheckCode code,
        string message) => Finding(
            item,
            code,
            PantsCloudCheckOutcome.Unverified,
            PantsCloudCheckSeverity.Warning,
            PantsCloudFailureKind.Unsupported,
            message);

    static PantsCloudValidationFinding NotApplicable(
        CloudStorageLocations.Item item,
        PantsCloudCheckCode code,
        string message) => Finding(
            item,
            code,
            PantsCloudCheckOutcome.Warning,
            PantsCloudCheckSeverity.Warning,
            PantsCloudFailureKind.NotApplicable,
            message);

    static PantsCloudValidationFinding Finding(
        CloudStorageLocations.Item item,
        PantsCloudCheckCode code,
        PantsCloudCheckOutcome outcome,
        PantsCloudCheckSeverity severity,
        PantsCloudFailureKind failureKind,
        string message) => new(
            CloudConfigurationValidator.Kind(item.Location.Provider),
            item.Roles,
            PantsCloudValidationMode.LivePreflight,
            code,
            outcome,
            severity,
            failureKind,
            message);

    static PantsCloudFailureKind Classify(Exception exception, bool deadlineExpired)
    {
        if (deadlineExpired || exception is TimeoutException or PantsTimeoutException)
        {
            return PantsCloudFailureKind.Timeout;
        }

        if (exception is CloudPreflightException preflight)
        {
            return preflight.FailureKind;
        }

        if (exception is AuthenticationException)
        {
            return PantsCloudFailureKind.EndpointOrTls;
        }

        if (exception is HttpRequestException request)
        {
            return request.StatusCode switch
            {
                HttpStatusCode.Unauthorized => PantsCloudFailureKind.Authentication,
                HttpStatusCode.Forbidden => PantsCloudFailureKind.Authorization,
                HttpStatusCode.NotFound => PantsCloudFailureKind.NotFound,
                null => PantsCloudFailureKind.EndpointOrTls,
                _ => PantsCloudFailureKind.Provider
            };
        }

        var message = exception.Message;
        if (message.Contains("HTTP 401", StringComparison.OrdinalIgnoreCase))
        {
            return PantsCloudFailureKind.Authentication;
        }

        if (message.Contains("HTTP 403", StringComparison.OrdinalIgnoreCase))
        {
            return PantsCloudFailureKind.Authorization;
        }

        if (message.Contains("HTTP 404", StringComparison.OrdinalIgnoreCase))
        {
            return PantsCloudFailureKind.NotFound;
        }

        return exception.InnerException is HttpRequestException or AuthenticationException
            ? PantsCloudFailureKind.EndpointOrTls
            : PantsCloudFailureKind.Provider;
    }

    static string Message(PantsCloudCheckCode code, PantsCloudFailureKind failureKind) =>
        $"{CheckName(code)} failed: {FailureName(failureKind)}.";

    static string CheckName(PantsCloudCheckCode code) => code switch
    {
        PantsCloudCheckCode.BackendResolution => "Provider backend resolution",
        PantsCloudCheckCode.NamespaceList => "Namespace LIST",
        PantsCloudCheckCode.ObjectHead => "Object HEAD",
        PantsCloudCheckCode.RangedRead => "Bounded object read",
        _ => "Cloud check"
    };

    static string FailureName(PantsCloudFailureKind failureKind) => failureKind switch
    {
        PantsCloudFailureKind.Timeout => "deadline expired",
        PantsCloudFailureKind.Authentication => "authentication was rejected",
        PantsCloudFailureKind.Authorization => "authorization was rejected",
        PantsCloudFailureKind.NotFound => "the selected object was not found",
        PantsCloudFailureKind.EndpointOrTls => "endpoint or TLS negotiation failed",
        PantsCloudFailureKind.Unsupported => "the capability is unsupported",
        _ => "the provider operation failed"
    };

    static TimeSpan Remaining(TimeSpan totalBudget, long started)
    {
        var elapsed = TimeProvider.System.GetElapsedTime(started);
        var remaining = totalBudget - elapsed;
        return remaining < TimeSpan.FromMilliseconds(1)
            ? TimeSpan.FromMilliseconds(1)
            : remaining;
    }
}

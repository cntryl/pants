namespace Pants;

internal static class RuntimeExceptionMapper
{
    public static Exception ToPublicException(Exception exception) => exception switch
    {
        OperationCanceledException => exception,
        PantsException => exception,
        IOException ioException => PantsException.FromIOException(ioException),
        UnauthorizedAccessException accessException => new PantsIOException(
            accessException.Message,
            accessException),
        _ => new PantsInternalException(
            "An unexpected runtime failure occurred.",
            exception)
    };
}

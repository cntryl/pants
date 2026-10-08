namespace Cntryl.Pants;

/// <summary>
///     How a failure should be acted on, as opposed to what produced it. This is the single
///     classification authority for hosts deciding whether to retry, back off, step down or halt.
/// </summary>
public enum PantsErrorSeverity
{
    /// <summary>The caller asked for something invalid. Retrying is pointless.</summary>
    Caller,

    /// <summary>An environmental failure that may succeed on a later attempt.</summary>
    Transient,

    /// <summary>A bounded resource is temporarily full. Retry after it is released.</summary>
    Backpressure,

    /// <summary>This writer no longer holds, or never held, durable authority.</summary>
    Fenced,

    /// <summary>
    ///     An engine invariant or a configured limit was violated. Durable data may still be
    ///     intact, so this is reported rather than retried, and is deliberately not retryable.
    /// </summary>
    Defect,

    /// <summary>Durable state cannot be trusted. The engine must stop, not retry.</summary>
    Fatal
}

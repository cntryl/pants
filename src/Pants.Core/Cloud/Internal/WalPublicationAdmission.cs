using Cntryl.Pants.Storage.Internal;

namespace Cntryl.Pants.Cloud.Internal;

/// <summary>
///     Reserves the transient payload envelope for a foreground WAL upload and readback without
///     queuing behind maintenance provider operations. A blocked retirement read must not delay a
///     CloudStrict commit.
/// </summary>
sealed class WalPublicationAdmission(ResourceBudget budget)
{
    public IDisposable Admit(long sizeBytes)
    {
        var bytes = ImmutablePublicationEnvelope.For(sizeBytes);
        if (bytes > budget.Limit)
        {
            throw PantsException.ResourceLimit(
                $"Publishing a WAL segment of {sizeBytes} bytes needs a {bytes}-byte maintenance " +
                $"envelope; the pool holds {budget.Limit} bytes.");
        }

        return budget.Reserve(bytes);
    }
}

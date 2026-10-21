namespace Cntryl.Pants.Cloud.Internal;

sealed class SstPublicationDeferredException : Exception
{
    public SstPublicationDeferredException()
        : base("SST mirror publication is deferred while maintenance memory is busy.")
    {
    }
}

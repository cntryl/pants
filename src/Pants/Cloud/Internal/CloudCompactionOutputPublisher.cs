namespace Cntryl.Pants.Cloud.Internal;

delegate ValueTask CloudCompactionOutputPublisher(
    IReadOnlyList<string> outputNames,
    CancellationToken cancellationToken);

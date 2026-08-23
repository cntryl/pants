namespace Cntryl.Pants;

delegate ValueTask CloudCompactionOutputPublisher(
    IReadOnlyList<string> outputNames,
    CancellationToken cancellationToken);

namespace Cntryl.Pants.Transactions.Internal;

/// <param name="MaximumEndNode">
///     Index of the node whose end is the largest in this node's subtree; the node's own index when
///     its own end is the largest.
/// </param>
sealed record TransactionSpillRangeNode(
    ulong Ordinal,
    ulong Left,
    ulong Right,
    byte[] Start,
    byte[] End,
    ulong MaximumEndNode);

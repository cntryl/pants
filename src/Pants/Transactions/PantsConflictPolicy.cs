namespace Cntryl.Pants.Transactions;

public enum PantsConflictPolicy
{
    LastWriteWins,
    AbortOnWriteConflict
}

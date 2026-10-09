namespace Cntryl.Pants.Cloud;

/// <summary>One single-mutation WAL transaction a retirement test publishes.</summary>
sealed record WalTestRecord(uint Family, string Key, int ValueBytes);

namespace Cntryl.Pants.Runtime.Internal;

sealed class PantsColumnFamilyHandle : IPantsColumnFamily
{
    internal readonly ColumnFamilyIdentity Identity;
    readonly object _owner;

    internal PantsColumnFamilyHandle(object owner, uint id, string name, int generation)
    {
        _owner = owner;
        Id = id;
        Name = name;
        Generation = generation;
        Identity = new ColumnFamilyIdentity(id, name, generation);
    }

    internal int Generation { get; }

    public uint Id { get; }
    public string Name { get; }

    public override string ToString() => $"{Name}#{Id}";

    internal bool IsOwnedBy(object owner) => ReferenceEquals(_owner, owner);
}

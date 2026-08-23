namespace Cntryl.Pants.Storage.Internal.Sst.Trie;

readonly record struct TrieEdge(byte FirstByte, uint ChildIndex);

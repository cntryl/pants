namespace Cntryl.Pants.Storage.Internal.Sst.Trie;

readonly record struct MidgeTrieEdge(byte FirstByte, uint ChildIndex);

namespace Cntryl.Pants;

internal readonly record struct MidgeTrieEdge(byte FirstByte, uint ChildIndex);

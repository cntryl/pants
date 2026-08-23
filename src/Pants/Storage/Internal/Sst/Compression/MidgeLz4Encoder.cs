using System.Buffers.Binary;

namespace Cntryl.Pants;

// Matches lz4_flex 0.14's canonical independent-block output so Pants and Midge
// emit identical persisted bytes. K4os remains the hardened decoder.
internal static class MidgeLz4Encoder
{
    const int HashTableSize = 4 * 1024;
    const int MinimumMatchLength = 4;
    const int MatchFindLimit = 12;
    const int EndOffset = 6;
    const int MinimumCompressibleLength = MatchFindLimit + 1;
    const int MaximumDistance = ushort.MaxValue;
    const int IncreaseStepSizeBitShift = 5;

    internal static byte[] CompressWithSizePrefix(ReadOnlySpan<byte> input)
    {
        var maximumOutputSize = checked(16 + 4 + (int)((long)input.Length * 110 / 100));
        using var output = new MemoryStream(maximumOutputSize);
        Span<byte> size = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(size, checked((uint)input.Length));
        output.Write(size);

        Compress(input, output);
        return output.ToArray();
    }

    static void Compress(ReadOnlySpan<byte> input, Stream output)
    {
        if (input.Length < MinimumCompressibleLength)
        {
            WriteLastLiterals(output, input, 0);
            return;
        }

        var hashTable = new int[HashTableSize];
        var useSmallHash = input.Length < ushort.MaxValue;
        var endPositionCheck = input.Length - MatchFindLimit;
        var literalStart = 0;
        var current = 1;
        Span<byte> offsetBytes = stackalloc byte[sizeof(ushort)];
        Put(hashTable, Hash(input, 0, useSmallHash), 0);

        while (true)
        {
            var nonMatchCount = 1 << IncreaseStepSizeBitShift;
            var nextCurrent = current;
            int candidate;
            ushort offset;

            while (true)
            {
                var stepSize = nonMatchCount >> IncreaseStepSizeBitShift;
                nonMatchCount++;
                current = nextCurrent;
                nextCurrent += stepSize;

                if (current > endPositionCheck)
                {
                    WriteLastLiterals(output, input, literalStart);
                    return;
                }

                var hash = Hash(input, current, useSmallHash);
                candidate = Get(hashTable, hash);
                Put(hashTable, hash, current);
                if (current - candidate > MaximumDistance)
                {
                    continue;
                }

                offset = checked((ushort)(current - candidate));
                if (BinaryPrimitives.ReadUInt32LittleEndian(input[candidate..]) ==
                    BinaryPrimitives.ReadUInt32LittleEndian(input[current..]))
                {
                    break;
                }
            }

            while (candidate > 0 &&
                   current > literalStart &&
                   input[current - 1] == input[candidate - 1])
            {
                current--;
                candidate--;
            }

            var literalLength = current - literalStart;
            current += MinimumMatchLength;
            candidate += MinimumMatchLength;
            var duplicateLength = CountSameBytes(input, ref current, candidate);

            Put(hashTable, Hash(input, current - 2, useSmallHash), current - 2);
            output.WriteByte(CreateToken(literalLength, duplicateLength));
            if (literalLength >= 0x0f)
            {
                WriteInteger(output, literalLength - 0x0f);
            }

            output.Write(input[literalStart..(literalStart + literalLength)]);
            BinaryPrimitives.WriteUInt16LittleEndian(offsetBytes, offset);
            output.Write(offsetBytes);
            if (duplicateLength >= 0x0f)
            {
                WriteInteger(output, duplicateLength - 0x0f);
            }

            literalStart = current;
        }
    }

    static int CountSameBytes(ReadOnlySpan<byte> input, ref int current, int candidate)
    {
        var inputEnd = current + Math.Min(
            input.Length - (current + EndOffset),
            input.Length - candidate);
        var start = current;
        while (current < inputEnd && input[current] == input[candidate])
        {
            current++;
            candidate++;
        }

        return current - start;
    }

    static int Hash(ReadOnlySpan<byte> input, int position, bool useSmallHash)
    {
        if (useSmallHash)
        {
            var sequence = BinaryPrimitives.ReadUInt32LittleEndian(input[position..]);
            return (int)(unchecked(sequence * 2_654_435_761U) >> 16);
        }

        var wideSequence = BinaryPrimitives.ReadUInt64LittleEndian(input[position..]);
        return (int)(unchecked((wideSequence << 24) * 889_523_592_379UL) >> 48);
    }

    static int Get(int[] hashTable, int hash) => hashTable[hash >> 4];

    static void Put(int[] hashTable, int hash, int value) => hashTable[hash >> 4] = value;

    static byte CreateToken(int literalLength, int duplicateLength)
    {
        var literalToken = literalLength < 0x0f ? literalLength << 4 : 0xf0;
        var duplicateToken = duplicateLength < 0x0f ? duplicateLength : 0x0f;
        return checked((byte)(literalToken | duplicateToken));
    }

    static void WriteInteger(Stream output, int value)
    {
        while (value >= byte.MaxValue)
        {
            value -= byte.MaxValue;
            output.WriteByte(byte.MaxValue);
        }

        output.WriteByte(checked((byte)value));
    }

    static void WriteLastLiterals(Stream output, ReadOnlySpan<byte> input, int start)
    {
        var literalLength = input.Length - start;
        output.WriteByte(checked((byte)(literalLength < 0x0f ? literalLength << 4 : 0xf0)));
        if (literalLength >= 0x0f)
        {
            WriteInteger(output, literalLength - 0x0f);
        }

        output.Write(input[start..]);
    }
}

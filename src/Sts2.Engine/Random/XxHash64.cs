using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Sts2.Engine.Random;

/// <summary>
/// XXH64, bit-for-bit identical to System.IO.Hashing.XxHash64.HashToUInt64 (seed 0).
/// The game hashes seed strings with it via StringHelper.GetDeterministicHashCode.
/// </summary>
public static class XxHash64
{
    private const ulong Prime1 = 11400714785074694791UL;
    private const ulong Prime2 = 14029467366897019727UL;
    private const ulong Prime3 = 1609587929392839161UL;
    private const ulong Prime4 = 9650029242287828579UL;
    private const ulong Prime5 = 2870177450012600261UL;

    public static ulong HashToUInt64(string value, ulong seed = 0) => HashToUInt64(Encoding.UTF8.GetBytes(value), seed);

    public static ulong HashToUInt64(ReadOnlySpan<byte> data, ulong seed = 0)
    {
        int length = data.Length;
        int offset = 0;
        ulong hash;

        if (length >= 32)
        {
            ulong v1 = seed + Prime1 + Prime2;
            ulong v2 = seed + Prime2;
            ulong v3 = seed;
            ulong v4 = seed - Prime1;

            int limit = length - 32;
            do
            {
                v1 = Round(v1, BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset)));
                v2 = Round(v2, BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset + 8)));
                v3 = Round(v3, BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset + 16)));
                v4 = Round(v4, BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset + 24)));
                offset += 32;
            }
            while (offset <= limit);

            hash = BitOperations.RotateLeft(v1, 1) + BitOperations.RotateLeft(v2, 7)
                + BitOperations.RotateLeft(v3, 12) + BitOperations.RotateLeft(v4, 18);
            hash = MergeRound(hash, v1);
            hash = MergeRound(hash, v2);
            hash = MergeRound(hash, v3);
            hash = MergeRound(hash, v4);
        }
        else
        {
            hash = seed + Prime5;
        }

        hash += (ulong)length;

        while (offset <= length - 8)
        {
            ulong k1 = Round(0, BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset)));
            hash ^= k1;
            hash = BitOperations.RotateLeft(hash, 27) * Prime1 + Prime4;
            offset += 8;
        }

        if (offset <= length - 4)
        {
            hash ^= BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset)) * Prime1;
            hash = BitOperations.RotateLeft(hash, 23) * Prime2 + Prime3;
            offset += 4;
        }

        while (offset < length)
        {
            hash ^= data[offset] * Prime5;
            hash = BitOperations.RotateLeft(hash, 11) * Prime1;
            offset++;
        }

        hash ^= hash >> 33;
        hash *= Prime2;
        hash ^= hash >> 29;
        hash *= Prime3;
        hash ^= hash >> 32;
        return hash;
    }

    private static ulong Round(ulong accumulator, ulong input)
    {
        accumulator += input * Prime2;
        accumulator = BitOperations.RotateLeft(accumulator, 31);
        accumulator *= Prime1;
        return accumulator;
    }

    private static ulong MergeRound(ulong accumulator, ulong value)
    {
        accumulator ^= Round(0, value);
        accumulator = accumulator * Prime1 + Prime4;
        return accumulator;
    }
}

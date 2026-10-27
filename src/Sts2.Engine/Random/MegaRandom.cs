using System.Numerics;

namespace Sts2.Engine.Random;

/// <summary>
/// xoshiro256** with splitmix64 seeding, copied from the game's MegaRandom.
/// Reference: _research/decompiled-v0.111.0/MegaCrit/sts2/Core/Random/MegaRandom.cs
/// </summary>
public sealed class MegaRandom
{
    private const double IncrementDouble = 1.1102230246251565E-16;
    private const float IncrementFloat = 5.9604645E-08f;

    private ulong _s0;
    private ulong _s1;
    private ulong _s2;
    private ulong _s3;

    public MegaRandom(ulong seed) => Reinitialise(seed);

    public static ulong Splitmix64(ref ulong x)
    {
        ulong num = x += 11400714819323198485UL;
        num = (num ^ (num >> 30)) * 13787848793156543929UL;
        num = (num ^ (num >> 27)) * 10723151780598845931UL;
        return num ^ (num >> 31);
    }

    public void Reinitialise(ulong seed)
    {
        _s0 = Splitmix64(ref seed);
        _s1 = Splitmix64(ref seed);
        _s2 = Splitmix64(ref seed);
        _s3 = Splitmix64(ref seed);
    }

    public void Reinitialise(ulong s0, ulong s1, ulong s2, ulong s3)
    {
        _s0 = s0;
        _s1 = s1;
        _s2 = s2;
        _s3 = s3;
    }

    public (ulong S0, ulong S1, ulong S2, ulong S3) State => (_s0, _s1, _s2, _s3);

    private ulong NextULongInner()
    {
        ulong s = _s0;
        ulong s2 = _s1;
        ulong s3 = _s2;
        ulong s4 = _s3;
        ulong result = BitOperations.RotateLeft(s2 * 5, 7) * 9;
        ulong num = s2 << 17;
        s3 ^= s;
        s4 ^= s2;
        s2 ^= s3;
        s ^= s4;
        s3 ^= num;
        s4 = BitOperations.RotateLeft(s4, 45);
        _s0 = s;
        _s1 = s2;
        _s2 = s3;
        _s3 = s4;
        return result;
    }

    public int Next(int maxValue)
    {
        if (maxValue < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxValue), maxValue, "maxValue must be > 0");
        }
        return NextInner(maxValue);
    }

    public int Next(int minValue, int maxValue)
    {
        if (minValue >= maxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(maxValue), maxValue, "maxValue must be > minValue");
        }
        long range = (long)maxValue - minValue;
        if (range <= int.MaxValue)
        {
            return NextInner((int)range) + minValue;
        }
        return (int)(NextInner(range) + minValue);
    }

    public double NextDouble() => (double)(NextULongInner() >> 11) * IncrementDouble;

    public int NextInt() => (int)(NextULongInner() >> 33);

    public uint NextUInt() => (uint)NextULongInner();

    public ulong NextULong() => NextULongInner();

    public bool NextBool() => (NextULongInner() & 0x8000000000000000UL) != 0;

    public float NextFloat() => (float)(NextULongInner() >> 40) * IncrementFloat;

    private int NextInner(int maxValue) => (int)(NextDouble() * maxValue);

    private long NextInner(long maxValue) => (long)(NextDouble() * maxValue);
}

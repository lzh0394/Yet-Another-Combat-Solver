namespace Sts2.Engine.Random;

/// <summary>
/// Deterministic RNG with a call counter, mirroring the game's Rng class.
/// Reference: _research/decompiled-v0.111.0/MegaCrit/sts2/Core/Random/Rng.cs
/// </summary>
public sealed class Rng
{
    private readonly MegaRandom _random;
    private int _counter;

    public Rng(ulong seed = 0UL) => _random = new MegaRandom(seed);

    public Rng(ulong seed, string name) : this(seed + StringHash.Deterministic(name)) { }

    public int Counter => _counter;

    public (ulong S0, ulong S1, ulong S2, ulong S3) State => _random.State;

    public bool NextBool()
    {
        _counter++;
        return _random.Next(2) == 0;
    }

    public int NextInt(int maxExclusive = int.MaxValue)
    {
        _counter++;
        return _random.Next(maxExclusive);
    }

    public int NextInt(int minInclusive, int maxExclusive)
    {
        if (minInclusive >= maxExclusive)
        {
            throw new ArgumentOutOfRangeException(nameof(minInclusive), "Minimum must be lower than maximum.");
        }
        _counter++;
        return _random.Next(minInclusive, maxExclusive);
    }

    public uint NextUnsignedInt(uint minInclusive, uint maxExclusive)
    {
        if (minInclusive >= maxExclusive)
        {
            throw new ArgumentOutOfRangeException(nameof(minInclusive), "Minimum must be lower than maximum.");
        }
        _counter++;
        double num = _random.NextDouble();
        double num2 = maxExclusive - minInclusive;
        return minInclusive + (uint)(num * num2);
    }

    public ulong NextUnsignedLong() 
    {
        _counter++;
        return _random.NextULong();
    }

    public ulong NextUnsignedLong(ulong minInclusive, ulong maxExclusive)
    {
        if (minInclusive >= maxExclusive)
        {
            throw new ArgumentOutOfRangeException(nameof(minInclusive), "Minimum must be lower than maximum.");
        }
        _counter++;
        double num = _random.NextDouble();
        double num2 = maxExclusive - minInclusive;
        return minInclusive + (ulong)(num * num2);
    }

    public float NextFloat(float max = 1f) => NextFloat(0f, max);

    public float NextFloat(float min, float max)
    {
        if (min > max)
        {
            throw new ArgumentOutOfRangeException(nameof(min), "Minimum must not be higher than maximum.");
        }
        _counter++;
        return (float)(_random.NextDouble() * (max - min) + min);
    }

    public double NextDouble()
    {
        _counter++;
        return _random.NextDouble();
    }

    public double NextDouble(double min, double max)
    {
        if (min > max)
        {
            throw new ArgumentOutOfRangeException(nameof(min), "Minimum must not be higher than maximum.");
        }
        _counter++;
        return _random.NextDouble() * (max - min) + min;
    }

    public T? NextItem<T>(IEnumerable<T> items)
    {
        T[] array = items as T[] ?? items.ToArray();
        if (array.Length == 0)
        {
            return default;
        }
        return array[NextInt(0, array.Length)];
    }

    public void Shuffle<T>(IList<T> list)
    {
        for (int num = list.Count - 1; num > 0; num--)
        {
            int num2 = NextInt(num + 1);
            (list[num], list[num2]) = (list[num2], list[num]);
        }
    }
}

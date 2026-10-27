using Sts2.Engine.Data;
using Sts2.Engine.Random;

namespace Sts2.Engine.Combat;

/// <summary>
/// Replicates EncounterModel.GenerateMonsters for the encounters whose source picks monsters at
/// runtime (the data package can only list AllPossibleMonsters for those). Each recipe mirrors the
/// RNG call order of the decompiled v0.111.0 source, because the monster ids themselves are drawn.
/// </summary>
internal static class EncounterGenerators
{
    public static List<(string Id, string? Slot)>? Generate(string encounterEntry, DataPack data, Rng rng)
    {
        switch (encounterEntry)
        {
            case "SLIMES_WEAK":
                return SlimesWeak(rng);

            default:
                return null;
        }
    }

    /// <summary>SlimesWeak.cs: pick small A, drop it, medium, then small B from the remainder.</summary>
    private static List<(string, string?)> SlimesWeak(Rng rng)
    {
        string[] small = { "MONSTER.LEAF_SLIME_S", "MONSTER.TWIG_SLIME_S" };
        string[] medium = { "MONSTER.LEAF_SLIME_M", "MONSTER.TWIG_SLIME_M" };
        List<string> pool = small.ToList();
        string first = pool[rng.NextInt(pool.Count)];
        pool.Remove(first);
        string second = pool[rng.NextInt(pool.Count)];
        string mid = medium[rng.NextInt(medium.Length)];
        return new List<(string, string?)> { (first, null), (mid, null), (second, null) };
    }
}

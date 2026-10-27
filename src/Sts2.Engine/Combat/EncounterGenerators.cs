using Sts2.Engine.Data;
using Sts2.Engine.Random;

namespace Sts2.Engine.Combat;

/// <summary>One monster spawned by an encounter recipe.</summary>
internal readonly record struct EncounterSpawn(
    string Id,
    string? Slot = null,
    int? Starter = null,
    int HpReduction = 0,
    string? InitialState = null);

/// <summary>
/// Replicates EncounterModel.GenerateMonsters for the encounters whose source picks monsters at
/// runtime (the data package can only list AllPossibleMonsters for those). Each recipe mirrors the
/// RNG call order of the decompiled v0.111.0 source, because the monster ids themselves are drawn.
/// </summary>
internal static class EncounterGenerators
{
    private static readonly string[] SmallSlimes = { "MONSTER.LEAF_SLIME_S", "MONSTER.TWIG_SLIME_S" };
    private static readonly string[] MediumSlimes = { "MONSTER.LEAF_SLIME_M", "MONSTER.TWIG_SLIME_M" };
    private static readonly string[] BowlbugWorkers = { "MONSTER.BOWLBUG_EGG", "MONSTER.BOWLBUG_SILK", "MONSTER.BOWLBUG_NECTAR" };
    private static readonly string[] BowlbugWeakBugs = { "MONSTER.BOWLBUG_EGG", "MONSTER.BOWLBUG_NECTAR" };
    private static readonly string[] Raiders =
    {
        "MONSTER.AXE_RUBY_RAIDER",
        "MONSTER.ASSASSIN_RUBY_RAIDER",
        "MONSTER.BRUTE_RUBY_RAIDER",
        "MONSTER.CROSSBOW_RUBY_RAIDER",
        "MONSTER.TRACKER_RUBY_RAIDER",
    };

    public static List<EncounterSpawn>? Generate(string encounterEntry, DataPack data, Rng rng)
    {
        switch (encounterEntry)
        {
            case "SLIMES_WEAK":
                return SlimesWeak(rng);
            case "SLIMES_NORMAL":
                return SlimesNormal(rng);
            case "CORPSE_SLUGS_WEAK":
                return CorpseSlugs(rng, 2);
            case "CORPSE_SLUGS_NORMAL":
                return CorpseSlugs(rng, 3);
            case "BOWLBUGS_WEAK":
                return BowlbugsWeak(rng);
            case "BOWLBUGS_NORMAL":
                return BowlbugsNormal(rng);
            case "FLYCONID_NORMAL":
                return FlyconidNormal(rng);
            case "SLITHERING_STRANGLER_NORMAL":
                return SlitheringStranglerNormal(rng);
            case "RUBY_RAIDERS_NORMAL":
                return RubyRaidersNormal(rng);
            case "OVICOPTER_NORMAL":
                return OvicopterNormal(rng);
            case "TWO_TAILED_RATS_NORMAL":
                return TwoTailedRatsNormal(rng);
            case "PUNCH_OFF_EVENT_ENCOUNTER":
                return PunchOff(rng);
            case "SCROLLS_OF_BITING_WEAK":
                return ScrollsOfBiting(rng, 3);
            case "SCROLLS_OF_BITING_NORMAL":
                return ScrollsOfBitingNormal(rng);
            default:
                return null;
        }
    }

    /// <summary>SlimesWeak.cs: small A, medium, small B from the remaining small slime.</summary>
    private static List<EncounterSpawn> SlimesWeak(Rng rng)
    {
        List<string> pool = SmallSlimes.ToList();
        string first = pool[rng.NextInt(pool.Count)];
        pool.Remove(first);
        string second = pool[rng.NextInt(pool.Count)];
        string mid = MediumSlimes[rng.NextInt(MediumSlimes.Length)];
        return Spawns((first, null), (mid, null), (second, null));
    }

    /// <summary>SlimesNormal.cs: one coin flip decides which small slime goes first, then mediums.</summary>
    private static List<EncounterSpawn> SlimesNormal(Rng rng)
    {
        bool flag = rng.NextBool();
        string a = flag ? "MONSTER.LEAF_SLIME_S" : "MONSTER.TWIG_SLIME_S";
        string b = flag ? "MONSTER.TWIG_SLIME_S" : "MONSTER.LEAF_SLIME_S";
        return Spawns(
            ("MONSTER.TWIG_SLIME_M", null),
            ("MONSTER.LEAF_SLIME_M", null),
            (a, null),
            (b, null));
    }

    /// <summary>CorpseSlugs(Weak|Normal).cs: one NextInt(3) then starter indices rotate.</summary>
    private static List<EncounterSpawn> CorpseSlugs(Rng rng, int count)
    {
        int num = rng.NextInt(3);
        List<EncounterSpawn> spawns = new(count);
        for (int i = 0; i < count; i++)
        {
            spawns.Add(new EncounterSpawn("MONSTER.CORPSE_SLUG", null, (num + i) % 3));
        }
        return spawns;
    }

    /// <summary>BowlbugsWeak.cs: rock in the odd slot, one random bug in the even slot.</summary>
    private static List<EncounterSpawn> BowlbugsWeak(Rng rng)
    {
        string bug = BowlbugWeakBugs[rng.NextInt(BowlbugWeakBugs.Length)];
        return Spawns(("MONSTER.BOWLBUG_ROCK", "odd"), (bug, "even"));
    }

    /// <summary>BowlbugsNormal.cs: rock first, then two distinct workers.</summary>
    private static List<EncounterSpawn> BowlbugsNormal(Rng rng)
    {
        List<EncounterSpawn> spawns = new() { new EncounterSpawn("MONSTER.BOWLBUG_ROCK", "first") };
        List<string> currentWorkers = new();
        string[] slots = { "middle", "last" };
        for (int i = 0; i < 2; i++)
        {
            List<string> candidates = BowlbugWorkers
                .Where(r => currentWorkers.Count(c => c == r) < 1)
                .ToList();
            string picked = candidates[rng.NextInt(candidates.Count)];
            currentWorkers.Add(picked);
            spawns.Add(new EncounterSpawn(picked, slots[i]));
        }
        return spawns;
    }

    /// <summary>FlyconidNormal.cs: a random medium slime followed by the Flyconid.</summary>
    private static List<EncounterSpawn> FlyconidNormal(Rng rng)
    {
        string mid = MediumSlimes[rng.NextInt(MediumSlimes.Length)];
        return Spawns((mid, null), ("MONSTER.FLYCONID", null));
    }

    /// <summary>SlitheringStranglerNormal.cs: one of three secondary groups plus the Strangler.</summary>
    private static List<EncounterSpawn> SlitheringStranglerNormal(Rng rng)
    {
        int kind = rng.NextInt(3);
        List<EncounterSpawn> spawns = new();
        switch (kind)
        {
            case 0:
                spawns.Add(new EncounterSpawn("MONSTER.SNAPPING_JAXFRUIT"));
                break;
            case 1:
                spawns.Add(new EncounterSpawn(MediumSlimes[rng.NextInt(MediumSlimes.Length)]));
                break;
            default:
                spawns.Add(new EncounterSpawn(SmallSlimes[rng.NextInt(SmallSlimes.Length)]));
                spawns.Add(new EncounterSpawn(SmallSlimes[rng.NextInt(SmallSlimes.Length)]));
                break;
        }
        spawns.Add(new EncounterSpawn("MONSTER.SLITHERING_STRANGLER"));
        return spawns;
    }

    /// <summary>RubyRaidersNormal.cs: three distinct raiders from the five kinds.</summary>
    private static List<EncounterSpawn> RubyRaidersNormal(Rng rng)
    {
        List<EncounterSpawn> spawns = new();
        List<string> current = new();
        for (int i = 0; i < 3; i++)
        {
            List<string> candidates = Raiders
                .Where(r => current.Count(c => c == r) < 1)
                .ToList();
            string picked = candidates[rng.NextInt(candidates.Count)];
            current.Add(picked);
            spawns.Add(new EncounterSpawn(picked));
        }
        return spawns;
    }

    /// <summary>OvicopterNormal.cs: a single Ovicopter in its own slot.</summary>
    private static List<EncounterSpawn> OvicopterNormal(Rng rng) =>
        new() { new EncounterSpawn("MONSTER.OVICOPTER", "ovicopter") };

    /// <summary>TwoTailedRatsNormal.cs: three rats with rotated starter indices.</summary>
    private static List<EncounterSpawn> TwoTailedRatsNormal(Rng rng)
    {
        int num = rng.NextInt(3);
        string[] slots = { "third", "fourth", "fifth" };
        List<EncounterSpawn> spawns = new(3);
        for (int i = 0; i < 3; i++)
        {
            spawns.Add(new EncounterSpawn("MONSTER.TWO_TAILED_RAT", slots[i], (num + i) % 3));
        }
        return spawns;
    }

    /// <summary>PunchOffEventEncounter.cs: two constructs with random HP loss, the first starts fast-punching.</summary>
    private static List<EncounterSpawn> PunchOff(Rng rng)
    {
        int first = rng.NextInt(2, 10);
        int second = rng.NextInt(2, 10);
        return new List<EncounterSpawn>
        {
            new("MONSTER.PUNCH_CONSTRUCT", null, null, first, "FAST_PUNCH_MOVE"),
            new("MONSTER.PUNCH_CONSTRUCT", null, null, second, "READY_MOVE"),
        };
    }

    /// <summary>ScrollsOfBitingWeak.cs.</summary>
    private static List<EncounterSpawn> ScrollsOfBiting(Rng rng, int count)
    {
        int num = rng.NextInt(3);
        List<EncounterSpawn> spawns = new(count);
        for (int i = 0; i < count; i++)
        {
            spawns.Add(new EncounterSpawn("MONSTER.SCROLL_OF_BITING", null, (num + i) % 3));
        }
        return spawns;
    }

    /// <summary>ScrollsOfBitingNormal.cs: four scrolls, the last always starts on MORE_TEETH (index 2).</summary>
    private static List<EncounterSpawn> ScrollsOfBitingNormal(Rng rng)
    {
        int num = rng.NextInt(3);
        return new List<EncounterSpawn>
        {
            new("MONSTER.SCROLL_OF_BITING", null, num % 3),
            new("MONSTER.SCROLL_OF_BITING", null, (num + 1) % 3),
            new("MONSTER.SCROLL_OF_BITING", null, (num + 2) % 3),
            new("MONSTER.SCROLL_OF_BITING", null, 2),
        };
    }

    private static List<EncounterSpawn> Spawns(params (string Id, string? Slot)[] spawns) =>
        spawns.Select(s => new EncounterSpawn(s.Id, s.Slot)).ToList();
}

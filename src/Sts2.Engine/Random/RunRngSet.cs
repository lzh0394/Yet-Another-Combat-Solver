namespace Sts2.Engine.Random;

/// <summary>Mirrors MegaCrit.Sts2.Core.Entities.Rngs.RunRngType (order matters only for display).</summary>
public enum RunRngType
{
    UpFront,
    Shuffle,
    UnknownMapPoint,
    CombatCardGeneration,
    CombatPotionGeneration,
    CombatCardSelection,
    CombatEnergyCosts,
    CombatTargets,
    MonsterAi,
    Niche,
    CombatOrbs,
    TreasureRoomRelics,
}

/// <summary>
/// The run-scoped RNG set. Seed handling matches RunRngSet: the string seed is hashed once with
/// XXH64, and each stream is MegaRandom(seed + XXH64(snake_case(stream_name))).
/// </summary>
public sealed class RunRngSet
{
    private readonly Dictionary<RunRngType, Rng> _rngs = new();

    public RunRngSet(string stringSeed)
    {
        StringSeed = stringSeed;
        Seed = StringHash.Deterministic(stringSeed);
        foreach (RunRngType type in Enum.GetValues<RunRngType>())
        {
            _rngs[type] = new Rng(Seed, StreamName(type));
        }
    }

    public string StringSeed { get; }

    public ulong Seed { get; }

    public Rng this[RunRngType type] => _rngs[type];

    public Rng UpFront => _rngs[RunRngType.UpFront];
    public Rng Shuffle => _rngs[RunRngType.Shuffle];
    public Rng MonsterAi => _rngs[RunRngType.MonsterAi];
    public Rng CombatTargets => _rngs[RunRngType.CombatTargets];
    public Rng CombatCardGeneration => _rngs[RunRngType.CombatCardGeneration];
    public Rng CombatCardSelection => _rngs[RunRngType.CombatCardSelection];
    public Rng CombatEnergyCosts => _rngs[RunRngType.CombatEnergyCosts];
    public Rng Niche => _rngs[RunRngType.Niche];
    public Rng CombatOrbs => _rngs[RunRngType.CombatOrbs];

    public static string StreamName(RunRngType type) => StringHash.SnakeCase(type.ToString());

    /// <summary>JSON stream name used by the parity transcripts.</summary>
    public static string SnapshotName(RunRngType type) => type switch
    {
        RunRngType.UpFront => "up_front",
        RunRngType.UnknownMapPoint => "unknown_map_point",
        _ => StreamName(type),
    };
}

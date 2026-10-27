using System.Text.Json.Serialization;

namespace Sts2.Contracts;

/// <summary>One line of a parity transcript: a full combat state plus the action that produced it.</summary>
public sealed class Snapshot
{
    public string Type { get; set; } = "snapshot";
    public ActionInfo Action { get; set; } = new();
    public int Round { get; set; }
    public int Turn { get; set; }
    public string Side { get; set; } = "Player";
    public bool CombatOver { get; set; }
    public PlayerView Player { get; set; } = new();
    public List<EnemyView> Enemies { get; set; } = new();
    public PilesView Piles { get; set; } = new();
    public RngView Rng { get; set; } = new();
}

public sealed class ActionInfo
{
    public string Type { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Index { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Slot { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Card { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Target { get; set; }
}

public sealed class PlayerView
{
    public string Id { get; set; } = "";
    public int Hp { get; set; }
    public int MaxHp { get; set; }
    public int Block { get; set; }
    public int Energy { get; set; }
    public int MaxEnergy { get; set; }
    public int Stars { get; set; }
    public List<PowerView> Powers { get; set; } = new();
    public List<RelicView> Relics { get; set; } = new();
    public List<PotionView> Potions { get; set; } = new();
}

public sealed class PowerView
{
    public string Id { get; set; } = "";
    public int Amount { get; set; }
}

public sealed class RelicView
{
    public string Id { get; set; } = "";
    public int Counter { get; set; }
}

public sealed class PotionView
{
    public int Slot { get; set; }
    public string? Id { get; set; }
}

public sealed class EnemyView
{
    public int Index { get; set; }
    public string Id { get; set; } = "";
    public int Hp { get; set; }
    public int MaxHp { get; set; }
    public int Block { get; set; }
    public bool Alive { get; set; }
    public List<PowerView> Powers { get; set; } = new();
    public List<IntentView> Intents { get; set; } = new();
}

public sealed class IntentView
{
    public string Type { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Damage { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Repeats { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Cards { get; set; }
}

public sealed class PilesView
{
    public List<CardView> Hand { get; set; } = new();
    public List<CardView> Draw { get; set; } = new();
    public List<CardView> Discard { get; set; } = new();
    public List<CardView> Exhaust { get; set; } = new();
    public List<CardView> Play { get; set; } = new();
}

public sealed class CardView
{
    public string Id { get; set; } = "";
    public int Upgrade { get; set; }
    public int Cost { get; set; }
    public bool CostsX { get; set; }
    public int StarCost { get; set; }
    public string? Target { get; set; }
    public List<string> Keywords { get; set; } = new();
    public string? Enchantment { get; set; }
    public string? Affliction { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? CanPlay { get; set; }
}

public sealed class RngView
{
    /// <summary>Stream name -> state, flattened into the same JSON object as <see cref="Monsters"/>.</summary>
    [JsonExtensionData]
    public Dictionary<string, object> Streams { get; set; } = new();

    public List<MonsterRngView> Monsters { get; set; } = new();
}

public sealed class RngStreamView
{
    public int Counter { get; set; }
    public ulong S0 { get; set; }
    public ulong S1 { get; set; }
    public ulong S2 { get; set; }
    public ulong S3 { get; set; }
}

public sealed class MonsterRngView
{
    public string Id { get; set; } = "";
    public RngStreamView Rng { get; set; } = new();
}

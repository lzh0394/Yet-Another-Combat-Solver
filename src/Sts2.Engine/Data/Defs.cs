using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sts2.Engine.Data;

/// <summary>One card as exported from the game (data/sts2/cards.json).</summary>
public sealed class CardDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Type { get; set; }
    public string? Rarity { get; set; }
    public string? Target { get; set; }
    public int? Cost { get; set; }
    public int? CostUpgraded { get; set; }
    public bool CostsX { get; set; }
    public int? StarCost { get; set; }
    public List<string> Keywords { get; set; } = new();
    public List<string> KeywordsUpgraded { get; set; } = new();
    public Dictionary<string, VarDef>? Vars { get; set; }
    public bool Multimodal { get; set; }
    public bool EffectComplete { get; set; }
    public List<EffectDef> Effect { get; set; } = new();

    [JsonIgnore]
    public string Entry => Id.StartsWith("CARD.", StringComparison.Ordinal) ? Id["CARD.".Length..] : Id;
}

public sealed class VarDef
{
    public decimal Base { get; set; }
    public decimal? Upgraded { get; set; }
}

public sealed class MonsterDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int? MinHp { get; set; }
    public int? MaxHp { get; set; }
    public HpAscensionDef? HpAscension { get; set; }
    public string? InitialState { get; set; }
    public List<string>? InitialStates { get; set; }
    public Dictionary<string, MoveDef> Moves { get; set; } = new();
    public string? MovesSource { get; set; }
    public List<EffectDef>? StartEffects { get; set; }
    public bool StartEffectComplete { get; set; }

    [JsonIgnore]
    public string Entry => Id.StartsWith("MONSTER.", StringComparison.Ordinal) ? Id["MONSTER.".Length..] : Id;
}

public sealed class MoveDef
{
    public string Kind { get; set; } = "MoveState";
    public List<IntentDef> Intents { get; set; } = new();
    public string? FollowUp { get; set; }
    public bool MustPerformOnce { get; set; }
    public List<string>? Branches { get; set; }

    /// <summary>Either a list of conditional branch ids or a boolean "unresolved".</summary>
    public JsonElement? BranchesConditional { get; set; }
    public List<EffectDef>? Effects { get; set; }
    public bool EffectComplete { get; set; }
    public List<ConditionDef>? Conditions { get; set; }
    public List<float>? Weights { get; set; }
    public List<string>? RepeatTypes { get; set; }
}

public sealed class ConditionDef
{
    public string Branch { get; set; } = "";
    public string Expr { get; set; } = "";
}

public sealed class EffectDef
{
    public string Op { get; set; } = "";
    public string Target { get; set; } = "self";
    public JsonElement? Amount { get; set; }
    public string? Power { get; set; }
    public JsonElement? Hits { get; set; }
    public string? Card { get; set; }
    public string? Orb { get; set; }
    public bool? Dequeue { get; set; }
    public string? Pile { get; set; }
    public JsonElement? Count { get; set; }

    public int IntAmount =>
        Amount is { ValueKind: JsonValueKind.Number } a ? a.GetInt32() : 0;
}

public sealed class HpAscensionDef
{
    public string Level { get; set; } = "";
    public int Min { get; set; }
    public int Max { get; set; }
}

public sealed class IntentDef
{
    public string Type { get; set; } = "";
    public int? Damage { get; set; }
    public int? Repeats { get; set; }
    public int? Cards { get; set; }
}

public sealed class EncounterDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? RoomType { get; set; }
    public bool IsWeak { get; set; }
    public List<string> AllPossibleMonsters { get; set; } = new();
    public List<List<string?>>? Monsters { get; set; }
    public StarterRngDef? StarterRng { get; set; }

    [JsonIgnore]
    public string Entry => Id.StartsWith("ENCOUNTER.", StringComparison.Ordinal) ? Id["ENCOUNTER.".Length..] : Id;
}

/// <summary>EncounterModel.Rng.NextInt(range) seeds each spawn's StarterMoveIdx offset.</summary>
public sealed class StarterRngDef
{
    public int Range { get; set; }
    public List<int> Offsets { get; set; } = new();
}

public sealed class CharacterDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int StartingHp { get; set; }
    public int StartingGold { get; set; }
    public int MaxEnergy { get; set; } = 3;
    public List<string> StartingDeck { get; set; } = new();
    public List<string> StartingRelics { get; set; } = new();
    public string? CardPool { get; set; }

    [JsonIgnore]
    public string Entry => Id.StartsWith("CHARACTER.", StringComparison.Ordinal) ? Id["CHARACTER.".Length..] : Id;
}

public sealed class PowerDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Type { get; set; }
    public string? StackType { get; set; }
    public Dictionary<string, VarDef>? Vars { get; set; }
}

public sealed class RelicDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Rarity { get; set; }
    public Dictionary<string, VarDef>? Vars { get; set; }
    public int? MerchantCost { get; set; }
}

using System.Text.Json;

namespace Sts2.Engine.Combat;

/// <summary>The scenario file shared with the real-game harness (harness/scenarios/*.json).</summary>
public sealed class Scenario
{
    public string Character { get; set; } = "IRONCLAD";
    public string Seed { get; set; } = "";
    public int Ascension { get; set; }
    public string Encounter { get; set; } = "";
    public string? RoomType { get; set; }
    public int? MaxHp { get; set; }
    public int? Hp { get; set; }
    public int? MaxEnergy { get; set; }
    public int? Energy { get; set; }
    public List<string>? Relics { get; set; }
    public List<DeckCard>? Deck { get; set; }
    public List<ScenarioAction> Actions { get; set; } = new();

    /// <summary>Seed for the deterministic random policy shared by the engine and the parity mod.</summary>
    public ulong PolicySeed { get; set; }

    /// <summary>Source-generated options so the CLI keeps working under native AOT.</summary>
    public static JsonSerializerOptions JsonOptions => Data.JsonDefaults.Options;
}

public sealed class DeckCard
{
    public string Id { get; set; } = "";
    public int Upgrade { get; set; }
}

public sealed class ScenarioAction
{
    public string Type { get; set; } = "";
    public int? Slot { get; set; }
    public string? Card { get; set; }
    public string? Target { get; set; }
    public int? Index { get; set; }
}

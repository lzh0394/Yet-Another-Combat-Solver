using Sts2.Engine.Data;
using Sts2.Engine.Random;

namespace Sts2.Engine.Model;

public sealed class PowerInstance
{
    public PowerInstance(string id, int amount)
    {
        Id = id;
        Amount = amount;
    }

    public string Id { get; }
    public int Amount { get; set; }

    /// <summary>Applied during the enemy side's turn; skips the very next end-of-side tick.</summary>
    public bool Fresh { get; set; }
}

public sealed class RelicInstance
{
    public RelicInstance(string id)
    {
        Id = id;
    }

    public string Id { get; }
    public int Counter { get; set; }
}

/// <summary>The mutable player combat state (single-player only).</summary>
public sealed class PlayerState : ICombatant
{
    public PlayerState(CharacterDef character)
    {
        Id = character.Entry;
        MaxHp = character.StartingHp;
        Hp = character.StartingHp;
        MaxEnergy = character.MaxEnergy;
        Character = character;
    }

    public CharacterDef Character { get; }
    public string Id { get; }
    public int Hp { get; set; }
    public int MaxHp { get; set; }
    public int Block { get; set; }
    public int Energy { get; set; }
    public int MaxEnergy { get; set; }
    public int Stars { get; set; }
    public List<PowerInstance> Powers { get; } = new();
    public List<RelicInstance> Relics { get; } = new();
    public List<string?> Potions { get; } = new() { null, null, null };

    public List<CardInstance> DrawPile { get; } = new();
    public List<CardInstance> Hand { get; } = new();
    public List<CardInstance> DiscardPile { get; } = new();
    public List<CardInstance> ExhaustPile { get; } = new();
    public List<CardInstance> PlayPile { get; } = new();

    public int PowerAmount(string id) => Powers.FirstOrDefault(p => p.Id == id)?.Amount ?? 0;

    public void AddPower(string id, int amount)
    {
        id = PowerIds.Normalize(id);
        PowerInstance? existing = Powers.FirstOrDefault(p => p.Id == id);
        if (existing is null)
        {
            Powers.Add(new PowerInstance(id, amount));
            return;
        }
        existing.Amount += amount;
        if (existing.Amount <= 0)
        {
            Powers.Remove(existing);
        }
    }
}

public sealed class MonsterInstance : ICombatant
{
    public MonsterInstance(MonsterDef def, int combatId)
    {
        Def = def;
        CombatId = combatId;
    }

    public MonsterDef Def { get; }
    public string Id => Def.Id;
    public int CombatId { get; }

    /// <summary>RitualPower skips the first end-of-side tick after it is applied.</summary>
    public bool JustAppliedRitual { get; set; }

    /// <summary>Move executed before the current one (CannotRepeat filtering).</summary>
    public string? LastPerformed { get; set; }
    public int Hp { get; set; }
    public int MaxHp { get; set; }
    public int Block { get; set; }
    public string? Slot { get; set; }
    public int? StarterIdx { get; set; }

    /// <summary>The next enemy-side turn is skipped (stunned after BurrowedPower breaks).</summary>
    public bool SkipNextTurn { get; set; }
    public List<PowerInstance> Powers { get; } = new();

    /// <summary>State the monster is currently in (a branch state before the first move is rolled).</summary>
    public string CurrentState { get; set; } = "";

    public List<string> StateLog { get; } = new();
    public bool PerformedFirstMove { get; set; }

    public Rng Rng { get; set; } = new Rng();

    public bool IsAlive => Hp > 0;

    public int PowerAmount(string id) => Powers.FirstOrDefault(p => p.Id == id)?.Amount ?? 0;

    public void AddPower(string id, int amount)
    {
        id = PowerIds.Normalize(id);
        PowerInstance? existing = Powers.FirstOrDefault(p => p.Id == id);
        if (existing is null)
        {
            Powers.Add(new PowerInstance(id, amount));
            return;
        }
        existing.Amount += amount;
        if (existing.Amount <= 0)
        {
            Powers.Remove(existing);
        }
    }

    public MoveDef State => Def.Moves.TryGetValue(CurrentState, out MoveDef? move)
        ? move
        : new MoveDef { Kind = "MoveState" };

    /// <summary>Intents that will be executed on the monster's next turn (branch states resolve to nothing).</summary>
    public IReadOnlyList<IntentDef> Intents => State.Kind == "MoveState" ? State.Intents : Array.Empty<IntentDef>();
}

/// <summary>Model ids carry a category prefix ("POWER.STRENGTH_POWER"); runtime state uses the entry.</summary>
public static class PowerIds
{
    public static string Normalize(string id) =>
        id.StartsWith("POWER.", StringComparison.Ordinal) ? id["POWER.".Length..] : id;
}

using Sts2.Engine.Combat;
using Sts2.Engine.Data;
using Sts2.Engine.Model;
using Sts2.Engine.Rules;

namespace Sts2.Engine.Effects;

/// <summary>
/// Card play resolution. The data package now carries effect opcodes for the cards whose
/// OnPlay bodies the exporter could resolve statically (239 of 596 at the time of writing);
/// cards with unresolved effects are rejected instead of being silently played as no-ops.
/// </summary>
public static class CardEffects
{
    private static readonly HashSet<string> OstyAttackCards = new(StringComparer.Ordinal) { "UNLEASH", "PROTECTOR", "SQUEEZE" };

    public static void Resolve(Combat.Combat combat, CardInstance card, MonsterInstance? target)
    {
        if (OstyAttackCards.Contains(card.Entry) && !combat.Player.OstyAlive)
        {
            // Osty.CheckMissingWithAnim: without Osty these cards do nothing at all.
            return;
        }

        if (card.Def.EffectComplete)
        {
            foreach (EffectDef effect in card.Def.Effect)
            {
                Apply(combat, card, effect, target);
            }
            return;
        }

        throw new CombatException($"card effect not implemented: {card.Entry}");
    }

    private static void Apply(Combat.Combat combat, CardInstance card, EffectDef effect, MonsterInstance? target)
    {
        switch (effect.Op)
        {
            case "damage":
            {
                int amount = Amount(combat, card, effect.Amount);
                int hits = effect.Hits is null ? 1 : Amount(combat, card, effect.Hits);
                foreach (MonsterInstance monster in DamageTargets(combat, card, effect, target))
                {
                    for (int i = 0; i < hits && monster.IsAlive; i++)
                    {
                        Damage.Attack(combat, combat.Player, monster, amount);
                    }
                }
                break;
            }

            case "block":
                Damage.GainBlock(combat.Player, Amount(combat, card, effect.Amount));
                break;

            case "heal":
                combat.Player.Hp = Math.Min(combat.Player.MaxHp, combat.Player.Hp + Amount(combat, card, effect.Amount));
                break;

            case "power":
            {
                string power = effect.Power ?? throw new CombatException($"{card.Entry}: power op without id");
                int amount = Amount(combat, card, effect.Amount);
                if (effect.Target == "self")
                {
                    combat.Player.AddPower(power, amount);
                }
                else if (effect.Target == "all_enemies")
                {
                    foreach (MonsterInstance monster in combat.Enemies.Where(m => m.IsAlive))
                    {
                        monster.AddPower(power, amount);
                    }
                }
                else
                {
                    MonsterInstance single = target ?? throw new CombatException($"{card.Entry} needs an enemy target");
                    single.AddPower(power, amount);
                }
                break;
            }

            case "draw":
                combat.DrawCards(Amount(combat, card, effect.Amount));
                break;

            case "discard":
            case "exhaust":
            {
                int count = effect.Count is null ? 1 : Amount(combat, card, effect.Count);
                bool exhaust = effect.Op == "exhaust";
                for (int i = 0; i < count && combat.Player.Hand.Count > 0; i++)
                {
                    // The harness installs a "first eligible card" selector on the game side, so the
                    // deterministic mirror is simply the first card in hand order.
                    CardInstance chosen = combat.Player.Hand[0];
                    combat.Player.Hand.RemoveAt(0);
                    if (exhaust)
                    {
                        combat.Player.ExhaustPile.Add(chosen);
                    }
                    else
                    {
                        combat.Player.DiscardPile.Add(chosen);
                    }
                }
                break;
            }

            case "channel":
                combat.ChannelOrb(effect.Orb ?? throw new CombatException($"{card.Entry}: channel op without orb"));
                break;

            case "evoke":
            {
                int count = effect.Count is null ? 1 : Amount(combat, card, effect.Count);
                bool dequeue = effect.Dequeue ?? true;
                for (int i = 0; i < count; i++)
                {
                    combat.EvokeFrontOrb(dequeue && i == count - 1);
                }
                break;
            }

            case "summon":
                combat.SummonOsty(Amount(combat, card, effect.Amount));
                break;

            case "stars":
                combat.Player.Stars += Amount(combat, card, effect.Amount);
                break;

            case "energy":
                combat.Player.Energy += Amount(combat, card, effect.Amount);
                break;

            default:
                throw new CombatException($"{card.Entry}: unsupported card effect op {effect.Op}");
        }
    }

    private static IEnumerable<MonsterInstance> DamageTargets(
        Combat.Combat combat,
        CardInstance card,
        EffectDef effect,
        MonsterInstance? target)
    {
        bool all = effect.Target == "all_enemies" || card.TargetType is "AllEnemies";
        if (all)
        {
            return combat.Enemies.Where(m => m.IsAlive);
        }
        MonsterInstance single = target ?? throw new CombatException($"{card.Entry} needs an enemy target");
        return new[] { single };
    }

    /// <summary>
    /// CalculatedDamageVar with FromOsty(): base + ExtraDamage * multiplier. The multiplier for the three
    /// Osty-attack cards is Osty's current HP (Unleash), Osty's max HP (Protector) or the number of other
    /// Osty-attack cards in combat (Squeeze).
    /// </summary>
    private static int CalculatedDamage(Combat.Combat combat, CardInstance card)
    {
        int baseValue = (int)card.Var("CalculationBase");
        int extra = (int)card.Var("ExtraDamage");
        return card.Entry switch
        {
            "UNLEASH" => baseValue + extra * combat.Player.OstyHp,
            "PROTECTOR" => baseValue + extra * combat.Player.OstyMaxHp,
            "SQUEEZE" => baseValue + extra * (
                combat.Player.Hand.Concat(combat.Player.DrawPile)
                    .Concat(combat.Player.DiscardPile).Concat(combat.Player.ExhaustPile)
                    .Count(c => !ReferenceEquals(c, card) && OstyAttackCards.Contains(c.Entry))),
            _ => throw new CombatException($"{card.Entry}: calculated damage not modeled"),
        };
    }

    private static int Amount(Combat.Combat combat, CardInstance card, System.Text.Json.JsonElement? element)
    {
        if (element is null)
        {
            return 0;
        }

        System.Text.Json.JsonElement value = element.Value;
        if (value.ValueKind == System.Text.Json.JsonValueKind.Number)
        {
            return value.GetInt32();
        }
        if (value.ValueKind == System.Text.Json.JsonValueKind.Object &&
            value.TryGetProperty("var", out System.Text.Json.JsonElement varName))
        {
            string name = varName.GetString() ?? "";
            if (name == "CalculatedDamage")
            {
                return CalculatedDamage(combat, card);
            }
            return (int)card.Var(name);
        }
        throw new CombatException($"{card.Entry}: unresolved effect amount {value}");
    }
}

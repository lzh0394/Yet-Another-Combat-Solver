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
    public static void Resolve(Combat.Combat combat, CardInstance card, MonsterInstance? target)
    {
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
                int amount = Amount(card, effect.Amount);
                int hits = effect.Hits is null ? 1 : Amount(card, effect.Hits);
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
                Damage.GainBlock(combat.Player, Amount(card, effect.Amount));
                break;

            case "heal":
                combat.Player.Hp = Math.Min(combat.Player.MaxHp, combat.Player.Hp + Amount(card, effect.Amount));
                break;

            case "power":
            {
                string power = effect.Power ?? throw new CombatException($"{card.Entry}: power op without id");
                int amount = Amount(card, effect.Amount);
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
                combat.DrawCards(Amount(card, effect.Amount));
                break;

            case "discard":
            case "exhaust":
            {
                int count = effect.Count is null ? 1 : Amount(card, effect.Count);
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
                        combat.Player.DiscardPile.Insert(0, chosen);
                    }
                }
                break;
            }

            case "stars":
                combat.Player.Stars += Amount(card, effect.Amount);
                break;

            case "energy":
                combat.Player.Energy += Amount(card, effect.Amount);
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

    private static int Amount(CardInstance card, System.Text.Json.JsonElement? element)
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
            return (int)card.Var(varName.GetString() ?? "");
        }
        throw new CombatException($"{card.Entry}: unresolved effect amount {value}");
    }
}

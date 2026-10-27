using System.Text.Json;
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
                int amount = Amount(combat, card, effect.Amount, target);
                int hits = effect.Hits is null ? 1 : Amount(combat, card, effect.Hits, target);
                if (effect.HitsIf is { } hitsIf)
                {
                    hits = ConditionalHits(combat, card, hitsIf, target);
                }
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
                Damage.GainBlock(combat.Player, Amount(combat, card, effect.Amount, target));
                break;

            case "auto_play":
            {
                int count = Amount(combat, card, effect.Count, target);
                List<CardInstance> pile = effect.Pile == "hand" ? combat.Player.Hand : combat.Player.DrawPile;
                for (int i = 0; i < count; i++)
                {
                    if (pile.Count == 0)
                    {
                        break;
                    }
                    CardInstance auto = pile[0];
                    pile.RemoveAt(0);
                    if (effect.Upgrade == true)
                    {
                        auto = new CardInstance(auto.Def, Math.Max(1, auto.Upgrade));
                    }
                    MonsterInstance? autoTarget = effect.Target == "target" ? target : combat.Enemies.FirstOrDefault(e => e.IsAlive);
                    Resolve(combat, auto, autoTarget);
                    if (auto.HasKeyword("Exhaust"))
                    {
                        combat.Player.ExhaustPile.Add(auto);
                    }
                    else
                    {
                        combat.Player.DiscardPile.Add(auto);
                    }
                }
                break;
            }

            case "discard_hand":
            {
                int discarded = combat.Player.Hand.Count;
                while (combat.Player.Hand.Count > 0)
                {
                    CardInstance tossed = combat.Player.Hand[0];
                    combat.Player.Hand.RemoveAt(0);
                    combat.Player.DiscardPile.Add(tossed);
                }
                combat.LastDiscardedCount = discarded;
                break;
            }

            case "discard_drawn":
            {
                int count = Amount(combat, card, effect.Count, target);
                List<CardInstance> drawn = combat.DrawCards(count);
                foreach (CardInstance pulled in drawn.ToList())
                {
                    if (pulled.Cost == 0)
                    {
                        continue;
                    }
                    if (combat.Player.Hand.Remove(pulled))
                    {
                        combat.Player.DiscardPile.Add(pulled);
                    }
                }
                break;
            }

            case "exhaust_hand":
            {
                int count = 0;
                foreach (CardInstance candidate in combat.Player.Hand.ToList())
                {
                    bool take = effect.Filter switch
                    {
                        "non_attack" => candidate.Def.Type != "Attack",
                        "status" => candidate.Def.Type == "Status",
                        _ => true,
                    };
                    if (!take)
                    {
                        continue;
                    }
                    combat.Player.Hand.Remove(candidate);
                    combat.Player.ExhaustPile.Add(candidate);
                    count++;
                }
                combat.LastExhaustedCount = count;
                break;
            }

            case "heal":
                combat.Player.Hp = Math.Min(combat.Player.MaxHp, combat.Player.Hp + Amount(combat, card, effect.Amount, target));
                break;

            case "power":
            {
                string power = effect.Power ?? throw new CombatException($"{card.Entry}: power op without id");
                int amount = Amount(combat, card, effect.Amount, target);
                if (amount <= 0)
                {
                    // Zero or negative stacks are a no-op (Malaise-style reductions are exported as
                    // negated amounts on the same target and handled by RemovePower).
                    break;
                }
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
                else if (card.TargetType is "AnyAlly" or "AllAllies")
                {
                    // Ally-targeted buffs land on the only ally the engine models: the player.
                    combat.Player.AddPower(power, amount);
                }
                else
                {
                    MonsterInstance single = target ?? throw new CombatException($"{card.Entry} needs an enemy target");
                    single.AddPower(power, amount);
                }
                break;
            }

            case "apply_keyword":
            {
                string keyword = effect.Keyword ?? throw new CombatException($"{card.Entry}: apply_keyword without keyword");
                foreach (CardInstance candidate in combat.Player.Hand)
                {
                    if (candidate.HasKeyword(keyword))
                    {
                        continue;
                    }
                    candidate.AddKeyword(keyword);
                    break;
                }
                break;
            }

            case "duplicate_pick":
            {
                CardInstance? cloneSource = combat.Player.Hand.FirstOrDefault(c => !ReferenceEquals(c, card));
                if (cloneSource is not null)
                {
                    int copies = effect.Count is null ? 1 : Amount(combat, card, effect.Count);
                    for (int i = 0; i < copies; i++)
                    {
                        combat.Player.Hand.Add(new CardInstance(cloneSource.Def, cloneSource.Upgrade));
                    }
                }
                break;
            }

            case "draw":
                combat.DrawCards(Amount(combat, card, effect.Amount, target));
                break;

            case "discard_hand_draw":
            {
                int count = combat.Player.Hand.Count;
                while (combat.Player.Hand.Count > 0)
                {
                    CardInstance tossed = combat.Player.Hand[0];
                    combat.Player.Hand.RemoveAt(0);
                    combat.Player.DiscardPile.Add(tossed);
                }
                combat.DrawCards(count);
                break;
            }

            case "return_from_discard":
            {
                for (int i = combat.Player.DiscardPile.Count - 1; i >= 0; i--)
                {
                    CardInstance recycled = combat.Player.DiscardPile[i];
                    bool matches = effect.Filter switch
                    {
                        "zero_cost" => recycled.Cost <= 0,
                        _ => true,
                    };
                    if (!matches)
                    {
                        continue;
                    }
                    combat.Player.DiscardPile.RemoveAt(i);
                    combat.Player.Hand.Add(recycled);
                }
                break;
            }

            case "add_from_draw":
            {
                int space = 10 - combat.Player.Hand.Count;
                for (int i = 0; i < combat.Player.DrawPile.Count && space > 0;)
                {
                    CardInstance fetched = combat.Player.DrawPile[i];
                    bool matches = effect.Rarity is null
                        || string.Equals(fetched.Def.Rarity, effect.Rarity, StringComparison.OrdinalIgnoreCase);
                    if (!matches)
                    {
                        i++;
                        continue;
                    }
                    combat.Player.DrawPile.RemoveAt(i);
                    combat.Player.Hand.Add(fetched);
                    space--;
                }
                break;
            }

            case "draw_until":
            {
                int guard = 0;
                while (combat.Player.DrawPile.Count > 0 || combat.Player.DiscardPile.Count > 0)
                {
                    if (combat.Player.DrawPile.Count == 0)
                    {
                        combat.DrawCards(0);
                    }
                    if (combat.Player.DrawPile.Count == 0)
                    {
                        break;
                    }
                    CardInstance next = combat.Player.DrawPile[0];
                    combat.DrawCards(1);
                    if (effect.Filter is null)
                    {
                        break;
                    }
                    bool still = string.Equals(next.Def.Type, effect.Filter, StringComparison.OrdinalIgnoreCase);
                    if (!still || ++guard > 60)
                    {
                        break;
                    }
                }
                break;
            }

            case "draw_to":
            {
                int cap = effect.IntAmount == 0 ? 10 : effect.IntAmount;
                combat.DrawCards(Math.Max(0, cap - combat.Player.Hand.Count));
                break;
            }

            case "exhaust_pick":
            {
                int count = effect.Count is null ? 1 : Amount(combat, card, effect.Count);
                List<CardInstance> source = Pile(combat.Player, effect.From ?? "Draw");
                for (int i = 0; i < count && source.Count > 0; i++)
                {
                    CardInstance picked = source[0];
                    source.RemoveAt(0);
                    combat.Player.ExhaustPile.Add(picked);
                }
                break;
            }

            case "exhaust_random":
            {
                int count = effect.Count is null ? 1 : Amount(combat, card, effect.Count);
                for (int i = 0; i < count; i++)
                {
                    List<CardInstance> pool = effect.Filter is null
                        ? combat.Player.Hand
                        : combat.Player.Hand.Where(c => string.Equals(c.Def.Type, effect.Filter, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (pool.Count == 0)
                    {
                        break;
                    }
                    int index = combat.Rng.CombatCardSelection.NextInt(pool.Count);
                    CardInstance picked = pool[index];
                    combat.Player.Hand.Remove(picked);
                    combat.Player.ExhaustPile.Add(picked);
                }
                break;
            }

            case "energy_current":
                combat.Player.Energy += combat.Player.Energy;
                break;

            case "gold":
                // Gold is a run-level resource; combat parity does not model it.
                break;

            case "max_hp":
            {
                int amount = Amount(combat, card, effect.Amount, target);
                combat.Player.MaxHp = Math.Max(1, combat.Player.MaxHp + amount);
                combat.Player.Hp = Math.Max(0, Math.Min(combat.Player.MaxHp, combat.Player.Hp + amount));
                break;
            }

            case "stun":
            {
                MonsterInstance? stunned = target ?? combat.Enemies.FirstOrDefault(m => m.IsAlive);
                if (stunned is not null)
                {
                    stunned.SkipNextTurn = true;
                }
                break;
            }

            case "orb_remove":
            {
                int amount = Math.Max(0, Amount(combat, card, effect.Amount, target));
                combat.OrbCapacity = Math.Max(0, combat.OrbCapacity - amount);
                while (combat.Player.Orbs.Count > combat.OrbCapacity)
                {
                    combat.EvokeFrontOrb(dequeue: true);
                }
                break;
            }

            case "orb_passive":
            {
                int count = (effect.Count is null ? 1 : Amount(combat, card, effect.Count))
                            + (card.Upgrade > 0 ? effect.ExtraIfUpgraded ?? 0 : 0);
                for (int i = 0; i < count; i++)
                {
                    combat.TriggerOrbPassives(effect.Orb);
                }
                break;
            }

            case "play_top":
            {
                int count = effect.Count is null ? 1 : Amount(combat, card, effect.Count);
                bool exhaust = effect.Exhaust ?? true;
                for (int i = 0; i < count; i++)
                {
                    if (combat.Player.DrawPile.Count == 0)
                    {
                        break;
                    }
                    CardInstance top = combat.Player.DrawPile[0];
                    combat.Player.DrawPile.RemoveAt(0);
                    combat.Player.PlayPile.Add(top);
                    if (top.Def.EffectComplete)
                    {
                        Resolve(combat, top, null);
                    }
                    combat.Player.PlayPile.Remove(top);
                    if (exhaust)
                    {
                        combat.Player.ExhaustPile.Add(top);
                    }
                    else
                    {
                        combat.Player.DiscardPile.Add(top);
                    }
                }
                break;
            }

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
            {
                string orb = effect.Orb ?? throw new CombatException($"{card.Entry}: channel op without orb");
                int count = effect.Count is null ? 1 : Amount(combat, card, effect.Count);
                for (int i = 0; i < count; i++)
                {
                    string kind = orb == "RANDOM"
                        ? new[] { "LIGHTNING", "FROST", "DARK" }[combat.Rng.CombatOrbs.NextInt(3)]
                        : orb;
                    combat.ChannelOrb(kind);
                }
                break;
            }

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
                combat.SummonOsty(Amount(combat, card, effect.Amount, target));
                break;

            case "stars":
                combat.Player.Stars += Amount(combat, card, effect.Amount, target);
                break;

            case "energy":
                combat.Player.Energy += Amount(combat, card, effect.Amount, target);
                break;

            case "upgrade":
            {
                int count = effect.Count is null ? Amount(combat, card, effect.Amount, target)
                    : (int)Rules.Ascension.Amount(effect.Count, combat.Scenario.Ascension);
                IReadOnlyList<CardInstance> pool = effect.Target == "all"
                    ? combat.Player.Hand.ToList()
                    : combat.Player.Hand;
                int done = 0;
                foreach (CardInstance held in pool.ToList())
                {
                    if (done >= count)
                    {
                        break;
                    }
                    int index = combat.Player.Hand.IndexOf(held);
                    if (index < 0)
                    {
                        continue;
                    }
                    combat.Player.Hand[index] = held.Upgraded();
                    done++;
                }
                break;
            }

            case "move_card":
            {
                int count = effect.Count is null ? 1 : (int)Rules.Ascension.Amount(effect.Count, combat.Scenario.Ascension);
                List<CardInstance> source = Pile(combat.Player, effect.From ?? "Discard");
                for (int i = 0; i < count && source.Count > 0; i++)
                {
                    CardInstance moved = source[0];
                    source.RemoveAt(0);
                    Pile(combat.Player, effect.To ?? "Hand").Add(moved);
                }
                break;
            }

            case "discard_all":
                while (combat.Player.Hand.Count > 0)
                {
                    CardInstance held = combat.Player.Hand[0];
                    combat.Player.Hand.RemoveAt(0);
                    combat.Player.DiscardPile.Add(held);
                }
                break;

            case "orb_slots":
                combat.OrbCapacity += Amount(combat, card, effect.Amount, target);
                break;

            case "power_modify":
                if (effect.Power is null || effect.Power.EndsWith("UNRESOLVED", StringComparison.Ordinal))
                {
                    throw new CombatException($"{card.Entry}: unresolved power_modify");
                }
                combat.Player.AddPower(effect.Power, Amount(combat, card, effect.Amount, target));
                break;

            case "hp_loss":
                // Unblockable self-damage (Bloodletting et al.); power hooks do not apply.
                combat.Player.Hp = Math.Max(0, combat.Player.Hp - Amount(combat, card, effect.Amount, target));
                break;

            case "add_card":
            {
                int count = effect.Count is null
                    ? Amount(combat, card, effect.Amount, target)
                    : Rules.Ascension.IsAscensionObject(effect.Count)
                        ? (int)Rules.Ascension.Amount(effect.Count, combat.Scenario.Ascension)
                        : Amount(combat, card, effect.Count, target);
                CardInstance added = effect.Card == "SELF" ? new CardInstance(card.Def, card.Upgrade)
                    : new CardInstance(combat.Data.Card(effect.Card ?? throw new CombatException($"{card.Entry}: add_card without id")));
                if (effect.Upgrade == true)
                {
                    added = new CardInstance(added.Def, Math.Max(1, added.Upgrade));
                }
                combat.AddCardToPile(added, effect.Pile, count);
                break;
            }

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

    private static decimal CalcMultiplier(Combat.Combat combat, CardInstance card, string mult, MonsterInstance? target) => mult switch
    {
        "owner_block" => combat.Player.Block,
        "orbs" => combat.Player.Orbs.Count,
        "hand" => combat.Player.Hand.Count,
        "draw" => combat.Player.DrawPile.Count,
        "discard" => combat.Player.DiscardPile.Count,
        "exhaust" => combat.Player.ExhaustPile.Count,
        "target_block" => target?.Block ?? 0,
        "target_vulnerable" => target?.PowerAmount("VULNERABLE_POWER") ?? 0,
        "target_doom" => target?.PowerAmount("DOOM_POWER") ?? 0,
        "owner_strength" => combat.Player.PowerAmount("STRENGTH_POWER"),
        "hand_skills" => combat.Player.Hand.Count(c => c.Def.Type == "Skill"),
        "hand_attacks" => combat.Player.Hand.Count(c => c.Def.Type == "Attack"),
        "attacks_played" => combat.AttacksPlayedThisTurn,
        "strikes_in_all_piles" => CountTag(combat, "Strike"),
        "defends_in_all_piles" => CountTag(combat, "Defend"),
        _ => throw new CombatException($"{card.Entry}: unsupported calc multiplier {mult}"),
    };

    private static int CountTag(Combat.Combat combat, string tag)
    {
        int count = 0;
        foreach (List<CardInstance> pile in new[]
                 {
                     combat.Player.Hand, combat.Player.DrawPile, combat.Player.DiscardPile,
                     combat.Player.ExhaustPile, combat.Player.PlayPile,
                 })
        {
            count += pile.Count(c => c.Entry.Contains(tag, StringComparison.OrdinalIgnoreCase));
        }
        return count;
    }

    /// <summary>Dismantle/Spite style hit counts: the count depends on combat state, not on a constant.</summary>
    private static int ConditionalHits(Combat.Combat combat, CardInstance card, JsonElement spec, MonsterInstance? target)
    {
        if (spec.TryGetProperty("power", out JsonElement power) && power.ValueKind == JsonValueKind.String)
        {
            string id = PowerIds.Normalize(power.GetString() ?? "");
            bool matched = target is not null && target.PowerAmount(id) > 0;
            return matched
                ? spec.TryGetProperty("when", out JsonElement when) ? (int)when.GetDouble() : 2
                : spec.TryGetProperty("else", out JsonElement otherwise) ? (int)otherwise.GetDouble() : 1;
        }
        if (spec.TryGetProperty("player_lost_hp", out JsonElement lost) && lost.ValueKind == JsonValueKind.True)
        {
            if (combat.PlayerLostHpThisTurn && spec.TryGetProperty("var", out JsonElement var) && var.ValueKind == JsonValueKind.String)
            {
                return (int)card.Var(var.GetString() ?? "");
            }
            return spec.TryGetProperty("else", out JsonElement fallback) ? (int)fallback.GetDouble() : 1;
        }
        return 1;
    }

    private static List<CardInstance> Pile(PlayerState player, string pile) => pile switch
    {
        "Hand" => player.Hand,
        "Draw" => player.DrawPile,
        "Discard" => player.DiscardPile,
        "Exhaust" => player.ExhaustPile,
        _ => player.DiscardPile,
    };

    private static int Amount(Combat.Combat combat, CardInstance card, System.Text.Json.JsonElement? element, MonsterInstance? target = null)
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
        if (value.ValueKind == System.Text.Json.JsonValueKind.Object &&
            value.TryGetProperty("calc", out System.Text.Json.JsonElement calcName))
        {
            string name = calcName.GetString() ?? "";
            if (card.Def.Calc is null || !card.Def.Calc.TryGetValue(name, out CalcDef? calc))
            {
                throw new CombatException($"{card.Entry}: no calculated var {name}");
            }
            decimal baseValue = card.Var(calc.Base);
            decimal extra = card.Var(calc.Extra);
            return (int)(baseValue + extra * CalcMultiplier(combat, card, calc.Mult, target));
        }
        if (value.ValueKind == System.Text.Json.JsonValueKind.Object &&
            value.TryGetProperty("scale", out System.Text.Json.JsonElement scaleName))
        {
            return (int)CalcMultiplier(combat, card, scaleName.GetString() ?? "", target);
        }
        if (value.ValueKind == System.Text.Json.JsonValueKind.Object &&
            value.TryGetProperty("block", out System.Text.Json.JsonElement blockFlag) && blockFlag.ValueKind == System.Text.Json.JsonValueKind.True)
        {
            return combat.Player.Block;
        }
        if (value.ValueKind == System.Text.Json.JsonValueKind.Object &&
            value.TryGetProperty("power_amount", out System.Text.Json.JsonElement powerAmount))
        {
            string power = PowerIds.Normalize(powerAmount.GetString() ?? "");
            ICombatant? who = value.TryGetProperty("target", out System.Text.Json.JsonElement tgt) && tgt.GetString() == "target"
                ? target
                : combat.Player;
            return who?.PowerAmount(power) ?? 0;
        }
        if (value.ValueKind == System.Text.Json.JsonValueKind.Object &&
            value.TryGetProperty("exhausted", out System.Text.Json.JsonElement exhausted) && exhausted.ValueKind == System.Text.Json.JsonValueKind.True)
        {
            return combat.LastExhaustedCount;
        }
        if (value.ValueKind == System.Text.Json.JsonValueKind.Object &&
            value.TryGetProperty("discarded", out System.Text.Json.JsonElement discardedFlag) &&
            discardedFlag.ValueKind == System.Text.Json.JsonValueKind.True)
        {
            return combat.LastDiscardedCount;
        }
        if (value.ValueKind == System.Text.Json.JsonValueKind.Object &&
            value.TryGetProperty("per_exhausted", out System.Text.Json.JsonElement perExhausted) && perExhausted.ValueKind == System.Text.Json.JsonValueKind.True)
        {
            int per = (int)card.Var(value.GetProperty("var").GetString() ?? "Block");
            return per * combat.LastExhaustedCount;
        }
        if (value.ValueKind == System.Text.Json.JsonValueKind.Object &&
            value.TryGetProperty("negate", out System.Text.Json.JsonElement negated))
        {
            return -Amount(combat, card, negated, target);
        }
        if (value.ValueKind == System.Text.Json.JsonValueKind.Object &&
            value.TryGetProperty("x", out System.Text.Json.JsonElement which))
        {
            return which.GetString() == "stars" ? combat.LastStarXValue : combat.LastXValue;
        }
        throw new CombatException($"{card.Entry}: unresolved effect amount {value}");
    }
}

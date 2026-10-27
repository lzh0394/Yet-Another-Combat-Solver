using Sts2.Engine.Model;

namespace Sts2.Engine.Diagnostics;

/// <summary>L3 invariants: cheap checks that catch internal contradictions after every action.</summary>
public static class Invariants
{
    public static void Check(Combat.Combat combat)
    {
        CheckPlayer(combat.Player);
        foreach (MonsterInstance monster in combat.Enemies)
        {
            if (monster.MaxHp < 0 || monster.Hp < 0 || monster.Hp > monster.MaxHp)
            {
                Fail($"{monster.Id}: hp {monster.Hp}/{monster.MaxHp}");
            }
            if (monster.Block < 0)
            {
                Fail($"{monster.Id}: block {monster.Block}");
            }
            CheckPowers(monster);
        }

        List<CardInstance> all = combat.Player.DrawPile
            .Concat(combat.Player.Hand)
            .Concat(combat.Player.DiscardPile)
            .Concat(combat.Player.ExhaustPile)
            .Concat(combat.Player.PlayPile)
            .ToList();
        if (all.Distinct(ReferenceEqualityComparer.Instance).Count() != all.Count)
        {
            Fail("the same card instance appears in more than one pile");
        }
        if (all.Count > 10 && combat.Player.Hand.Count > 10)
        {
            Fail($"hand has {combat.Player.Hand.Count} cards (max 10)");
        }
    }

    private static void CheckPlayer(PlayerState player)
    {
        if (player.MaxHp < 0 || player.Hp < 0 || player.Hp > player.MaxHp)
        {
            Fail($"{player.Id}: hp {player.Hp}/{player.MaxHp}");
        }
        if (player.Block < 0)
        {
            Fail($"{player.Id}: block {player.Block}");
        }
        if (player.Energy < 0)
        {
            Fail($"{player.Id}: energy {player.Energy}");
        }
        CheckPowers(player);
    }

    private static void CheckPowers(ICombatant combatant)
    {
        foreach (PowerInstance power in combatant.Powers)
        {
            // ShrinkPower uses -1 as the "infinite duration" marker.
            // Strength/Dexterity go negative on purpose (e.g. Resonance), ShrinkPower uses -1 as
            // the "infinite duration" marker; everything else must stay positive.
            bool mayBeNonPositive = power.Id is Combat.Combat.ShrinkPower or "STRENGTH_POWER" or "DEXTERITY_POWER";
            if (power.Amount <= 0 && !mayBeNonPositive)
            {
                Fail($"{combatant.Id}: power {power.Id} has amount {power.Amount}");
            }
        }
    }

    private static void Fail(string message) => throw new Combat.CombatException("invariant violated: " + message);
}

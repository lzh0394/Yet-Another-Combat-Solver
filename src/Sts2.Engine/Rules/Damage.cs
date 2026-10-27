using Sts2.Engine.Combat;
using Sts2.Engine.Model;

namespace Sts2.Engine.Rules;

/// <summary>
/// Damage and block math, mirroring CreatureCmd.Damage / Creature.GainBlock.
/// All arithmetic stays decimal; the only truncation is at HP loss and block bookkeeping.
/// </summary>
public static class Damage
{
    public const decimal MaxValue = 999999999m;

    /// <summary>
    /// Hook.ModifyDamage: all additive listeners first (Strength), then all multiplicative
    /// listeners (Vulnerable on the target, Weak on the dealer), then clamp.
    /// </summary>
    public static decimal Modify(Combat.Combat combat, ICombatant source, ICombatant target, decimal baseDamage)
    {
        decimal additive = 0m;
        decimal multiplier = 1m;
        combat.Hooks.ModifyDamage(combat, source, target, ref additive, ref multiplier);
        decimal value = (baseDamage + additive) * multiplier;
        value = Math.Min(value, MaxValue);
        return Math.Max(0m, value);
    }

    /// <summary>Deal an already-modified amount to a target with block. Returns the HP actually lost.</summary>
    public static int Apply(int block, int hp, decimal amount, out int newBlock)
    {
        int blocked = (int)Math.Min(block, (int)amount);
        newBlock = block - blocked;
        decimal through = amount - blocked;
        return (int)Math.Clamp(through, 0m, MaxValue);
    }

    public static void Attack(Combat.Combat combat, ICombatant source, ICombatant target, decimal baseDamage)
    {
        decimal amount = Modify(combat, source, target, baseDamage);
        int loss = Apply(target.Block, target.Hp, amount, out int block);
        target.Block = block;
        target.Hp -= loss;
    }

    /// <summary>CreatureCmd.GainBlock after ModifyBlock (no block powers in the slice).</summary>
    public static void GainBlock(ICombatant target, decimal amount) =>
        target.Block = (int)Math.Min(target.Block + amount, MaxValue);
}

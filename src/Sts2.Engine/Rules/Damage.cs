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
        // ThornsPower.BeforeDamageReceived: a powered attack into the owner damages the dealer first.
        int thorns = target.PowerAmount(Combat.Combat.ThornsPower);
        if (thorns > 0 && source is PlayerState && !ReferenceEquals(source, target))
        {
            int extra = Apply(source.Block, source.Hp, thorns, out int sourceBlock);
            source.Block = sourceBlock;
            source.Hp = Math.Max(0, source.Hp - extra);
        }
        decimal amount = Modify(combat, source, target, baseDamage);
        int blockBefore = target.Block;
        int loss = Apply(target.Block, target.Hp, amount, out int block);
        target.Block = block;

        // BurrowedPower.AfterBlockBroken: breaking the burrow stuns the monster for a turn.
        if (target is MonsterInstance burrower && blockBefore > 0 && block == 0
            && amount >= blockBefore && burrower.PowerAmount(Combat.Combat.BurrowedPower) > 0)
        {
            burrower.Powers.RemoveAll(p => p.Id == Combat.Combat.BurrowedPower);
            burrower.Block = 0;
            burrower.CurrentState = "BITE_MOVE";
            burrower.SkipNextTurn = true;
        }
        // DieForYouPower: a powered attack's unblocked damage is dealt to Osty instead of the owner.
        if (loss > 0 && target is PlayerState owner && owner.OstyAlive && source is MonsterInstance)
        {
            int absorbed = Math.Min(owner.OstyHp, loss);
            owner.OstyHp -= absorbed;
            loss -= absorbed;
        }
        target.Hp = Math.Max(0, target.Hp - loss);
        if (loss > 0 && target is PlayerState hurt)
        {
            combat.PlayerLostHpThisTurn = true;
        }

        // PaperCutsPower.AfterDamageGiven: the scroll's powered attack also cuts the player's max HP.
        if (loss > 0 && source is MonsterInstance scroll && target is PlayerState player)
        {
            int cuts = scroll.PowerAmount(Combat.Combat.PaperCutsPower);
            if (cuts > 0)
            {
                player.MaxHp = Math.Max(1, player.MaxHp - cuts);
                player.Hp = Math.Min(player.Hp, player.MaxHp);
            }
        }
    }

    /// <summary>CreatureCmd.GainBlock after ModifyBlock (no block powers in the slice).</summary>
    public static void GainBlock(ICombatant target, decimal amount) =>
        target.Block = (int)Math.Min(target.Block + amount, MaxValue);
}

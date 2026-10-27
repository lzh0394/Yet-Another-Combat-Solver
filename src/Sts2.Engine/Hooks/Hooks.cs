namespace Sts2.Engine.Hooks;

/// <summary>
/// Hook points used by the vertical slice. The real game runs every listener in two full passes
/// (all additive, then all multiplicative); the hook contract keeps that split explicit.
/// </summary>
public interface IDamageModifier
{
    void Modify(Combat.Combat combat, Model.ICombatant source, Model.ICombatant target, ref decimal additive, ref decimal multiplier);
}

public sealed class HookSet
{
    public List<IDamageModifier> DamageModifiers { get; } = new();

    public void ModifyDamage(Combat.Combat combat, Model.ICombatant source, Model.ICombatant target, ref decimal additive, ref decimal multiplier)
    {
        foreach (IDamageModifier modifier in DamageModifiers)
        {
            modifier.Modify(combat, source, target, ref additive, ref multiplier);
        }
    }
}

/// <summary>
/// The built-in power listeners: Strength adds damage, Vulnerable (on the target) and Weak
/// (on the source) multiply it. Mirrors StrengthPower/VulnerablePower/WeakPower.
/// </summary>
public sealed class PowerDamageModifier : IDamageModifier
{
    public void Modify(Combat.Combat combat, Model.ICombatant source, Model.ICombatant target, ref decimal additive, ref decimal multiplier)
    {
        additive += source.PowerAmount(Combat.Combat.StrengthPower);
        if (target.PowerAmount(Combat.Combat.VulnerablePower) > 0)
        {
            multiplier *= 1.5m;
        }
        if (source.PowerAmount(Combat.Combat.WeakPower) > 0)
        {
            multiplier *= 0.75m;
        }
        if (source.PowerAmount(Combat.Combat.ShrinkPower) != 0)
        {
            multiplier *= 0.7m;
        }
    }
}

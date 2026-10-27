using Sts2.Engine.Combat;
using Sts2.Engine.Model;
using Sts2.Engine.Rules;

namespace Sts2.Engine.Effects;

/// <summary>
/// Play effects for the vertical slice. The data package does not carry effect opcodes yet, so the
/// three Ironclad basics are hand-coded from _research/decompiled-v0.111.0/.../Cards/.
/// </summary>
public static class CardEffects
{
    public static void Resolve(Combat.Combat combat, CardInstance card, MonsterInstance? target)
    {
        switch (card.Entry)
        {
            case "STRIKE_IRONCLAD":
                Damage.Attack(combat, combat.Player, RequireTarget(card, target), card.Var("Damage"));
                break;

            case "DEFEND_IRONCLAD":
                Damage.GainBlock(combat.Player, card.Var("Block"));
                break;

            case "BASH":
                // Bash.cs: await DamageCmd.Attack(...).Execute(); then PowerCmd.Apply<VulnerablePower>.
                MonsterInstance bashTarget = RequireTarget(card, target);
                Damage.Attack(combat, combat.Player, bashTarget, card.Var("Damage"));
                if (bashTarget.IsAlive)
                {
                    bashTarget.AddPower(Combat.Combat.VulnerablePower, (int)card.Var("VulnerablePower"));
                }
                break;

            default:
                throw new CombatException($"card effect not implemented: {card.Entry}");
        }
    }

    private static MonsterInstance RequireTarget(CardInstance card, MonsterInstance? target) =>
        target ?? throw new CombatException($"{card.Entry} needs an enemy target");
}

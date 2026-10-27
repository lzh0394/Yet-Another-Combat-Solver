using System.Text;
using Sts2.Contracts;

namespace Sts2.View;

/// <summary>V1 TUI: renders one transcript snapshot as a text panel and lists legal actions.</summary>
public static class TuiView
{
    public static string Render(Snapshot snapshot)
    {
        StringBuilder sb = new();
        sb.AppendLine($"=== round {snapshot.Round} / turn {snapshot.Turn} / {snapshot.Side} side{(snapshot.CombatOver ? " / COMBAT OVER" : "")} ===");
        PlayerView p = snapshot.Player;
        sb.AppendLine($"player {p.Id}  hp {p.Hp}/{p.MaxHp}  block {p.Block}  energy {p.Energy}/{p.MaxEnergy}  stars {p.Stars}");
        if (p.Powers.Count > 0)
        {
            sb.AppendLine("  powers: " + string.Join(", ", p.Powers.Select(x => $"{x.Id} {x.Amount}")));
        }
        if (p.Relics.Count > 0)
        {
            sb.AppendLine("  relics: " + string.Join(", ", p.Relics.Select(x => x.Counter == 0 ? x.Id : $"{x.Id}({x.Counter})")));
        }

        sb.AppendLine("enemies:");
        foreach (EnemyView e in snapshot.Enemies)
        {
            sb.AppendLine($"  [{e.Index}] {e.Id}  hp {e.Hp}/{e.MaxHp}  block {e.Block}" +
                          (e.Powers.Count > 0 ? "  powers: " + string.Join(", ", e.Powers.Select(x => $"{x.Id} {x.Amount}")) : "") +
                          "  intents: " + string.Join(", ", e.Intents.Select(FormatIntent)));
        }

        sb.AppendLine("hand:");
        for (int i = 0; i < snapshot.Piles.Hand.Count; i++)
        {
            CardView c = snapshot.Piles.Hand[i];
            sb.AppendLine($"  [{i}] {Name(c)}  cost {(c.CostsX ? "X" : c.Cost.ToString())}  target {c.Target ?? "Self"}" +
                          (c.Keywords.Count > 0 ? "  [" + string.Join(", ", c.Keywords) + "]" : "") +
                          (c.CanPlay == false ? "  (unplayable)" : ""));
        }

        sb.AppendLine($"piles: draw {snapshot.Piles.Draw.Count}  discard {snapshot.Piles.Discard.Count}  exhaust {snapshot.Piles.Exhaust.Count}  play {snapshot.Piles.Play.Count}");
        return sb.ToString();
    }

    private static string Name(CardView c) => c.Upgrade > 0 ? $"{c.Id}+{c.Upgrade}" : c.Id;

    private static string FormatIntent(IntentView intent) => intent.Type switch
    {
        "Attack" => intent.Damage is int d ? (intent.Repeats is int r && r > 1 ? $"Attack {d}x{r}" : $"Attack {d}") : "Attack",
        "StatusCard" => intent.Cards is int n ? $"StatusCard {n}" : "StatusCard",
        _ => intent.Type,
    };
}

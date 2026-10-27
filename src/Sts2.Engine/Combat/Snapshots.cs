using Sts2.Contracts;
using Sts2.Engine.Model;
using Sts2.Engine.Random;
using Sts2.Engine.Rules;

namespace Sts2.Engine.Combat;

/// <summary>Maps engine state onto the transcript DTOs (same schema the parity mod writes).</summary>
public static class Snapshots
{
    public static Snapshot Build(Combat combat, ActionInfo action)
    {
        Snapshot snapshot = new()
        {
            Action = action,
            Round = combat.Round,
            Turn = combat.Turn,
            Side = combat.Side,
            CombatOver = combat.CombatOver,
            Player = Player(combat),
            Piles = Piles(combat),
            Rng = Rng(combat),
        };

        int index = 0;
        foreach (MonsterInstance monster in combat.Enemies)
        {
            snapshot.Enemies.Add(Enemy(combat, monster, index++));
        }

        return snapshot;
    }

    private static PlayerView Player(Combat combat)
    {
        PlayerState player = combat.Player;
        return new PlayerView
        {
            Id = player.Id,
            Hp = player.Hp,
            MaxHp = player.MaxHp,
            Block = player.Block,
            Energy = player.Energy,
            MaxEnergy = player.MaxEnergy,
            Stars = player.Stars,
            Powers = player.Powers.Select(p => new PowerView { Id = p.Id, Amount = p.Amount }).ToList(),
            Relics = player.Relics.Select(r => new RelicView { Id = r.Id, Counter = r.Counter }).ToList(),
            Potions = player.Potions.Select((id, slot) => new PotionView { Slot = slot, Id = id }).ToList(),
        };
    }

    private static EnemyView Enemy(Combat combat, MonsterInstance monster, int index) => new()
    {
        Index = index,
        Id = monster.Def.Entry,
        Hp = monster.Hp,
        MaxHp = monster.MaxHp,
        Block = monster.Block,
        Alive = monster.IsAlive,
        Powers = monster.Powers.Select(p => new PowerView { Id = p.Id, Amount = p.Amount }).ToList(),
        Intents = monster.Intents.Select(i => Intent(combat, monster, i)).ToList(),
    };

    private static IntentView Intent(Combat combat, MonsterInstance monster, Data.IntentDef intent)
    {
        IntentView view = new() { Type = intent.Type, Cards = intent.Cards };
        if (intent.Type == "Attack")
        {
            decimal baseDamage = intent.Damage ?? 0;
            decimal modified = Damage.Modify(combat, monster, combat.Player, baseDamage);
            int repeats = intent.Repeats ?? 1;
            view.Damage = Math.Max(0, (int)modified) * repeats;
            view.Repeats = repeats;
        }
        return view;
    }

    private static PilesView Piles(Combat combat) => new()
    {
        Hand = combat.Player.Hand.Select(c => Card(c, combat)).ToList(),
        Draw = combat.Player.DrawPile.Select(c => Card(c, null)).ToList(),
        Discard = combat.Player.DiscardPile.Select(c => Card(c, null)).ToList(),
        Exhaust = combat.Player.ExhaustPile.Select(c => Card(c, null)).ToList(),
        Play = combat.Player.PlayPile.Select(c => Card(c, null)).ToList(),
    };

    private static CardView Card(CardInstance card, Combat? combat)
    {
        CardView view = new()
        {
            Id = card.Entry,
            Upgrade = card.Upgrade,
            Cost = card.Cost,
            CostsX = card.CostsX,
            StarCost = card.StarCost,
            Target = card.Def.Target,
            Keywords = card.Keywords.ToList(),
        };
        if (combat is not null)
        {
            view.CanPlay = card.Cost < 0 || card.CostsX ? combat.Player.Energy > 0 : combat.Player.Energy >= card.Cost;
        }
        return view;
    }

    private static RngView Rng(Combat combat)
    {
        RngView view = new();
        Add(view, "shuffle", combat.Rng.Shuffle);
        Add(view, "monster_ai", combat.Rng.MonsterAi);
        Add(view, "combat_targets", combat.Rng.CombatTargets);
        Add(view, "combat_card_generation", combat.Rng.CombatCardGeneration);
        Add(view, "combat_card_selection", combat.Rng.CombatCardSelection);
        Add(view, "combat_energy_costs", combat.Rng.CombatEnergyCosts);
        Add(view, "niche", combat.Rng.Niche);
        foreach (MonsterInstance monster in combat.Enemies)
        {
            view.Monsters.Add(new MonsterRngView { Id = monster.Def.Entry, Rng = Stream(monster.Rng) });
        }
        return view;
    }

    private static void Add(RngView view, string name, Rng rng) => view.Streams[name] = Stream(rng);

    private static RngStreamView Stream(Rng rng)
    {
        (ulong s0, ulong s1, ulong s2, ulong s3) = rng.State;
        return new RngStreamView { Counter = rng.Counter, S0 = s0, S1 = s1, S2 = s2, S3 = s3 };
    }
}

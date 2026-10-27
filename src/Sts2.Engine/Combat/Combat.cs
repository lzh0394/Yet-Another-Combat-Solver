using Sts2.Engine.Data;
using Sts2.Engine.Diagnostics;
using Sts2.Engine.Effects;
using Sts2.Engine.Hooks;
using Sts2.Engine.Model;
using Sts2.Engine.Random;

namespace Sts2.Engine.Combat;

public sealed class CombatException : Exception
{
    public CombatException(string message) : base(message) { }
}

/// <summary>
/// Single-player combat state machine. Vertical slice: Ironclad basics vs Nibbit.
/// Ordering mirrors the game: deck order -> UnstableShuffle (shuffle stream) -> draw 5 free:
/// play costs energy, end turn discards, enemies act, debuffs tick at enemy-side end, then
/// the player turn starts (block clear, energy reset, draw 5 with StableShuffle reshuffle).
/// </summary>
public sealed class Combat
{
    public const string VulnerablePower = "VULNERABLE_POWER";
    public const string WeakPower = "WEAK_POWER";
    public const string FrailPower = "FRAIL_POWER";
    public const string StrengthPower = "STRENGTH_POWER";
    public const string ShrinkPower = "SHRINK_POWER";
    public const string RitualPower = "RITUAL_POWER";
    public const string ThornsPower = "THORNS_POWER";
    public const string RampartPower = "RAMPART_POWER";
    public const string PaperCutsPower = "PAPER_CUTS_POWER";
    public const string BurrowedPower = "BURROWED_POWER";
    public const string ConstrictPower = "CONSTRICT_POWER";
    public const string RavenousPower = "RAVENOUS_POWER";

    private readonly DataPack _data;
    private readonly EncounterDef _encounter;

    public Combat(DataPack data, Scenario scenario)
    {
        _data = data;
        Scenario = scenario;
        Hooks = new HookSet();
        Hooks.DamageModifiers.Add(new PowerDamageModifier());
        Rng = new RunRngSet(scenario.Seed);
        CharacterDef character = data.Character(scenario.Character);
        Player = new PlayerState(character)
        {
            MaxHp = scenario.MaxHp ?? character.StartingHp,
            MaxEnergy = scenario.MaxEnergy ?? character.MaxEnergy,
        };
        Player.Hp = scenario.Hp ?? Player.MaxHp;
        // Ascension: TightBelt (A4+) removes one potion slot; AscendersBane (A5+) adds a curse.
        if (Rules.Ascension.Active("TIGHT_BELT", scenario.Ascension) && Player.Potions.Count > 1)
        {
            Player.Potions.RemoveAt(Player.Potions.Count - 1);
        }

        Player.Energy = scenario.Energy ?? Player.MaxEnergy;
        OrbCapacity = Player.Id == "DEFECT" ? 3 : 0;

        foreach (string relic in scenario.Relics ?? character.StartingRelics)
        {
            Player.Relics.Add(new RelicInstance(relic));
        }

        List<CardInstance> deck = new();
        if (scenario.Deck is { Count: > 0 })
        {
            foreach (DeckCard card in scenario.Deck)
            {
                deck.Add(new CardInstance(data.Card(card.Id), card.Upgrade));
            }
        }
        else
        {
            foreach (string id in character.StartingDeck)
            {
                deck.Add(new CardInstance(data.Card(id)));
            }
        }

        if (Rules.Ascension.Active("ASCENDERS_BANE", scenario.Ascension))
        {
            deck.Add(new CardInstance(data.Card("CARD.ASCENDERS_BANE")));
        }

        Player.DrawPile.AddRange(deck);
        Rng.Shuffle.Shuffle(Player.DrawPile);

        EncounterDef encounter = data.Encounter(scenario.Encounter);
        _encounter = encounter;
        Rng encounterRng = new(unchecked(Rng.Seed + 1UL + StringHash.Deterministic(encounter.Entry)));
        // Recipes win over the packaged list: some encounters need per-spawn starter indices or
        // fixed initial states that the flat JSON list cannot express (e.g. the fourth Scroll of Biting).
        List<EncounterSpawn>? spawns = EncounterGenerators.Generate(encounter.Entry, data, encounterRng);
        spawns ??= encounter.Monsters?
            .Select(m => new EncounterSpawn(m[0] ?? throw new CombatException("monster id missing"), m.Count > 1 ? m[1] : null))
            .ToList();
        if (spawns is null)
        {
            throw new CombatException($"encounter {encounter.Id} has no monster list and no generator");
        }
        int? starterBase = encounter.StarterRng is { } starterRng ? encounterRng.NextInt(starterRng.Range) : null;
        int combatId = 1;
        int spawnIndex = 0;
        foreach (EncounterSpawn spawn in spawns)
        {
            MonsterDef spawnDef = data.Monster(spawn.Id);
            MonsterInstance monster = new(spawnDef, combatId++)
            {
                Slot = spawn.Slot,
            };
            if (spawn.Starter is { } explicitStarter)
            {
                monster.StarterIdx = explicitStarter;
            }
            else if (starterBase is { } baseIndex && encounter.StarterRng is { } sr && spawnIndex < sr.Offsets.Count)
            {
                monster.StarterIdx = (baseIndex + sr.Offsets[spawnIndex]) % sr.Range;
            }
            RollHp(monster);
            if (spawn.HpReduction > 0)
            {
                monster.Hp = Math.Max(1, monster.Hp - spawn.HpReduction);
            }
            monster.CurrentState = spawn.InitialState
                ?? (spawnDef.InitialStates is { Count: > 0 } states && monster.StarterIdx is { } si
                    ? states[si % states.Count]
                    : spawnDef.InitialState ?? "");
            monster.Rng = new Rng(Rng.Seed + (ulong)monster.CombatId);
            Enemies.Add(monster);
            spawnIndex++;
            if (monster.Def.StartEffectComplete)
            {
                foreach (EffectDef effect in monster.Def.StartEffects ?? new List<EffectDef>())
                {
                    ApplyEffect(monster, effect);
                }
            }
        }

        GrantRampart();
        foreach (MonsterInstance monster in Enemies)
        {
            RollMove(monster);
        }

        Turn = 1;
        Round = 1;
        DrawCards(5);
        if (HasRelic("BOUND_PHYLACTERY"))
        {
            // BeforeCombatStart: the starter relic summons Osty with 1 HP.
            SummonOsty(1);
        }
        if (HasRelic("CRACKED_CORE"))
        {
            // Cracked Core channels a Lightning orb when combat starts.
            ChannelOrb("LIGHTNING");
        }
        if (HasRelic("DIVINE_RIGHT"))
        {
            // Divine Right grants three stars when combat starts.
            Player.Stars += 3;
        }
        if (HasRelic("RING_OF_THE_SNAKE"))
        {
            // Ring of the Snake draws two extra cards on the first turn.
            DrawCards(2);
        }
    }

    private CombatSide _side = CombatSide.Player;

    public string Side => PlayerDead ? "Enemy" : _side == CombatSide.Enemy ? "Enemy" : "Player";

    public DataPack Data => _data;
    public Scenario Scenario { get; }
    public HookSet Hooks { get; }
    public RunRngSet Rng { get; }
    public PlayerState Player { get; }
    public List<MonsterInstance> Enemies { get; } = new();
    public int Turn { get; private set; }
    public int Round { get; private set; }
    public bool PlayerDead => Player.Hp <= 0;
    public bool CombatOver => Enemies.All(e => !e.IsAlive) || PlayerDead;
    public string LastActionType { get; private set; } = "start";
    public int? LastIndex { get; private set; }
    public int? LastSlot { get; private set; }
    public string? LastCard { get; private set; }
    public string? LastTarget { get; private set; }

    public void PlayCard(int? slot, string? cardId, string? target, int? actionIndex)
    {
        CardsPlayedTotal++;
        if (CombatOver)
        {
            throw new CombatException("combat is over");
        }

        CardInstance card = FindCard(slot, cardId);
        int cost = card.Cost;
        if (cost < 0)
        {
            throw new CombatException($"{card.Entry} is unplayable");
        }
        if (card.CostsX ? Player.Energy < 1 : Player.Energy < cost)
        {
            throw new CombatException($"{card.Entry} costs {cost}, only {Player.Energy} energy");
        }
        if (card.StarCost >= 0 && Player.Stars < card.StarCost)
        {
            throw new CombatException($"{card.Entry} costs {card.StarCost} stars, only {Player.Stars} stars");
        }

        MonsterInstance? targetMonster = ResolveTarget(card, target);

        _side = CombatSide.Player;
        LastXValue = card.CostsX ? Player.Energy : 0;
        Player.Energy -= card.CostsX ? Player.Energy : cost;
        if (card.StarCost > 0)
        {
            Player.Stars -= card.StarCost;
        }
        Player.Hand.Remove(card);
        Player.PlayPile.Add(card);

        CardEffects.Resolve(this, card, targetMonster);
        if (string.Equals(card.Def.Type, "Attack", StringComparison.OrdinalIgnoreCase))
        {
            AttacksPlayedThisTurn++;
        }
        TriggerRavenousOnDeaths();
        Enemies.RemoveAll(e => !e.IsAlive);

        LastActionType = "play";
        LastIndex = actionIndex;
        LastSlot = slot;
        LastCard = card.Entry;
        LastTarget = target;

        if (CombatOver)
        {
            // Combat ended during OnPlay: the card stays in the Play pile and piles are cleared later
            // by PlayerCombatState.AfterCombatEnd, so the final snapshot still shows it there.
            TriggerRavenousOnDeaths();
            Enemies.RemoveAll(e => !e.IsAlive);
            Invariants.Check(this);
            return;
        }

        Player.PlayPile.Remove(card);
        bool endTurn = RequestEndTurn;
        RequestEndTurn = false;
        if (card.Keywords.Contains("Exhaust", StringComparer.Ordinal))
        {
            Player.ExhaustPile.Add(card);
        }
        else
        {
            Player.DiscardPile.Add(card);
        }

        Invariants.Check(this);
        if (endTurn && !CombatOver)
        {
            EndTurn(actionIndex);
        }
    }

    /// <summary>
    /// Greedy policy shared with the parity mod ("auto_turn"): play the first hand slot that is
    /// playable (first alive enemy for enemy-targeted cards), then end the turn. The whole turn is
    /// one action, so the caller emits a single snapshot afterwards.
    /// </summary>

    /// <summary>
    /// Deterministic random policy shared with the parity mod: pick one of the currently playable
    /// hand slots with MegaRandom(policySeed + decisionIndex), or end the turn when nothing is playable.
    /// </summary>
    public void RandomPlay(ulong policySeed, int decisionIndex, int? actionIndex)
    {
        List<int> slots = new();
        for (int i = 0; i < Player.Hand.Count; i++)
        {
            CardInstance card = Player.Hand[i];
            bool cost = card.Cost >= 0 && (card.CostsX ? Player.Energy > 0 : Player.Energy >= card.Cost);
            bool stars = card.StarCost <= 0 || Player.Stars >= card.StarCost;
            bool target = card.TargetType != "AnyEnemy" || Enemies.Any(e => e.IsAlive);
            if (cost && stars && target)
            {
                slots.Add(i);
            }
        }

        if (slots.Count == 0)
        {
            EndTurn(actionIndex);
            return;
        }

        Random.MegaRandom rng = new(policySeed + (ulong)decisionIndex);
        int slot = slots[rng.Next(slots.Count)];
        string? targetId = Player.Hand[slot].TargetType == "AnyEnemy"
            ? Enemies.First(e => e.IsAlive).Def.Entry
            : null;
        PlayCard(slot, null, targetId, actionIndex);
    }

    public void AutoTurn(int? actionIndex)
    {
        if (CombatOver)
        {
            throw new CombatException("combat is over");
        }

        while (TryPickPlayable(out int slot, out string? target))
        {
            PlayCard(slot, null, target, null);
            if (CombatOver)
            {
                // The real game finishes EndCombatInternal before the auto loop's next frame:
                // piles are cleared and out-of-combat relic hooks (Burning Blood) already healed.
                SettleAfterVictory();
                return;
            }
        }
        EndTurn(actionIndex);
    }

    public void SummonOsty(int amount)
    {
        if (amount <= 0)
        {
            return;
        }
        if (Player.OstyAlive)
        {
            Player.OstyMaxHp += amount;
            Player.OstyHp = Math.Min(Player.OstyMaxHp, Player.OstyHp + amount);
        }
        else
        {
            Player.OstyMaxHp = amount;
            Player.OstyHp = amount;
        }
    }

    /// <summary>Number of orb slots; Defect starts with three and cards such as Capacitor add more.</summary>
    public int OrbCapacity { get; set; }

    public void ChannelOrb(string orb)
    {
        if (Player.Orbs.Count >= OrbCapacity)
        {
            EvokeFrontOrb(dequeue: true);
        }
        Player.Orbs.Add(new OrbState { Kind = orb });
    }

    public void EvokeFrontOrb(bool dequeue)
    {
        if (Player.Orbs.Count == 0)
        {
            return;
        }
        OrbState orb = Player.Orbs[0];
        switch (orb.Kind)
        {
            case "LIGHTNING":
                TriggerOrbDamage(8);
                break;
            case "FROST":
                Player.Block = (int)Math.Min(Player.Block + 5, 999999999m);
                break;
            case "DARK":
                TriggerOrbDamage(orb.Charge);
                orb.Charge = 0;
                break;
        }
        if (dequeue)
        {
            Player.Orbs.RemoveAt(0);
        }
    }

    private void TriggerOrbPassives()
    {
        foreach (OrbState orb in Player.Orbs.ToList())
        {
            TriggerOrbPassive(orb);
        }
    }

    private void TriggerOrbPassive(OrbState orb)
    {
        switch (orb.Kind)
        {
            case "LIGHTNING":
                TriggerOrbDamage(3);
                break;
            case "FROST":
                Player.Block = (int)Math.Min(Player.Block + 3, 999999999m);
                break;
            case "DARK":
                orb.Charge += 6;
                break;
        }
    }

    /// <summary>OrbCmd.Passive: trigger the passive of every orb of one kind (Tesla Coil).</summary>
    public void TriggerOrbPassives(string? orbKind)
    {
        foreach (OrbState orb in Player.Orbs.ToList())
        {
            if (orbKind is not null && orb.Kind != orbKind)
            {
                continue;
            }
            TriggerOrbPassive(orb);
        }
    }

    /// <summary>Lightning is an unpowered hit: no strength, no vulnerable multiplier, random target.</summary>
    private void TriggerOrbDamage(int amount)
    {
        List<MonsterInstance> alive = Enemies.Where(e => e.IsAlive).ToList();
        if (alive.Count == 0)
        {
            return;
        }
        int index = Rng.CombatTargets.NextInt(alive.Count);
        MonsterInstance target = alive[index];
        int block = Rules.Damage.Apply(target.Block, target.Hp, amount, out int newBlock);
        target.Block = newBlock;
        target.Hp = Math.Max(0, target.Hp - block);
        Enemies.RemoveAll(e => !e.IsAlive);
    }

    /// <summary>
    /// RavenousPower.AfterDeath: when a monster on the owner's side dies, every living Ravenous
    /// monster gains Strength equal to its stack and is stunned for a turn.
    /// </summary>
    private void TriggerRavenousOnDeaths()
    {
        List<MonsterInstance> dead = Enemies.Where(e => !e.IsAlive && !e.DeathProcessed).ToList();
        if (dead.Count == 0)
        {
            return;
        }
        foreach (MonsterInstance d in dead)
        {
            d.DeathProcessed = true;
        }
        foreach (MonsterInstance owner in Enemies.Where(e => e.IsAlive && e.PowerAmount(RavenousPower) > 0).ToList())
        {
            foreach (MonsterInstance _ in dead)
            {
                owner.AddPower(StrengthPower, owner.PowerAmount(RavenousPower));
                owner.SkipNextTurn = true;
            }
        }
    }

    private bool HasRelic(string entry)
        => Player.Relics.Any(r => r.Id == entry || r.Id == "RELIC." + entry);

    private void SettleAfterVictory()
    {
        // EndCombatInternal leaves the combat on the player side, even if the killing blow happened
        // during the enemy side's turn (e.g. a lightning orb passive at end of turn).
        _side = CombatSide.Player;
        Player.Hand.Clear();
        Player.DrawPile.Clear();
        Player.DiscardPile.Clear();
        Player.PlayPile.Clear();
        Player.ExhaustPile.Clear();
        Player.Energy = 0;
        Player.Block = 0;
        Player.Powers.Clear();
        foreach (RelicInstance relic in Player.Relics)
        {
            if (relic.Id == "RELIC.BURNING_BLOOD" || relic.Id == "BURNING_BLOOD")
            {
                Player.Hp = Math.Min(Player.Hp + 6, Player.MaxHp);
            }
        }
    }

    private bool TryPickPlayable(out int slot, out string? target)
    {
        for (int i = 0; i < Player.Hand.Count; i++)
        {
            CardInstance card = Player.Hand[i];
            int cost = card.Cost;
            if (cost < 0 || (card.CostsX ? Player.Energy < 1 : Player.Energy < cost))
            {
                continue;
            }
            string? resolved = card.TargetType switch
            {
                "AnyEnemy" => Enemies.Any(e => e.IsAlive) ? "0" : null,
                "AnyPlayer" => "player",
                _ => null,
            };
            if (card.TargetType == "AnyEnemy" && resolved is null)
            {
                continue;
            }
            slot = i;
            target = resolved;
            return true;
        }
        slot = -1;
        target = null;
        return false;
    }

    public void EndTurn(int? actionIndex)
    {
        if (CombatOver)
        {
            throw new CombatException("combat is over");
        }

        TriggerOrbPassives();

        // discard hand; Ethereal cards exhaust, Retain cards stay in hand
        List<CardInstance> retained = new();
        foreach (CardInstance card in Player.Hand)
        {
            if (card.RetainThisTurn || card.HasKeyword("Retain"))
            {
                card.RetainThisTurn = false;
                retained.Add(card);
            }
            else if (card.HasKeyword("Ethereal"))
            {
                Player.ExhaustPile.Add(card);
            }
            else
            {
                Player.DiscardPile.Add(card);
            }
        }
        Player.Hand.Clear();
        Player.Hand.AddRange(retained);

        // Duration debuffs on the player tick at the end of the player's own turn. The real game's
        // SkipNextDurationTick only suppresses the tick that would otherwise run in the same
        // enemy turn as the application; the player's end-of-turn tick still decrements.
        TickDurations(Player, honorFresh: false);

        // ConstrictPower.AfterSideTurnEnd fires when the owner's own side ends its turn. It is
        // Unpowered damage from the owner to itself, so block absorbs it and no powers modify it.
        int constrict = Player.PowerAmount(ConstrictPower);
        if (constrict > 0)
        {
            int constrictLoss = Rules.Damage.Apply(Player.Block, Player.Hp, constrict, out int blockAfterConstrict);
            Player.Block = blockAfterConstrict;
            Player.Hp = Math.Max(0, Player.Hp - constrictLoss);
            if (constrictLoss > 0)
            {
                PlayerLostHpThisTurn = true;
            }
            if (PlayerDead)
            {
                _side = CombatSide.Enemy;
                LastActionType = "end_turn";
                return;
            }
        }

        // enemy turn
        foreach (MonsterInstance monster in Enemies)
        {
            if (monster.PowerAmount(BurrowedPower) == 0)
            {
                monster.Block = 0;
            }
        }
        _side = CombatSide.Enemy;
        foreach (MonsterInstance monster in Enemies.Where(e => e.IsAlive).ToList())
        {
            if (monster.SkipNextTurn)
            {
                monster.SkipNextTurn = false;
                continue;
            }
            ExecuteMonsterTurn(monster);
            TriggerRavenousOnDeaths();
        int removed = Enemies.RemoveAll(e => !e.IsAlive);
            if (removed > 0)
            {
                // ConstrictPower.AfterDeath: the debuff goes away with the monster that applied it.
                Player.Powers.RemoveAll(p => p.Id == ConstrictPower
                    && !Enemies.Any(e => e.IsAlive && e.Def.Entry == p.Applier));
            }
            if (PlayerDead || CombatOver)
            {
                break;
            }
            AdvanceMove(monster);
        }

        if (!CombatOver)
        {
            // Vulnerable/Weak tick at the end of the enemy side's turn, for both sides' creatures.
            foreach (MonsterInstance monster in Enemies)
            {
                TickDurations(monster);
            }
            foreach (MonsterInstance monster in Enemies.Where(e => e.IsAlive))
            {
                int ritual = monster.PowerAmount(RitualPower);
                if (ritual <= 0)
                {
                    continue;
                }
                if (monster.JustAppliedRitual)
                {
                    monster.JustAppliedRitual = false;
                    continue;
                }
                monster.AddPower(StrengthPower, ritual);
            }
        }

        LastActionType = "end_turn";
        LastIndex = actionIndex;
        LastSlot = null;
        LastCard = null;
        LastTarget = null;

        if (PlayerDead)
        {
            // Defeat: the player side is torn down, combat ends on the enemy side.
            Player.Powers.Clear();
            Player.Block = 0;
            LastActionType = "end_turn";
            LastIndex = actionIndex;
            LastSlot = null;
            LastCard = null;
            LastTarget = null;
            Invariants.Check(this);
            return;
        }
        if (CombatOver)
        {
            TriggerRavenousOnDeaths();
            Enemies.RemoveAll(e => !e.IsAlive);
            // The real game runs EndCombatInternal while the driver waits for the next turn, so the
            // snapshot after this action already shows cleared piles and out-of-combat relic hooks.
            SettleAfterVictory();
            Invariants.Check(this);
            return;
        }

        _side = CombatSide.Player;
        StartPlayerTurn();
        Invariants.Check(this);
    }

    private void StartPlayerTurn()
    {
        Turn++;
        Round++;
        PlayerLostHpThisTurn = false;
        AttacksPlayedThisTurn = 0;
        Player.Block = 0;
        Player.Energy = Player.MaxEnergy;
        if (HasRelic("BOUND_PHYLACTERY"))
        {
            // AfterEnergyResetLate: the phylactery re-summons Osty at the start of every turn after the first.
            SummonOsty(1);
        }
        GrantRampart();
        DrawCards(5);
        foreach (MonsterInstance monster in Enemies.Where(e => e.IsAlive))
        {
            RollMove(monster);
        }
    }

    /// <summary>RampartPower.AfterSideTurnStart: shield monsters grant their block to TurretOperators.</summary>
    private void GrantRampart()
    {
        foreach (MonsterInstance shield in Enemies.Where(e => e.IsAlive))
        {
            int amount = shield.PowerAmount(RampartPower);
            if (amount <= 0)
            {
                continue;
            }
            foreach (MonsterInstance turret in Enemies.Where(e => e.IsAlive && e.Def.Entry == "TURRET_OPERATOR"))
            {
                turret.Block = (int)Math.Min(turret.Block + amount, 999999999m);
            }
        }
    }

    public List<CardInstance> DrawCards(int count)
    {
        List<CardInstance> drawn = [];
        for (int i = 0; i < count; i++)
        {
            if (Player.DrawPile.Count == 0)
            {
                ReshuffleDiscardIntoDraw();
            }
            if (Player.DrawPile.Count == 0)
            {
                return drawn;
            }
            CardInstance card = Player.DrawPile[0];
            Player.DrawPile.RemoveAt(0);
            Player.Hand.Add(card);
            drawn.Add(card);
            CardsDrawnTotal++;
        }
        return drawn;
    }

    private void ReshuffleDiscardIntoDraw()
    {
        if (Player.DiscardPile.Count == 0)
        {
            return;
        }
        List<CardInstance> cards = Player.DiscardPile.ToList();
        cards.AddRange(Player.DrawPile);
        Player.DiscardPile.Clear();
        Player.DrawPile.Clear();

        // CardPileCmd.Shuffle: StableShuffle sorts by ModelId (ordinal) then upgrade level, then
        // UnstableShuffle. List.Sort is introsort; entry ids are unique in practice.
        cards.Sort(static (a, b) =>
        {
            int id = string.CompareOrdinal(a.Id, b.Id);
            return id != 0 ? id : a.Upgrade.CompareTo(b.Upgrade);
        });
        Rng.Shuffle.Shuffle(cards);
        Player.DrawPile.AddRange(cards);
    }

    private CardInstance FindCard(int? slot, string? cardId)
    {
        if (slot is int s)
        {
            if (s < 0 || s >= Player.Hand.Count)
            {
                throw new CombatException($"hand slot {s} out of range ({Player.Hand.Count} cards)");
            }
            CardInstance card = Player.Hand[s];
            if (cardId is not null && card.Entry != NormalizeEntry(cardId))
            {
                throw new CombatException($"hand slot {s} is {card.Entry}, expected {NormalizeEntry(cardId)}");
            }
            return card;
        }
        string entry = NormalizeEntry(cardId ?? throw new CombatException("play action needs card or slot"));
        CardInstance? match = Player.Hand.FirstOrDefault(c => c.Entry == entry);
        if (match is null)
        {
            throw new CombatException($"card {entry} not in hand: {string.Join(", ", Player.Hand.Select(c => c.Entry))}");
        }
        return match;
    }

    private MonsterInstance? ResolveTarget(CardInstance card, string? target)
    {
        string targetType = card.Def.Target ?? "Self";
        if (targetType is "Self" or "None")
        {
            return null;
        }
        List<MonsterInstance> alive = Enemies.Where(e => e.IsAlive).ToList();
        if (alive.Count == 0)
        {
            throw new CombatException("no living enemies");
        }
        if (target is null || target is "0")
        {
            return alive[0];
        }
        if (target is "player" or "self")
        {
            return null;
        }
        if (int.TryParse(target, out int index) && index >= 0 && index < Enemies.Count)
        {
            return Enemies[index];
        }
        return alive.FirstOrDefault(e => e.Def.Entry == target.Replace("MONSTER.", "", StringComparison.Ordinal).ToUpperInvariant())
            ?? alive.FirstOrDefault(e => e.Def.Entry == target)
            ?? throw new CombatException("target not found: " + target);
    }

    private static string NormalizeEntry(string id) => id.StartsWith("CARD.", StringComparison.Ordinal) ? id["CARD.".Length..] : id;

    private void RollHp(MonsterInstance monster)
    {
        int min = monster.Def.MinHp ?? 1;
        int max = monster.Def.MaxHp ?? min;
        if (monster.Def.HpAscension is { } asc && Rules.Ascension.Active(asc.Level, Scenario.Ascension))
        {
            min = asc.Min;
            max = asc.Max;
        }
        List<int> candidates = new();
        for (int hp = min; hp <= max; hp++)
        {
            if (!Enemies.Any(e => e.MaxHp == hp))
            {
                candidates.Add(hp);
            }
        }
        if (candidates.Count == 0)
        {
            candidates.Add(min);
        }
        int value = Rng.Niche.NextItem(candidates);
        monster.MaxHp = value;
        monster.Hp = value;
    }

    private void RollMove(MonsterInstance monster)
    {
        if (monster.CurrentState.Length > 0
            && monster.Def.Moves.TryGetValue(monster.CurrentState, out MoveDef? current)
            && current.Kind == "MoveState")
        {
            return;
        }
        if (!monster.PerformedFirstMove && monster.CurrentState.Length == 0
            && monster.Def.Moves.TryGetValue(monster.Def.InitialState ?? "", out MoveDef? initial) && initial.Kind == "MoveState")
        {
            monster.CurrentState = monster.Def.InitialState!;
            return;
        }
        if (monster.CurrentState.Length == 0 || monster.Def.Moves[monster.CurrentState].Kind != "MoveState")
        {
            monster.CurrentState = ResolveBranches(monster);
        }
    }

    /// <summary>
    /// Branch resolution from exported predicates/weights. Conditions are evaluated in source order
    /// (first true branch wins); unknown predicates throw instead of silently diverging.
    /// </summary>
    private string ResolveBranches(MonsterInstance monster)
    {
        string stateId = monster.CurrentState.Length > 0 ? monster.CurrentState : monster.Def.InitialState ?? "";
        MoveDef state = monster.Def.Moves.TryGetValue(stateId, out MoveDef? move)
            ? move
            : throw new CombatException($"monster {monster.Id} has no state {stateId}");
        if (state.Kind == "MoveState")
        {
            return stateId;
        }

        List<string> branches = state.Branches ?? new List<string>();
        if (state.Kind == "RandomBranchState" && branches.Count > 0)
        {
            if (state.Weights is { Count: > 0 } weights && weights.Count == branches.Count)
            {
                List<string>? repeats = state.RepeatTypes;
                List<float> effective = new(weights.Count);
                for (int i = 0; i < weights.Count; i++)
                {
                    bool cannotRepeat = repeats is { Count: > 0 } r
                        && i < r.Count
                        && r[i] == "CannotRepeat"
                        && monster.LastPerformed == branches[i];
                    effective.Add(cannotRepeat ? 0f : weights[i]);
                }
                if (effective.Sum() <= 0f)
                {
                    effective = weights;
                }
                float total = effective.Sum();
                float roll = Rng.MonsterAi.NextFloat(total);
                for (int i = 0; i < branches.Count; i++)
                {
                    roll -= effective[i];
                    if (roll <= 0f)
                    {
                        return branches[i];
                    }
                }
                return branches[^1];
            }
            float value = Rng.MonsterAi.NextFloat(branches.Count);
            int index = Math.Clamp((int)value, 0, branches.Count - 1);
            return branches[index];
        }

        foreach (ConditionDef condition in state.Conditions ?? new List<ConditionDef>())
        {
            if (EvalCondition(monster, condition.Expr) && monster.Def.Moves.ContainsKey(condition.Branch))
            {
                return condition.Branch;
            }
        }

        if (branches.Count == 0)
        {
            throw new CombatException($"monster {monster.Id} state {stateId} has no branches");
        }
        return branches[0];
    }

    /// <summary>
    /// Mini evaluator for the predicates monsters use on branch states. Only the shapes that
    /// actually occur in the game data are supported; anything else is an explicit error.
    /// </summary>
    private bool EvalCondition(MonsterInstance monster, string expr)
    {
        string e = expr.Trim();
        bool negate = false;
        while (e.StartsWith("!", StringComparison.Ordinal))
        {
            negate = !negate;
            e = e[1..].Trim().TrimStart('(').TrimEnd(')').Trim();
        }

        bool result;
        System.Text.RegularExpressions.Match slot =
            System.Text.RegularExpressions.Regex.Match(e, "SlotName\\s*==\\s*\"([^\"]*)\"");
        System.Text.RegularExpressions.Match allies =
            System.Text.RegularExpressions.Regex.Match(e, "GetAllyCount\\(\\)\\s*(>=|<=|==|>|<)\\s*(\\d+)");
        if (slot.Success)
        {
            result = string.Equals(monster.Slot ?? "", slot.Groups[1].Value, StringComparison.Ordinal);
        }
        else if (allies.Success)
        {
            int count = Enemies.Count(m => m.IsAlive && !ReferenceEquals(m, monster));
            int rhs = int.Parse(allies.Groups[2].Value);
            result = allies.Groups[1].Value switch
            {
                ">" => count > rhs,
                "<" => count < rhs,
                ">=" => count >= rhs,
                "<=" => count <= rhs,
                _ => count == rhs,
            };
        }
        else if (e.Contains("IsAlone", StringComparison.Ordinal))
        {
            result = Enemies.Count(m => m.IsAlive) == 1;
        }
        else if (e.Contains("IsFront", StringComparison.Ordinal))
        {
            // Encounters without explicit slot names place the first spawn in front.
            result = monster.Slot switch
            {
                "front" => true,
                "back" => false,
                _ => Enemies.IndexOf(monster) == 0,
            };
        }
        else if (e.Contains("CanFabricate", StringComparison.Ordinal)
            && System.Text.RegularExpressions.Regex.IsMatch(e, "^(CanFabricate|!CanFabricate)$"))
        {
            // Fabricator summons while fewer than four allies are alive.
            result = Enemies.Count(m => m.IsAlive && !ReferenceEquals(m, monster)) < 4;
        }
        else if (e.Contains("CanLay", StringComparison.Ordinal)
            && System.Text.RegularExpressions.Regex.IsMatch(e, "^(CanLay|!CanLay)$"))
        {
            result = Enemies.Count(m => m.IsAlive && !ReferenceEquals(m, monster)) <= 3;
        }
        else if (System.Text.RegularExpressions.Regex.Match(e, @"HasPower<\s*(\w+)\s*>") is { Success: true } hasPower)
        {
            string powerId = PowerIds.Normalize(CamelToScreaming(hasPower.Groups[1].Value));
            result = monster.PowerAmount(powerId) > 0;
        }
        else if (e.Contains("IsOffBalance", StringComparison.Ordinal))
        {
            // The off-balance flag flips only inside the unmodeled charge/knock-down moves; the
            // entry state is balanced, so the default is false.
            result = false;
        }
        else if (e.Contains("HasBeetleCharged", StringComparison.Ordinal))
        {
            result = e.Contains("CurrentHp", StringComparison.Ordinal) && monster.Hp * 2 >= monster.MaxHp;
        }
        else
        {
            throw new CombatException($"unhandled branch condition for {monster.Def.Entry}: {expr}");
        }
        return negate ? !result : result;
    }

    private static string CamelToScreaming(string name)
    {
        System.Text.StringBuilder sb = new();
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (char.IsUpper(c) && i > 0 && (!char.IsUpper(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]))))
            {
                sb.Append('_');
            }
            sb.Append(char.ToUpperInvariant(c));
        }
        return sb.ToString();
    }

    private void AdvanceMove(MonsterInstance monster)
    {
        monster.LastPerformed = monster.CurrentState;
        MoveDef state = monster.State;
        if (state.FollowUp is { Length: > 0 } next)
        {
            monster.CurrentState = next;
        }
        monster.PerformedFirstMove = true;
        if (monster.Def.Moves[monster.CurrentState].Kind != "MoveState")
        {
            monster.CurrentState = ResolveBranches(monster);
        }
    }

    private void ExecuteMonsterTurn(MonsterInstance monster)
    {
        MoveDef state = monster.State;
        if (state.Effects is { Count: > 0 } effects && state.EffectComplete)
        {
            int damageOps = effects.Count(e => e.Op == "damage");
            int repeats = damageOps == 1
                ? Math.Max(1, monster.Intents.Where(i => i.Type == "Attack").Sum(i => i.Repeats ?? 1))
                : 1;
            foreach (EffectDef effect in effects)
            {
                int times = effect.Op == "damage" ? repeats : 1;
                for (int i = 0; i < times; i++)
                {
                    ApplyEffect(monster, effect);
                }
            }
            return;
        }
        foreach (IntentDef intent in monster.Intents)
        {
            switch (intent.Type)
            {
                case "Attack":
                {
                    int repeats = intent.Repeats ?? 1;
                    for (int i = 0; i < repeats; i++)
                    {
                        Rules.Damage.Attack(this, monster, Player, intent.Damage ?? 0);
                        Player.Hp = Math.Max(0, Player.Hp);
                        if (PlayerDead)
                        {
                            break;
                        }
                    }
                    break;
                }
                case "Defend":
                {
                    decimal block = monster.Def.Entry == "NIBBIT" ? 5m : 5m;
                    monster.Block = (int)Math.Min(monster.Block + block, 999999999m);
                    break;
                }
                case "Buff":
                {
                    if (monster.Def.Entry == "NIBBIT")
                    {
                        monster.AddPower(StrengthPower, 2);
                    }
                    break;
                }
                case "Summon":
                {
                    // Approximation: the game picks a bot from a per-monster set via
                    // Rng.MonsterAi.NextItem; the data pack only carries the encounter's
                    // possible monsters, so summon the first one not already on the board.
                    SpawnSummonedMonster();
                    break;
                }
                case "Sleep":
                case "Hidden":
                    // Sleep/Hidden turns are pure no-ops for the board state.
                    break;
                default:
                    throw new CombatException($"intent {intent.Type} not implemented for {monster.Def.Entry}");
            }
        }
    }


    /// <summary>Spawns the first encounter monster that is not already present (SummonIntent).</summary>
    private void SpawnSummonedMonster()
    {
        string? candidate = _encounter.AllPossibleMonsters
            .FirstOrDefault(id => Enemies.All(e => e.Def.Id != id));
        if (candidate is null)
        {
            return;
        }
        MonsterDef def = _data.Monster(candidate);
        MonsterInstance monster = new(def, NextCombatId())
        {
            Slot = _encounter.Monsters?
                .Select(m => m.Count > 1 ? m[1] : null)
                .FirstOrDefault(s => !string.IsNullOrEmpty(s) && Enemies.All(e => e.Slot != s)),
        };
        RollHp(monster);
        monster.CurrentState = def.InitialState ?? "";
        monster.Rng = new Rng(Rng.Seed + (ulong)monster.CombatId);
        Enemies.Add(monster);
        if (def.StartEffectComplete)
        {
            foreach (EffectDef effect in def.StartEffects ?? new List<EffectDef>())
            {
                ApplyEffect(monster, effect);
            }
        }
        RollMove(monster);
    }

    /// <summary>Energy spent by the most recent X-cost card.</summary>
    public int LastXValue { get; private set; }

    /// <summary>Spite: tracks whether the player took unblocked damage this turn.</summary>
    public bool PlayerLostHpThisTurn { get; set; }

    /// <summary>Attack cards played (and finished) during the current player turn.</summary>
    public int AttacksPlayedThisTurn { get; set; }

    /// <summary>Cards removed by the most recent whole-hand exhaust (Fiend Fire / Second Wind / Flak Cannon).</summary>
    public int LastExhaustedCount { get; set; }

    /// <summary>Cards removed by the most recent whole-hand discard (Storm of Steel).</summary>
    public int LastDiscardedCount { get; set; }

    /// <summary>Unblocked damage dealt by the most recent card damage effect (Blight Strike / Omnislice).</summary>
    public int LastDamageDealt { get; set; }

    /// <summary>Overkill damage from that same effect.</summary>
    public int LastDamageOverkill { get; set; }

    // Combat history counters (CombatManager.History entries the cards can read).
    public int CardsPlayedTotal { get; set; }
    public int CardsDrawnTotal { get; set; }
    public int CardsDiscardedTotal { get; set; }
    public int CardsGeneratedTotal { get; set; }
    public int StarsGainedTotal { get; set; }
    public int DamageReceivedEvents { get; set; }
    public int AttacksReceivedEvents { get; set; }

    /// <summary>Set by card effects (Void Form) to end the turn after the card resolves.</summary>
    public bool RequestEndTurn { get; set; }

    public void KillOsty()
    {
        Player.OstyHp = 0;
    }

    /// <summary>Stars spent by the most recent X-cost card (star X costs are not modelled yet).</summary>
    public int LastStarXValue => 0;

    private int NextCombatId()
    {
        int max = Enemies.Count == 0 ? 0 : Enemies.Max(e => e.CombatId);
        return max + 1;
    }

    /// <summary>Places a copy of a card in one of the player's piles.</summary>
    public void AddCardToPile(CardInstance card, string? pile, int count)
    {
        for (int i = 0; i < count; i++)
        {
            CardInstance added = new(card.Def, card.Upgrade);
            switch (pile ?? "Discard")
            {
                case "Hand":
                    Player.Hand.Add(added);
                    break;
                case "Draw":
                    Player.DrawPile.Add(added);
                    break;
                case "Exhaust":
                    Player.ExhaustPile.Add(added);
                    break;
                default:
                    Player.DiscardPile.Add(added);
                    break;
            }
        }
    }

    private void ApplyEffect(MonsterInstance monster, EffectDef effect)
    {
        int amount = (int)Rules.Ascension.Amount(effect.Amount, Scenario.Ascension);
        switch (effect.Op)
        {
            case "damage":
                Rules.Damage.Attack(this, monster, Player, amount);
                Player.Hp = Math.Max(0, Player.Hp);
                break;
            case "block":
                monster.Block = (int)Math.Min(monster.Block + amount, 999999999m);
                break;
            case "heal":
                monster.Hp = Math.Min(monster.Hp + amount, monster.MaxHp);
                break;
            case "add_card":
            {
                string cardId = effect.Card ?? throw new CombatException($"add_card without card id on {monster.Def.Entry}");
                int count = effect.Count is { ValueKind: System.Text.Json.JsonValueKind.Number } c ? c.GetInt32() : 0;
                AddCardToPile(new CardInstance(_data.Card(cardId)), effect.Pile, count);
                break;
            }
            case "power":
            {
                string power = effect.Power
                    ?? throw new NotSupportedException($"unresolved power effect on {monster.Def.Entry}");
                if (effect.Amount is not { } powerAmount
                    || (powerAmount.ValueKind != System.Text.Json.JsonValueKind.Number
                        && !Rules.Ascension.IsAscensionObject(powerAmount)))
                {
                    throw new CombatException($"unresolved power amount on {monster.Def.Entry}: {power}");
                }
                ICombatant target = effect.Target == "player" ? Player : monster;
                target.AddPower(power, amount);
                // PowerCmd.Apply sets SkipNextDurationTick when a Debuff lands on a player-side
                // creature, regardless of whose turn it is.
                if (target is PlayerState)
                {
                    string normalized = PowerIds.Normalize(power);
                    PowerInstance? applied = target.Powers.FirstOrDefault(p => p.Id == normalized);
                    if (applied is not null)
                    {
                        applied.Fresh = true;
                        applied.Applier ??= monster.Def.Entry;
                    }
                }
                if (PowerIds.Normalize(power) == RitualPower && target == monster)
                {
                    monster.JustAppliedRitual = true;
                }
                break;
            }
            default:
                throw new CombatException($"effect {effect.Op} not implemented");
        }
    }

    private static void TickDurations(ICombatant monster, bool honorFresh = true)
    {
        foreach (PowerInstance power in monster.Powers.ToList())
        {
            if (power.Id is VulnerablePower or WeakPower or FrailPower)
            {
                if (honorFresh && power.Fresh)
                {
                    power.Fresh = false;
                    continue;
                }
                power.Amount--;
                if (power.Amount <= 0)
                {
                    monster.Powers.Remove(power);
                }
            }
        }
    }
}

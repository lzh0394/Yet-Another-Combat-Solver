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
    public const string StrengthPower = "STRENGTH_POWER";

    private readonly DataPack _data;

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
        Player.Energy = scenario.Energy ?? Player.MaxEnergy;

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

        Player.DrawPile.AddRange(deck);
        Rng.Shuffle.Shuffle(Player.DrawPile);

        EncounterDef encounter = data.Encounter(scenario.Encounter);
        List<(string Id, string? Slot)> spawns = encounter.Monsters?.Select(m => (m[0] ?? throw new CombatException("monster id missing"), m.Count > 1 ? m[1] : null)).ToList()
            ?? throw new CombatException($"encounter {encounter.Id} has no monster list (only all_possible_monsters)");
        int combatId = 1;
        foreach ((string id, string? slot) in spawns)
        {
            MonsterInstance monster = new(data.Monster(id), combatId++)
            {
                Slot = slot,
            };
            RollHp(monster);
            monster.Rng = new Rng(Rng.Seed + (ulong)monster.CombatId);
            Enemies.Add(monster);
        }

        foreach (MonsterInstance monster in Enemies)
        {
            RollMove(monster);
        }

        Turn = 1;
        Round = 1;
        DrawCards(5);
    }

    public DataPack Data => _data;
    public Scenario Scenario { get; }
    public HookSet Hooks { get; }
    public RunRngSet Rng { get; }
    public PlayerState Player { get; }
    public List<MonsterInstance> Enemies { get; } = new();
    public int Turn { get; private set; }
    public int Round { get; private set; }
    public bool CombatOver => Enemies.All(e => !e.IsAlive);
    public string LastActionType { get; private set; } = "start";
    public int? LastIndex { get; private set; }
    public int? LastSlot { get; private set; }
    public string? LastCard { get; private set; }
    public string? LastTarget { get; private set; }

    public void PlayCard(int? slot, string? cardId, string? target, int? actionIndex)
    {
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

        MonsterInstance? targetMonster = ResolveTarget(card, target);

        Player.Energy -= card.CostsX ? Player.Energy : cost;
        Player.Hand.Remove(card);
        Player.PlayPile.Add(card);

        CardEffects.Resolve(this, card, targetMonster);

        LastActionType = "play";
        LastIndex = actionIndex;
        LastSlot = slot;
        LastCard = card.Entry;
        LastTarget = target;

        if (CombatOver)
        {
            // Combat ended during OnPlay: the card stays in the Play pile and piles are cleared later
            // by PlayerCombatState.AfterCombatEnd, so the final snapshot still shows it there.
            Enemies.RemoveAll(e => !e.IsAlive);
            Invariants.Check(this);
            return;
        }

        Player.PlayPile.Remove(card);
        if (card.Keywords.Contains("Exhaust", StringComparer.Ordinal))
        {
            Player.ExhaustPile.Add(card);
        }
        else
        {
            Player.DiscardPile.Add(card);
        }

        Invariants.Check(this);
    }

    public void EndTurn(int? actionIndex)
    {
        if (CombatOver)
        {
            throw new CombatException("combat is over");
        }

        // discard hand
        Player.DiscardPile.AddRange(Player.Hand);
        Player.Hand.Clear();

        // player end of turn: no powers in the slice
        // enemy turn
        foreach (MonsterInstance monster in Enemies.Where(e => e.IsAlive).ToList())
        {
            ExecuteMonsterTurn(monster);
            AdvanceMove(monster);
            if (CombatOver)
            {
                break;
            }
        }

        if (!CombatOver)
        {
            // Vulnerable/Weak tick only at the end of the enemy side's turn.
            foreach (MonsterInstance monster in Enemies)
            {
                TickDurations(monster);
            }
        }

        LastActionType = "end_turn";
        LastIndex = actionIndex;
        LastSlot = null;
        LastCard = null;
        LastTarget = null;

        if (CombatOver)
        {
            Enemies.RemoveAll(e => !e.IsAlive);
            Invariants.Check(this);
            return;
        }

        StartPlayerTurn();
        Invariants.Check(this);
    }

    private void StartPlayerTurn()
    {
        Turn++;
        Round++;
        Player.Block = 0;
        Player.Energy = Player.MaxEnergy;
        DrawCards(5);
        foreach (MonsterInstance monster in Enemies.Where(e => e.IsAlive))
        {
            RollMove(monster);
        }
    }

    public void DrawCards(int count)
    {
        for (int i = 0; i < count; i++)
        {
            if (Player.DrawPile.Count == 0)
            {
                ReshuffleDiscardIntoDraw();
            }
            if (Player.DrawPile.Count == 0)
            {
                return;
            }
            CardInstance card = Player.DrawPile[0];
            Player.DrawPile.RemoveAt(0);
            Player.Hand.Add(card);
        }
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
        if (!monster.PerformedFirstMove && monster.Def.Moves.TryGetValue(monster.Def.InitialState ?? "", out MoveDef? initial) && initial.Kind == "MoveState")
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
    /// Branch resolution. Conditional branches need predicates that the data package does not keep;
    /// for the slice we encode Nibbit's conditions directly and fall back to the first branch.
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
        if (state.Kind == "ConditionalBranchState" && monster.Def.Entry == "NIBBIT")
        {
            // Nibbit.cs: alone => BUTT_MOVE; otherwise front => SLICE_MOVE, back => HISS_MOVE.
            bool alone = Enemies.Count(e => e.IsAlive) == 1;
            if (alone)
            {
                return "BUTT_MOVE";
            }
            bool front = monster.Slot is null or "front";
            return front ? "SLICE_MOVE" : "HISS_MOVE";
        }

        if (branches.Count == 0)
        {
            throw new CombatException($"monster {monster.Id} state {stateId} has no branches");
        }
        return branches[0];
    }

    private void AdvanceMove(MonsterInstance monster)
    {
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
                        if (Player.Hp <= 0)
                        {
                            throw new CombatException("player died (defeat handling not implemented)");
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
                default:
                    throw new CombatException($"intent {intent.Type} not implemented for {monster.Def.Entry}");
            }
        }
    }

    private static void TickDurations(MonsterInstance monster)
    {
        foreach (PowerInstance power in monster.Powers.ToList())
        {
            if (power.Id is VulnerablePower or WeakPower)
            {
                power.Amount--;
                if (power.Amount <= 0)
                {
                    monster.Powers.Remove(power);
                }
            }
        }
    }
}

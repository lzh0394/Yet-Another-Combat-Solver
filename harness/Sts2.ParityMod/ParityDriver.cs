using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2.ParityMod;

/// <summary>
/// Drives one scripted combat on the real game and streams a JSON-lines snapshot after every action.
/// The scenario comes from the STS2_PARITY_SCENARIO environment variable, the output path from
/// STS2_PARITY_OUT. Everything runs on the Godot main thread by awaiting process frames.
/// </summary>
internal sealed class ParityDriver
{
    private const int FrameBudget = 12000;

    private readonly SceneTree _tree;
    private readonly string _scenarioPath;
    private readonly string _outPath = string.Empty;
    private StreamWriter? _writer;

    public ParityDriver(SceneTree tree)
    {
        _tree = tree;
        _scenarioPath = OS.GetEnvironment("STS2_PARITY_SCENARIO") ?? string.Empty;
        _outPath = OS.GetEnvironment("STS2_PARITY_OUT") ?? string.Empty;
    }

    public async Task RunAsync()
    {
        try
        {
            if (string.IsNullOrEmpty(_scenarioPath) || string.IsNullOrEmpty(_outPath))
            {
                Log.Error("[parity] missing STS2_PARITY_SCENARIO or STS2_PARITY_OUT");
                return;
            }
            Log.Info("[parity] driver starting, scenario=" + _scenarioPath);
            var scenario = JsonNode.Parse(File.ReadAllText(_scenarioPath))?.AsObject()
                ?? throw new InvalidOperationException("scenario must be a JSON object");
            _writer = new StreamWriter(_outPath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            await ExecuteAsync(scenario);
        }
        catch (Exception ex)
        {
            Log.Error("[parity] scenario failed: " + ex);
            TryWrite(new JsonObject { ["type"] = "error", ["error"] = ex.ToString() });
        }
        finally
        {
            _writer?.Flush();
            _writer?.Dispose();
            Log.Info("[parity] driver finished");
            _tree.Quit();
        }
    }

    private async Task ExecuteAsync(JsonObject scenario)
    {
        await WaitUntil(() => NGame.Instance != null, "NGame instance");
        var game = NGame.Instance!;
        await game.GameStartupComplete;
        Log.Info("[parity] game startup complete");

        var character = ModelDb.GetById<CharacterModel>(IdOf(scenario["character"]?.GetValue<string>() ?? "IRONCLAD", "CHARACTER"));
        var player = Player.CreateForNewRun(character, MegaCrit.Sts2.Core.Unlocks.UnlockState.all, 1UL);

        BuildDeck(player, scenario["deck"]?.AsArray());
        if (scenario["relics"] is JsonArray relics)
        {
            foreach (var relic in player.Relics.ToList())
            {
                player.RemoveRelicInternal(relic, silent: true);
            }
            foreach (var relicNode in relics)
            {
                var relic = ModelDb.GetById<RelicModel>(IdOf(relicNode!.GetValue<string>(), "RELIC")).ToMutable();
                player.AddRelicInternal(relic, -1, silent: true);
            }
        }

        int maxHp = scenario["max_hp"]?.GetValue<int>() ?? character.StartingHp;
        int hp = scenario["hp"]?.GetValue<int>() ?? maxHp;
        player.Creature.SetMaxHpInternal(maxHp);
        player.Creature.SetCurrentHpInternal(hp);
        player.MaxEnergy = scenario["max_energy"]?.GetValue<int>() ?? character.MaxEnergy;

        int ascension = scenario["ascension"]?.GetValue<int>() ?? 0;
        string seed = scenario["seed"]?.GetValue<string>() ?? "PARITY-1";

        var acts = ActModel.GetDefaultList().Select(a => a.ToMutable()).ToList();
        var runState = RunState.CreateForNewRun(new[] { player }, acts, Array.Empty<ModifierModel>(),
            GameMode.Standard, ascension, seed);

        RunManager.Instance.SetUpNewSingleplayer(runState, shouldSave: false);
        await PreloadManager.LoadRunAssets(new[] { character });
        RunManager.Instance.Launch();

        game.RootSceneContainer.SetCurrentScene(NRun.Create(runState));
        await RunManager.Instance.SetActInternal(0);
        RunManager.Instance.RunLocationTargetedBuffer.OnLocationChanged(runState.RunLocation);
        RunManager.Instance.MapSelectionSynchronizer.OnLocationChanged(runState.MapLocation);

        _policySeed = scenario["policy_seed"]?.GetValue<ulong>() ?? 0UL;
        var encounterId = IdOf(scenario["encounter"]?.GetValue<string>() ?? "NIBBITS_WEAK", "ENCOUNTER");
        var encounter = ModelDb.GetById<EncounterModel>(encounterId).ToMutable();
        var roomType = ParseEnum<RoomType>(scenario["room_type"]?.GetValue<string>() ?? "Monster");
        await RunManager.Instance.EnterRoomDebug(roomType, MapPointType.Unassigned, encounter);
        Log.Info("[parity] combat room requested for " + encounterId);

        await WaitUntil(() => CombatManager.Instance.IsInProgress
            && player.PlayerCombatState is { Phase: PlayerTurnPhase.Play }
            && CombatManager.Instance.DebugOnlyGetState()?.CurrentSide == CombatSide.Player,
            "player combat turn");

        var combatState = CombatManager.Instance.DebugOnlyGetState()
            ?? throw new InvalidOperationException("combat state missing after start");

        // Overrides are applied after combat setup so they cannot be clobbered by initialization.
        if (scenario["block"] is JsonNode blockNode)
        {
            SetBlock(player.Creature, blockNode.GetValue<int>());
        }
        if (scenario["energy"] is JsonNode energyNode)
        {
            player.PlayerCombatState!.Energy = energyNode.GetValue<int>();
        }
        if (scenario["stars"] is JsonNode starsNode)
        {
            player.PlayerCombatState!.GainStars(starsNode.GetValue<int>());
        }
        ApplyEnemyOverrides(combatState, scenario["enemies"]?.AsArray());

        WriteLine(Snapshot(combatState, player, new JsonObject { ["type"] = "start" }));

        var actions = scenario["actions"]?.AsArray() ?? new JsonArray();
        for (int i = 0; i < actions.Count; i++)
        {
            var action = actions[i]?.AsObject() ?? throw new InvalidOperationException("action must be an object");
            action["index"] = i;
            if (!CombatManager.Instance.IsInProgress)
            {
                Log.Info("[parity] combat ended before action " + i);
                break;
            }

            await ExecuteAction(player, combatState, action);
            await Frame();
            await Frame();

            combatState = CombatManager.Instance.DebugOnlyGetState() ?? combatState;
            WriteLine(Snapshot(combatState, player, action));
        }

        Log.Info("[parity] scenario complete, " + (actions.Count) + " actions requested");
    }

    private ulong _policySeed;

    private async Task ExecuteAction(Player player, CombatState combatState, JsonObject action)
    {
        string type = action["type"]?.GetValue<string>() ?? throw new InvalidOperationException("action.type required");
        switch (type)
        {
            case "play":
            {
                var card = FindCard(player, action);
                var target = ResolveTarget(combatState, player, action);
                if (!card.CanPlay(out var reason, out _))
                {
                    throw new InvalidOperationException($"card {card.Id} cannot be played: {reason}");
                }

                Log.Info("[parity] playing " + card.Id.Entry + (target != null ? " -> " + target.LogName : ""));
                var (energy, stars) = await card.SpendResources();
                var resources = new ResourceInfo
                {
                    EnergySpent = energy,
                    EnergyValue = energy,
                    StarsSpent = stars,
                    StarValue = stars,
                };
                await card.OnPlayWrapper(new BlockingPlayerChoiceContext(), target, isAutoPlay: false, resources);
                break;
            }
            case "end_turn":
            {
                int turn = player.PlayerCombatState!.TurnNumber;
                Log.Info("[parity] end turn " + turn);
                PlayerCmd.EndTurn(player, canBackOut: false);
                await WaitUntil(() => !CombatManager.Instance.IsInProgress
                    || (player.PlayerCombatState is { Phase: PlayerTurnPhase.Play } pcs && pcs.TurnNumber > turn),
                    "next player turn after " + turn);
                break;
            }
            case "auto_turn":
            {
                while (TryPickPlayable(player, combatState, out var autoCard, out var autoTarget))
                {
                    Log.Info("[parity] auto playing " + autoCard!.Id.Entry);
                    var (energy, stars) = await autoCard.SpendResources();
                    var resources = new ResourceInfo
                    {
                        EnergySpent = energy,
                        EnergyValue = energy,
                        StarsSpent = stars,
                        StarValue = stars,
                    };
                    await autoCard.OnPlayWrapper(new BlockingPlayerChoiceContext(), autoTarget, isAutoPlay: false, resources);
                    await Frame();
                    await Frame();
                    if (!CombatManager.Instance.IsInProgress)
                    {
                        return;
                    }
                    combatState = CombatManager.Instance.DebugOnlyGetState() ?? combatState;
                }
                int turn = player.PlayerCombatState!.TurnNumber;
                PlayerCmd.EndTurn(player, canBackOut: false);
                await WaitUntil(() => !CombatManager.Instance.IsInProgress
                    || (player.PlayerCombatState is { Phase: PlayerTurnPhase.Play } pcs && pcs.TurnNumber > turn),
                    "next player turn after auto " + turn);
                break;
            }
            case "random_play":
            {
                var hand = PileType.Hand.GetPile(player).Cards;
                var candidates = new List<(CardModel Card, Creature? Target)>();
                foreach (var candidate in hand)
                {
                    if (!candidate.CanPlay(out _, out _))
                    {
                        continue;
                    }
                    Creature? resolved = candidate.TargetType switch
                    {
                        TargetType.AnyEnemy => combatState.Enemies.FirstOrDefault(c => c.IsAlive),
                        TargetType.AnyPlayer => player.Creature,
                        _ => null,
                    };
                    if (candidate.TargetType == TargetType.AnyEnemy && resolved == null)
                    {
                        continue;
                    }
                    candidates.Add((candidate, resolved));
                }

                int decision = action["index"]?.GetValue<int>() ?? 0;
                if (candidates.Count == 0)
                {
                    int turn = player.PlayerCombatState!.TurnNumber;
                    PlayerCmd.EndTurn(player, canBackOut: false);
                    await WaitUntil(() => !CombatManager.Instance.IsInProgress
                        || (player.PlayerCombatState is { Phase: PlayerTurnPhase.Play } pcs && pcs.TurnNumber > turn),
                        "next player turn after random " + turn);
                    break;
                }

                // Deterministic policy shared with the engine: MegaRandom(policySeed + decisionIndex).
                var rng = new MegaRandom(_policySeed + (ulong)decision);
                var pick = candidates[rng.Next(candidates.Count)];
                Log.Info("[parity] random playing " + pick.Card.Id.Entry);
                var (energy, stars) = await pick.Card.SpendResources();
                var resources = new ResourceInfo
                {
                    EnergySpent = energy,
                    EnergyValue = energy,
                    StarsSpent = stars,
                    StarValue = stars,
                };
                await pick.Card.OnPlayWrapper(new BlockingPlayerChoiceContext(), pick.Target, isAutoPlay: false, resources);
                break;
            }
            case "potion":
                throw new NotSupportedException("potion actions are not implemented yet");
            default:
                throw new InvalidOperationException("unsupported action type " + type);
        }
    }

    private void BuildDeck(Player player, JsonArray? deck)
    {
        player.Deck.Clear(silent: true);
        if (deck == null)
        {
            foreach (var canonical in player.Character.StartingDeck)
            {
                player.Deck.AddInternal(canonical.ToMutable(), -1, silent: true);
            }
            return;
        }

        foreach (var node in deck)
        {
            var entry = node?.AsObject() ?? throw new InvalidOperationException("deck entry must be an object");
            var canonical = ModelDb.GetById<CardModel>(IdOf(entry["id"]?.GetValue<string>() ?? throw new InvalidOperationException("deck entry id"), "CARD"));
            var card = canonical.ToMutable();
            int upgrade = entry["upgrade"]?.GetValue<int>() ?? 0;
            for (int i = 0; i < upgrade && i < card.MaxUpgradeLevel; i++)
            {
                card.UpgradeInternal();
            }
            player.Deck.AddInternal(card, -1, silent: true);
        }
    }

    private static void ApplyEnemyOverrides(CombatState combatState, JsonArray? enemies)
    {
        if (enemies == null)
        {
            return;
        }
        for (int i = 0; i < enemies.Count && i < combatState.Enemies.Count; i++)
        {
            var node = enemies[i]?.AsObject();
            if (node == null)
            {
                continue;
            }
            var creature = combatState.Enemies[i];
            if (node["max_hp"] is JsonNode maxHp)
            {
                creature.SetMaxHpInternal(maxHp.GetValue<int>());
            }
            if (node["hp"] is JsonNode hp)
            {
                creature.SetCurrentHpInternal(hp.GetValue<int>());
            }
            if (node["block"] is JsonNode block)
            {
                SetBlock(creature, block.GetValue<int>());
            }
        }
    }

    private static void SetBlock(Creature creature, int target)
    {
        if (creature.Block > target)
        {
            creature.LoseBlockInternal(creature.Block - target);
        }
        else if (creature.Block < target)
        {
            creature.GainBlockInternal(target - creature.Block);
        }
    }

    /// <summary>
    /// Greedy policy used by "auto_turn": play the first hand slot that is playable, targeting the
    /// first alive enemy for enemy-targeted cards. Both the engine and this mod implement it.
    /// </summary>
    private static bool TryPickPlayable(Player player, CombatState combatState, out CardModel? card, out Creature? target)
    {
        var hand = PileType.Hand.GetPile(player).Cards;
        for (int i = 0; i < hand.Count; i++)
        {
            var candidate = hand[i];
            if (!candidate.CanPlay(out _, out _))
            {
                continue;
            }
            Creature? resolved = candidate.TargetType switch
            {
                TargetType.AnyEnemy => combatState.Enemies.FirstOrDefault(c => c.IsAlive),
                TargetType.AnyPlayer => player.Creature,
                _ => null,
            };
            if (candidate.TargetType == TargetType.AnyEnemy && resolved == null)
            {
                continue;
            }
            card = candidate;
            target = resolved;
            return true;
        }
        card = null;
        target = null;
        return false;
    }

    private static CardModel FindCard(Player player, JsonObject action)    {
        var hand = PileType.Hand.GetPile(player).Cards;
        if (action["slot"] is JsonNode slotNode)
        {
            int slot = slotNode.GetValue<int>();
            if (slot < 0 || slot >= hand.Count)
            {
                throw new InvalidOperationException($"hand slot {slot} out of range ({hand.Count} cards)");
            }
            var cardAtSlot = hand[slot];
            if (action["card"] is JsonNode expected)
            {
                string expectedEntry = IdOf(expected.GetValue<string>(), "CARD").Entry;
                if (cardAtSlot.Id.Entry != expectedEntry)
                {
                    throw new InvalidOperationException($"hand slot {slot} is {cardAtSlot.Id.Entry}, expected {expectedEntry}");
                }
            }
            return cardAtSlot;
        }

        ModelId wanted = IdOf(action["card"]?.GetValue<string>() ?? throw new InvalidOperationException("play action needs card or slot"), "CARD");
        string entry = wanted.Entry;
        int upgrade = action["upgrade"]?.GetValue<int>() ?? -1;
        var matches = hand.Where(c => c.Id.Entry == entry && (upgrade < 0 || c.CurrentUpgradeLevel == upgrade)).ToList();
        if (matches.Count == 0)
        {
            throw new InvalidOperationException($"card {entry} (upgrade {upgrade}) not in hand: {string.Join(", ", hand.Select(c => c.Id.Entry))}");
        }
        return matches[0];
    }

    private static Creature? ResolveTarget(CombatState combatState, Player player, JsonObject action)
    {
        string? target = action["target"]?.GetValue<string>();
        if (target == null || target == "self")
        {
            return null;
        }
        if (target == "player")
        {
            return player.Creature;
        }

        var alive = combatState.Enemies.Where(c => c.IsAlive).ToList();
        if (int.TryParse(target, out int index))
        {
            if (index < 0 || index >= alive.Count)
            {
                throw new InvalidOperationException($"target index {index} out of range ({alive.Count} alive)");
            }
            return alive[index];
        }

        var byName = alive.FirstOrDefault(c => c.Monster?.Id.Entry == target);
        return byName ?? throw new InvalidOperationException("target not found: " + target);
    }

    private JsonObject Snapshot(CombatState state, Player player, JsonObject action)
    {
        var pcs = player.PlayerCombatState;
        bool over = !CombatManager.Instance!.IsInProgress || state.Enemies.All(e => !e.IsAlive);
        var root = new JsonObject
        {
            ["type"] = "snapshot",
            ["action"] = action.DeepClone(),
            ["round"] = state.RoundNumber,
            ["turn"] = pcs?.TurnNumber ?? 0,
            ["side"] = state.CurrentSide.ToString(),
            ["combat_over"] = over,
            ["player"] = PlayerSnapshot(player),
            ["enemies"] = new JsonArray(state.Enemies.Select((c, i) => (JsonNode)EnemySnapshot(c, i, player)).ToArray()),
            ["piles"] = PileSnapshot(player),
            ["rng"] = RngSnapshot(state, player),
        };
        return root;
    }

    private static JsonObject PlayerSnapshot(Player player)
    {
        var pcs = player.PlayerCombatState;
        return new JsonObject
        {
            ["id"] = player.Character.Id.Entry,
            ["hp"] = player.Creature.CurrentHp,
            ["max_hp"] = player.Creature.MaxHp,
            ["block"] = player.Creature.Block,
            ["energy"] = pcs?.Energy ?? 0,
            ["max_energy"] = pcs?.MaxEnergy ?? player.MaxEnergy,
            ["stars"] = pcs?.Stars ?? 0,
            ["powers"] = PowersSnapshot(player.Creature),
            ["relics"] = new JsonArray(player.Relics.Select(r => (JsonNode)new JsonObject
            {
                ["id"] = r.Id.Entry,
                ["counter"] = r.DisplayAmount,
            }).ToArray()),
            ["potions"] = new JsonArray(player.PotionSlots.Select((p, i) => (JsonNode)new JsonObject
            {
                ["slot"] = i,
                ["id"] = p?.Id.Entry,
            }).ToArray()),
        };
    }

    private static JsonObject EnemySnapshot(Creature creature, int index, Player player)
    {
        var intentArray = new JsonArray();
        var nextMove = creature.Monster?.NextMove;
        if (nextMove != null)
        {
            foreach (var intent in nextMove.Intents)
            {
                var intentNode = new JsonObject { ["type"] = intent.IntentType.ToString() };
                if (intent is AttackIntent attack)
                {
                    try
                    {
                        intentNode["damage"] = attack.GetTotalDamage(new[] { player.Creature }, creature);
                    }
                    catch (Exception ex)
                    {
                        intentNode["damage_error"] = ex.GetType().Name;
                    }
                    intentNode["repeats"] = attack.Repeats;
                }
                intentArray.Add(intentNode);
            }
        }

        return new JsonObject
        {
            ["index"] = index,
            ["id"] = creature.Monster?.Id.Entry ?? creature.LogName,
            ["hp"] = creature.CurrentHp,
            ["max_hp"] = creature.MaxHp,
            ["block"] = creature.Block,
            ["alive"] = creature.IsAlive,
            ["powers"] = PowersSnapshot(creature),
            ["intents"] = intentArray,
        };
    }

    private static JsonArray PowersSnapshot(Creature creature)
    {
        return new JsonArray(creature.Powers.Select(p => (JsonNode)new JsonObject
        {
            ["id"] = p.Id.Entry,
            ["amount"] = p.Amount,
        }).ToArray());
    }

    private static JsonObject PileSnapshot(Player player)
    {
        var piles = player.PlayerCombatState?.AllPiles ?? Array.Empty<CardPile>();
        var result = new JsonObject();
        foreach (var pile in piles.OrderBy(p => p.Type.ToString(), StringComparer.Ordinal))
        {
            var cards = new JsonArray();
            foreach (var card in pile.Cards)
            {
                cards.Add(CardSnapshot(card, includePlayable: pile.Type == PileType.Hand));
            }
            result[pile.Type.ToString().ToLowerInvariant()] = cards;
        }
        return result;
    }

    private static JsonObject CardSnapshot(CardModel card, bool includePlayable)
    {
        var node = new JsonObject
        {
            ["id"] = card.Id.Entry,
            ["upgrade"] = card.CurrentUpgradeLevel,
            ["cost"] = card.EnergyCost.GetResolved(),
            ["costs_x"] = card.EnergyCost.CostsX,
            ["star_cost"] = card.GetStarCostWithModifiers(),
            ["target"] = card.TargetType.ToString(),
            ["keywords"] = new JsonArray(card.Keywords.Select(k => (JsonNode)JsonValue.Create(k.ToString())!).ToArray()),
            ["enchantment"] = card.Enchantment?.Id.Entry,
            ["affliction"] = card.Affliction?.Id.Entry,
        };
        if (includePlayable)
        {
            node["can_play"] = card.CanPlay(out _, out _);
        }
        return node;
    }

    private static JsonObject RngSnapshot(CombatState state, Player player)
    {
        var result = new JsonObject();
        try
        {
            var runRng = state.RunState.Rng;
            result["shuffle"] = RngState(runRng.Shuffle);
            result["monster_ai"] = RngState(runRng.MonsterAi);
            result["combat_targets"] = RngState(runRng.CombatTargets);
            result["combat_card_generation"] = RngState(runRng.CombatCardGeneration);
            result["combat_card_selection"] = RngState(runRng.CombatCardSelection);
            result["combat_energy_costs"] = RngState(runRng.CombatEnergyCosts);
            result["niche"] = RngState(runRng.Niche);
        }
        catch (Exception ex)
        {
            result["error"] = ex.GetType().Name;
        }

        var monsters = new JsonArray();
        foreach (var enemy in state.Enemies)
        {
            try
            {
                monsters.Add(new JsonObject
                {
                    ["id"] = enemy.Monster?.Id.Entry,
                    ["rng"] = RngState(enemy.Monster!.Rng),
                });
            }
            catch (Exception ex)
            {
                monsters.Add(new JsonObject { ["id"] = enemy.Monster?.Id.Entry, ["error"] = ex.GetType().Name });
            }
        }
        result["monsters"] = monsters;
        return result;
    }

    private static JsonObject RngState(Rng rng)
    {
        var s = rng.ToSerializable();
        return new JsonObject
        {
            ["counter"] = s.counter,
            ["s0"] = s.state0,
            ["s1"] = s.state1,
            ["s2"] = s.state2,
            ["s3"] = s.state3,
        };
    }

    private void WriteLine(JsonObject node)
    {
        TryWrite(node);
    }

    private void TryWrite(JsonObject node)
    {
        try
        {
            _writer?.WriteLine(node.ToJsonString());
            _writer?.Flush();
        }
        catch (Exception ex)
        {
            Log.Error("[parity] write failed: " + ex);
        }
    }

    private async Task Frame()
    {
        await _tree.ToSignal(_tree, SceneTree.SignalName.ProcessFrame);
    }

    private async Task WaitUntil(Func<bool> condition, string what)
    {
        for (int frame = 0; frame < FrameBudget; frame++)
        {
            if (condition())
            {
                return;
            }
            await Frame();
        }
        throw new TimeoutException("timed out waiting for " + what);
    }

    private static ModelId IdOf(string text, string category)
    {
        return text.Contains('.') ? ModelId.Deserialize(text) : ModelId.Deserialize(category + "." + text);
    }

    private static T ParseEnum<T>(string value) where T : struct, Enum
    {
        return Enum.TryParse<T>(value, ignoreCase: true, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"invalid {typeof(T).Name}: {value}");
    }
}

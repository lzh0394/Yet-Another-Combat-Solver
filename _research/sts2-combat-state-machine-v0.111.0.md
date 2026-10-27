# Slay the Spire 2 v0.111.0 — combat turn/phase state machine (single-player)

Refs relative to `C:\workspaces\Yet-Another-Combat-Solver\_research\decompiled-v0.111.0\MegaCrit\sts2\Core\`.
`CM` = `Combat/CombatManager.cs`, `CS` = `Combat/CombatState.cs`, `PCS` = `Entities/Players/PlayerCombatState.cs`, `Creature` = `Entities/Creatures/Creature.cs`, `Monster` = `Models/MonsterModel.cs`, `Card` = `Models/CardModel.cs`, `CPC` = `Commands/CardPileCmd.cs`, `AQS` = `GameActions/Multiplayer/ActionQueueSynchronizer.cs`.

## 1. Combat setup — exact ordered sequence

`Rooms/CombatRoom.cs:197 StartCombat(runState)`:
```csharp
if (!Encounter.HaveMonstersBeenGenerated) Encounter.GenerateMonstersWithSlots(CombatState.RunState);
if (ShouldCreateCombat) await PreloadManager.LoadRoomCombatAssets(Encounter, CombatState.RunState);
foreach (var (monsterModel, slot) in Encounter.MonstersWithSlots) {
    monsterModel.AssertMutable();
    if (ShouldCreateCombat) {
        Creature creature = CombatState.CreateCreature(monsterModel, CombatSide.Enemy, slot);
        CombatState.AddCreature(creature);
    }
    CombatState.RunState.CurrentMapPointHistoryEntry?.Rooms.Last().MonsterIds.Add(monsterModel.Id);
}
NRun.Instance?.SetCurrentRoom(NCombatRoom.Create(this, CombatRoomMode.ActiveCombat));
CombatManager.Instance.SetUpCombat(CombatState);
if (runState != null) await Hook.AfterRoomEntered(runState, this);
CombatManager.Instance.AfterCombatRoomLoaded();
```
1. **Encounter monster generation** — `Models/EncounterModel.cs:254`: `ulong seed = (ulong)((long)runState.Rng.Seed + (long)runState.TotalFloor) + StringHelper.GetDeterministicHashCode(base.Id.Entry); _rng = new Rng(seed);` then `_monstersWithSlots = GenerateMonsters();`. → **RNG**: a *per-encounter* `Rng` (seed + `TotalFloor` + encounter id hash), not part of `RunRngSet`, never saved; comment at `EncounterModel.cs:41-48` says this is deliberate.
2. **Creature creation + HP roll** — `CS:233 CreateCreature`: `monster.RunRng = RunState.Rng; Creature creature = new Creature(monster, side, slot);` then for enemies `creature.SetUniqueMonsterHpValue(creaturesOnSide, RunState.Rng.Niche);` + `ScaleMonsterHpForMultiplayer(...)`; `AttachCreature` assigns `CombatId`; `monster.Rng = new Rng((ulong)((long)RunState.Rng.Seed + (long)(RunState.CurrentMapCoord?.col ?? 0) + (RunState.CurrentMapCoord?.row ?? 0) + RunState.CurrentActIndex + creature.CombatId.Value));`.
   HP roll `Creature:372`: `HashSet<int> hashSet = Enumerable.Range(minInitialHp, num - minInitialHp).ToHashSet(); hashSet.ExceptWith(from e in creaturesOnSide.Except(<this>) select e.MaxHp); MonsterMaxHpBeforeModification = (_currentHp = (_maxHp = (hashSet.Count <= 0) ? rng.NextInt(minInitialHp, num) : rng.NextItem(hashSet)));` → **RNG**: 1 draw on `RunState.Rng.Niche` per enemy spawn (unique HP per simultaneous group). Player HP is not rolled: `Player.cs:262 Creature = new Creature(this, currentHp, maxHp)`.
3. **Player combat state + initial shuffle** — `CM:444 SetUpCombat`: `_turnState = new CombatTurnState(...)` (IsStarting=true, IsInProgress=true) → `State.MultiplayerScalingModel?.OnCombatEntered` → `StateTracker.SetState` → `foreach player: player.ResetCombatState()` (`Player.cs:797` → new `PlayerCombatState`, `TurnNumber=1`, `Energy=0`) → `foreach player2: player2.PopulateCombatState(player2.RunState.Rng.Shuffle, State)` → `NetCombatCardDb.Instance.StartCombat` → `foreach creature: AddCreature(creature)` (→ `creature.Monster?.SetUpForCombat()` at `CM:1106`) → `CombatSetUp` event.
   `Player.cs:806 PopulateCombatState`: clone each `Deck.Cards` entry (`state.CloneCard`, set `DeckVersion`, `DrawPile.AddInternal`) then `DrawPile.RandomizeOrderInternal(this, rng, state)`; `Entities/Cards/CardPile.cs:75`:
   ```csharp
   _cards.UnstableShuffle(rng);
   TestRngInjector.ConsumeInitialShuffleOverride()?.Invoke(_cards);       // test only
   Hook.ModifyShuffleOrder(state, player, _cards, isInitialShuffle: true);
   ```
   → **RNG**: `deckCount - 1` `NextInt` calls on the run-wide `RunState.Rng.Shuffle` (`Runs/RunRngSet.cs:40`); the counter is not reset per combat. `Monster:410 SetUpForCombat`: `MoveStateMachine = GenerateMoveStateMachine(); SpawnedThisTurn = true;`
4. `CM:470 AfterCombatRoomLoaded`: `SetCombatState(PreCombatSetup)` then starts the turn-loop task.
5. **StartCombatInternal** — `CM:576`: `ThrowIfCancellationRequested(); ActionExecutor.Unpause(); await FinishedExecutingActions(); Pause();` → BGM → `foreach creature: await AfterCreatureAdded(creature, State)` → `SetCombatState(NotPlayPhase)` → `IsInProgress = true; IsStarting = false;` → `await Hook.BeforeCombatStart(RunState, State)` (`Hook.cs:311`, calls `BeforeCombatStart` then `BeforeCombatStartLate`) → `CombatBegan` → FTUE/music → `await Cmd.CustomScaledWait(0.5f, 1f)` (UI) → `await StartTurn(turnState)`.
   `CM:1128 AfterCreatureAdded`: `await creature.AfterAddedToRoom(); if (creature.IsEnemy && state.CurrentSide == CombatSide.Player) creature.Monster.RollMove(state.Players.Select(p => p.Creature));` → **RNG**: first intent roll, `Monster:416 RollMove` = `NextMove = MoveStateMachine.RollMove(targets, Creature, RunRng.MonsterAi)` (stream `RunState.Rng.MonsterAi`).
6. **Turn loop**:
   ```csharp
   while (turnState.IsLive) {
       if (State.CurrentSide == CombatSide.Player) {
           await StartTurn(turnState);
           actionDuringEnemyTurn = await AwaitTurnEndAndSwitchSides(turnState);
       } else { await StartTurn(turnState, actionDuringEnemyTurn); actionDuringEnemyTurn = null; }
   }
   ```
7. **First player turn** (inside `CM:688 StartTurn`, player branch) — `SetPhaseForAllPlayers(None)`; `foreach c: c.BeforeTurnStart(CurrentSide)` (snapshots `PowerModel.AmountOnTurnStart`, `Creature:678`); `Hook.BeforeSideTurnStart`; `SetPhaseForAllPlayers(Start)`; clear ready sets + new `EndTurnSignalSource`/`BeginEnemyTurnSignalSource`; `if (!isExtraPlayerTurn) foreach enemy: enemy.PrepareForNextTurn(State.PlayerCreatures)`; `CustomScaledWait(0.5,0.8)`; `foreach c: await c.AfterTurnStart(CurrentSide)` (**block clear**); `foreach c: await Hook.AfterBlockCleared`; `foreach player: SetupPlayerTurn(...)`; `Hook.AfterSideTurnStart`; per-player `PCS.OrbQueue.AfterTurnStart`; checksum `"After player turn start"`; mark dead players ready; `RunAutoPrePlayPhase`; `CheckWinCondition`; `ActionExecutor.Unpause(); SetCombatState(PlayPhase); IsEnemyTurnStarted=false;` `TurnStarted`.
   - **No block clear on turn 1** — `Creature:686`:
     ```csharp
     public async Task AfterTurnStart(CombatSide side) {
         if (side == CombatSide.Player) { Player? player = Player;
             if (player != null && player.PlayerCombatState?.TurnNumber == 1) return; }
         await ClearBlock();
     }
     ```
     So start-of-combat block survives the whole first player turn; from turn 2 it is cleared at turn start.
   - **Energy reset** — `CM:880 SetupPlayerTurn`: `if (Hook.ShouldPlayerResetEnergy(state, player)) { SfxCmd.Play("event:/sfx/ui/gain_energy"); player.PlayerCombatState.ResetEnergy(); } else player.PlayerCombatState.AddMaxEnergyToCurrent();` then `Hook.AfterEnergyReset`, `Hook.BeforeHandDraw`.
   - **Opening hand** — `decimal handDraw = Hook.ModifyHandDraw(state, player, 5m, out modifiers)`; `Hook.AfterModifyingHandDraw`; if `TurnNumber == 1`: `ShouldStartAtBottomOfDrawPile` enchantments → pile bottom, `Innate` cards → top, `handDraw = Math.Max(handDraw, innateCount); handDraw = Math.Min(handDraw, CardPile.MaxCardsInHand /*10*/)`; `await CardPileCmd.Draw(ctx, handDraw, player, fromHandDraw: true)`; `await Hook.AfterPlayerTurnStart(...)`.
8. **BurningBlood** (`Models/Relics/BurningBlood.cs:10-24`): starter relic, `HealVar(6m)`, **no combat-start hook**:
   ```csharp
   public override async Task AfterCombatVictory(CombatRoom _) {
       if (!base.Owner.Creature.IsDead) { Flash(); await CreatureCmd.Heal(base.Owner.Creature, base.DynamicVars.Heal.BaseValue); }
   }
   ```
   Called from `Hook.AfterCombatVictory` (`Hook.cs:352`: `AfterCombatVictoryEarly` for all listeners, then `AfterCombatVictory`) inside `CM:1300 EndCombatInternal`.

## 2. `PlayerTurnPhase` — values, transitions, turn counter

`Combat/PlayerTurnPhase.cs:6-61`: `None` (not in combat / enemy turn), `Start` (side switch → cards playable: block clear, `Hook.AfterSideTurnStart`, orb passive, energy reset, hand draw), `AutoPrePlay` (`Hook.AfterAutoPrePlayPhaseEntered`), `Play` (full player control; hooks only react to player actions), `AutoPostPlay` (`Hook.AfterAutoPostPlayPhaseEntered`), `End` (all players ended + queue drained, until side switch: `Hook.BeforeSideTurnEnd`, orb passives, hand flush, `Hook.AfterSideTurnEnd`).

Legal transitions (all through `CM:314 SetPhaseForAllPlayers(state, phase)`):
```
None --(StartTurn player branch, CM:725)--> Start
Start --(RunAutoPrePlayPhase, CM:862)-----> AutoPrePlay --> Play
Play --(EndPlayerTurnPhaseOneInternal, CM:1518)-> AutoPostPlay --> End
End --(SwitchSides; next StartTurn)-------> None        // also CM:1192 Reset, CM:1312 EndCombatInternal
```
- Phase active while the player can act: **`Play`** — set *after* all start-of-turn hooks, together with `ActionExecutor.Unpause()` and `SetCombatState(PlayPhase)`.
- Phase is per player (`PCS:44-58`, raises `PlayerTurnPhaseChanged`) but always written for all players.
- **Turn increments only on enemy→player switch** — `CM:1852 SwitchSides`:
  ```csharp
  if (State.CurrentSide == CombatSide.Player && extraTurnPlayers.Count == 0) State.CurrentSide = CombatSide.Enemy;
  else { State.CurrentSide = CombatSide.Player; if (extraTurnPlayers.Count == 0) State.RoundNumber++; }
  foreach (Player item in (extraTurnPlayers.Count > 0) ? extraTurnPlayers : State.Players)
      item.PlayerCombatState.IncrementTurnNumber();
  ```
  `TurnNumber` starts at 1 (`PCS:37`), `RoundNumber` at 1 (`CS:157`).
- `Combat/CombatTurnState.cs:21-132`: `PlayersReadyToEndTurn`, `PlayersReadyToBeginEnemyTurn`, `PlayersTakingExtraTurn`, `EndTurnSignalSource`, `BeginEnemyTurnSignalSource`, `PendingLoss`, `IsStarting`, `IsEnemyTurnStarted`, `EndingPlayerTurnPhaseOne/Two`, `IsLive => IsInProgress && !cancel`. `Combat/EndTurnSignal.cs:15`:
  ```csharp
  internal sealed record EndTurnSignal(GameAction? RunningAction, int ScheduledTurnNumber, Player ScheduledPlayer, Func<Task>? ActionDuringEnemyTurn);
  ```

## 3. End turn — exact ordering

### 3.1 Ready declaration (no state change yet)
`Nodes/Combat/NEndTurnButton.cs:589` → `RequestEnqueue(new EndPlayerTurnAction(me, turnNumber))`. `GameActions/EndPlayerTurnAction.cs:19-47` (`ActionType => CombatPlayPhaseOnly`): if `_player.PlayerCombatState.TurnNumber != _turnNumber` → log "Ignoring end turn action." and return; else `PlayerCmd.EndTurn(_player, canBackOut: true)`.
`Commands/PlayerCmd.cs:279`:
```csharp
public static void EndTurn(Player player, bool canBackOut, Func<Task>? actionDuringEnemyTurn = null) {
    if (!CombatManager.Instance.IsPlayerReadyToEndTurn(player)) {          // CM:1035, under turnState.ReadyLock
        if (LocalContext.IsMe(player)) CombatManager.Instance.OnEndedTurnLocally();   // CM:995 -> PlayerActionsDisabled = true
        CombatManager.Instance.SetReadyToEndTurn(player, canBackOut, actionDuringEnemyTurn);
    }
}
```
`CM:935 SetReadyToEndTurn`: add to `PlayersReadyToEndTurn`, fire `PlayerEndedTurn`; when all players ready:
```csharp
GameAction? gameAction = ActionExecutor.CurrentlyRunningAction;   // nulled unless ActionQueueSet.IsGameActionPlayerDriven
int scheduledTurnNumber = player.PlayerCombatState?.TurnNumber ?? -1;
turnState.EndTurnSignalSource?.TrySetResult(new EndTurnSignal(gameAction, scheduledTurnNumber, player, actionDuringEnemyTurn));
```
### 3.2 Hand-off in the turn loop
`CM:635 AwaitTurnEndAndSwitchSides`: `await EndTurnSignalSource.Task` → `await endTurnSignal.RunningAction.CompletionTask` (in-flight card play always finishes first) → `AfterAllPlayersReadyToEndTurn` → `await BeginEnemyTurnSignalSource.Task` → `AfterAllPlayersReadyToBeginEnemyTurn`.
`CM:1440 AfterAllPlayersReadyToEndTurn`: stale-turn check (`"stale player-turn-end transition"` log); `EndingPlayerTurnPhaseOne = true; SetCombatState(EndTurnPhaseOne)` (→ `AQS:129` cancels all player-driven combat actions); `await WaitUntilQueueIsEmptyOrWaitingOnNonPlayerDrivenAction` (`CM:1478`); `await EndPlayerTurnPhaseOneInternal`; then `RequestEnqueue(new ReadyToBeginEnemyTurnAction(...))`.
### 3.3 Phase one — turn-end effects (hand still in hand)
`CM:1518 EndPlayerTurnPhaseOneInternal`: throws `InvalidOperationException($"EndPlayerTurn called while the current side is {CurrentSide}!")` if side ≠ Player; `WaitForUnpause`; `playersEndingTurn` = extra-turn players else all players;
1. per player: `Phase = AutoPostPlay` + `await Hook.AutoPostPlayPhaseEntered`-equivalent (`Hook.AfterAutoPostPlayPhaseEntered(state, player)`), then `WaitForCompletion` + `Phase = End`;
2. `await Hook.BeforeSideTurnEnd(State, CurrentSide, playersEndingTurn.Select(p => p.Creature))`;
3. `if (await CheckWinCondition(turnState)) return;` ← combat may end here, skipping the rest;
4. per player `await DoTurnEnd(turnState, player, ctx)` (`CM:1602`): `await player.PlayerCombatState.OrbQueue.BeforeTurnEnd(ctx)` → early return if combat ending → scan hand: `HasTurnEndInHandEffect` cards collected, Ethereal cards passing `Hook.ShouldEtherealTrigger` → `await CardCmd.Exhaust(ctx, card, causedByEthereal: true)` → `DoTurnEndCards` → per card `await card.OnTurnEndInHandWrapper(ctx)` (`CM:1671`) then move to Discard bottom (or Exhaust);
5. `CheckWinCondition`; per player `await Hook.BeforeFlush(...)`; checksum `"After player turn phase one end"`; `CheckWinCondition`.
**The hand is not discarded here** — turn-end-in-hand and ethereal cards resolve first; the hand is flushed in phase two.
`GameActions/ReadyToBeginEnemyTurnAction.cs`: `ActionType => Combat`, `ExecuteAction() => CombatManager.Instance.SetReadyToBeginEnemyTurn(_player, _actionDuringEnemyTurn)`. `CM:1006`: completes `BeginEnemyTurnSignalSource` when all players ready **or immediately in single-player**: `flag2 = (PlayersReadyToBeginEnemyTurn.Count == State.Players.Count && flag) || (flag && RunManager.Instance.NetService.Type == NetGameType.Singleplayer);` (`flag = CurrentSide == Player`).
### 3.4 Phase two — cleanup, discard hand, side switch
`CM:1702 AfterAllPlayersReadyToBeginEnemyTurn`: `EndingPlayerTurnPhaseTwo = true; SetCombatState(NotPlayPhase)` (→ `AQS:133` pauses all player queues); `AboutToSwitchToEnemyTurn`; `await Task.Yield();` if still Player → `await EndPlayerTurnPhaseTwoInternal(turnState); await SwitchFromPlayerToEnemySide(turnState);`
`CM:1741 EndPlayerTurnPhaseTwoInternal`: per player `await FlushPlayerHand(...)`; `await Hook.AfterSideTurnEnd(State, CurrentSide, playersEndingTurn.Select(p => p.Creature))`; checksum `"after player turn phase two end"`.
`CM:1780 FlushPlayerHand`:
```csharp
bool flag = Hook.ShouldFlush(state, player);                       // false -> keep whole hand
foreach (CardModel card in player.PlayerCombatState.Hand.Cards.ToList())
    if (!flag || card.ShouldRetainThisTurn) cardsToRetain.Add(card); else cardsToFlush.Add(card);
if (cardsToFlush.Count > 0) await CardPileCmd.Add(cardsToFlush, PileType.Discard);
await Hook.AfterFlush(state, player, ctx, cardsToFlush, cardsToRetain);
player.PlayerCombatState.EndOfTurnCleanup();                        // PCS:268
```
`CM:1820 SwitchFromPlayerToEnemySide`: clear `PlayersTakingExtraTurn`, re-add players where `Hook.ShouldTakeExtraTurn`; `SwitchSides`; per extra player `await Hook.AfterTakingExtraTurn`; `WaitForUnpause`.
`CM:1852 SwitchSides`: `CurrentSide = Enemy` (players' `TurnNumber` **not** incremented); `foreach (Creature c in State.Creatures) c.OnSideSwitch()` → `Monster:483` sets `SpawnedThisTurn = false`; fires `TurnEnded`.
### 3.5 Enemy turn
`StartTurn` enemy branch (`CM:688`): `SetPhaseForAllPlayers(None)`; `BeforeTurnStart`; `Hook.BeforeSideTurnStart`; `IsEnemyTurnStarted = true`; checksum `"After enemy turn start"`; `WaitForUnpause`; `CheckWinCondition`; `await ExecuteEnemyTurn(turnState, actionDuringEnemyTurn)`.
`CM:1405 ExecuteEnemyTurn`:
```csharp
if (actionDuringEnemyTurn != null) await actionDuringEnemyTurn();
foreach (Creature enemy in turnState.State.Enemies.ToList()) {          // list order == act order
    if (!State.ContainsCreature(enemy)) continue;
    NCreature nCreature = NCombatRoom.Instance?.GetCreatureNode(enemy);
    if (nCreature != null) await nCreature.PerformIntent();              // UI animation only (Nodes/Combat/NCreature.cs:647)
    await enemy.TakeTurn();
    await WaitForUnpause();
    await CheckWinCondition(turnState);                                   // combat may end mid-enemy-turn
    if (!IsInProgress) return;
}
ChecksumTracker.GenerateChecksum("After enemy turn end");
await EndEnemyTurn(turnState);
```
Act order = `State.Enemies` insertion order, re-sorted by `Encounter.Slots` when slots are declared (`CS:496 SortEnemiesBySlotName`); `CS:504 SetEnemyIndex` throws if slots exist.
`Creature:711 TakeTurn`: throws `InvalidOperationException("Only enemy monsters can take automated turns.")` unless monster on Enemy side; `if (!Monster.SpawnedThisTurn) await Monster.PerformMove();`
`Monster:435 PerformMove`:
```csharp
await Cmd.CustomScaledWait(0.1f, 0.2f);           // UI
IsPerformingMove = true; MoveState state = NextMove;
IReadOnlyList<Creature> targets = combatState.PlayerCreatures;
await state.PerformMove(targets);                  // the actual move effects
MoveStateMachine?.OnMovePerformed(state);          // advance state machine (MonsterMoveStateMachine.cs:49)
CombatManager.Instance.History.MonsterPerformedMove(combatState, this, state, targets);
IsPerformingMove = false;
if (Creature.IsDead && Hook.ShouldCreatureBeRemovedFromCombatAfterDeath(combatState, Creature))
    combatState.RemoveCreature(Creature);
await Cmd.CustomScaledWait(0.1f, 0.4f);            // UI
```
Intent advance rule — `MonsterMoves/MonsterMoveStateMachine/MonsterMoveStateMachine.cs:54 FindNextMoveState`: early return if `!_currentState.CanTransitionAway || (!_performedFirstMove && _currentState.IsMove)`, else `do { string nextState = _currentState.GetNextState(owner, rng); ... } while (!_currentState.IsMove);` (`rng` = `MonsterAi`); `OnMovePerformed` sets `_performedFirstMove = true`. So the state machine advances *right after* a move is performed, and the next intent is rolled at the start of the next player turn.
`CM:1076 EndEnemyTurn`: `throw` if side ≠ Enemy; `WaitForUnpause`; `EndEnemyTurnInternal`; `CheckWinCondition`; `if (!IsCombatEnding(turnState)) { SwitchSides(turnState); await WaitForUnpause(turnState); }`.
`CM:1691 EndEnemyTurnInternal`: `Hook.BeforeSideTurnEnd`; per player `PCS.EndOfTurnCleanup()`; `Hook.AfterSideTurnEnd`.
### 3.6 Start of the next player turn
Concretely (see §1.7 and §5 for the block/poison ordering): `Phase=None` → `BeforeTurnStart` (power snapshots) → `Hook.BeforeSideTurnStart` → `Phase=Start` → clear ready sets / new signal sources → enemy intents re-rolled (`enemy.PrepareForNextTurn`, `Creature:547` → `Monster.RollMove` + `Ncreature.RefreshIntents`) → `CustomScaledWait` → `creature.AfterTurnStart` (**block=0**) → `Hook.AfterBlockCleared` → `SetupPlayerTurn` (energy reset, draw, `Hook.AfterPlayerTurnStart`) → `Hook.AfterSideTurnStart` (poison) → orb passives → checksum → `RunAutoPrePlayPhase` → `CheckWinCondition` → `Unpause` + `SetCombatState(PlayPhase)`.
Block clear body — `Creature:723`: `if (Hook.ShouldClearBlock(CombatState, this, out AbstractModel preventer)) Block = 0; else await Hook.AfterPreventingBlockClear(CombatState, preventer, this);`

## 4. Energy and stars

- Reset only in `CM:880 SetupPlayerTurn` (once per player turn, after block clear and before the draw). `Hook.ShouldPlayerResetEnergy` (`Hook.cs:2387`) fails if **any** listener returns false → then `AddMaxEnergyToCurrent()` instead (`PCS:167`, additive).
- `PCS:101 MaxEnergy => (int)Hook.ModifyMaxEnergy(combatState, player, _player.MaxEnergy)`; `PCS:162 ResetEnergy() => Energy = MaxEnergy()`; `LoseEnergy`/`GainEnergy` clamp `0..999999999` (`PCS:172-188`).
- **X-cost capture** — `Card:1807 SpendResources`, called from `GameActions/PlayCardAction.cs:62-104` *before* `OnPlayWrapper`:
  ```csharp
  int energy = Owner.PlayerCombatState.Energy;
  int energyToSpend = EnergyCost.GetAmountToSpend();                  // X cards: current Energy
  int starsToSpend  = Math.Max(0, GetStarCostWithModifiers());
  if (energyToSpend > energy && Hook.ShouldPayExcessEnergyCostWithStars(CombatState, Owner)) {
      starsToSpend += (energyToSpend - energy) * 2; energyToSpend = energy;   // 1 missing energy -> 2 stars
  }
  await SpendEnergy(energyToSpend); await SpendStars(starsToSpend);
  ```
  `Card:1822 SpendEnergy`: `if (EnergyCost.CostsX) EnergyCost.CapturedXValue = amount;` → `History.EnergySpent` → `Owner.PlayerCombatState.LoseEnergy(Math.Max(0, amount))` → `await Hook.AfterEnergySpent(...)`.
  `CardEnergyCost.cs:134 GetAmountToSpend()`: `if (CostsX) return _card.Owner.PlayerCombatState?.Energy ?? 0; return Math.Max(0, GetWithModifiers(CostModifiers.All));` `CapturedXValue` (`:61-80`) throws unless `CostsX`; `GetResolved()` (`:155`) returns it for X cards. Card effects must read X via `CardModel.ResolveEnergyXValue` (applies X modifiers such as ChemicalX), not `CapturedXValue`.
- **Stars persist across turns** (`PCS:103-119`, writes go through `CombatManager.History.StarsModified`); nothing resets them in `SetupPlayerTurn`; only player death zeroes them (`CM:1221 HandlePlayerDeath` → `PlayerCmd.SetEnergy(0m)` / `PlayerCmd.SetStars(0m)`). `PlayerCmd.cs:69 SetEnergy`, `:90 GainStars`, `:104 LoseStars`, `:119 SetStars`.

## 5. Block

- Gain `Creature:460 GainBlockInternal(decimal) → Block = (int)Math.Min(Block + amount, 999999999m)`; loss `Creature:469 LoseBlockInternal → Math.Max(Block - amount, 0)`; setter rejects negative (`Creature:39-58`).
- Absorption `Creature.cs:433`: `decimal num = props.HasFlag(ValueProp.Unblockable) ? 0m : Math.Min(Block, amount); Block -= (int)num;`
- **Cleared at the start of the owner's own side turn**, never at end of turn: `Creature:686 AfterTurnStart` → `Creature:723 ClearBlock`, gated by `Hook.ShouldClearBlock` (else `Hook.AfterPreventingBlockClear`). Skipped entirely for players on `TurnNumber == 1`.
- Exact ordering relative to other start-of-turn effects (`CM:688`): (1) `BeforeTurnStart` snapshots `AmountOnTurnStart` → (2) `Hook.BeforeSideTurnStart` → (3) `Phase=Start` → (4) enemy intents rolled → (5) `CustomScaledWait` (UI) → (6) **`creature.AfterTurnStart` → Block = 0** → (7) `Hook.AfterBlockCleared` → (8) `SetupPlayerTurn` (energy reset, draw) → (9) `Hook.AfterSideTurnStart` = **poison tick** → (10) orb start passives → (11) checksum → `RunAutoPrePlayPhase` → `CheckWinCondition`.
  Poison `Models/Powers/PoisonPower.cs`: `public override async Task AfterSideTurnStart(CombatSide side, IReadOnlyList<Creature> participants, ICombatState combatState) { if (participants.Contains(base.Owner)) await Trigger(); }` and `Trigger()` = `CreatureCmd.Damage(new ThrowingPlayerChoiceContext(), base.Owner, base.Amount, ValueProp.Unblockable | ValueProp.Unpowered, null, null)` + `PowerCmd.Decrement`. So block is gone before poison resolves, and poison is unblockable regardless; poison resolves *after* the hand is drawn.

## 6. Death and combat end

`Commands/CreatureCmd.cs:461 Kill(...)` → `KillWithoutCheckingWinCondition` (`:515`):
1. `creature.LoseHpInternal(currentHp, Unblockable|Unpowered)` + `Hook.AfterCurrentHpChanged` (when HP > 0).
2. `await Hook.BeforeDeath(...)`; `if (force || creature.MaxHp <= 0 || Hook.ShouldDie(runState, combatState, creature, out preventer))`.
3. `creature.InvokeDiedEvent();` (`Creature:510`, fires `Died`).
4. `shouldRemoveFromCombat = Hook.ShouldCreatureBeRemovedFromCombatAfterDeath(...)`; UI death anim length captured here (ignore).
5. `await Hook.AfterDeath(runState, combatState, creature, wasRemovalPrevented: false, deathAnimLength)`.
6. enemy + removable + still in `Enemies` → `CombatManager.Instance.RemoveCreature(creature)` and, if not mid-move, `combatState.RemoveCreature(creature)` (`:556-564`).
7. `isPrimaryEnemy = creature.IsPrimaryEnemy;` then `foreach (PowerModel p in creature.RemoveAllPowersAfterDeath()) await p.AfterRemoved(creature);` and, if `isPrimaryEnemy && teammates.Count != 0 && teammates.All(t => t.IsSecondaryEnemy)` → `await Kill(teammates)` (minions die with their primary).
8. player: `OrbQueue.Clear()`, kill Osty, `player.DeactivateHooks()`, `await CombatManager.Instance.HandlePlayerDeath(combatId, player)`.
9. prevention branch: `Hook.AfterDeath(wasRemovalPrevented: true, 0f)` + `Hook.AfterPreventingDeath`, retry; ≥10 recursions → `InvalidOperationException("Combat is ending, but something is continually preventing the last creature from being killed!")`.
10. back in `Kill`: all players dead → `CombatManager.Instance.LoseCombat()` (+ game-over screen, UI); otherwise any dead player whose turn was active → `PlayerCmd.EndTurn(item2.Player, canBackOut: false)`.

Win/loss — `CM:1390 CheckWinCondition`: `PendingLoss != null` → `ProcessPendingLoss` (`IsInProgress = false`, `CombatEnded`); else `IsCombatEnding(turnState)` → `await EndCombatInternal(turnState)`.
`CM:417 IsCombatEnding`: `false` if `!IsInProgress`; `true` if `PendingLoss != null`; `false` if `State.Enemies.Any(e => e != null && e.IsAlive && e.IsPrimaryEnemy)`; `false` if `Hook.ShouldStopCombatFromEnding`; else `true`. `CM:1265 LoseCombat` only sets `PendingLoss`.
`CheckWinCondition` call sites: `ActionExecutor` after every action **except** `EndPlayerTurnAction`/`ReadyToBeginEnemyTurnAction` (`ActionExecutor.cs:161-171`), inside `ExecuteEnemyTurn` after each enemy, in `EndEnemyTurn`, in `StartTurn` (both sides), and five times inside `EndPlayerTurnPhaseOneInternal`.

`CM:1300 EndCombatInternal` cleanup order:
```
turnsTaken = localPlayer.PlayerCombatState.TurnNumber
room = (CombatRoom)runState.CurrentRoom
IsInProgress = false; PlayersTakingExtraTurn.Clear(); SetPhaseForAllPlayers(None); PlayerActionsDisabled = false
foreach player: await player.ReviveBeforeCombatEnd()             // multiplayer only, heal to 1 HP
await Hook.AfterCombatEnd(runState, combatState, room)          // relics reset per-combat state
History.Clear(); room.OnCombatEnded()                           // gold proportion
WriteReplay(stopRecording: true)
foreach player: player.AfterCombatEnd()
    -> Creature.RemoveAllPowersInternalExcept(); PCS.AfterCombatEnd(); Creature.LoseBlockInternal(Creature.Block)
    -> PCS:142: unsubscribe StateTracker, allPile.Clear() for all 5 piles, _pets.Clear()
await Hook.AfterCombatVictory(runState, combatState, room)       // ** BurningBlood heal 6 **
NHoverTipSet.Clear(); history TurnsTaken/win time; room.MarkPreFinished(); SaveRun; progress update
MultiplayerScalingModel?.OnCombatFinished(); CombatWon(room); ActionExecutor.Unpause();
SetCombatState(NotInCombat); music; CombatEnded
```
`EndCombatInternal` does **not** empty `State.Enemies`; creature removal + `CS.RemoveCreature` happen later in `CM:1187 Reset(graceful)`: `SetPhaseForAllPlayers(None); foreach creature: item.Reset(); RemoveCreature(item); State.RemoveCreature(item); _turnState = null; DebugForcedTopCardOnNextShuffle = null; History.Clear(); SetCombatState(NotInCombat);`. Piles are emptied at combat end (`PCS:142`), before rewards.

## 7. Action queue and player-choice interleaving

- One `ActionQueue` per player (`ActionQueueSet.cs:20-155`); single-player `RequestEnqueue` (`AQS:146`) short-circuits: `case NetGameType.Singleplayer: case NetGameType.Host: EnqueueAction(action, _netService.NetId);`.
- **Deferral rule** — `AQS:148-153`:
  ```csharp
  if (action.ActionType == GameActionType.CombatPlayPhaseOnly && CombatState == ActionSynchronizerCombatState.NotPlayPhase) {
      _requestedActionsWaitingForPlayerTurn.Add(action); return;      // released when PlayPhase is entered
  }
  ```
  `AQS:80-138 SetCombatState`: `PlayPhase` → re-`RequestEnqueue` all deferred actions + `UnpauseAllPlayerQueues()`; `EndTurnPhaseOne` → `StartCancellingAllPlayerDrivenCombatActions()`; `NotPlayPhase` → `PauseAllPlayerQueues()`; `NotInCombat` → `CombatEnded()`, cancel deferred actions.
  `GameActionType` (`Entities/Multiplayer/GameActionType.cs`): `None, Combat, CombatPlayPhaseOnly, NonCombat, Any`. `ActionSynchronizerCombatState` (`Entities/Multiplayer/ActionSynchronizerCombatState.cs`): `NotInCombat, PreCombatSetup, PlayPhase, EndTurnPhaseOne, NotPlayPhase`.
- **One action at a time**: `ActionExecutor.ExecuteActions` (`ActionExecutor.cs:123`) takes `_actionQueueSet.GetReadyAction()` (lowest action id, skipping queues waiting on a player choice), runs it to completion (per-frame `ProcessFrame` await — frame timing only), then `if (!(readyAction is EndPlayerTurnAction || readyAction is ReadyToBeginEnemyTurnAction)) await CombatManager.Instance.CheckWinCondition();` and loops.
- `GameAction` state machine (`GameActions/GameAction.cs`): `WaitingForExecution → Executing → (GatheringPlayerChoice → ReadyToResumeExecuting → Executing)* → Finished`; `CompletionTask` (`:61`) completes at `:147` after `JustBeforeFinished`.
- **Player choices**: `HookPlayerChoiceContext.SignalPlayerChoiceBegun` (`:163`) creates a `GenericHookGameAction` via `AQS.GenerateHookAction`, enqueues it as a separate synchronized action, and pauses the current one. `BlockingPlayerChoiceContext` (`GameActions/Multiplayer/BlockingPlayerChoiceContext.cs:19-32`) is a no-op (`OwnerId => null`, both signals `Task.CompletedTask`) — used during the enemy turn so the enemy-turn task simply awaits the choice.
- **A plain attack/defend combat never triggers a player choice**: `PlayCardAction → OnPlayWrapper → OnPlay` never calls `SignalPlayerChoiceBegun`, so exactly one action runs per click and the end-turn sequence above proceeds with no interleaving. The only relevant dependency is `await endTurnSignal.RunningAction.CompletionTask` (`CM:635`), which prevents an in-flight card play from overlapping the end-of-turn phases.
- `GameActions/PlayCardAction.cs:62-104`: resolve card+target → verify still in `Hand` and legal, else `Cancel()` → `await _card.SpendResources()` → `GameActionPlayerChoiceContext` → `await _card.OnPlayWrapper(PlayerChoiceContext, target, isAutoPlay: false, resources)`.
  `Card:1858 OnPlayWrapper`: `CardPileCmd.AddDuringManualCardPlay` (`CPC:827`, hand→Play pile) → `resultLocation` + `Hook.ModifyCardPlayResultLocation` → `playCount` → per iteration `Hook.BeforeCardPlayed`, `OnPlay`, `Enchantment.OnPlay`, `Affliction.OnPlay`, `Hook.AfterCardPlayed` → Play→`resultLocation` (`CardPileCmd.Add` / `CardCmd.Exhaust` / `RemoveFromCombat`) → `CombatManager.CheckForEmptyHand` → `EnergyCost.AfterCardPlayedCleanup()`; result pile for a normal card is `Discard` (`Card:2078-2085`).

## 8. Draw-pile reshuffle

`CPC:1007 DrawInternal` loop:
```csharp
int drawsRequested = (count > 0m) ? (int)Math.Ceiling(count) : 0;
int num = Math.Max(0, CardPile.MaxCardsInHand - hand.Cards.Count);        // MaxCardsInHand = 10 (CardPile.cs:21)
for (int i = 0; i < drawsRequested; i++) {
    if (num <= 0 || CombatManager.Instance.IsOverOrEnding) break;
    if (!CheckIfDrawIsPossibleAndShowThoughtBubbleIfNot(player)) break;
    await ShuffleIfNecessary(choiceContext, player);
    if (!CheckIfDrawIsPossibleAndShowThoughtBubbleIfNot(player)) break;
    CardModel card = drawPile.Cards.FirstOrDefault();                     // index 0 == top
    if (card == null || hand.Cards.Count >= CardPile.MaxCardsInHand) break;
    result.Add(card); await Add(card, hand);
    CombatManager.Instance.History.CardDrawn(combatState, card, fromHandDraw);
    ... Hook.AfterCardDrawn / card.InvokeDrawn() ...
}
```
`CPC:1186 ShuffleIfNecessary`: `if (!pile.Cards.Any() && pile2.Cards.Any()) { await ShuffleFtueCheck(); await Shuffle(choiceContext, player); }` (draw empty AND discard non-empty).
`CPC:1073 Shuffle`:
```csharp
List<CardModel> list = PileType.Discard.GetPile(player).Cards.ToList();
HashSet<CardModel> drawPileCards = pile.Cards.ToHashSet();
list.AddRange(drawPileCards);                                    // defensively include leftovers
list.StableShuffle(player.RunState.Rng.Shuffle);                 // <-- RNG: run-wide "shuffle" stream
Hook.ModifyShuffleOrder(combatState, player, list, isInitialShuffle: false);
if (DebugForcedTopCardOnNextShuffle != null) { ... }             // debug only
await Add(list, pile, CardPilePosition.Bottom, null, skipVisuals: true);
... UI shuffle tween wait loop (Rng.Chaotic) ...
await Cmd.CustomScaledWait(0.2f, 0.5f);
await Hook.AfterShuffle(...);
```
`Extensions/ListExtensions.cs:22-60`:
```csharp
public static List<T> StableShuffle<T>(this List<T> list, Rng rng) where T : IComparable<T> {
    List<T> list2 = list.ToList(); list2.Sort();                    // order-independent result
    for (int i = 0; i < list.Count; i++) list[i] = list2[i];
    return list.UnstableShuffle(rng);
}
public static List<T> UnstableShuffle<T>(this List<T> list, Rng rng) {
    int num = list.Count;
    while (num > 1) { num--; int num2 = rng.NextInt(num + 1); swap(list[num], list[num2]); }
    return list;
}
```
Consequences:
- A reshuffle consumes exactly **`n - 1`** `NextInt(...)` values on the run-wide `Rng.Shuffle` stream, `n` = discard count (+ leftover draw cards). The list is **sorted first**, so the result depends only on (card multiset, RNG state).
- There is no dedicated "shuffle counter": `Rng` keeps a single `_counter` per stream, incremented on every `Next*` call (`Random/Rng.cs:74,85,101,127,136,155,184,194`). Only the number of prior draws on the `Shuffle` stream matters.
- Card effects that shuffle consume the same stream (e.g. `Models/Cards/BeatDown.cs:26` `.StableShuffle(Owner.RunState.Rng.Shuffle)`).
- Draw order: `drawPile.Cards[0]` is drawn first; the initial shuffle is `CardPile.cs:75 RandomizeOrderInternal` which also fires `Hook.ModifyShuffleOrder(..., isInitialShuffle: true)`.

## RNG consumption summary (counter matching)

| stream (`Runs/RunRngSet.cs`) | consumed by |
|---|---|
| `RunState.Rng.Shuffle` (:40) | initial deck shuffle in `SetUpCombat` (`deckCount-1` NextInt); every reshuffle (`n-1` NextInt); card effects using `StableShuffle` |
| `RunState.Rng.MonsterAi` (:77) | `Monster.RollMove` → `MonsterMoveStateMachine.RollMove` → `MonsterState.GetNextState(owner, rng)`; first roll in `AfterCreatureAdded`, then once per enemy at each player-turn start (`PrepareForNextTurn`) |
| `RunState.Rng.Niche` (:83) | one draw per enemy spawn (`SetUniqueMonsterHpValue`: `NextInt(min,max)` or `NextItem(hashSet)`) |
| per-encounter `EncounterModel._rng` (`EncounterModel.cs:263`) | `GenerateMonsters()` selection (seed `runSeed + TotalFloor + idHash`; not persisted) |
| per-monster `MonsterModel.Rng` (`CS:245`) | monster-internal randomness (seed `runSeed + mapCol + mapRow + actIndex + CombatId`) |
| `CombatCardSelection`/`CombatCardGeneration`/`CombatEnergyCosts`/`CombatTargets`/`CombatOrbs` (`RunRngSet.cs:51-88`) | generated cards, random costs, random targets, orbs — unused in a plain attack/defend turn |
| `Rng.Chaotic` (`Rng.cs:25`) | visuals only (shuffle tween timing) — non-deterministic, ignore |

## Frame-timing / UI-only (safe to ignore)

`ActionExecutor` per-`ProcessFrame` wait loop; `Cmd.CustomScaledWait(...)`; `NCreature.PerformIntent()`/`RefreshIntents()`; death anim (`StartDeathAnim`/`RemoveCreatureNode`); card-pile tween waits; `NEndTurnButton` gating; FTUE banners, BGM/SFX; `ChecksumTracker.GenerateChecksum(...)` (names mark deterministic sync points: `"After player turn start"`, `"After enemy turn start"`, `"After enemy turn end"`, `"After player turn phase one end"`, `"after player turn phase two end"`).

# Card-play & damage/block pipeline — STS2 v0.111.0 (Ironclad basics vs Nibbit)

Paths relative to `_research/decompiled-v0.111.0/` (namespace root `MegaCrit.Sts2`). Damage/block are `decimal`
end-to-end; `int` casts occur only at HP loss and the block write. `(int)` on positive `decimal` in C# **truncates**
= floor; there is no `Math.Floor` anywhere in the damage path.

## 1. PlayCardAction.ExecuteAction — exact ordering
`MegaCrit/sts2/Core/GameActions/PlayCardAction.cs:62-104` (the entire flow; nothing else runs):
```csharp
_card = NetCombatCard.ToCardModel();                                  // 64
Creature target = await Player.Creature.CombatState.GetCreatureAsync(TargetId, 10.0); // 66
CardPile? pile = _card.Pile;                                          // 67
if (pile == null || pile.Type != PileType.Hand)                       // 68 hand check
{ NCardPlayQueue.Instance?.RemoveCardFromQueueForCancellation(this); return; }  // 70-71 silent abort
...
if (!_card.CanPlay(out UnplayableReason _, out AbstractModel _) || !_card.IsValidTarget(target)) // 85
{ Cancel(); return; }                                                 // 87-88 card stays in hand
(int, int) tuple = await _card.SpendResources();                      // 92 BEFORE leaving hand
ResourceInfo resources = new ResourceInfo { EnergySpent = item, EnergyValue = item,
    StarsSpent = item2, StarValue = item2 };                          // 95-101
PlayerChoiceContext = new GameActionPlayerChoiceContext(this);        // 102
await _card.OnPlayWrapper(PlayerChoiceContext, target, isAutoPlay: false, resources); // 103
```
Order: hand check → target resolve → `CanPlay` + `IsValidTarget` → `SpendResources` → `OnPlayWrapper`.

`CardModel.CanPlay` (`MegaCrit/sts2/Core/Models/CardModel.cs:1725-1755`): `Unplayable` keyword →
`Owner.PlayerCombatState.HasEnoughResourcesFor(this, out reason2)` → `AnyAlly` with ≤1 living ally →
`Hook.ShouldPlay(combatState, this, out preventer, AutoPlayType.None)` → `!IsPlayable` (BlockedByCardLogic).
Energy check only, no spend (`PlayerCombatState.cs:190-209`):
```csharp
int num  = Math.Max(0, card.EnergyCost.GetWithModifiers(CostModifiers.All));
int num2 = Math.Max(0, card.GetStarCostWithModifiers());
if (num > Energy && Hook.ShouldPayExcessEnergyCostWithStars(...)) { num2 += (num - Energy) * 2; num = Energy; }
if (num  > Energy) reason |= UnplayableReason.EnergyCostTooHigh;
if (num2 > Stars)  reason |= UnplayableReason.StarCostTooHigh;
```
`IsValidTarget` (`CardModel.cs:1762-1785`): `null` OK only for non-`AnyEnemy`/`AnyAlly`; `AnyEnemy` needs
`target.IsAlive && target.Side != Owner.Creature.Side`.

`SpendResources` / `SpendEnergy` (`CardModel.cs:1807-1834`):
```csharp
int energy = Owner.PlayerCombatState.Energy;
int energyToSpend = EnergyCost.GetAmountToSpend();          // CostsX -> current Energy (CardEnergyCost.cs:134-141)
int starsToSpend  = Math.Max(0, GetStarCostWithModifiers());
if (energyToSpend > energy && Hook.ShouldPayExcessEnergyCostWithStars(CombatState, Owner))
{ starsToSpend += (energyToSpend - energy) * 2; energyToSpend = energy; }   // 1 excess energy = 2 stars
await SpendEnergy(energyToSpend); await SpendStars(starsToSpend);
// SpendEnergy:  if (EnergyCost.CostsX) EnergyCost.CapturedXValue = amount;  // X captured at spend time
//               History.EnergySpent(...); Owner.PlayerCombatState.LoseEnergy(Math.Max(0, amount));
//               await Hook.AfterEnergySpent(CombatState, this, amount);
```
`LoseEnergy` → `Energy = (int)Math.Clamp((decimal)Energy - amount, 0m, 999999999m)` (`PlayerCombatState.cs:178`).

`OnPlayWrapper` (`CardModel.cs:1858-2019`), condensed but order-exact:
```csharp
choiceContext.PushModel(this); await CombatManager.Instance.WaitForUnpause();
CurrentTarget = target; CurrentPlayIndex = 0;
if (!isAutoPlay) await CardPileCmd.AddDuringManualCardPlay(this);   // 1866 LEAVES HAND HERE
else await CardPileCmd.Add(this, PileType.Play, CardPilePosition.Bottom, null, skipCardPileVisuals);
CardLocation resultLocation = GetResultLocationForCardPlay();        // 1881 computed BEFORE OnPlay
resultLocation = Hook.ModifyCardPlayResultLocation(combatState, this, isAutoPlay, resources, resultLocation, out modifiers);
foreach (AbstractModel item in modifiers) await item.AfterModifyingCardPlayResultLocation(this, resultLocation);
int playCount = await GeneratePlayCount(combatState, target);        // 1887
for (int i = 0; i < playCount; i++) {
  CardPlay cardPlay = new CardPlay { Card=this, Player=Owner, Target=target, ResultPile=resultLocation.pileType,
      Resources=resources, IsAutoPlay=isAutoPlay, PlayIndex=i, PlayCount=playCount };       // 1915-1925
  await Hook.BeforeCardPlayed(combatState, cardPlay);                                       // 1926
  History.CardPlayStarted(...); BranchingPlayerChoiceContext branching = new ...; branching.PushModel(this);
  Task task = OnPlay(branching, cardPlay);                                                  // 1933
  await branching.AssignTaskAndWaitForPauseOrCompletion(task);
  InvokeExecutionFinished();                                                                // 1939
  if (Enchantment != null) { await Enchantment.OnPlay(choiceContext, cardPlay); ... }        // 1940-1948
  if (Affliction  != null) { await Affliction.OnPlay(choiceContext, target);  ... }         // 1949-1958
  History.CardPlayFinished(combatState, cardPlay);                                          // 1961
  if (CombatManager.Instance.IsInProgress) await Hook.AfterCardPlayed(combatState, choiceContext, cardPlay); } // 1965
// then (if Pile.Type == Play, 1990): None -> CardPileCmd.RemoveFromCombat / Exhaust -> CardCmd.Exhaust
//                                  / default -> CardPileCmd.Add(this, resultLocation.pileType, resultLocation.position)
await CombatManager.Instance.CheckForEmptyHand(effectCombatId, choiceContext, originalOwner); // 2006
```
`CardPileCmd.AddDuringManualCardPlay` (`MegaCrit/sts2/Core/Commands/CardPileCmd.cs:827-850`) is the exact hand-exit:
`card.RemoveFromCurrentPile();` (849) then `PileType.Play.GetPile(card.Owner).AddInternal(card);` (850).
So during `OnPlay` the card's pile is `Play` (matters: `Hook.ModifyDamage`'s preview branch accepts `Hand` or `Play`,
`Hook.cs:1520-1528`).
Result pile (`CardModel.cs:2084-2096`):
```csharp
if (IsDupe || Type == CardType.Power) return new CardLocation(Owner, PileType.None, CardPilePosition.Bottom);
if (ExhaustOnNextPlay || Keywords.Contains(CardKeyword.Exhaust))
{ ExhaustOnNextPlay = false; return new CardLocation(Owner, PileType.Exhaust, CardPilePosition.Bottom); }
return new CardLocation(Owner, PileType.Discard, CardPilePosition.Bottom);
```
Full order: hand check → CanPlay/IsValidTarget → SpendResources → hand→Play → result-location (+hook) → playCount →
`BeforeCardPlayed` → `OnPlay` → `InvokeExecutionFinished` → enchantment → affliction → `CardPlayFinished` →
`AfterCardPlayed` → move to Discard/Exhaust/None.

## 2. DamageCmd.Attack → CreatureCmd.Damage → math
`MegaCrit/sts2/Core/Commands/DamageCmd.cs:14-17`: `Attack(decimal damagePerHit) => new AttackCommand(damagePerHit);`
`AttackCommand` (`.../Commands/Builders/AttackCommand.cs`): `DamageProps = ValueProp.Move` by default (127);
`FromCard` sets `Attacker = owner.Creature; ModelSource = card; CardPlay = cardPlay;` (214-236); `Targeting` (273-286);
`Unpowered()` → `DamageProps |= ValueProp.Unpowered` (349-353).
`Execute` (526-674):
```csharp
await Hook.BeforeAttack(combatState, this);                                        // 549
decimal attackCount = Hook.ModifyAttackHitCount(combatState, this, _hitCount);     // 550
for (int i = 0; (decimal)i < attackCount; i++) {                                   // 551
  List<Creature> validTargets = GetPossibleTargets().Where(c => c.IsAlive).ToList();  // 557-559
  ... attacker anim/vfx ... if (_beforeDamage != null) await _beforeDamage();
  AddResultsInternal(await CreatureCmd.Damage(
      amount: (_calculatedDamageVar == null) ? _damagePerHit : _calculatedDamageVar.Calculate(singleTarget),
      choiceContext: choiceContext ?? new BlockingPlayerChoiceContext(),
      targets: (singleTarget != null) ? new List<Creature>(1){ singleTarget } : validTargets,
      props: DamageProps, dealer: Attacker, cardSource: ModelSource as CardModel, cardPlay: CardPlay)); // 669
}
CombatManager.Instance.History.CreatureAttacked(combatState, Attacker, ...);        // 671
await Hook.AfterAttack(combatState, choiceContext ?? new BlockingPlayerChoiceContext(), this); // 672
```
`CreatureCmd.Damage` (`MegaCrit/sts2/Core/Commands/CreatureCmd.cs:258-434`), the single-target spine:
```csharp
decimal modifiedAmount = Hook.ModifyDamage(runState, combatState, originalTarget, dealer, amount, props,
    cardSource, cardPlay, ModifyDamageHookType.All, CardPreviewMode.None, out modifiers);        // 283
await Hook.AfterModifyingDamageAmount(runState, combatState, cardSource, modifiers);            // 284
await Hook.BeforeDamageReceived(choiceContext, runState, combatState, originalTarget, modifiedAmount, props, dealer, cardSource); // 285
Creature creature = originalTarget.PetOwner?.Creature ?? originalTarget;                        // 286
decimal blockedDamage   = creature.DamageBlockInternal(modifiedAmount, props);                  // 287
decimal unblockedDamage = Hook.ModifyHpLost(runState, combatState, originalTarget,
    Math.Max(modifiedAmount - blockedDamage, 0m), props, dealer, cardSource, HpLossHookPhase.BeforeOsty, out modifiers); // 288
await Hook.AfterModifyingHpLostBeforeOsty(...);
Creature unblockedDamageTarget = Hook.ModifyUnblockedDamageTarget(combatState, originalTarget, unblockedDamage, props, dealer); // 290
unblockedDamage = Hook.ModifyHpLost(..., unblockedDamageTarget, unblockedDamage, ..., HpLossHookPhase.AfterOsty, out modifiers); // 291
await Hook.AfterModifyingHpLostAfterOsty(...);
DamageResult res = unblockedDamageTarget.LoseHpInternal(unblockedDamage, props);               // 293
bool wasBlockBroken  = originalTarget.Block <= 0 && blockedDamage > 0m;                         // 295
bool wasFullyBlocked = !props.HasFlag(ValueProp.Unblockable) && (blockedDamage > 0m || originalTarget.Block > 0)
                       && (int)unblockedDamage == 0;                                           // 296
```
Post-loop per result (394-431): `Hook.AfterBlockBroken` (400) → if `UnblockedDamage > 0`
`Hook.AfterCurrentHpChanged(runState, combatState, target, -UnblockedDamage)` (404) → `dealer.Player.ExtraFields.DamageDealt
+= UnblockedDamage` (406-409) → `Hook.AfterDamageGiven(choiceContext, combatState, dealer, r, props, target, cardSource)`
(412) → `Hook.AfterDamageReceived(...)` unless the target died (416) → deferred `await Kill(killedCreatures)` (431).
The modifier math — `Hook.cs:1495-1577`, then `Hook.cs:2520-2567`:
```csharp
// ModifyDamage: enchantment additive then multiplicative (1499-1509); [multi-creature-targeting branch skipped
// for single target]; num = ModifyDamageInternal(...); return Math.Max(0m, num);   // 1574
if (type.HasFlag(Additive))       foreach (listener) { num2 = item.ModifyDamageAdditive(target, num, props, dealer, cardSource, cardPlay); num += num2; }
if (type.HasFlag(Multiplicative)) foreach (listener) { num3 = item.ModifyDamageMultiplicative(target, num, props, dealer, cardSource, cardPlay); num *= num3; }
if (type.HasFlag(Cap))            foreach (listener) { num5 = item.ModifyDamageCap(...); if (num5 < num4) { num4 = num5; if (num > num5) num = num5; } }
```
`ModifyDamageHookType.All = Additive|Multiplicative|Cap = 0xE`. Three separate full passes: **all** additive first,
then **all** multiplicative, then cap. Effective formula:
`dmg = max(0, cap( (base + Σadditive) × Πmultiplicative ))`, with `Σadditive` including Strength and
`Πmultiplicative` including Vulnerable 1.5 and Weak 0.75 (they compose multiplicatively: 1.5 × 0.75 = 1.125).
Truncation happens only here (`Creature.cs:431-458`):
```csharp
public decimal DamageBlockInternal(decimal amount, ValueProp props)
{ decimal num = (props.HasFlag(ValueProp.Unblockable) ? 0m : Math.Min(Block, amount)); Block -= (int)num; return num; }
public DamageResult LoseHpInternal(decimal amount, ValueProp props)
{ bool flag = CurrentHp > 0 && amount >= (decimal)CurrentHp; int currentHp = CurrentHp;
  int num = (int)Math.Clamp(amount, 0m, 999999999m);          // the only damage->int truncation
  CurrentHp = Math.Max(CurrentHp - num, 0);
  return new DamageResult(this, props) { UnblockedDamage = currentHp - CurrentHp, WasTargetKilled = flag,
      OverkillDamage = (flag ? Math.Max(num - currentHp, 0) : 0) }; }
```
Consequences to copy: `7 × 1.5 = 10.5` → **10** HP lost (truncate, not round). `10.5` dmg vs `10` block →
`blocked = 10`, `unblocked = 0.5`, `(int)0.5 = 0` HP lost and `WasFullyBlocked == true` (tested via
`(int)unblockedDamage == 0`, line 296) — the fraction is silently discarded.
**Why Bash's 8 is not amplified, but a later Strike 6 → 9** (`MegaCrit/sts2/Core/Models/Cards/Bash.cs:29-36`):
```csharp
await DamageCmd.Attack(base.DynamicVars.Damage.BaseValue).FromCard(this, cardPlay).Targeting(cardPlay.Target)
    .WithHitFx("vfx/vfx_attack_blunt", null, "blunt_attack.mp3").Execute(choiceContext);   // 32-34 damage resolves fully
await PowerCmd.Apply<VulnerablePower>(choiceContext, cardPlay.Target, base.DynamicVars.Vulnerable.BaseValue,
    base.Owner.Creature, this);                                                            // 35 then Vulnerable
```
Two sequential awaits: during Bash's own hit the target has no `VulnerablePower`, so
`VulnerablePower.ModifyDamageMultiplicative` returns `1m` (line 32 `if (target != base.Owner) return 1m;`).
Bash deals `8m`, then the power lands. A later Strike's `6m × 1.5m = 9m` exactly (no truncation). Bash onto an
already-Vulnerable target deals `8 × 1.5 = 12`.

## 3. Vulnerable / Weak / Strength
`MegaCrit/sts2/Core/Models/Powers/VulnerablePower.cs`:
```csharp
public override PowerType Type => PowerType.Debuff;                       // 19
public override PowerStackType StackType => PowerStackType.Counter;       // 21
protected override IEnumerable<DynamicVar> CanonicalVars =>
    new ...(new DynamicVar("DamageIncrease", 1.5m));                      // 23
public override decimal ModifyDamageMultiplicative(Creature? target, decimal amount, ValueProp props, ...)
{ if (target != base.Owner) return 1m;                                    // 30 only the debuffed creature
  if (!props.IsPoweredAttack()) return 1m;                                // 34
  decimal num = base.DynamicVars["DamageIncrease"].BaseValue;             // 38 = 1.5
  ... PaperPhrog / CrueltyPower / DebilitatePower ...  return num; }
public override async Task AfterSideTurnEnd(PlayerChoiceContext ctx, CombatSide side, IEnumerable<Creature> participants)
{ if (side == CombatSide.Enemy) await PowerCmd.TickDownDuration(this); }   // 60-66
```
`WeakPower.cs`: same shape, `if (dealer != base.Owner) return 1m;` (27), factor `new DynamicVar("DamageDecrease", 0.75m)` (23),
also ticks on `side == CombatSide.Enemy` (49-55).
`StrengthPower.cs:16-27`: `if (base.Owner != dealer) return 0m; if (!props.IsPoweredAttack()) return 0m; return base.Amount;`
plus `AllowNegative => true` (14). `IsPoweredAttack` = `props.HasFlag(ValueProp.Move) && !props.HasFlag(ValueProp.Unpowered)`
(`ValueProps/ValuePropExtensions.cs:5-12`); `ValueProp` flags: `Unblockable = 2, Unpowered = 4, Move = 8, SkipHurtAnim = 0x10`.
Stacking: amounts are `int` (`PowerModel.Amount`, `PowerModel.cs:187-197`); `PowerStackType.Counter` = visible number.
Re-apply merges into the existing instance (`PowerCmd.FindExistingInstanceForStacking`, `PowerCmd.cs:169-178`, then
`int newAmount = power.Amount + (int)modifiedOffset; power.SetAmount(newAmount, silent);`, 240-241). Vulnerable 2 + 2 = 4
turns, **not** 1.5². Bash applies `2m` (`PowerVar<VulnerablePower>(2m)`), `Bash+` applies 3.
Removal: `PowerModel.ShouldRemoveDueToAmount` (`PowerModel.cs:478-489`) — Vulnerable/Weak have `AllowNegative == false`,
removed at `Amount <= 0`; Strength is removed exactly at 0.
Decrement timing — **only at end of the enemy side's turn**, whoever owns the power:
- `CombatManager.EndEnemyTurnInternal` (`Combat/CombatManager.cs:1691-1700`): `Hook.BeforeSideTurnEnd` → each player's
  `PlayerCombatState.EndOfTurnCleanup()` → `Hook.AfterSideTurnEnd(state, state.CurrentSide /*Enemy*/, enemies)`.
- `CombatManager.cs:1774` dispatches `AfterSideTurnEnd(..., CurrentSide /*Player*/, ...)` → these powers do nothing.
`PowerCmd.TickDownDuration` (`PowerCmd.cs:194-204`) decrements by 1 via `Decrement` → `ModifyAmount(-1m)` unless
`SkipNextDurationTick` is set; that flag is set when a **Debuff is applied to a player** (`PowerCmd.cs:148-151`:
`if (target.Side == CombatSide.Player && power.Type == PowerType.Debuff) power.SkipNextDurationTick = true;`).
Timeline (Bash on player turn 1): Bash → Vulnerable 2; player turn-end → no tick; enemy turn 1 acts; enemy turn-end →
1; player turn 2 Strike still amplified → 9; enemy turn 2 acts; enemy turn-end → 0, removed. Vulnerable 2 therefore
amplifies **two** player turns.

## 4. Bash
`MegaCrit/sts2/Core/Models/Cards/Bash.cs`:
```csharp
protected override IEnumerable<DynamicVar> CanonicalVars => new ...(new DynamicVar[2] {
    new DamageVar(8m, ValueProp.Move),           // 20
    new PowerVar<VulnerablePower>(2m) });        // 21
public Bash() : base(2, CardType.Attack, CardRarity.Basic, TargetType.AnyEnemy) { }   // 25
protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay) {
    ArgumentNullException.ThrowIfNull(cardPlay.Target, "cardPlay.Target");            // 31
    await DamageCmd.Attack(base.DynamicVars.Damage.BaseValue).FromCard(this, cardPlay).Targeting(cardPlay.Target)
        .WithHitFx("vfx/vfx_attack_blunt", null, "blunt_attack.mp3").Execute(choiceContext);   // 32-34
    await PowerCmd.Apply<VulnerablePower>(choiceContext, cardPlay.Target, base.DynamicVars.Vulnerable.BaseValue,
        base.Owner.Creature, this); }                                                 // 35
protected override void OnUpgrade() { base.DynamicVars.Damage.UpgradeValueBy(2m);     // 40
                                      base.DynamicVars.Vulnerable.UpgradeValueBy(1m); } // 41
```
Order: damage fully resolves, **then** Vulnerable. Base cost 2 / 8 dmg / Vulnerable 2; `Bash+` 10 dmg / Vulnerable 3.
Target = `cardPlay.Target` from `PlayCardAction.TargetId` (`PlayCardAction.cs:40,66`), validated by
`IsValidTarget` (`CardModel.cs:1776-1779`, `target.Side != Owner.Creature.Side`); `PowerCmd.Apply` gets
`applier = Owner.Creature`, `cardSource = this`.

## 5. DefendIronclad / GainBlock
`MegaCrit/sts2/Core/Models/Cards/DefendIronclad.cs`:
```csharp
public override bool GainsBlock => true;                                             // 13
protected override IEnumerable<DynamicVar> CanonicalVars => new ...(new BlockVar(5m, ValueProp.Move));  // 17
public DefendIronclad() : base(1, CardType.Skill, CardRarity.Basic, TargetType.Self) { }                 // 20
protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
{ await CreatureCmd.GainBlock(base.Owner.Creature, base.DynamicVars.Block, cardPlay); }                   // 26
protected override void OnUpgrade() { base.DynamicVars.Block.UpgradeValueBy(3m); }                        // 31
```
Base 5 block, `Defend+` 8. `TargetType.Self` ⇒ target is null and unused; the creature is always `Owner.Creature`.
`CreatureCmd.GainBlock` (`Commands/CreatureCmd.cs:650-701`):
```csharp
return await GainBlock(creature, blockVar.BaseValue, blockVar.Props, cardPlay, fast);        // 652 (props = Move)
await Hook.BeforeBlockGained(combatState, creature, amount, props, cardPlay?.Card);          // 679
decimal modifiedAmount = Hook.ModifyBlock(combatState, creature, amount, props, cardPlay?.Card, cardPlay, out modifiers); // 681
modifiedAmount = Math.Max(modifiedAmount, 0m);                                              // 682
await Hook.AfterModifyingBlockAmount(...); if (modifiedAmount > 0m) { ... creature.GainBlockInternal(modifiedAmount); ... }
await Hook.AfterBlockGained(combatState, creature, modifiedAmount, props, cardPlay?.Card);   // 699
```
`Hook.ModifyBlock` (`Hook.cs:1322-1350`) mirrors damage: enchantment additive/multiplicative, then one additive pass
over all listeners (1332-1340), then one multiplicative pass (1341-1344).
`Creature.GainBlockInternal` (`Creature.cs:460-467`) — only cap, and the truncation point:
`Block = (int)Math.Min((decimal)Block + amount, 999999999m);` (negative amount throws).
Dexterity is additive (`Powers/DexterityPower.cs:23-38`): returns `base.Amount` when
`cardSource.Owner.Creature == Owner` (or, without a card source, `Owner == target`) and
`props.IsPoweredCardOrMonsterMoveBlock()`. Defend with Dex 2 = 7. No per-card cap; block clears at the start of the
owner's own turn (`Creature.AfterTurnStart` → `ClearBlock()` → `Block = 0`, `Creature.cs:686-697,723-733`;
player turn 1 skips the clear; Barricade/Blur/SturdyClamp prevent it via `Hook.ShouldClearBlock`).

## 6. StrikeIronclad
`MegaCrit/sts2/Core/Models/Cards/StrikeIronclad.cs`:
```csharp
protected override HashSet<CardTag> CanonicalTags => new HashSet<CardTag> { CardTag.Strike };  // 17
protected override IEnumerable<DynamicVar> CanonicalVars => new ...(new DamageVar(6m, ValueProp.Move)); // 19
public StrikeIronclad() : base(1, CardType.Attack, CardRarity.Basic, TargetType.AnyEnemy) { }  // 25
await DamageCmd.Attack(base.DynamicVars.Damage.BaseValue).FromCard(this, cardPlay).Targeting(cardPlay.Target)
    .WithHitFx("vfx/vfx_attack_slash").Execute(choiceContext);                                  // 32-34
protected override void OnUpgrade() { base.DynamicVars.Damage.UpgradeValueBy(3m); }             // 39
```
1 energy, 6 dmg, `Strike+` 9; single hit (`_hitCount = 1`); no state effects beyond HP loss/block absorption,
`dealer.Player.ExtraFields.DamageDealt += UnblockedDamage` (`CreatureCmd.cs:406-409`), and history entries
(`History.DamageReceived` at `CreatureCmd.cs:318-321`, `History.CreatureAttacked` at `AttackCommand.cs:671`).

## 7. Card piles
`Entities/Cards/PileType.cs`: `enum PileType { None, Draw, Hand, Discard, Exhaust, Play, Deck }` where `Play` is
"A temporary pile that a card lives in while it's mid-play, so that it isn't counted towards your hand or your discard pile."
Manually played card: **Hand → Play** at the start of `OnPlayWrapper` (`CardPileCmd.cs:849-850`), before `OnPlay`;
after resolution → `Discard` by default (`CardModel.cs:2095`), `Exhaust` if `Keywords.Contains(CardKeyword.Exhaust)` or
`ExhaustOnNextPlay` (2090-2094), or removed from combat (`PileType.None`) for Power/dupe cards (2086-2089, 1994-1996).
All three basic paths use `CardPilePosition.Bottom`. A card in `Play` is in neither hand nor discard; the post-loop move
is gated on `Pile.Type == PileType.Play` (`CardModel.cs:1990`).

## 8. Intents (display + repeats)
`MonsterMoves/Intents/SingleAttackIntent.cs`: `Repeats => 1` (10); `SingleAttackIntent(int damage) { base.DamageCalc = () => damage; }`
(14-17); `GetTotalDamage(...) => GetSingleDamage(...)` (24-27).
`MonsterMoves/Intents/AttackIntent.cs:84-93` — display is a live recompute, not a stored number:
```csharp
decimal num = DamageCalc();
Player me = LocalContext.GetMe(owner.CombatState);
if (me != null) num = Hook.ModifyDamage(me.RunState, me.Creature.CombatState,
    me.Creature,        // target  = the local player creature
    owner,              // dealer  = the monster
    DamageCalc(), ValueProp.Move, null, null, ModifyDamageHookType.All, CardPreviewMode.None, out _);
return Math.Max(0, (int)num);   // truncated to int for display
```
So the icon **does** include the monster's Strength (additive, `Owner == dealer`), the monster's Weak (×0.75), the
player's Vulnerable (×1.5, `Owner == target`), and any other additive/multiplicative/cap listener (e.g. Intangible).
It does not account for block. It is recomputed when a power amount changes: `PowerCmd.ModifyAmount` ends with
`await nCreature.UpdateIntent(combatState.Allies)` for monster owners (`PowerCmd.cs:255-263`).
`MultiAttackIntent.cs`:
```csharp
private readonly int _repeat; private readonly Func<int>? _repeatCalc;
public override int Repeats => _repeatCalc?.Invoke() ?? _repeat;                                  // 16
public MultiAttackIntent(int damage, int repeat) { base.DamageCalc = () => damage; _repeat = repeat; } // 18-22
public override int GetTotalDamage(...) => GetSingleDamage(targets, owner) * Repeats;             // 30-33
```
Label shows per-hit `Damage` + `Repeat` (35-42); icon tier/animation uses `GetTotalDamage` (`AttackIntent.cs:42-72`:
`<5 → 1, <10 → 2, <20 → 3, <40 → 4, else 5`). Execution is **not** driven by the intent: each hit is a separate
`CreatureCmd.Damage` from `AttackCommand.Execute`'s loop (`_hitCount` / `Hook.ModifyAttackHitCount`,
`AttackCommand.cs:550-551,669`), and targets are re-filtered per hit (`if (validTargets.Count == 0 && combatState.IsLiveCombat()) break;` 560-563).
Nibbit (`Models/Monsters/Nibbit.cs`): `ButtDamage = DeadlyEnemies ? 13 : 12` (30), `SliceBlock = ToughEnemies ? 6 : 5` (32),
`SliceDamage = DeadlyEnemies ? 7 : 6` (34), `HissStrengthGain = DeadlyEnemies ? 3 : 2` (36), HP
`MinInitialHp = 44 : 42`, `MaxInitialHp = 48 : 46` (26-28). Moves (68-92): `BUTT_MOVE` = `SingleAttackIntent(ButtDamage)`;
`SLICE_MOVE` = `SingleAttackIntent(SliceDamage)` + `DefendIntent()`; `HISS_MOVE` = `BuffIntent()`. Alone → BUTT;
not-alone front → SLICE, back → HISS; follow-ups SLICE → HISS → BUTT. `Encounters/NibbitsNormal.cs:19-27` spawns two
(front + back).

## 9. BurningBlood
`Models/Relics/BurningBlood.cs` (whole file):
```csharp
public override RelicRarity Rarity => RelicRarity.Starter;                                    // 12
protected override IEnumerable<DynamicVar> CanonicalVars => new ...(new HealVar(6m));         // 14
public override async Task AfterCombatVictory(CombatRoom _)                                   // 16
{ if (!base.Owner.Creature.IsDead) { Flash(); await CreatureCmd.Heal(base.Owner.Creature, base.DynamicVars.Heal.BaseValue); } }
```
Trigger: **once, after combat victory** (`AfterCombatVictory(CombatRoom)`) — not per turn, not on card play.
Amount: **6 HP** (`HealVar(6m)`). `CreatureCmd.Heal` (`CreatureCmd.cs:738-750`) → `HealInternal` → `SetCurrentHpInternal`
→ `CurrentHp = (int)Math.Min(amount, MaxHp)` (`Creature.cs:478-492`): overheal clamped to `MaxHp`, value truncates.

## Implementation checklist
1. Costs are `int`; deduct energy before the card leaves hand; capture X = energy at spend time.
2. Hand → `Play` before `OnPlay`; `BeforeCardPlayed` after that move; result pile chosen before `OnPlay`, applied after.
3. Damage: `decimal` base → all additive → all multiplicative → cap → `max(0)`; then block in `decimal`; one `(int)` truncation at HP loss.
4. `blocked = min(Block, dmgDecimal)`, `Block -= (int)blocked`, `unblocked = max(dmg - blocked, 0)`, `hpLoss = (int)unblocked`; fractional leftovers are lost.
5. Bash: 8 resolves fully, then Vulnerable 2 — no self-amplification on the same play.
6. Vulnerable/Weak stack additively in turns and tick only at end of the enemy turn.
7. Intent display recomputes through the same `Hook.ModifyDamage` with `target = player`, `dealer = monster`.

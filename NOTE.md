# NOTE：实现陷阱与已踩过的坑

按主题记录上一轮（v0.111.0 数据基线 + 值 oracle）遇到的具体问题、原因与对策。数字与报错原文取自当时的实测记录。

## 0. 先把产物提交

M0 的产品（`data/sts2/`、导出器、反编译树）当时都是未跟踪或 gitignore 状态，`rm -rf` 之后无法用 `git restore` 找回，只能从头重建。

**约定**：每个里程碑结束就把可复现的产物提交；反编译树、oracle 输出这类大文件可以只提交生成脚本与哈希清单。

## 1. 进程内调用 sts2.dll

| 现象 | 原因 | 对策 |
|---|---|---|
| `Player.CreateForNewRun` 直接 `0xC0000005` 崩进程，无托管异常 | 栈：`Godot.NativeInterop.NativeFuncs.godotsharp_dictionary_new` ← `CommandLineHelper..cctor` ← `PlatformUtil..cctor` ← `SaveManager.ConstructDefault` ← `Player..ctor` | 一切需要 `Player`/`CombatManager.StartCombatInternal` 的路径都必须进真机，不在纯 .NET 进程里跑 |
| `ModelDb.Init()` 抛 `ModManager is not finished initializing! ReflectionHelper.ModTypes cannot be called before that.` | `ModManager` 未初始化 | 用 `AbstractModelSubtypes.All`（1660 个类型）逐个 `ModelDb.Inject(type)`，实测 0 失败、约 83 ms |
| `ModelDb.AllPowers` 抛同一句 `ModTypes` | 同上 | 用 `ModelDb.All` 全量实例按基类过滤 |
| `ModelIdSerializationCache.Init()` 报 `ContentSorter called before AssemblyInfo was initialized.` / `used before it was initialized!` | 依赖装配信息 | oracle 里不要用它；需要 Entry 名时直接反射 `Id.Entry` |
| 读部分模型的 `CanonicalVars` 时抛 `NullReferenceException` | 非游戏进程里 `LocString.GetFormattedText()` 不可用；`GalvanicPower`、`VitalSparkPower`、`PaelsClaw`、`PaelsGrowth`、`RoyalStamp` 会炸 | 每个属性读取单独 try/catch，失败记入 `errors`，不中断整个 probe |
| 升级后的费用读出来不变 | `EnergyCost.Canonical` 不随升级变，`UpgradeBy` 只改 base | 读 `EnergyCost.GetResolved()` |

**结论**：`sts2.dll` 只能当"值 + 纯函数"的 oracle（RNG、数值、枚举、HP、费用、卡牌变量、洗牌函数），行为级验证必须走真机 harness。每个 probe 独立子进程，避免一次崩溃带走整批数据。

## 2. 导出器（C# 静态解析）

- **抽象基类被当成内容类**：`DecimillipedeSegment`、`TemporaryDexterityPower`/`TemporaryFocusPower`/`TemporaryStrengthPower`、`BattlewornDummyEventEncounter`，以及目录在 Relics 下但不是遗物的 `VakuuCardSelector : ICardSelector`。要按继承链过滤，而不是按目录。
- **同名类跨目录**：`Byrdpip`（Monster + Relic）、`LostWisp`（Event + Relic）、`PaelsLegion`（Monster + Relic）。全局"类名 → 文件"字典只保留第一个候选会漏内容；要保留候选列表并按 marker 基类 BFS 判断。
- **变量名归一**：`base.DynamicVars.Weak` 对应变量键 `WeakPower`（`PowerVar<T>` 的泛型名）；`Vulnerable`/`Strength`/`Dexterity` 同理。不归一会产生 52 处无法解析的 `{"var": ...}`。
- **表现层 `if` 吞掉后面的语句**：切分 `if (...) { ... }` 时把花括号之后的 `tail` 拼回 body，导致 `if (FastMode) { num += 0.2f; }` 之后的 `DamageCmd.Attack` 被当成 if 内部语句，`effect_complete` 却是 true。先切出 `(head, body, tail)`，tail 单独处理。
- **条件表达式里的攻击**：`if ((await DamageCmd.Attack(...).Execute(ctx)).Any(...))` —— attack 写在条件里，不在 body 里。要从 head 里抽 gameplay op（Sunder、KnockoutBlow 即此）。
- **钩子白名单只看返回类型**：只抓 `Task` 会漏 `int`/`decimal`/`bool` 返回值，`RingOfTheSnake.ModifyHandDraw` 就是这样丢的。
- **显式命名的变量**：`PowerVar<T>("SappingWeak", 2m)` 的键是字符串而不是泛型名（`MadScience` 有 11 个）；`StringVar("StarterCard")` 只有一个字符串参数。按字符串取名。
- **间接 HP 表达式**：`MinInitialHp => FirstFormHp` 这类标识符引用要链式解析（`TestSubject` 100/111）。
- **继承占位挡住下放**：monster 的 `{"min": null, "max": null}` 是非空 dict，`resolve_inheritance` 不会用父类值覆盖。先把占位归 `None` 再继承（`MysteriousKnight` 101/108、`DecimillipedeSegment*` 40–46/46–52）。
- **块体属性有两个值**：`TemporaryStrengthPower.Type` 返回 Buff 与 Debuff 两值，序列化成 `{"variants": [...]}`；对账时按"包含"比较。
- **多形态卡**：`OnPlay` 里有 `switch` 的卡（`MadScience`、`CosmicIndifference`）没有单一 type/target，标记 `multimodal` 且 `effect_complete=false`。
- **计算型变量**：`CalculatedDamage`/`CalculatedHits` 等只有表达式，没有 base 值，数据包给 `{"expr": ...}`，对账时归入 `unverifiable`。

## 3. 数据包口径（对账前先统一）

| 项 | 数据包语义 | 踩过的坑 |
|---|---|---|
| `cost_upgraded` | `cost + upgrade.cost_delta` | 拿 oracle 的 `Canonical` 比会全错，要用 `GetResolved()` |
| `unplayable` | `cost = -1` 时 `cost=None` | oracle 报 0，必须跳过 |
| `vars` | 空时为 `None` | oracle 是 `{}`，比较前归一 |
| upgraded 卡 | 变量比 `vars[name].upgraded` | 拿 base 比会得到大批假差异 |

程序集里有、数据包里没有的类应当全是预期排除项：`Mock*`（卡 7、怪 6、能力 18、药水 1、遭遇 8）与 `DeprecatedCharacter`/`Deprived`/`RandomCharacter`。出现别的差异就是导出器 bug。

## 4. 真机环境

- **工坊 mod 会干扰**：正常启动会加载全部工坊 mod，其中 CombatSolver 会写 `.combatsolver-precombat/` 目录、改 `sts2.runtimeconfig.json`。`--nomods` 能关掉它们，但也会关掉 parity mod；因此改为把 `steamapps/workshop/content/2868840` 整体改名为 `2868840.disabled-by-parity`，Steam 仍会列出订阅，但目录读不到，日志只剩 WARN。
- **直接启动需要 appid**：不经过 Steam 拉起时 Steamworks 报 `No appID found` 并走错误弹窗、退出；在游戏根目录放 `steam_appid.txt`（内容 `2868840`）即可。`run_scenario.py` 会写这个文件。
- **`--nomods` 不再是哨兵**；新哨兵是 `RUNNING MODDED! Loaded 1 mods`、`main_assembly_hash` 等于预期、数据包 manifest 与游戏版本一致、`[parity] scenario complete`。任一不满足就中止。
- **参考实现不能当种子 oracle**：`sts2_env/core/rng.py` 用的是 .NET `System.Random` 兼容实现，同种子轨迹与游戏必分叉。

## 5. 时序与随机

- 动画时长本身不抽 RNG。真正改变消耗顺序的是：`await` 成为任务交错点（mod、输入、AutoSlay 可在其间注入随机）、等待是取消点（QuickRestart 类可中断）、节点存在性分支（`NCard.FindOnTable != null`、`NCombatRoom.Instance?.`）、mod 的 Harmony 补丁与 tween 回调。
- `FastMode` 只门控等待与音效：`Neutralize` 里 `num += 0.2f` 在 `if (SaveManager...FastMode == Normal)` 内，而 `DamageCmd.Attack` 在该 if 之外。静态解析时把这类 if 当表现层剥掉，但别把里面的 gameplay 语句一起扔掉。
- 跳过等待走 `NonInteractiveMode.AutoSlayerCheck = () => true`；不要开 `TestMode`——它同样会激活 `NonInteractiveMode`，但会切换测试专用分支。

## 6. 版本漂移（v0.107.1 → v0.111.0）

反编译树来自旧快照时，计数会整体偏移：579→596 / 121→120 / 260→265 / 298→299 / 64→65 / 88→90。内容也有增删（新增 19 张卡、删除 `Grapple`/`Scare`、`BattlewornDummyEventEncounter` 拆成 V1/V2/V3）。

**先确认版本再对账**：拿 `release_info.json` 的 `main_assembly_hash` 当唯一版本标识，不要凭安装时间或目录名判断。

## 7. 工具使用

- 用 bash heredoc 追加超过约 200 行会被截断，曾把文件尾巴整段截掉又"看起来成功了"。大文件一律用 write/edit 工具。
- 往 Python 脚本里打 JSON 转义补丁时容易静默不生效（转义层数不对，`old_string` 匹配不到却报成功）。改完必须 `grep` 或跑一次验证。
- plan 模式的计划三次被 dismiss，原因都是"没先回答用户当轮的疑问"。先答问题，再提交计划。

## 8. 真机 harness 实现记录（M2）

- **不能继承 `Node`**：mod 工程只引用 `GodotSharp.dll`，没有 Godot 源生成器，引擎不会回调 `_Ready` 等虚方法（`AddChild` 后一无所获）。驱动写成普通类，在 `[ModInitializer]`（主线程，`SynchronizationContext.Current` 是 `Godot.GodotSynchronizationContext`）里直接开始 async；逐帧等待用 `await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame)`，收尾 `tree.Quit()`。
- **启动蓝本**：`Player.CreateForNewRun` → 覆盖牌组/遗物/HP/能量 → `RunState.CreateForNewRun(players, acts, modifiers, GameMode.Standard, ascension, seed)` → `RunManager.SetUpNewSingleplayer(state, shouldSave:false)` → `PreloadManager.LoadRunAssets` → `RunManager.Launch()` → `RootSceneContainer.SetCurrentScene(NRun.Create(state))` → `SetActInternal(0)` → `RunLocationTargetedBuffer/MapSelectionSynchronizer.OnLocationChanged` → `EnterRoomDebug(RoomType.Monster, MapPointType.Unassigned, encounter.ToMutable())`。
- **真实出牌**用 `CardModel.TryManualPlay(target)`，它入队 `PlayCardAction`；`SpendResources()` 扣能量/星，随后 `OnPlayWrapper` 触发钩子。`CardCmd.AutoPlay` 的 `EnergySpent` 为 0，只适合"不花钱"的对照。
- **快照**：每行动一行 JSONL，含 `action`、回合/阶段、玩家（HP/格挡/能量/星/能力/遗物/药水）、敌人（HP/格挡/能力/意图）、五个牌堆（手牌带 `can_play`）、七条 RNG 流的 counter 与前四个状态字。RNG counter 是判断随机消耗顺序是否一致的关键。
- 录制场景：`harness/scenarios/ironclad_nibbits.json`，`tests/vectors/real/ironclad_nibbits.jsonl`（10 行，含击杀收尾）。场景动作的 `slot` 指"动作发生时手牌下标"，比按 id 找牌稳定。
- 实机行为样本：NibbitsWeak 的 Nibbit 起手 43/43（随机 HP），首招 `Attack 12`；回合 2 变 `Attack 6 + Defend`；Defend 给 5 格挡；Bash 先结算伤害、后上易伤（本体不吃自己的易伤），后续 Strike 6×1.5=9；易伤叠到 3 层时仍只放大 1.5 倍。

## 9. 引擎纵切实现记录（M3）

- **命名空间**：`Data`（读 `data/sts2/*.json`）、`Random`（XXH64 + xoshiro256\*\*）、`Model`（`CardInstance`/`PlayerState`/`MonsterInstance`，统一实现 `ICombatant`）、`Combat`（状态机）、`Rules`（伤害/格挡）、`Hooks`（`IDamageModifier` 两遍结算）、`Effects`（本切片的三张牌）、`Diagnostics`（L3 不变量）。`Contracts` 是唯一出口，`View` 只读 `Contracts`。
- **出牌**：校验手牌与能量 → 扣能量（X 费取当前能量）→ 手牌入 Play 堆 → `Resolve` → 进弃牌堆或消耗堆；若本次出牌导致战斗结束，卡牌留在 Play 堆（真机 `EndCombatInternal` 之后才清牌堆），最后一行快照因此能看到它。
- **伤害**：`(基础 + 力量) × 易伤1.5 × 虚弱0.75`，再 `min(..., 999999999)`、`max(0, ...)`；格挡吸收 `int min(block, (int)amount)`，剩余部分 `(int)Math.Clamp(...)` 截断。因此 7×1.5=10.5 打 10 格挡是"完全格挡、0 点掉血"。
- **回合**：玩家回合开始才清格挡（第 1 回合豁免）、回能量、抽 5；结束回合先弃手牌，再逐个敌人执行意图并推进状态机，然后才在敌方侧结束递减易伤/虚弱，最后进入下一个玩家回合并重掷意图。
- **洗牌**：初始洗牌用 `UnstableShuffle`（降序 Fisher-Yates，每次 `NextInt(num+1)`）；牌堆耗尽时"排序 + UnstableShuffle"，排序键是 `CardModel.CompareTo`（先 id 序、再升级等级）。这些都走 run 级 `shuffle` 流，counter 不按战斗重置。
- **验证命令**：
  - `dotnet run --project tests/Sts2.Engine.Tests`（8 项：伤害截断、格挡、RNG 状态、整场对拍、能量校验、敌方攻击）。
  - `dotnet run --project src/Sts2.Cli -- run --scenario harness/scenarios/ironclad_nibbits.json --pack data/sts2 --out artifacts/engine/x.jsonl`
  - `python tools/compare_snapshots.py --a tests/vectors/real/ironclad_nibbits.jsonl --b artifacts/engine/x.jsonl` → `ok: 10 snapshots identical`。
  - `dotnet run --project src/Sts2.Cli -- tui --scenario harness/scenarios/ironclad_nibbits.json`：`play <slot> [target]` / `end` / `map` / `quit` 单步。
- **对拍细节**：快照字段名与真机 mod 完全一致；`action` 里只有非空字段输出（`start` 无 `index`），卡牌里的 `enchantment`/`affliction` 即使是 null 也保留；RNG 的七条流平铺在 `rng` 对象上，`monsters` 数组在旁。数值比较用 1e-9 容差、结构逐键比较。

## 10. 怪物招式 effects 与 auto 对拍（M5 进行中）

- `tools/export_sts2_data.py` 的 `_move_effects()` 从每个 MoveState 的招式方法体按源码顺序解析
  `DamageCmd.Attack(expr)`、`CreatureCmd.GainBlock(creature, expr, ...)`、`CreatureCmd.Heal(...)`、
  `PowerCmd.Apply<T>(ctx, target, expr, ...)`，写成 move 的 `effects`（op/target/amount/power）与
  `effect_complete`；无法识别的 Cmd 调用记入 unsupported 并置 `effect_complete=false`。
  覆盖 346 个 MoveState 中的 333 个。
- `effect_complete` 为真时引擎按 effects 执行，否则回退旧的 intent 分支；多段攻击用
  `effects` 里唯一的 damage op 按 `intents` 的 repeats 重复。
- 真机 mod 新增 `auto_turn` 动作：贪心打出手牌（最低槽位、AnyEnemy 打第一个存活敌人）后结束回合；
  引擎 `Combat.AutoTurn` 实现同一策略，整回合算一个 action，只产出一行快照。
  战斗在 auto 过程中结束时，真机的 EndCombatInternal 已经跑完（清空牌堆、Burning Blood 回 6 血），
  引擎用 `SettleAfterVictory()` 对齐这个状态；脚本动作击杀仍保留“卡在 Play 堆”的中间态。
- 意图伤害按真机 `AttackIntent.GetTotalDamage`：`GetSingleDamage`（截断到 int）再乘 `Repeats`。
- 已支持的力量/易伤/虚弱/Shrink/Ritual 在 `Hooks.PowerDamageModifier` 与 `Combat` 里；
  Weak/Vulnerable 在敌方回合结束时对双方生物一起递减，Ritual 在敌方回合结束时加力量
  （应用当回合跳过，对应 `WasJustAppliedByEnemy`）。
- 对拍通过（0 差异）：`ironclad_nibbits`（脚本动作）、`ironclad_nibbits_auto`、`ironclad_seapunk_weak`、
  `ironclad_fuzzy_wurm_crawler_weak`。`ironclad_exoskeletons_weak` 起暴露已知缺口：条件分支谓词
  （SlotName/IsAlone 等）没有导出，引擎目前对所有条件分支取第一个分支；RandomBranchState 用
  `Rng.MonsterAi.NextFloat(total)` 且默认权重相等（权重未导出）。后续需要把条件表达式与权重写进数据包。

## 11. 弱遭遇铺开记录（M5，进行中）

导出器新增（`tools/export_sts2_data.py`）：
- 怪物招式 `effects`：按源码顺序解析 `DamageCmd.Attack`（damage/player）、`CreatureCmd.GainBlock(base.Creature, …)`（block/self）、`CreatureCmd.Heal`（heal/self）、`PowerCmd.Apply<T>`（power；目标由第二参判定 self/player）。无法静态求值的金额写成 `{"expr": …}`，由引擎在运行时拒绝，而不是猜。
- 条件分支 `conditions`（`ConditionalBranchState.AddState(var, () => expr)`，表达式保留源码文本）、随机分支 `weights`/`repeat_types`、开场效果 `start_effects`（`AfterAddedToRoom`）。
- `AddBranch(state, int)` 是 `maxRepeats` 重载，不是权重；只有浮点字面量才是权重（`ScrollOfBiting` 的 `AddBranch(moveState2, 2)` 因此是权重 1）。
- 多初始状态：`return (StarterMoveIdx % 3) switch { … }` 导出成 `initial_states` 数组；遭遇里的 `num = (var.StarterMoveIdx = base.Rng.NextInt(3))` 导出成 `starter_rng {range, offsets}`。

引擎新增（`src/Sts2.Engine`）：
- 遭遇 RNG：`Rng(seed + 1 + XXH64(encounter.Entry))`（首层 `TotalFloor=1`），每个 spawn 的 `StarterIdx = (num + offsets[i]) % range`。
- `EvalCondition` 支持 `SlotName == "…"`、`GetAllyCount()` 比较、`IsAlone`、`IsFront`（无槽位时首个 spawn 视为前排）；未知谓词抛异常。
- 力量/易伤/虚弱/Shrink(×0.7)/Ritual/Thorns/Rampart/PaperCuts/BurrowedPower 语义；`Fresh` 标记让敌方回合施加的 Debuff 跳过当回合 tick（与真机一致）。
- 敌方回合开头清空格挡（`BurrowedPower` 例外），玩家死亡时清玩家能力并停在敌方侧，胜利时清空牌堆/能力并触发 Burning Blood。
- 怪物 `SkipNextTurn`（Burrowed 破盾后眩晕一回合）。

已 0 差异的对拍向量（`tests/vectors/real/` ↔ `artifacts/engine/`）：nibbits（脚本 10 行）、nibbits_auto、seapunk、fuzzy_wurm_crawler、exoskeletons、toadpoles、devoted_sculptor、sludge_spinner、turret_operator、shrinker_beetle、scrolls_of_biting、tunneler。`thieving_hopper` 仍缺偷牌（CardDebuffIntent）实现。

## 12. 卡牌 effects 数据驱动（M5，进行中）

- `tools/export_sts2_data.py` 的 `_card_effects(info)` 扫描 `OnPlay` 方法体，把命令按源码顺序转成 op 列表：
  `DamageCmd.Attack`（damage，`.WithHitCount` → hits，`TargetingAllOpponents` → all_enemies）、
  `CreatureCmd.GainBlock`（block/self）、`CreatureCmd.Heal`（heal/self）、`PowerCmd.Apply<T>`
  （power；目标由第二参判定 target/self/all_enemies）、`CardPileCmd.Draw`（draw）、
  `PlayerCmd.GainEnergy`（energy）。金额里的 `base.DynamicVars.X.BaseValue` / `["X"].IntValue`
  写成 `{"var": "X"}`，由引擎在运行时取升级后的值；局部变量（`int amount = …`）先查再解析。
- 覆盖：596 张卡中 239 张 `effect_complete=true`；35 张 complete 但 effect 为空（诅咒/状态/Unplayable）；
  其余 322 张因 `CardSelectCmd`/`OrbCmd`/`SummonCmd`/复杂表达式未解析，`effect_complete=false`。
  白名单外的命令不会静默：未转换的白名单命令、`{"expr"}` 金额、或“ops 为空但方法体有实质代码”
  一律置 `effect_complete=false`。
- 引擎 `src/Sts2.Engine/Effects/CardEffects.cs` 按 `CardDef.Effect` 执行；`effect_complete=false`
  时抛 `CombatException("card effect not implemented: …")`，绝不静默 no-op。`EffectDef` 支持
  `amount`/`var`、`hits`、`count`（`add_card` 用 `count`，不是 `amount`）。
- 怪物招式同样支持 `CardPileCmd.AddToCombat*` → `add_card`（如 Leaf Slime 的 Slimed 入弃牌堆）。
- 动态遭遇：`src/Sts2.Engine/Combat/EncounterGenerators.cs` 目前实现 `SLIMES_WEAK`
  （小史莱姆 A/B 在 Leaf/Twig 中按顺序 NextItem，中史莱姆在 LeafM/TwigM 中 NextItem）；其余
  14 个 `monsters=null` 的遭遇待实现。
- 新增 0 差异向量：`ironclad_cards_showcase`（Iron Wave/Pommel Strike/Uppercut/Thunderclap/Shrug It Off）、
  `ironclad_slimes_weak_auto`（动态生成 + Slimed）。CI 现在重放 `harness/scenarios/` 下全部场景，
  跳过 `KNOWN_GAPS` 中的 `ironclad_thieving_hopper_weak`（缺 CardDebuff 偷牌）。

## 13. 动态遭遇生成（M5，进行中）

`encounters.json` 里 `monsters: null` 的 12 个遭遇需要在运行时按 RNG 选怪，引擎用
`src/Sts2.Engine/Combat/EncounterGenerators.cs` 复刻（顺序与源码一致，否则 RNG 消耗不同）：

- `SLIMES_WEAK`（已有）、`SLIMES_NORMAL`（先 `NextBool` 决定小史莱姆顺序，再两个中史莱姆）、
  `CORPSE_SLUGS_WEAK/NORMAL`（一次 `NextInt(3)`，初始招按 +1 轮转）、
  `BOWLBUGS_WEAK/NORMAL`（Rock 固定槽位，工人按上限 1 抽）、`FLYCONID_NORMAL`、
  `SLITHERING_STRANGLER_NORMAL`（三种副怪之一 + 本体）、`RUBY_RAIDERS_NORMAL`（5 选 3 不重复）、
  `OVICOPTER_NORMAL`、`TWO_TAILED_RATS_NORMAL`（3 只、初始招轮转、槽位 third/fourth/fifth）、
  `PUNCH_OFF_EVENT_ENCOUNTER`（两只构造体各 `NextInt(2,10)` 扣血，第一只 FAST_PUNCH 起手）、
  `SCROLLS_OF_BITING_WEAK/NORMAL`（第 4 只固定 `MORE_TEETH`）。
- 生成器的优先级高于 `monsters` 列表：有些遭遇（如第四只卷轴）需要逐只初始状态，扁平列表表达不了。
  `EncounterSpawn` 可带 `Starter`/`HpReduction`/`InitialState`。
- 另外从源码补了怪物 `(StarterMoveIdx % 3) switch` 形式的 `initial_states`（CorpseSlug、
  TwoTailedRat、ScrollOfBiting、Decimillipede 三段），此前这些怪物的初始状态为空。

新增 0 差异向量：`ironclad_slimes_normal`（4 只怪、先 `NextBool` 再抽中史莱姆）、
`ironclad_raiders`（5 选 3、三个不同 raider 的招式）。

**已知缺口**（轨迹与场景在 `harness/scenarios/known_gaps/`，不参与 CI）：
`RavenousPower`（同侧死亡 +力量并眩晕）、玩家侧 Debuff 的递减时机、Flyconid/Strangler 的
持续伤害差异、Ovicopter 召唤与 `Rng.Niche` 顺序、Thieving Hopper 偷牌。

计划口径（成功标准 3）允许“未结构化/未实现”显式报错；这些缺口目前表现为对拍差异或
`CombatException`，不会静默通过。

## 14. 单文件 HTML 回放（M6）

`src/Sts2.View/HtmlView.cs` 把一份快照逐行 JSONL 渲染成单个 HTML：数据以
`const DATA = [...]` 内嵌，CSS/JS 内联，没有外部字体或 CDN。输入既可以是引擎产物，
也可以是真机轨迹——两者 schema 相同，所以同一份页面能并排看两边。

- 入口：`dotnet run --project src/Sts2.Cli -- html --transcript <jsonl> --out <html> [--title T]`。
- 页面内容：玩家（HP 条/格挡/能量/星星/能力/遗物）、敌人（HP 条/格挡/意图/能力）、
  手牌（费用、升级、是否可打）与五牌堆计数、逐动作日志；`←`/`→`/`Home`/`End` 与滑杆翻页。
- 只读：页面不做任何规则判断，避免出现“第二个真相来源”。
- 校验：`tools/check_replay.py <html> <jsonl>` 检查无外部引用、`DATA` 数组存在、内嵌快照数
  等于轨迹行数；CI 对 `tests/vectors/real/*.jsonl` 全量渲染并上传 `replays` artifact。
  引擎测试里也有一条同样的自包含断言（9 checks）。
- 注意：`HtmlView` 只依赖 `Sts2.Contracts`，自己的 `JsonOptions` 用 snake_case，
  不要引用 `Sts2.Engine`（会形成 View→Engine 的反向依赖，构建会失败）。

## 15. 进阶（A1–A10）与原生 AOT（M7）

导出器把 `AscensionHelper.GetValueIfAscension(level, asc, base)` 的两侧都留下：
怪物 `min_hp`/`max_hp` 是 A0 值，另有 `hp_ascension {level, min, max}`；招式效果的
`amount` 是 `{base, ascension, level}` 对象。引擎 `Rules/Ascension.cs` 用枚举声明顺序
（`SwarmingElites=1 … ToughEnemies=8, DeadlyEnemies=9, DoubleBoss=10`）判定
`ascension >= level`，选对应一侧——等价于游戏里的 `RunManager.HasAscension`。

引擎侧另外复刻了两处进阶对**运行**的影响（`AscensionManager.ApplyEffectsTo`）：
- A4 `TightBelt`：最大药水槽 −1；
- A5 `AscendersBane`：起始牌组加入 `CARD.ASCENDERS_BANE`。这张诅咒带 `Ethereal`，
  所以回合结束弃手牌时 Ethereal 进消耗堆而不是弃牌堆，这是 A5 以后轨迹能对上的前提。
- 快照里的 `cost` 按游戏口径钳到 0（`-1` 只表示“不可打出”），`can_play` 对不可打出的卡为 false。

已录制并对齐：`ironclad_nibbits_a4/a5/a9/a10`（A10 = ToughEnemies + DeadlyEnemies 同时生效：
HP 44–48、猛击 13、斩击 7 伤害 + 6 格挡、嘶鸣 +3 力量）。A1 只差“胜利结算后剩余能量”
一项，已移到 `known_gaps`。

**AOT**：`dotnet publish src/Sts2.Cli -c Release -r win-x64 -p:PublishAot=true`。
反射式 `JsonSerializer` 在 AOT 下被禁用，因此数据包、场景、快照都改成
`JsonSerializerContext` 源生成（`Sts2.Engine/Data/PackJsonContext.cs`、
`Sts2.View/ViewJsonContext.cs`），读取一律走 `PackJsonContext.Default.X` /
`ViewJsonContext.Default.X`，不传 `JsonSerializerOptions`。

两个坑：
1. 不要在 context 类**自己的静态初始化**里引用 `Default`（`Options` 属性放在同一个类中），
   会拿到 resolver 为 null 的半成品；单独建 `JsonDefaults`/`ViewJsonDefaults`。
2. `[JsonExtensionData]` 只能是 `IDictionary<string, object>`（或 `JsonElement`），
   因此 `RngView.Streams` 保持 `Dictionary<string, object>`，并在 context 上补
   `[JsonSerializable(typeof(JsonElement))]`。

本机实测（win-x64 AOT）：单场 `ironclad_nibbits` 约 **21.6 µs/场、46k 场/秒**；
JIT 下约 57.7 µs/场、17k 场/秒。CI 在 linux-x64 上做同样的发布与冒烟测试。

## 16. 本地 Web 人机对弈（M8）

`sts2 web --port 5080 --pack data/sts2 --scenario harness/scenarios/ironclad_nibbits.json` 起一个只监听 127.0.0.1 的对弈服务：引擎是唯一真相，浏览器只渲染快照并 POST 动作。

- 传输层用 `TcpListener` 手写最小 HTTP/1.1，不用 `HttpListener`：后者在非交互/无 URL ACL 的会话里直接抛 `HttpListenerException (6): 句柄无效`（http.sys 需要 URL 预留）。
- 端点：`GET /`（内嵌单页）、`POST /api/new`（按 scenario 建一场 Combat，返回 8 位 session id）、`GET /api/state?id=`、`POST /api/play {slot,target}`、`POST /api/end`。响应统一 `{state, log, over}`，错误 `{error}` + 400/404/500。
- 快照直接复用 `ViewJsonContext` 的源生成序列化，页面不需要知道引擎内部结构；非法出牌由引擎抛 `CombatException`，服务转 400 并把消息显示在页面上。
- 页面零外部依赖（内联 CSS/JS），展示玩家 HP/格挡/能量/能力、敌人意图与血条、手牌（费用、X 费、不可打出置灰）、五牌堆计数与动作日志；点手牌即出牌，`end turn` 结束回合，战斗结束禁用结束按钮。
- `python tools/check_web.py` 起服务并跑 7 项断言（页面、建局 5 张起手、出牌生效、结束回合进入第 2 回合、状态一致、未知 session 404），CI 中作为独立步骤执行。
- 限制：单进程内存 session、无鉴权/持久化/存档，仅适合本地手动体验；AOT 发布未验证 `TcpListener` 路径（CI 的 AOT 冒烟只覆盖 `run`/`bench`）。

## 17. 五角色铺开（M5，进行中）

成功标准 2 要求“5 角色 × Overgrowth 弱遭遇，随机动作序列与真机零差异”。当前进度：**五个角色（Ironclad、Silent、Regent、Defect、Necrobinder）起手牌组对 NibbitsWeak 均已 0 差异**。

- **选择类卡牌的确定性**：真机在无 UI 的 headless 下会卡在 `CardSelectCmd`（例如 Silent 的 `SURVIVOR` 要弃一张牌）。parity mod 在 `[ModInitializer]` 里 `CardSelectCmd.PushSelector(new FirstCardSelector())`，策略是“按列表顺序取前 N 张”；引擎的 `discard`/`exhaust` op 用同一策略（手牌首位）。这是 harness 的约定，不是游戏原生的自动选择。
- **导出器**：`CardSelectCmd.FromHandForDiscard/FromHandForExhaust` → `{op: discard|exhaust, count: N, pick: "first"}`（N 取自 `CardSelectorPrefs` 的第二参）；紧随其后的 `CardCmd.Discard/Exhaust` 视为已由选择 op 覆盖，不再记 unsupported。结构化卡牌从 239 增至 **242**/596；随后把 `CreatureCmd.TriggerAnim` 等纯表现命令列为可忽略（它们不携带战斗状态），结构化覆盖跳到 **414**/596；补上 `OrbCmd.Channel/EvokeNext` 后为 **425**/596。
- **引擎**：`CardEffects` 新增 discard/exhaust；`Combat` 在开局抽 5 后按 `RING_OF_THE_SNAKE` 再抽 2（Silent 起手 7 张）。
- **星辰资源（Regent）**：`PlayerCmd.GainStars` → `{op: stars}`；`DIVINE_RIGHT` 开局给 3 点；`PlayCard` 校验并扣除 `star_cost`（FALLING_STAR 0 费 2 星）；快照 `player.stars` 直接映射。`regent_nibbits_auto`（PARITY-13，5 行）0 差异。
- **充能球（Defect）**：`OrbCmd.Channel<T>` → `{op: channel, orb}`、`OrbCmd.EvokeNext(..., dequeue: false)` → `{op: evoke, count, dequeue}`；引擎实现 `PlayerState.Orbs` + 3 球位、`CRACKED_CORE` 开局引导闪电球、引导满位时先激发最前一颗、闪电被动（回合结束 3 点）与激发（8 点）走 `Rng.CombatTargets.NextInt` 选随机敌人、且为 unpowered（不吃力量/易伤）。`defect_nibbits_auto`（PARITY-11，5 行）0 差异。
- **已通过**：`silent_nibbits_auto`（PARITY-10，9 行，含弃牌选择、Weak、蛇戒起手）、`regent_nibbits_auto`（PARITY-13，5 行，含星辰起手、星辰费用、Falling Star 的易伤+虚弱）。
- **Osty（Necrobinder）**：`OstyCmd.Summon(...)` → `{op: summon, amount}`；引擎把 Osty 建模成玩家的 HP 池（`PlayerState.OstyHp/OstyMaxHp`），`BOUND_PHYLACTERY` 开局召唤 1 HP、之后每个玩家回合开始再召唤 1（`AfterEnergyResetLate`），存活时召唤改为“上限 +N 且治疗 N”。`DieForYouPower` 的语义是**溢出**而非全免：敌方 powered 攻击的未格挡伤害先扣 Osty HP，Osty 死后剩余部分才落到玩家（真机首回合 12 伤害 → Osty 6 点全吃、玩家掉 6；实测确认）。`UNLEASH`/`PROTECTOR`/`SQUEEZE` 的 `CalculatedDamageVar` 按 `base + ExtraDamage × multiplier` 计算（Osty 当前 HP / Osty 上限 / 其它 OstyAttack 卡数），Osty 不在场时整张卡不结算。`necrobinder_nibbits_auto`（PARITY-12，6 行）0 差异。
- 结构化卡牌 **429**/596；剩余 167 张主要卡在 `CardSelectCmd` 的其它变体（升级/变形/选牌入手）、`OrbCmd` 的其它球、`ForgeCmd` 与自定义命令。

## 18. 随机策略与五角色 × 四遭遇（M5，进行中）

成功标准 2 要求“5 角色 × Overgrowth 4 个弱遭遇，随机动作序列与真机轨迹零差异”。
`auto_turn` 是贪心策略（总打最左手可出的牌），覆盖面太窄，因此新增了一个**确定性随机策略**，
真机 mod 与引擎各实现一份完全相同的算法。

**策略契约**（`harness/Sts2.ParityMod/ParityDriver.cs` 与 `src/Sts2.Engine/Combat/Combat.cs`）：

- 场景 JSON 增加 `policy_seed`（整数）。
- 动作类型 `random_play`：每次决策用 `new MegaRandom(policy_seed + decisionIndex)` 抽一次
  `Next(候选数)`，其中 `decisionIndex` 是该动作在 `actions` 数组里的下标。
- 候选集 = 手牌中满足下列条件的槽位（按手牌顺序）：费用付得起（X 费要求能量 > 0）、
  星辰够付、`AnyEnemy` 目标存在存活敌人。
- 没有候选时改为结束回合（与真机 `PlayerCmd.EndTurn` 一致）。

因为两边的 RNG 只由 `policy_seed + decisionIndex` 决定，**不消耗游戏自身的 RNG 流**，
所以策略选择不会污染伤害、洗牌与怪物 AI 的随机序列，也不需要在两侧同步额外状态。

**真机侧的两个细节**：

- 快照里的 `can_play` 必须包含星辰校验（`FALLING_STAR` 0 费 2 星、只有 1 星时不可打出），
  引擎的 `Snapshots.CanPlay` 与 `RandomPlay` 的候选过滤都补上了这条。
- `CardSelectCmd` 的弃牌选择在真机里走“首张”选择器，弃牌堆是**追加**到末尾
  （`DiscardPile.Add`），不是插到 0 号位；引擎此前插 0 号位，在 Silent 的
  `SURVIVOR` 连续弃牌时才暴露出来（`silent_random_nibbits` 第 3 行起就分叉）。

**回合末击杀的结算**：闪电球被动在 `EndTurn` 里打死最后一个敌人时，真机的
`EndCombatInternal` 会在驱动等待下一回合的期间跑完——牌堆清空、格挡归零、Burning Blood 治疗、
`side` 回到 `Player`。引擎在 `EndTurn` 的 `CombatOver` 分支里补了 `SettleAfterVictory()`，
并在其中把 `_side` 设回 `Player`；否则 `defect_random_*` 的最后一帧会差一个 `side` 字段。

**产物**：`harness/scenarios/{character}_random_{encounter}.json` 20 个（5 角色 ×
NIBBITS/SEAPUNK/FUZZY_WURM_CRAWLER/TOADPOLES 四个弱遭遇，`policy_seed` 各不相同，
40 个 `random_play` 上限），真机轨迹在 `tests/vectors/real/`，全部与引擎逐字段一致
（每局 13–29 帧，战斗结束后截断）。顶层场景总数 45，`compare_values.py` 仍是 0 差异。

**仍未满足的部分**：成功标准 2 的“每局 ≥ 200 步”目前没有达到——一个弱遭遇战斗在 15–30 个
动作内结束，20 个场景合计约 400 帧。要做到单局 200 步，需要让一局连续打多场遭遇
（每次重新建 `RunState`，玩家 HP 与牌堆不跨场保留），这条还没做。

## 19. 多场连续会话（M5，进行中）

成功标准 2 还要求“每局 ≥ 200 步”。单场弱遭遇只有 13–29 个动作，凑不够，
因此引入**会话文件**：一个场景里带 `encounters` 数组，按顺序打多场战斗。

**为什么不是一个进程连打**：真机的 `RunManager` 是单例状态机，第二次
`SetUpNewSingleplayer` 会直接抛 `InvalidOperationException: State is already set`。
所以会话录制由 `harness/run_session.py` 拆成“每场一次无头启动”，
每场都用同一份初始状态（同一角色、牌组、遗物、HP），把各场轨迹拼接起来，
并把动作下标改成全局递增。引擎侧原生支持同一个 `encounters` 数组：
`Scenario.Merge` 只让每个条目覆盖遭遇相关的字段（encounter/actions/policy_seed 等），
角色、牌组、遗物、进阶跟会话走。

**索引契约**：会话里的动作下标全局递增；`random_play` 的决策下标是**本场内的下标**
（`index - baseIndex`），因为真机每场都是独立进程、决策从 0 重新开始。
每场结束后 `base += len(actions)`（按脚本动作数，不按实际执行数），两侧一致。

**产物**：`harness/scenarios/{character}_session.json` 5 个，每个 16 场（4 个弱遭遇 ×
4 轮，每场 40 个 `random_play` 上限），真机轨迹 `tests/vectors/real/{character}_session.jsonl`
（约 220 行、204 个动作，超过 200 步门槛）。

**已知偏差**：每场都从相同的初始状态开始（HP 满、牌组重洗），这与“一次真实爬塔里
连续打四场”不同——真机的牌堆与 HP 会跨场保留。这个偏差是有意的：引擎按单场建模，
两侧行为一致即可对拍；如果以后要模拟真实 run 的跨场状态，需要引擎引入 run 级状态。

**录制命令**：`python harness/run_session.py harness/scenarios/ironclad_session.json --no-build`。

### 20. 遭遇覆盖与条件谓词（M5，进行中）

`sts2 smoke --pack data/sts2` 会遍历所有 `RoomType == Monster` 的遭遇：建场、跑 12 个随机动作，
捕获异常。当前 **58 个可启动 / 6 个显式不支持 / 0 个崩溃**（`DENSE_VEGETATION_EVENT_ENCOUNTER`
与 `DEPRECATED_ENCOUNTER` 属于事件/废弃内容，已在冒烟里显式排除）。

为让这批遭遇能起步，引擎补了几条**近似谓词**（`Combat.EvalCondition`）：

- `CanFabricate` → 存活队友 < 4；`CanLay` → 存活队友 ≤ 3（与源码的 `GetTeammatesOf(...).Count` 对应）。
- `IsOffBalance` → 恒 false（该标志只在未建模的充能/击倒招式里翻转；起始态是平衡态）。
- `HasBeetleCharged` → 若表达式含 `CurrentHp` 则退化为 `Hp*2 >= MaxHp`，否则 false。
- `base.Creature.HasPower<XPower>()` → `PowerAmount(X_POWER) > 0`。
- `Sleep`/`Hidden` 意图 → 空过一回合。

仍显式不支持的 6 个：4 个 `Summon` 意图（FABRICATOR、FOGMOG、THE_OBSCURA、TWO_TAILED_RAT）与
GREMLIN_MERC/THIEVING_HOPPER 的未解析 power 效果。它们会以 `NotSupportedException` 报出，不静默。

### 21. 会话级对拍结果（M5，进行中）

| 会话 | 帧数 | 动作数 | 结果 |
|---|---|---|---|
| ironclad_session | 220 | 204 | 逐字段一致 |
| silent_session | 396 | 380 | 逐字段一致 |
| regent_session | 216 | 200 | 逐字段一致 |

会话都是 16 场（4 个弱遭遇 × 4 轮），每场 40 个 `random_play` 上限。defect/necrobinder 两个会话
的录制脚本仍在后台跑，完成后补进来。

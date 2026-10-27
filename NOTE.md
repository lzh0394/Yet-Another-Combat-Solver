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

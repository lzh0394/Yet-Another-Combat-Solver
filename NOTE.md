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

- **工坊 mod 会干扰**：正常启动会加载全部工坊 mod（32 个 manifest、16 DLL、11 PCK，日志 `RUNNING MODDED! Loaded 17 mods (26 total)`），其中 CombatSolver 会写 `.combatsolver-precombat/` 目录、改 `sts2.runtimeconfig.json`。必须 `--headless --nomods`。
- **Steam 初始化失败不致命**：`No appID found` 之后仍能加载存档与本地化，可以继续用。
- **三条环境哨兵**：日志含 `skipping mod initialization`；`main_assembly_hash` 等于预期；数据包 manifest 与游戏版本一致。任一不满足就中止，不要"先跑跑看"。
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

# STS2 战斗模拟器（C#）实施计划

把《Slay the Spire 2》的战斗层复刻成可编程、可对拍、可视化的 C# 模拟器。目标是**确定性**：同一场景同一随机种子，模拟器与游戏逐动作一致。

## 1. 目标与非目标

**目标**

- 单人战斗：A0 起步，逐步覆盖 A1–A9；5 个可玩角色、全部卡牌/能力/遗物/药水/怪物/遭遇。
- 可编程接口：外部用 JSON 描述初始状态与动作序列，模拟器返回状态快照与结果。
- 与真机对拍：能用安装版游戏录制轨迹，并在模拟器中重放、逐动作 diff。
- 可视化回放：终端 TUI、单文件 HTML、按需的本地 Web，三者只消费快照。

**非目标**

- 地图、事件、商店、奖励、营火、多人、每日挑战。
- 美术、音效、动画本身；动画只作为时序噪声来源被记录。

## 2. 成功标准

1. 基准场景（Ironclad vs NibbitsWeak）在 TUI 中可单步执行，每个动作的状态与真机一致。
2. 5 角色 × Overgrowth 4 个弱遭遇，随机动作序列（每局 ≥ 200 步）与真机轨迹零差异。
3. 全部结构化卡牌（导出后 `effect_complete: true` 的部分）与全部普通遭遇可跑通；未结构化部分显式报"未实现"，不静默出错。
4. 整数、`decimal`、随机数三条路径都有独立回归；AOT 发布可用。

## 3. 版本基线

- 游戏：Steam appid 2868840，安装版 **v0.111.0**，`commit 41cef1ea`，`main_assembly_hash 222455745`，`sts2.dll` 9,757,184 字节。
- 程序集内模型计数：卡 603、怪 126、能力 283、遗物 299、药水 66、遭遇 98、角色 8、幕 5。
- 内容口径（去掉 Mock 与非可玩角色后）：卡 596、怪 120（346 招）、能力 265、遗物 299、药水 65、遭遇 90、角色 5、幕 5。
- 游戏升级后 M0 必须重做，所有对拍基线随之失效。

## 4. 技术选型

| 项 | 选择 | 理由 |
|---|---|---|
| 运行时 | .NET 10（net10.0） | LTS；游戏本体是 net9.0，模拟器不与其同进程，无需降级 |
| 引擎依赖 | 零 NuGet | 便于 AOT、离线构建、长期可复现 |
| 数值 | 全链路 `decimal` | 与游戏一致；`(int)` 截断统一封装，禁止散落 |
| 随机 | xoshiro256\*\* + splitmix64 播种 | 与 `MegaRandom` 一致，逐位复刻 |
| 测试 | xUnit v3 | 需要 Subprocess 与集合夹具 |
| CLI | 独立可执行，Release 用 PublishAot | 训练侧可直接拉起进程 |
| 可视化 | 只读快照 → TUI / HTML / Web | 与引擎单向解耦 |

## 5. 架构与目录

```
src/Sts2.Contracts/     快照 DTO（CombatSnapshot、RouteSnapshot），不引用引擎
src/Sts2.Engine/        核心；不引用 Contracts 以外的任何项目
  Data/                 加载数据包，校验 schema
  Random/               Rng、流管理、可重放计数器
  Model/                卡牌/能力/遗物/药水/怪物/遭遇/角色的运行期模型
  Combat/               战斗状态机、牌堆、回合时序
  Rules/                伤害、格挡、能量、死亡、关键字
  Hooks/                钩子分发（监听器顺序是语义的一部分）
  Effects/              卡牌与怪物招式的 op 解释器
  Content/              具体卡/怪实现（按 op 词汇表）
  Diagnostics/          冒烟、不变式、未实现清单
src/Sts2.Cli/           命令行：跑场景、导出快照、对拍
src/Sts2.View/          TUI、HTML、Web 三种渲染器
tools/Sts2.Oracle/      反射读出厂 sts2.dll，导出数值真值（仅本机，需要游戏）
tests/Sts2.Engine.Tests/
tests/vectors/          对拍与数值向量
```

数据流：`data/sts2/*.json` → `Data` → `Model` → `Combat`；`Contracts` 是唯一出口；`View` 只读 `Contracts`。

## 6. 里程碑

| 里程碑 | 内容 | 预计 | 完成判据 |
|---|---|---|---|
| M0 | 重建数据基线 | 1 天 | 数据包与安装版同源，导出两次字节一致 |
| M1 | 值 oracle | 0.5 天 | `game_values.json` 与数据包零数值差异 |
| M2 | 真机 harness | 1.5 天 | 单场景在真机跑通并产出快照 |
| M3 | 引擎纵切 + V1 TUI | 4 天 | Ironclad 对 NibbitsWeak 在 TUI 中可单步 |
| M4 | 对拍闭环（V2） | 1 天 | 同场景与真机零差异，CI 可执行 |
| M5 | 全内容铺开 | 5 天 | 成功标准 1–3 达成 |
| M6 | 单文件 HTML（V3） | 1 天 | 一份 `snapshots.jsonl` 生成可离线回放的 HTML |
| M7 | 进阶与性能 | 2 天 | A1–A9 通过，AOT 发布，基准达标 |
| M8 | 本地 Web（V4） | 2 天，按需 | 浏览器中可人机对弈 |

### M0 重建数据基线

当前仓库没有数据包、没有导出器、没有反编译树，全部需要重建：

1. `dotnet tool install -g ilspycmd --version 9.1.0.7988`；反编译 `sts2.dll` 到 `_research/decompiled-v0.111.0/`（约 3538 个 `.cs`，不入库）。
2. 重写 `tools/export_sts2_data.py`：从 C# 源码静态解析出数据包（字段与 op 词汇见 [NOTE.md](NOTE.md) 的导出器陷阱一节）。
3. 导出 `data/sts2/*.json`，`manifest.json` 记 `schema_version`、`game.version/commit/main_assembly_hash`、`decompiled_at`、`exported_at`。
4. 与程序集计数逐项对账（603/126/283/299/66/98/8/5 减去 Mock 与不可玩角色），并验证两次导出字节一致。

**先把这一步的产物提交进 git。** 上一轮这些未提交的产物被整体删除后无法恢复，这是本项目已经付过一次的代价。

### M1 值 oracle

- `tools/Sts2.Oracle`：net10.0 控制台，**每个 probe 独立子进程**，反射加载出厂 `sts2.dll`。
- 模型注册：`AbstractModelSubtypes.All` + 逐个 `ModelDb.Inject`（1660 个，实测 0 失败、约 83 ms）。
- probe 清单：meta、cards、monsters、powers、relics、potions、encounters、characters、acts、rng。
- 产物 `tests/vectors/game_values.json`；对账脚本 `tools/compare_values.py` 要求零数值差异（结构差异单独登记）。
- 崩溃与读不到的项见 [NOTE.md](NOTE.md)。

### M2 真机 harness

- `harness/Sts2.ParityMod`，net9.0（游戏自带 .NET 9），DLL-only mod：`mods/sts2parity/mod_manifest.json` + `sts2parity.dll`，入口 `[ModInitializer]`，不需要 Godot 编辑器。mod 工程没有 Godot 源生成器，所以驱动类**不继承 `Node`**，`[ModInitializer]` 在主线程直接 `async` 跑，用 `SceneTree.ToSignal(ProcessFrame)` 逐帧等待。
- 输入 `scenario.json`：角色、HP、能量、牌组、遗物、药水、球位、遭遇与 slot、seed、进阶、动作列表。
- 执行：真机路径 `CardModel.TryManualPlay`（真实扣能量、走 `OnPlayWrapper`）与 `PlayerCmd.EndTurn`；`CardCmd.AutoPlay` 会跳过费用，只用于对照测试。每步写 `snapshots.jsonl`。
- 启动 `SlayTheSpire2.exe --headless`，不由 Steam 拉起；游戏根目录需要 `steam_appid.txt`（2868840），否则 Steamworks 初始化失败。
- `--nomods` 与"加载 parity mod"互斥：改为把 `steamapps/workshop/content/2868840` 移到 `2868840.disabled-by-parity`，让游戏只加载本 mod。
- 三条环境哨兵必须通过：日志含 `RUNNING MODDED! Loaded 1 mods`、`main_assembly_hash` 等于预期、数据包 manifest 版本一致；再加 `[parity] scenario complete`。
- `harness/run_scenario.py` 负责构建、安装、禁工坊、启动、哨兵校验，并把轨迹写到 `tests/vectors/real/`。

### M3 引擎纵切与 V1 TUI

先实现 `Data`、`Random`、`Model`、`Combat`、`Rules`、`Hooks`、`Effects`、`Diagnostics` 八个命名空间，只覆盖 Ironclad 的 5 张起始牌 + 2 个敌人招式路径。TUI 显示：双方 HP/格挡/能力、手牌与费用、抽牌堆/弃牌堆计数、意图、可选动作。

### M4–M8

对拍闭环 → 全内容 → HTML 回放 → 进阶与性能 → Web，逐级依赖前一级的判据；任一里程碑不达标不进入下一级。

## 7. 验证体系

| 层 | 内容 | 作用 |
|---|---|---|
| L1 | 单元测试：管线顺序、截断、关键字 | 快，能定位 |
| L2 | 值 oracle：`sts2.dll` 反射出的数值 | 校准数据包 |
| L3 | 不变量：HP ≤ max、格挡 ≥ 0、能力层数语义 | 发现内部矛盾 |
| L4 | 规则对拍：纯函数表驱动 + 变形测试 + 变异测试 | 覆盖分支 |
| L5 | 轨迹对拍：真机录制的逐动作快照 | 唯一的行为证据 |

L5 是最终判据；L1–L4 只是让 L5 的失败可定位。CI 里 L5 允许跳过（无游戏环境），失败信息必须指到具体动作序号与字段。

## 8. 可视化四层

| 版本 | 形态 | 依赖 |
|---|---|---|
| V1 | 终端 TUI 单步 | M3 |
| V2 | 对拍 diff 视图（模拟 vs 真机逐字段） | M4 |
| V3 | 单文件 HTML 回放（内嵌 `snapshots.jsonl`） | M6 |
| V4 | 本地 Web 人机对弈 | M8，按需 |

渲染器只读快照；预览类数据（伤害预览、可打出判定、意图显示）由引擎算进快照，避免第二个真相来源。

## 9. 风险

| 风险 | 影响 | 应对 |
|---|---|---|
| 游戏升级导致行为漂移 | 高 | 版本哨兵直接报错；升级后重做 M0 与 M2 |
| 数据包未提交即丢失 | 高 | M0 产物先提交再继续（已发生过一次） |
| 卡牌/能力语义静态解析不全 | 中 | `effect_complete=false` 显式暴露，按原文手写补充 |
| 真机环境受 mod 干扰 | 中 | `--nomods` + 三条哨兵；默认启动下工坊 mod 会加载 |
| 动画/await 时序引入随机消耗 | 中 | 走 `NonInteractiveMode`；对比时只信快照字段 |
| `decimal` 与游戏浮点细节不一致 | 中 | 统一截断封装 + 数值向量逐项比对 |

## 10. 假设

- 只用单人、A0–A9；多人缩放不在范围内。
- 真机轨迹采集允许在本机安装游戏与 .NET 9，CI 不跑 L5。
- 数据包以 C# 类名为 id，不依赖本地化文本。
- 快照 schema 只追加字段，破坏性变更升 `schemaVersion`。

已知实现陷阱单独记在 [NOTE.md](NOTE.md)，动手前先读一遍。

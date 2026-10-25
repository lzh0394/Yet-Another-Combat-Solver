# CombatSolver 深挖笔记（Lead 自研部分）

> 对象：https://github.com/Torch1230/CombatSolver ；本地快照 `_research/CombatSolver`（shallow clone，2026-10-24）。
> 版本状态：README 称已发布 **0.48.0**，但 `docs/releases/` 已到 **0.50.1**（说明 README 首页版本号有滞后）；适配游戏 **《杀戮尖塔 2》0.111.0**，依赖 RitsuLib、仅单人。
> 规模：922 个 `.cs`、492 个 `.md`、53.7 MB（含归档），C# / .NET 9 / Godot；MIT；模拟核心改造自同游戏的 **Random Foreseer**（hotwords123，MIT）。

## 1. 问题域特征决定了算法选择

这个项目最值得先说的不是"用了什么算法"，而是**它把问题变成了什么形状**：

| 维度 | 普通卡牌 AI（扑克/斗地主） | 本项目 |
|---|---|---|
| 隐藏信息 | 对手手牌不可见，需信念/CFR | 抽牌堆顺序、怪物意图、9 条 RNG 流都可从客户端内存读出（mod 权限）→ **退化为完全信息** |
| 随机性 | 需期望值或采样 | RNG 状态可捕获并前推，`CombatPredictionRngSet` 冻结 9 条流，洗牌用 `StableShuffleProjection` 精确投影 → **确定性转移** |
| 目标 | 最大化期望收益 | 给定预算内给出**一条可执行、可解释的路线**（逐回合：出牌/目标/选牌/药水/结束回合） |
| 资源 | GPU 集群 | 玩家一台 PC 的 1–16 线程，且**不能拖垮游戏帧率** |

结论：不需要 CFR/MCTS，需要的是**确定性状态图上的启发式束搜索 + 严格预算 + 可复用增量**。README 也明确写"使用受时间、节点和内存预算约束的 Beam Search…不承诺全局最优"。

## 2. 运行链路（四段式，预测与执行彻底分离）

1. **主线程捕获稳定根**：`CombatRootSnapshot.Capture`（`src/Runtime/CombatRootSnapshot.cs`）冻结身份、敌人、`IntentForecast`、药水/遗物/成长目标、`LiveCombatStamp`、`ContinuationStamp`。
2. **冻结政策**：设置 → `SearchPolicySnapshot`（时间/节点/内存/并行/药水策略），后台算法不读设置单例。
3. **后台分支搜索**：`CombatSearchCoordinator` → `CombatBeamSolver`，只在根派生出的影子状态上推进（含卡牌、牌堆、RNG、怪物 AI、药水、遗物、跨回合效果）。
4. **只读结果 + 原生部署**：`SolverResult` → `SolverOverlaySnapshot` → 覆盖层展示；执行时走**游戏原生公开入口**逐动作重放，`ContinuationStamp` 核对实机与预测，一致则复用路线，偏离则重新搜索。

工程硬约束（AGENTS.md + ARCHITECTURE.md）：后台**不得读会随实机变化的 live 值**，**不得修改真实战斗**；未知语义必须显式失败或形成明确搜索边界（`SearchBoundaryReason.UnsupportedEffect`），不允许吞异常后当成胜利。

## 3. 搜索算法：束搜索 + 多通道保留 + 宽度组合（这是最有价值的部分）

核心参数（`src/Search/SolverSearchProfile.cs`，默认 = "中"档）：

```
BeamWidth = 60            MaxExpandedNodes = 120_000
MaxCardBranchesPerNode = 32   MaxPileChoiceBranchesPerAction = 18
MaxHandChoiceBranchesPerAction = 24   SoftTimeBudgetMilliseconds = 120_000
```
四档预设（README）：低 60s/6 万节点，中 120s/12 万，高 180s/25 万，极高 300s/50 万；并行 1–16（默认 4，按逻辑核数下调），NoGC 区域预算默认 16 GB。

关键机制（都能在源码里找到对应实现）：

- **状态指纹去重**：`BuildStateKey` 组合回合、玩家/敌人、有序牌堆/手牌/弃牌、Power 计数、遗物/药水槽、9 条 RNG 状态、已处理敌人死亡集合；另有 `unorderedPileKey` 与 `cyclePileShapeKey` 专门做**循环检测**（卡组无限循环是这类游戏的真实风险）。
- **转置表 + 6 维 Pareto 支配**（`CombatBeamSolver.Transpositions.cs`）：标签 =（用药数、药水战略成本、未来卖血、累计掉血、动作数、得分），只保留互不支配的标签，单标签内联、多标签才分配 List（为 GC 优化）。
- **保留不是单一打分，而是显式通道**：防守、进攻、能力铺垫、资源、控制、药水状态、集火、复活窗口、破坏性选择等各有配额（`BeamRetentionPolicy`），再加"必保通道"防误杀；这是对纯 top-K 束搜索"贪心近视"的正面回应。
- **宽度组合（Beam Width Portfolio）**：用同一份预算跑多个成员——基线宽度、宽精炼、窄精炼、次段（第 W+1..2W 名）、只基础分、能力前缀变体——再按**最终政策**（整场战损、药水成本、成长、回合数）选最优；成员准入有成本门控（`BeamWidthPortfolioGate`），被拒成员不花预算。
- **预算账本 + 早停**：`SearchBudgetLedger` 统一记时间/节点/展开/转移；找到满足战损目标与成长/资源/必要用药条件的完整胜利可提前结束。
- **并行但确定性**：固定 worker lane、已准入作业、按原输入顺序提交（`ParallelExpansion` / `AdmittedExpansion`），保证单线程与多线程在同根同预算下结果一致（release notes 0.21.0 明确验证过）。
- **帧率保护**：`SearchFramePressureSignal` 采样主线程空闲帧间隔基线，压力超过基线 1.5 倍且 >33 ms 就让 worker 让出；NoGC 区域 + 内存检查点回收（`SearchGcPolicy`）保证长搜索不把游戏拖死。

## 4. 模拟器：不是"简化模型"，是逐条镜像原版结算

- `src/Engine/InCombat/Mirrors/` 按卡牌/怪物/遗物/药水/Power/球/Hook 逐项注册镜像方法；`MethodMirrorRegistryDescriptor` 把支持元数据暴露给覆盖目录；`src/Prediction/` 负责怪物 AI、隐藏状态、死亡/召唤、选牌、subscriber 捕获。
- **单一权威实现**约束："每项战斗语义只有一个权威结算实现"，防止镜像与补偿双重结算。
- **状态所有权**：可变值必须属于根快照 / 影子状态 / 克隆 Model / `PredictionStateStore` 之一；Fork 用统一 `PredictionForkContext` 重映射引用，COW 先取可写所有者。
- **保真度是可度量的**：`docs/COMBAT_HOOK_COVERAGE.md`（由工具生成）统计：**2305 个 Exact hook、730 个 OutOfScope**，其中 Card 1186 项（219 精确/152 推断/226 不支持）、Power 520 项；"主动 Exact 且带运行时差分证据" 的 hook 合计约 1500+。这解释了为什么它敢在后台做整场搜索。

## 5. 工程质量：这套流程比算法本身更难复制

- **严格差分测试**：同一动作在实机与模拟器分别执行，比较完整状态、顺序、引用、RNG 和续用合同（`src/Testing/`，按 Contracts/Regressions/Replay 分层）。
- **检查点回放**：玩家问题包 → `Preflight`（静态）/`RestoreOnly`（恢复对账）/`ReplayRecorded`/`SearchOnly`/`DeploySolver` 五级模式。
- **离线搜索宿主**：`tools/search/OfflineSearchHarness`（普通 .NET 9 控制台，不启 Godot，加载 `sts2.dll` 建局并调 `CombatSearchCoordinator.Solve`），用于几十到上百根的宽度/预算/保留策略对照；README 明确"只产指标，不做正确性验收"。
- **覆盖目录工具化**：`CoverageCatalog` 从注册表与证据 JSON 生成覆盖报告，文档不手改；`verify-refactor-boundaries.ps1/.sh` 守职责边界。
- **社区流水线**：issue #171 按批发布故障修复/路线优化任务，每批五主题、每主题 1–2 个 0.47.x 代表包；PR 验收要求"目标修复证据 + 固定代表哨兵"（同根同政策同预算下路线质量不退化、耗时无明显增加）。
- **性能有公开数字**：PR #106 在频繁卡牌变形的重战斗中提速约 3–4 倍；PR #207 代表战斗平均 2–3 倍、部分复杂场景 >30 倍（release notes 0.40.2 / 0.50.0）。

## 6. 明确的局限（作者自己写的边界）

- 不保证全局最优；视野无固定回合上限，但循环检测、状态合并、预算终止会限制实际范围。
- 只覆盖 0.111.0 单人战斗原版内容；多人、局外流程、第三方 Mod 战斗效果不在通用兼容范围（第三方需自行写适配层，`docs/third-party/README.md`）。
- 内存预算与 NoGC 是双刃剑：16 GB 区域请求、32 GB 只建议 48 GB 物理内存的机器。
- 前置预测 API（`PreCombatForecastApi` v6）通过**另起 headless 游戏子进程**还原跑局来算，首次请求要建隔离镜像，属于"重量级但精确"的取舍。

## 7. 一句话总结

CombatSolver 的本质是：**用 mod 权限把"不完全信息 + 随机"的卡牌战斗，转成"完全信息 + 确定性"的状态图搜索问题，然后用束搜索 + Pareto 去重 + 多通道保留 + 可调预算，在玩家 PC 的可接受卡顿内求解**。它的技术护城河不在搜索算法（束搜索很古老），而在**模拟器保真度 + 差分测试 + 预算/内存/帧率工程**。

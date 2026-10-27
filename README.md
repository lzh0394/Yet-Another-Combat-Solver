# 卡牌战斗求解器与棋类引擎调研报告

> 调研时间：2026-10-24 ｜ 证据：`Torch1230/CombatSolver` 上游快照（commit `5c773ca`，核对后已删除本地克隆）+ 各项目 README/源码直连抓取（`_research/raw/`）
> 阅读指引：§1 = 目标仓库 CombatSolver 的解剖；§2 = 其他卡牌游戏的求解器与模拟器；§3 = 国际象棋与围棋引擎；§4 = 如果要自建同类求解器的落地方案。
> 说明：本会话的 `web_search`/`web_fetch` 工具因 API Key 失效（HTTP 401）不可用，全部资料经 `Invoke-WebRequest` 直连 `raw.githubusercontent.com` / `github.com` / `en.wikipedia.org` 抓取，逐条可复核。

---

## 0. 摘要

1. **CombatSolver 的本质不是"搜索算法强"，而是"把问题变简单"**：它借 mod 权限读取抽牌堆顺序、怪物意图和 9 条战斗 RNG 流，把《杀戮尖塔 2》的战斗从"不完全信息 + 随机"转成"完全信息 + 确定性"的状态图，然后用**预算化束搜索（Beam Search）**求解。README 自己写明"不承诺数学意义上的全局最优"。
2. **它的护城河是模拟器保真度与工程纪律**：2305 个判定为 Exact 的战斗 hook（另有 730 个明确排除）、实机/模拟严格差分、检查点回放、离线搜索宿主、覆盖率自动生成、社区任务批次、NoGC 内存区域与帧率保护。搜索算法（束搜索 + Pareto 去重 + 多通道保留）是 1970 年代的旧技术，难复制的是"敢在后台跑整场"这件事。
3. **其他卡牌项目的三条技术线**：① 确定性搜索/模拟器线（StS2 生态的 spire-mentor、sts2-rl-agent、sts_lightspeed，与 CombatSolver 同构）；② CFR/不完全信息线（扑克：Libratus、DeepStack、ReBeL、TexasSolver、OpenSpiel）；③ 自对弈 RL 线（斗地主 DouZero、麻将 Suphx/Mortal、TCG 的 MirrorForce）。**与 CombatSolver 同类问题最相关的是第一条**，后两条的成本与形态都不匹配"单机实时给建议"。同机横评（§2.5）：单核 `actions/s` 为 sts2core 2.03 M > spire-mentor 1.30 M > sansb 0.383 M，而游戏本体饱和后只有 871 actions/s。
4. **棋类引擎的可迁移部分是工程范式，不是算法**：Stockfish 的 alpha-beta+NNUE、KataGo 的 MCTS+神经网络都依赖完全信息与大规模离线训练；能直接搬的是**预算/时间管理、并行提交确定性、状态哈希去重、评估函数特征工程、SPRT 评测、自对弈冷启动**。alpha-beta 的交替零和假设、MCTS 的"状态可观测"假设在卡牌战斗里都不成立。
5. **如果要做一个新的 Combat Solver**：推荐"读真实根状态 + 自研确定性模拟器 + 束搜索（宽度 60–200）+ 多维 Pareto 去重 + 预算账本 + 严格差分对拍"；隐藏信息只有牌序/随机数时加 **PIMC/determinization** 即可，只有出现"对手私有信息且会针对你"时才考虑小范围 CFR；不建议在战斗循环里放 LLM 或大网络。

---

## 1. CombatSolver 解剖

### 1.1 项目概况

| 项 | 值 |
|---|---|
| 仓库 | [Torch1230/CombatSolver](https://github.com/Torch1230/CombatSolver) |
| 定位 | 《杀戮尖塔 2》单人战斗路线求解器（mod），后台搜索 + 覆盖层建议 + 可代打 |
| 技术栈 | C# / .NET 9 / Godot（Harmony patch 注入），Windows + Linux |
| 许可 | MIT；战斗模拟核心改造自同游戏的 [Random Foreseer](https://github.com/hotwords123/StS2.RandomForeseer)（MIT） |
| 版本 | README 首页写 0.48.0，但 `docs/releases/` 已到 **0.50.1**（首页版本号滞后）；适配游戏 0.111.0 |
| 规模 | 922 个 `.cs`、492 个 `.md`，仓库 53.7 MB（含 `docs/archive` 历史卷） |
| 依赖 | RitsuLib 0.6.0+，仅单人 |

### 1.2 问题建模：为什么是束搜索而不是 CFR/MCTS

这是整份报告最关键的判断，值得单独说清楚：

| 维度 | 扑克/斗地主 | 围棋/象棋 | CombatSolver 面对的战斗 |
|---|---|---|---|
| 隐藏信息 | 对手手牌不可见 | 无 | **有，但 mod 能读**（抽牌堆顺序、敌人意图、RNG） |
| 随机性 | 发牌随机 | 无 | 有，但 RNG 状态可捕获并前推（9 条流） |
| 对手 | 会针对你的策略 | 完全信息博弈 | 怪物 AI 是确定性状态机（`IntentForecaster` 可前推数回合） |
| 目标 | 纳什均衡/期望收益 | 胜率 | 给定预算内一条**可执行、可解释**的路线 |
| 资源 | GPU 集群 | 单机强 CPU/GPU | 玩家 PC 的 1–16 线程，且**不能掉帧** |

所以在 CombatSolver 的世界里：**搜索树是确定性的、状态可完整哈希、动作空间有限、深度有限（一场战斗十几回合）**。这是一个典型的"用启发式评估 + 宽度剪枝"的场景，不需要信念状态，也不需要价值网络。README 的原文是"使用受时间、节点和内存预算约束的 Beam Search"，这句话就是全部算法定位。

### 1.3 四段式运行链路（预测与执行彻底分离）

```
主线程捕获稳定根 → 冻结搜索政策 → 后台分支搜索 → 只读路线快照 → 原生入口逐动作部署
        ↑                                                          ↓
        └──────────── ContinuationStamp 核对，一致则复用，偏离则重搜 ─────────┘
```

1. **捕获根**：`src/Runtime/CombatRootSnapshot.cs` 冻结玩家身份、敌人、`IntentForecast`（未来数回合怪物行动）、药水/遗物/成长目标、`LiveCombatStamp` 与 `ContinuationStamp`。
2. **冻结政策**：设置 → `SearchPolicySnapshot`（时间/节点/内存/并行/药水策略）。后台算法**不读设置单例**，只消费注入的不可变快照。
3. **后台搜索**：`CombatSearchCoordinator` → `CombatBeamSolver`，只在根派生的影子状态上推进；后台**不得读 live 值、不得改真实战斗**。
4. **部署**：`SolverResult` → `SolverOverlaySnapshot`（只读投影）→ 覆盖层；执行时走游戏原生公开入口重放动作，用续用戳核对实机与预测。

这套"预测/执行分离 + 戳校验"的设计，是它能安全做全自动代打的原因，也是最值得抄的架构决策之一。

### 1.4 搜索内核

**默认档参数**（`src/Search/SolverSearchProfile.cs`）：

| 参数 | 默认值 | 含义 |
|---|---:|---|
| `BeamWidth` | 60 | 每层保留节点数 |
| `MaxExpandedNodes` | 120,000 | 总展开节点上限 |
| `MaxCardBranchesPerNode` | 32 | 单节点出牌分支上限 |
| `MaxPileChoiceBranchesPerAction` | 18 | 牌堆选牌分支 |
| `MaxHandChoiceBranchesPerAction` | 24 | 手牌选牌分支 |
| `SoftTimeBudgetMilliseconds` | 120,000 | 软时间预算 |

四档预设（README）：低 60s/6 万节点、中 120s/12 万、高 180s/25 万、极高 300s/50 万；并行 1–16（默认 4，按逻辑核下调），NoGC 区域预算默认 16 GB。

**六个关键机制**（都能在源码里找到实现）：

1. **状态指纹去重**：`BuildStateKey` 组合回合、玩家/敌人、有序牌堆/手牌/弃牌、Power 计数、遗物/药水槽、9 条 RNG 状态、已处理敌人死亡集合；另有 `unorderedPileKey`、`cyclePileShapeKey` 专门做**循环检测**（无限连是这类游戏的真实风险）。
2. **转置表 + 6 维 Pareto 支配**（`CombatBeamSolver.Transpositions.cs`）：标签 =（用药数、药水战略成本、未来卖血、累计掉血、动作数、得分），只保留互不支配者；单标签内联存储，多标签才分配 List（GC 优化）。
3. **多通道保留而非单一打分**：防守、进攻、能力铺垫、资源、控制、药水状态、集火、复活窗口、破坏性选择各设配额，另有"必保通道"。这是对纯 top-K 束搜索"贪心近视"的正面回应——**用结构化的多样性代替更大的宽度**。
4. **宽度组合（Beam Width Portfolio）**：同一预算内跑多个成员（基线宽度、宽/窄精炼、次段第 W+1..2W 名、只基础分、能力前缀变体），再按**最终政策**（整场战损 → 药水成本 → 成长 → 回合数）选最优；成员准入有成本门控，被拒成员不花预算。
5. **预算账本 + 早停**：`SearchBudgetLedger` 统一记时间/节点/展开/转移；找到满足战损目标与成长/资源/必要用药条件的完整胜利可提前结束。
6. **并行确定性 + 帧率保护**：固定 worker lane、按原输入顺序提交，保证单/多线程同根同预算结果一致；`SearchFramePressureSignal` 采样主线程空闲帧间隔，压力超过基线 1.5 倍且 >33ms 就让 worker 让出。

### 1.5 模拟器与保真度

- **不是简化模型，是逐条镜像原版结算**：`src/Engine/InCombat/Mirrors/` 按卡牌/怪物/遗物/药水/Power/球/Hook 注册镜像方法；`src/Prediction/` 负责怪物 AI、隐藏状态、死亡/召唤、选牌、subscriber 捕获。硬约束是"每项战斗语义只有一个权威结算实现"，防止镜像与补偿双重结算。
- **状态所有权严格**：可变值必须属于根快照 / 影子状态 / 克隆 Model / `PredictionStateStore`；Fork 用统一 `PredictionForkContext` 重映射引用，COW 先取可写所有者。
- **保真度可度量**：`docs/COMBAT_HOOK_COVERAGE.md`（工具生成）显示 **2305 个 Exact hook、730 个 OutOfScope**；其中 Card 1186 项（219 精确/152 推断/226 不支持）、Power 520 项、Relic 508 项。
- **未知即失败**：遇到未支持的语义，形成明确搜索边界（`SearchBoundaryReason.UnsupportedEffect`），不会把未完成模拟当成胜利——这条纪律比多写几百个镜像更重要。

### 1.6 工程质量（比算法更难复制）

| 实践 | 实现 |
|---|---|
| 严格差分测试 | 同一动作在实机与模拟器分别执行，比较完整状态、顺序、引用、RNG 与续用合同（`src/Testing/`，按 Contracts/Regressions/Replay 分层） |
| 检查点回放 | 玩家问题包五级模式：`Preflight` / `RestoreOnly` / `ReplayRecorded` / `SearchOnly` / `DeploySolver` |
| 离线搜索宿主 | `tools/search/OfflineSearchHarness`：普通 .NET 9 控制台，不启 Godot，加载 `sts2.dll` 建局后直接调求解器；明确"只产指标，不做正确性验收" |
| 覆盖目录工具化 | `CoverageCatalog` 从注册表与证据 JSON 生成覆盖报告，文档不手改；`verify-refactor-boundaries.ps1/.sh` 守职责边界 |
| 社区流水线 | issue #171 按批发布任务，每批五主题、每主题 1–2 个代表包；PR 验收要求"修复证据 + 固定代表哨兵"（同根同政策同预算下质量不退化、耗时无明显增加） |
| 性能公开数字 | PR #106 在频繁卡牌变形的重战斗中提速约 3–4 倍；PR #207 代表战斗平均 2–3 倍、部分复杂场景 >30 倍 |

### 1.7 局限（作者自述）

- 不保证全局最优；视野无固定回合上限，但循环检测、状态合并、预算终止限制实际范围。
- 只覆盖 0.111.0 单人原版内容；多人、局外流程、第三方 Mod 效果不在通用兼容范围（第三方需自写适配层）。
- 内存/NoGC 是双刃剑：16 GB 区域请求，32 GB 只建议 48 GB 物理内存的机器。
- 战前预测 API（`PreCombatForecastApi` v6）通过**另起 headless 游戏子进程**还原跑局，首次请求要建隔离镜像——"精确但重量级"的取舍。

---

## 2. 其他卡牌的求解器 + 模拟器

按"与 CombatSolver 的相似度"分三层：**同游戏/同类型 → 同数学结构（不完全信息） → 同工程问题（规则引擎/训练）**。

### 2.1 第一层：StS / StS2 生态（最直接对标）

| 项目 | 类型 | 核心做法 | 关键数字 | 可借鉴 / 局限 |
|---|---|---|---|---|
| [spire-mentor](https://github.com/tfriedel/spire-mentor) | Rust 模拟器 + AI | 全角色全卡 Rust 移植；逐动作对拍 + fuzz；MCTS 战斗 + PPO 整局 | 约 **1500×** 快于游戏自身无头战斗（单核 ~60k combats/s）；Ironclad parity 100%，其他角色 99.4–99.8%；Ironclad A0 90%、A9 39%、A10 11% | **lockstep 对拍 + 5000 seed fuzz** 是保真度标杆；明确 **non-clairvoyant**（用 chance node，不偷看 RNG）——与 CombatSolver 的"全知"路线形成对照 |
| [sts2-rl-agent](https://github.com/zhiyue/sts2-rl-agent) | Python 模拟器 + RL | 无头引擎 + 内容表（577 卡/260 Power/121 怪/290 遗物/63 药水）+ Gym 环境 + C# 桥 | ~5 万行、133 源文件、408 测试；~1,200 combats/s、~28k steps/s；Act1 Ironclad 战斗胜率 ~92% | **引擎/内容表/环境三层分离**，新卡池按表接入 |
| [sts_lightspeed](https://github.com/gamerpuppy/sts_lightspeed) | C++ 模拟器/搜索 | 追求 100% RNG 一致；已知 RNG 下做树搜索 | 100 万随机 playout / 5s / 16 线程 | 吞吐口径参考；只实现 Ironclad + 无色卡 |
| [sts-ironclad-agent](https://github.com/destire-mio/sts-ironclad-agent) | 整局 agent | 基于 lightspeed，局外冻结网络 + 战斗搜索 | 真实游戏 501 局 Heart 胜率 **50.1%**（CI 45.7–54.5） | 反面教训：自认 parity 不完整仍报胜率——**回放通过 ≠ 模拟正确** |
| [Random Foreseer](https://github.com/hotwords123/StS2.RandomForeseer) | RNG 预测 Mod | 不推进真实 RNG 就预演后续随机结果（变牌/事件/抽牌/洗牌/球/目标） | v0.13.19，MIT | CombatSolver 的模拟核心来源；"不改状态的前瞻"这一定位 |
| [ClawTheSpire](https://github.com/dbyrne/ClawTheSpire) | 整局 bot | 战斗 DFS 穷举打分 + 局外本地 LLM | 小项目 | **搜索负责算术、LLM 只负责开放权衡** |
| [bottled_ai](https://github.com/xaved88/bottled_ai) | 整局 bot（StS1） | 手写优先级 + 图遍历模拟出牌顺序，约 40 个加权值 | Watcher 52% 胜率，80 star | 不训练也能用；但策略手工维护、换版本成本高 |
| [BalatroBot](https://github.com/coder/balatrobot) | 游戏 API Mod | Lua Mod 暴露完整状态与控制（JSON-RPC/HTTP） | 79 star | "外部进程当大脑、Mod 只当 IO" |

**共性结论**：StS2 这一代已经出现"高性能重实现 + 游戏内桥"的标准组合，且都把**吞吐**当一等指标写在 README 首屏；两个最强项目都是"读一次真实状态，然后在自己引擎里 Fork"，没有靠游戏内部接口做反事实推演。

### 2.2 第二层：不完全信息博弈（扑克 / CFR）

这一层回答的是"如果新游戏的隐藏信息不能读，怎么办"。

| 项目 | 类型 | 核心算法 | 预算量级 | 对我们的意义 |
|---|---|---|---|---|
| [OpenSpiel](https://github.com/google-deepmind/open_spiel) | 算法/框架 | CFR、CFR+、DCFR、MCCFR、PIMC、IS-MCTS、Deep CFR | 无预算参数，小 infoset 充分测试 | 最好的"算法试验台"；`Game/State/chance/infoset` 四件套值得抄接口 |
| [TexasSolver](https://github.com/bupticybee/TexasSolver) | 开源求解器 | CFR 家族迭代，树构建 DSL + exploitability 日志 + JSON 导出 | flop/SPR=10/6 线程：**172s 收敛到 0.275%** exploitability（PioSOLVER 1.0 同配置 242s/0.29%） | "预算—收敛度—可读输出"的产品形态与路线快照最接近 |
| [PioSOLVER](https://github.com/bupticybee/TexasSolver/blob/master/benchmark/benchmark_piosolver.txt) | 商业求解器 | CFR 类 | 6 线程/492MB/242s/0.29% | 输入即"树构建配置"：用下注尺度集合把树压到可解规模 |
| [DeepStack](https://en.wikipedia.org/wiki/DeepStack) | 超人类 AI | 深度有限搜索 + 价值网络叶子 + continual resolving | 训练算力未公开 | "用网络替代深搜尾部"的范式 |
| [Libratus](https://en.wikipedia.org/wiki/Libratus) | 超人类 AI | CFR+ + endgame solving | 蓝图 >15M 核心小时，比赛期每晚另 4M | "离线蓝图 + 在线子博弈重解"的分层结构 |
| [Pluribus](https://en.wikipedia.org/wiki/Pluribus_(poker_bot)) | 超人类 AI（6 人） | 离线自博弈 + 在线继续学习 | 基础策略 8 天、约 $144 | 多人场景可经验性奏效，但无均衡保证 |
| [ReBeL](https://github.com/facebookresearch/ReBeL) | 递归信念搜索 | 公共信念状态 + 价值网络 + 子博弈 CFR | 多 GPU 自博弈；`mdp_depth 2`、`subgame_iters 1024` | 若真要做"隐藏手牌战斗"，这是最接近的形态 |
| [PokerKit](https://github.com/uoftcprg/pokerkit) | 模拟器 | 纯 Python 规则/评估，99% 覆盖率 | 无求解器 | 自定义变体成本低的 API 设计 |
| [Pgx](https://github.com/sotetsuk/pgx) | JAX 模拟器 | `jit` + `vmap`，随机性显式 `key` | 一次并行 1024 局 | 把随机性显式传入，避免规划期信息泄漏 |

**方法论对照（关键）**：

| 维度 | PIMC / determinization | CFR 家族 | 确定性完全信息搜索（CombatSolver） |
|---|---|---|---|
| 核心假设 | 隐藏信息可由一个与观察一致的"世界"代表 | 对手有私有信息且会针对你；求近似均衡 | 状态全可见、随机可前推 |
| 做法 | 采样 K 个世界，各跑一次完全信息搜索，加权汇总 | 迭代更新 infoset 后悔值 | 束搜索 + 预算剪枝 |
| 算力 | K × 单次搜索（线性可调） | 表格式指数爆炸；子博弈式需抽象 + 每步重解 | 单次搜索，预算最好控 |
| 可解释性 | 中（可给"最坏世界下的路线"） | 低（输出概率策略，不是一条路线） | 高（一条具体路线 + 战损） |
| 主要风险 | 策略融合（同一动作在不同世界的最优理由不同） | 抽象带来可利用性；多人无保证 | 对隐藏信息"假装看得见"，路线偏乐观 |

### 2.3 第三层：斗地主/麻将/TCG（RL 与规则引擎）

| 项目 | 类型 | 核心做法 | 关键数字 | 借鉴点 |
|---|---|---|---|---|
| [DouZero](https://github.com/kwai/DouZero) | 训练框架 | Deep Monte-Carlo + 动作编码 + 并行 actor | 4 GPU、数天；Botzone 344 AI 第一 | 动态合法动作集 + mask，而非固定动作维 |
| [RLCard](https://github.com/datamllab/rlcard) | 环境集合 | `env.make()/set_agents()/run()`，统一 `state_shape/action_shape` | 无限注德州 infoset ~10^162、斗地主 10^53~10^83、麻将 10^121 | 算法与游戏解耦的接口设计 |
| [Mortal](https://github.com/Equim-chan/Mortal) | 训练/推理 | 立直麻将深度 RL，Rust | AGPL-3.0，代码全开、权重另发 | "权重不进仓库"的发布纪律 |
| [kanachan](https://github.com/Cryolite/kanachan) | 数据 + 训练 | 牌谱标注 + 纯 token 特征 + curriculum | 天凤凤凰卓 11 年 1,700 万局 vs 雀魂 2021-08 约 6,500 万局 | 数据规模论证方式 |
| [Forge](https://github.com/Card-Forge/forge) / [XMage](https://github.com/magefree/mage) | MTG 规则引擎 + AI | Java，卡牌以代码实现，AI 与规则同栈 | 社区长期维护 | 卡牌 = 数据 + 类/脚本的注册表分层 |
| [SabberStone](https://github.com/HearthSim/SabberStone) | 炉石模拟器 | C#；"洋葱层"处理光环/附魔/触发；`SabberStoneBasicAI` 独立成层 | 每张卡自动生成单测 | **每卡单测自动生成** + AI 在引擎之上单独一层 |
| [hearthstone-ai](https://github.com/peter1591/hearthstone-ai) | 模拟器 + MCTS | header-only C++17；多观察者 MCTS 处理隐藏信息 | 334 star | 多观察者 MCTS = PIMC 的一种工程实现 |
| [EDOPro](https://github.com/edo9300/edopro) / [WindBot](https://github.com/IceYGO/windbot) | 游戏王引擎 + bot | `ocgcore` 卡效果写成 Lua 脚本；bot 走服务器协议接入 | 社区活跃 | 引擎无 AI，bot 在外部——等价于 CombatSolver 的"外部决策 + 原生执行" |
| [MirrorForce](https://github.com/tommy85/MirrorForce) | 自对弈 agent | 复用 YGOPro 引擎，28M 参数 transformer | 自对弈约 3 天 / 32×H20 | 大算力路线的成本参照 |

### 2.4 模拟器保真度：三条路线对照

| 方案 | 代表 | 成本 | 风险 |
|---|---|---|---|
| 官方/Mod API 读真实状态 | CommunicationMod、BalatroBot、CombatSolver | 低 | ① 协议**泄露隐藏信息**（`draw_pile` 顺序、敌人意图），不裁剪就是作弊；② 拿不到反事实；③ 动作重放有帧延迟/竞态 |
| 规则重实现 | spire-mentor、sts2-rl-agent、lightspeed、Forge、ocgcore | 高（5 万行级/577 卡） | 语义漂移；靠 parity 测试收敛 |
| 录像/检查点回放 | kanachan、CombatSolver 的检查点回放 | 最低 | 只能覆盖已发生轨迹，不能评估被剪掉的候选 |

**CombatSolver 的特殊位置**：它既不是纯 API 读取（不经 JSON 序列化，同进程读真实 `Model` 再克隆影子状态），也不是纯离线重写（镜像直接调原版方法链）。好处是没有协议损耗、能拿到最细的领域对象；代价是**缺少外部真值**——游戏自身是唯一裁判，一旦镜像分叉没有第二份实现对拍。spire-mentor 的"headless C# harness 跑真实游戏代码当对拍基准"正好补上这一点。

---

### 2.5 StS2 模拟器 benchmark 横向对比（专项）

> 主要依据：[spire-mentor 官方基准页](https://tfriedel.github.io/spire-mentor/benchmarks.html) 与其[完整测试记录 `sts2-rs/docs/perf/2026-10-14-benchmarks.md`](https://github.com/tfriedel/spire-mentor/blob/main/sts2-rs/docs/perf/2026-10-14-benchmarks.md)（2026-10-14，同一台机器、同一套 harness、同一 workload，测了三个引擎 + 游戏本体）。这是目前唯一一次**同机同口径**的 StS2 模拟器横评，其余项目的数字都是各自机器上的自报值。

#### 2.5.1 目前公开的 StS2 模拟器

| 项目 | 语言 | 范围 | 游戏版本 | 备注 |
|---|---|---|---|---|
| [spire-mentor](https://github.com/tfriedel/spire-mentor) | Rust | **整局**（地图/事件/商店/奖励/三幕） | v0.107.1 | 5 角色；有 MCTS + PPO AI；WASM 工作台 |
| [sansb/sts2-sim](https://github.com/sansb/sts2-sim) | Rust | 仅战斗 | **v0.111.0**（三者最新） | 用真实游戏写出的 checksum 认证对局；自述 ~300k t/c/s 典型、峰值 ~1M |
| [lain-wood/sts2core](https://github.com/lain-wood/sts2core) | Rust | 仅战斗（+ 求解器 + 构筑顾问） | v0.107.1 | 零依赖；3,880 字节 POD 状态、按值传递；实质只覆盖 Ironclad |
| [iRyougi/sts2-sim](https://github.com/iRyougi/sts2-sim) | C# / .NET 9 | **整局** | **v0.111.0** | 照游戏代码逐方法移植；RNG 顺序模式与客户端逐位一致；`CombatState.Clone()` / `Rng.CloneExact` 专为搜索设计；与 CombatSolver 同栈（作者也是 CombatSolver 反馈后台维护者） |
| [sts2-rl-agent](https://github.com/zhiyue/sts2-rl-agent) | Python | 战斗 + 整局环境 | —— | 自报 ~1,200 combats/s、~28k steps/s（硬件未说明） |
| [Spirebird](https://spirebird.com/sandbox.html) | 闭源 | 浏览器沙盒 | —— | 无源码、无基准 |
| CombatSolver | C#（游戏内嵌） | 战斗（镜像原版结算） | v0.111.0 | 不是独立模拟器；不对外发布吞吐数字 |

#### 2.5.2 基准口径（为什么这些数字可比）

- **机器**：单路 AMD EPYC 7443P（24 核 / 48 线程，Zen 3），256 GiB DDR4-3200，Ubuntu 24.04，Rust 1.96（fat LTO）、.NET 9.0.15 默认配置。
- **单核**：一个单线程进程绑 CPU 20，SMT 兄弟 CPU 44 空闲；**整机**：24 或 48 个进程同时释放。
- **workload W1**：Ironclad A0、10 张初始牌、80/80 HP + Burning Blood，对 22 个第一幕遭遇轮转，均匀随机合法动作打到战斗结束；每次 110,000 场（2,200 seed × 50），约 22 动作/场。
- **动作 = 一次状态转移**（出一张牌或结束回合）。**fights/s 不能跨引擎比**，因为三个引擎的合法动作合并策略不同（会改变随机玩家和分支因子）；统一用 `actions/s`，并额外提供 "instance player"（每张手牌每个目标一个动作）来对齐。
- 每个 harness 都打印 digest 做**同工作量校验**（111 项校验全过），比值只在不同引擎做了同样工作时才成立。

#### 2.5.3 主结果：随机对局、单核（W1）

| 引擎（随机玩家） | fights/s | actions/s | ns/动作 | 胜率 | 动作/场 |
|---|---:|---:|---:|---:|---:|
| **sts2core**（instance player） | 90,200 | **2.03 M** | **493** | 35.0% | 22.47 |
| sts2core（自带合并动作） | 106,000 | 2.06 M | 485 | 21.0% | 19.41 |
| **spire-mentor**（Burning Blood 生效） | 59,000 | **1.30 M** | **770** | 32.4% | 21.98 |
| spire-mentor（无遗物） | 54,700 | 1.20 M | 831 | 32.4% | 21.98 |
| **sansb/sts2-sim**（instance player） | 17,500 | **383,000** | **2,610** | 32.5% | 21.95 |
| sansb（自带合并动作） | 20,300 | 386,000 | 2,590 | 20.3% | 19.01 |

**单核排序：sts2core ≈ 1.56× spire-mentor ≈ 3.4× sansb**（spire-mentor ÷ sansb = 3.13× 无遗物 / 3.39× 带遗物；spire-mentor ÷ sts2core = 0.640）。

**整机扩展（actions/s，instance player）**：

| 预设 | spire-mentor | sansb | sts2core |
|---|---:|---:|---:|
| 单核 1 进程 | 1.32 M | 388,000 | 2.01 M |
| 单核双线程 2 进程 | 1.42 M | 341,000 | 2.20 M |
| 1 个 L3 域 6 核 | 7.84 M | 2.29 M | 11.6 M |
| 24 核 24 进程 | 30.2 M | 8.79 M | 44.7 M |
| 48 线程 48 进程 | **31.0 M** | 7.52 M | **44.7 M** |

整机最优对比：spire-mentor ÷ sansb = **3.52×**；spire-mentor ÷ sts2core = 0.692（**sts2core 快 1.44×**）。注意 SMT 收益：spire-mentor +3%、sts2core 0%、sansb **−14.5%**。

#### 2.5.4 交叉验证：用 sansb 自己的 fixture（W2）

W2 = sansb 的 `ironclad_toadpoles_uniform_v1`（68/80 HP、无遗物、两个 Toadpole，240,000 次随机 playout，每步克隆状态）。三方都用"每步拷贝状态"的方式跑：

| 引擎 / 模式 | actions/s | playouts/s | 胜率 | 动作/playout |
|---|---:|---:|---:|---:|
| spire-mentor（每步拷贝，对齐 sansb） | 658,000 | 35,300 | 88.7% | 18.7 |
| spire-mentor（原地修改，自用方式） | 1.19 M | 63,800 | 88.7% | 18.7 |
| sansb（instance player） | 404,000 | 21,000 | 88.2% | 19.3 |
| sansb（自报 bench 口径） | 414,000 | 24,300 | – | 17.0 |
| sts2core（instance player） | 1.95 M | 106,000 | 89.3% | 18.5 |
| sts2core（自带合并动作） | 1.95 M | 114,000 | 61.1% | 17.2 |

结论：在 sansb 的主场上，spire-mentor 仍快 1.63×（同为每步拷贝）；sts2core 则是 spire-mentor 的约 3×（拷贝模式）。**"谁最快"依赖状态拷贝策略**——spire-mentor 原地修改，比每步拷贝快约 1.8×。

**作者自报 vs 同机复测**（说明为什么不能直接引 README）：

| 自报基准 | 作者机器 | 同机复测（EPYC CPU 20） |
|---|---|---|
| sansb `sts-sim bench` | 1,049,000 transitions/s（Apple M5 Max） | 414,000 actions/s |
| sansb `eval_throughput` | 中位 302,000 transitions/s | 中位 120,000（10–90 分位 66,700–338,000） |
| sts2core `bench` | 3.56–3.61 M steps/s 单线程、54.9–56.5 M 32 线程（Windows，作者自称机器噪声大） | 2.68 M 单线程、44.4 M 24 核、55.3 M 48 线程 |

#### 2.5.5 与游戏本体比（spire-mentor vs `sts2.dll` headless）

游戏本体把 `sts2.dll` IL-patch 后在 .NET 9 上无窗口运行（Godot stub），Python driver 用 JSON over pipe 驱动，随机策略与 Rust 侧一致（8,448 场统计：胜率 32.2% vs 32.4%，21.98 动作/场）。

| 比法 | 游戏本体 | 引擎 | 倍数 |
|---|---:|---:|---:|
| 单核最好（16 个游戏进程共享 1 核） | 871 actions/s | 1.30 M（1 线程） | **1,500×** |
| 单核双线程 | 871 | 1.42 M | 1,630× |
| 整机最好（192 个游戏进程） | 12,700 actions/s | 31.0 M（48 进程） | **2,430×** |
| 每动作 CPU（整个进程） | 2.28 ms | 766 ns | 2,970× |
| 每动作 CPU（游戏线程） | 1.73 ms | 766 ns | 2,250× |
| 单进程 wall（热进程） | 10.4 ms | 766 ns | 13,500× |
| 单进程 wall（每场冷启动，**已弃用口径**） | 17.9 ms | 766 ns | 23,400× |

关键洞察：**游戏本体其实"大部分时间在睡"**——每个动作后 harness 用 `Thread.Sleep(1)` 轮询动作队列排空，一个热进程 10.4 ms wall 里只有 2.28 ms 在 CPU 上（22%）。所以"23,400×"这种冷启动口径是错的（把 JIT 冷启动 7.8 ms + 轮询睡眠 7.8 ms 都算进去了）；公平口径是 CPU 时间 2,250–2,970× 或饱和进程数下的 1,500×/2,430×。

#### 2.5.6 速度必须和保真度一起看

| 维度 | spire-mentor | sansb/sts2-sim | sts2core | iRyougi/sts2-sim |
|---|---|---|---|---|
| 游戏版本 | v0.107.1 | **v0.111.0** | v0.107.1 | **v0.111.0** |
| 范围 | 整局 | 仅战斗 | 仅战斗 | 整局 |
| 角色覆盖 | 5 | 5 | 实质 Ironclad | 5 |
| 内容量 | 580 卡 id / 297 遗物 / 121 怪 / 69 事件 | 596 卡 / 299 遗物 / 105 怪 / 88 遭遇 | 117 卡 / 105 敌（Boss Queen 缺） | v0.111.0 全部卡与药水、四幕遭遇与事件 |
| RNG 保真 | 复现游戏 15 条流 | 复现 9 条战斗流 | **自研 RNG**（分布一致、种子不一致） | 顺序模式与客户端逐位一致 |
| 验证方式 | lockstep + 5,000 seed fuzz；Ironclad 6,104/6,104；其他角色 99.4–99.8% | 真实游戏写出的 checksum：1,196/1,206 场回放完全一致 | 79 段实录：1,565 匹配 / 1 不符；敌人下一动作 85% | 实录回放逐项比对，仍有已知保真差异（issue 标签） |
| 搜索是否偷看 | 否（部署版隐藏抽牌顺序） | 是（回看求解器按设计看真状态） | 部分（用真实抽牌堆顺序） | 提供 `CloneExact`/`CloneReseeded` 两种语义 |
| 许可 | MIT | MIT | MIT | MIT |

**一句话**：sts2core 最快但覆盖最窄（实质 Ironclad、自研 RNG）；spire-mentor 是唯一"整局 + 5 角色 + 可训练"且经过大规模 lockstep 验证的；sansb 的独特价值是**最新 build + 真实游戏 checksum 认证**；iRyougi 的 C# 版本与 CombatSolver 同栈、同版本、RNG 逐位一致，但没有同机基准（sansb README 称其"10× slower as of 2026-10-04"，未核实）。

#### 2.5.7 怎么选 / 对 CombatSolver 的启示

| 你的目标 | 选择 |
|---|---|
| 纯搜索/训练要极致吞吐，接受窄覆盖 | sts2core（Rust，493 ns/动作） |
| 整局 RL、需要 5 角色与全流程 | spire-mentor（Rust，1.30 M 动作/s）或 iRyougi（C#，同版本） |
| 要认证"模拟 = 真游戏"、跟最新 build | sansb/sts2-sim（checksum 认证） |
| 与 CombatSolver 同栈、要 `Clone`/RNG 逐位一致 | iRyougi/sts2-sim |
| Python 快速原型 | sts2-rl-agent（~1.3k 战斗/s，比 Rust 慢 1–2 个数量级） |

**对 CombatSolver 的启示**：它没有公开 simulator throughput，UI 里的"世界线/s"其实是**展开节点/秒**（`SolverOverlaySnapshot` 用 `result.TotalExpandedNodes` 算），与 `transitions/s` 不同量纲，不能与上表直接比。若要对外证明性能，建议补两个口径：① 每秒状态转移数（`transitions/s`，含 fork/snapshot/fingerprint 分解）；② 固定工作量 ABBA 基准——它已有 `tools/performance/PerformanceBenchmarks` 的 ABBA + `VmHWM` + 同工作量校验框架，缺的只是"模拟吞吐"这一类指标。

---

## 3. 国际象棋与围棋引擎

这一章的目的不是"介绍两个棋种"，而是回答：**成熟博弈引擎里，哪些工程范式与博弈结构无关、可以直接搬到卡牌战斗上？**

### 3.1 两条路线：搜索广度与评估深度可互换

| | Stockfish（象棋） | AlphaZero / Lc0（象棋） | KataGo（围棋） |
|---|---|---|---|
| 搜索 | alpha-beta（PVS）+ 极强剪枝 | MCTS + PUCT + 神经网络先验 | MCTS + PUCT + 神经网络 |
| 评估 | NNUE（整数、SIMD、增量更新） | 策略头 + 价值头（GPU/TPU） | 策略头 + 价值头 + score/ownership 辅助头 |
| 吞吐 | 约 **7000 万局面/秒** | 约 **8 万局面/秒**（象棋；将棋 4 万 vs Elmo 3500 万） | 数百~数千 visits/手 |
| 战绩 | —— | 1000 局 **155 胜 6 负 839 和**，Stockfish 需 **10:1 时间让步**才能打平 | 消费级单卡数月训练即可超人类 |
| 结论 | 便宜评估 × 极多节点 | 昂贵评估 × 极少节点，用先验聚焦 | 同上，但靠辅助目标与工程效率把算力压到 1.4 GPU 年 |

**这张表最重要的一行是吞吐差三个数量级**：AlphaZero 每秒只看 8 万个局面，却赢了每秒 7000 万个的 Stockfish（注意 AZ 用了 4 TPU + 44 核，SF 是 44 核 + Syzygy + 32GB 哈希，并非纯算法对比）。对卡牌求解器的启示：**与其把束宽从 60 加到 600，不如把评估/排序做准**——这正是 CombatSolver 用"多通道保留 + 宽度组合 + Pareto"而不是单纯加宽度的原因。

### 3.2 象棋引擎的关键机制（为什么有效）

| 机制 | 实现要点 | 为什么有效 / 对卡牌的启示 |
|---|---|---|
| PVS / 零窗口 | 先零宽窗口验证"是否更好"，fail-high 再全窗口重搜 | 多数走法在验证阶段被否掉，省下的节点远多于重搜成本 |
| 置换表（TT） | 同存 value + depth + **bound 类型** + generation 老化；上限可配到 32 TB | 浅搜结论绝不冒充深搜；**"预算层级"必须进状态键** |
| LMR（后期走法减深） | 减深量随深度、走法序号、improving 增长；赌错时 fail-high 重搜 | "期望无损"的选择性搜索：只损失被忽略走法的机会成本 |
| Null move / ProbCut | 跳过一手预判"够好"；超界概率预判 | 对 zugzwang 失效，高深度加 verification；**卡牌里"跳过一手"=结束回合，通常直接输 → 不适用** |
| Singular extension | 排除 TT 最佳走法做缩减搜索，确认唯一性后加深 | 用"是否唯一"决定加深/剪枝，比固定规则更稳 |
| 时间管理 | fallingEval、bestMoveInstability、距上次最优走法改变的深度 | **用搜索自身的不稳定度决定加时/收手**——这是预算再分配最直接的模板 |
| NNUE | 首层稀疏线性 + 增量累加器；SF16.1 用 HalfKAv2_hm、王位分桶、每视角 2560 维、8 个 layer stack；master 换 K32Q2/PP_3Wide/FullThreats、L1=1024、32 stacks | 前提是"一步只动少量输入"；卡牌一次出牌同时改能量/手牌/牌堆/Power/格挡/敌人，**增量更新只在 HP/格挡/能量成立** |
| SPRT 回归门禁 | Fishtest 累计 20,700 CPU 年 / 103 亿局；序贯检验在"提升=0"与"提升=δ"间尽早停机 | 没有统计门禁的"变强了"不可信；**评测必须能早停** |

### 3.3 围棋引擎的关键机制

| 机制 | 实现要点 | 为什么有效 |
|---|---|---|
| PUCT | `argmax Q(a) + c·P(a)·√ΣN(b)/(1+N(a))`，c≈1.1 | 分母让访问少的子节点获补偿，分子让补偿随总预算缓增，策略先验把补偿集中到合理分支 |
| Dirichlet 根噪声 | α=0.3/0.15/0.03（象棋/将棋/围棋），只加根节点 | 防自对弈走同一条线；按合法走法数反比缩放 |
| 虚拟损失 | KataGo `cpp/search/search.cpp`：选子后先 `virtualLosses.fetch_add`，评估前临时压低其 PUCT | 多线程同时下降会重复选同一节点；先记"假访问"把其他线程赶走，回传时撤销 |
| 批量推理 | GPU/TPU 必须凑批；KataGo 提供 JSON 批量分析引擎 | 单次推理固定开销 >> 边际开销；搜索与推理队列必须解耦 |
| 树复用 | `treeReuseCarryOverTimeFactor`；`clear_cache` 同时清树与 NN 缓存 | 对手走完后子树仍有效；但必须显式清理，否则旧树污染新局 |
| 辅助训练目标 | score、ownership、6/16/50 回合短期价值头 | 终局胜负是二值高噪声信号；短期目标提供低方差梯度 |
| 在线偏差修正 | 子树价值偏差修正 λ=0.35/α=0.8（约 30–60 Elo）；动态 cPUCT + 不确定性加权（合计约 +75 Elo） | 在线学习"网络在某类局部战术上系统性偏乐观/悲观" |
| 固定访问数预算 | Pachi `-t 5000`、`max_tree_size` 以 MB 计 | 把"想多久"换成"想多少"：**慢机器只是更慢，不会更弱** |

### 3.4 训练算力账（判断"这条路我们走不走得起"）

| 系统 | 算力 | 数据量 | 备注 |
|---|---|---|---|
| AlphaGo（分布式） | 1202 CPU + 176 GPU | —— | 监督策略网 + 价值网 + MCTS |
| AlphaGo Zero | 64 GPU worker + 19 CPU 参数服务器 | 3 天 490 万局 | 17 平面输入，ResNet 20/40×256，双头 |
| AlphaZero（推广） | 5000 一代 TPU 生成 + 64 二代 TPU 训练 | 象棋 4400 万局 / 9 小时；将棋 12h；围棋 34h | 700k 步 × 4096 批 |
| ELF OpenGo | 约 2000×V100，13–14 天 | —— | 约 **74 GPU 年** |
| KataGo | ≤28×V100，19 天 | 420 万局 / 2.41 亿样本 | 约 **1.4 GPU 年**，超过 ELF 最终模型 |
| Leela Chess Zero | 社区分布式 | 25 亿局（2025-06），约 100 万局/天 | 2022 起改 transformer |

**结论**：围棋这条路从"41 TPU 年"被 KataGo 压到"1.4 GPU 年"，靠的全是**与领域无关的工程改进**（playout 上限随机化、辅助目标、权重滑动平均与门禁）。这对卡牌项目的启示是：**如果要做学习型评估，先做工程效率，再谈模型规模**。

### 3.5 跨引擎工程共性

| 维度 | 共性结论 |
|---|---|
| 搜索树内存 | 必须有"容量硬上限 + 老化/淘汰"，否则长会话必然内存爆炸；容量对棋力收益递减 |
| 并行 | 根并行天然确定性（只在根层合并）；树并行省内存但需处理写冲突与重复投入 |
| 推理批处理 | 批量是降低单位推理开销的手段；CPU 场景应改为降低单位状态评估开销（缓存、增量、特征复用） |
| 预算控制 | 时间不可复现，**工作量（节点/访问/扩展）才可复现**；外层用时间租约，内层用工作量记账 |
| 外部知识缓存 | Syzygy 7 子从 140 TB 压到 18.4 TB，WDL 可放 SSD 供搜索中查、DTZ 只在根用 → 按"查询频率 × 体积"分层 |
| 自对弈规模 | 数据量与"每局信息密度"可互换；门禁测试（200 局赢 100 局才换网）比绝对数量更重要 |
| 评测 | SPRT + Elo 置信区间 + 外部榜；没有统计门禁的进步不可信 |

### 3.6 迁移矩阵：对卡牌战斗求解器

| 引擎机制 | 判定 | 说明 |
|---|---|---|
| 工作量预算（节点/访问/展开计数） | ✅ 直接可用 | CombatSolver 已有 `SearchBudgetLedger`；应强化为三类计数各自单调可复现，时间只做外层租约 |
| 并行 + 虚拟损失 | ✅ 直接可用 | 给"已准入未提交"的前沿节点打临时惩罚，提交时撤销；按准入顺序施加即天然确定 |
| SPRT 风格回归门禁 | ✅ 直接可用 | 卡牌目标是向量（胜率/战损/回合/资源），固定其余指标对主指标做序贯检验，或用帕累托支配对数做复合统计量 |
| 带 bound/depth/generation 的状态表 | 🔧 需改造 | 卡牌里"同状态不同预算/政策"值不同，状态键须纳入预算层级，并区分精确值/上界/下界 |
| 预算再分配跟随不稳定度 | 🔧 需改造 | 对应量是"最优路线被替换次数、最优与次优质量差、前沿覆盖度变化"；必须基于已提交只读快照，不能用 wall-clock 抖动 |
| 分桶浅层评估（NNUE 思路） | 🔧 需改造 | 卡牌不满足"一步一子"局部性；路径是"手工特征 → 按局面类型/敌人/职业分桶 → 每桶极小线性模型 → 离线蒸馏" |
| PUCT 式按访问计数分配预算 | 🔧 需改造 | 当前 Beam 是 top-k + Pareto，每个候选只看一次；可给节点加"已投入预算"字段，同分按 1/(1+投入) 分配下一轮 |
| 自对弈冷启动 | 🔧 需改造 | 真自对弈需信念状态与对手模型；更现实的是"高预算求解器生成路线 → 蒸馏低预算策略 → 门禁用对局验收" |
| alpha-beta 整体框架 | ❌ 不适用 | 要求完全信息、零和、交替行动且"对手最优"；卡牌敌人是确定性但不可知的程序 |
| null move / 静态搜索类启发式 | ❌ 不适用 | "跳过一手"在卡牌里等于结束回合，通常直接输 |

---

## 4. 综合：给"下一个 Combat Solver"的落地方案

### 4.1 推荐架构（六层）

```
① 读取层   官方/Mod API 或内存读取，拿"稳定根"：手牌/牌堆顺序/敌人意图/RNG 状态/遗物药水
              ↓（不可变快照 + 指纹）
② 模拟层   自研确定性规则引擎：状态、动作、结算、RNG 流、Fork/COW
              ↓（每层用"实机 vs 模拟"严格差分验收）
③ 搜索层   束搜索（宽度 60–200）+ 状态指纹去重 + 多维 Pareto 支配 + 多通道保留
              ↓
④ 预算层   工作量账本（展开数/评估数/字节）+ 时间租约 + 帧率压力信号 + 早停
              ↓
⑤ 输出层   只读路线快照（逐回合动作 + 预计战损 + 置信说明）→ UI
              ↓
⑥ 执行层   走原生入口逐动作部署 + 续用戳核对，偏离即重搜
```

**与 CombatSolver 的差异建议**（如果目标不是 StS2，而是新游戏）：
- 读取层如果没有 mod 权限，就只能走"官方协议 + 可见信息裁剪"，这时**必须加 PIMC**（见 4.2）。
- 模拟层建议一开始就用**"逐动作 lockstep 对拍 + 大 seed fuzz"**做 CI 门禁（spire-mentor 的做法），而不是等出问题再补。
- 执行层如果游戏没有原生入口，可退化为"建议 + 人工执行"（ClawTheSpire/bottled_ai 形态）。

### 4.2 算法选型决策树

```
隐藏信息能否读取？
├─ 能（mod/内存/官方全量协议）──→ 确定性完全信息束搜索（CombatSolver 路线）
│      └─ 评估函数：手工特征 + 多通道保留；预算不够再加宽度组合
└─ 不能
   ├─ 隐藏信息只是"牌序/随机数"，对手不建模你 ──→ PIMC/determinization
   │      └─ 现有搜索器当"单世界求解器"，K 个世界各分 1/K 预算，输出期望/最坏
   └─ 对手持有私有信息且会针对你 ──→ CFR（限小抽象子博弈）
          └─ 先做信息集/动作抽象；整局 CFR 不可行（扑克 infoset 10^162 量级）
```

**不要做的事**：整局 CFR、单机 CPU 上跑深度价值网络、把 LLM 放进战斗循环、用加大预算掩盖模拟偏差。

### 4.3 分阶段路线图（按 12 周估）

| 阶段 | 目标 | 验收标准 |
|---|---|---|
| 0. 可行性（1 周） | 确认能否拿到"根状态 + 未来 RNG + 敌人意图"；确认有无 mod/协议 | 能导出一次战斗的完整状态 JSON 与 RNG 状态 |
| 1. 模拟器 MVP（4 周） | 只做 1 个角色 × 3 个敌人 × 20 张卡 | 逐动作对拍通过率 100%；1000 seed fuzz 无差异 |
| 2. 搜索 MVP（3 周） | 束搜索 + 状态指纹 + 预算账本 | 同根同预算可复现；路线能打完整场；比贪心基线战损低 |
| 3. 保真度扩展（3 周） | 覆盖全部卡/怪/遗物；差分测试进 CI | 覆盖表里"未支持"项显式失败而非静默跳过 |
| 4. 产品化（1 周+） | UI、并行、帧率保护、路线复用 | 搜索期间主线程帧时间不超过空闲基线的 1.5 倍；复用命中率可观测 |

### 4.4 关键指标（建议从第一天就埋点）

| 类别 | 指标 |
|---|---|
| 保真度 | 逐动作对拍通过率、fuzz seed 数、未支持语义数（应为 0 静默） |
| 吞吐 | 展开节点/秒、状态快照/秒、每次搜索总展开数 |
| 质量 | 整场战损、胜率、回合数、药水消耗（多目标，禁止单指标宣称优化） |
| 预算 | 时间/节点/内存三类预算达成率、早停率、OOM/降级次数 |
| 交互 | 搜索期间主线程帧时间 P99、>50ms 帧数 |
| 稳定性 | 计划外重搜次数、续用戳命中率、崩溃率 |

### 4.5 风险清单

1. **模拟器保真度**是最大风险（也是最大工作量）：语义漂移会让搜索给出"看起来更优但实机不可执行"的路线。缓解：对拍 + fuzz + 每效果单测 + 未支持即失败。
2. **隐藏信息假设**：如果读取层拿不到完整 RNG，"全知搜索"会给出偏乐观路线。缓解：显式标注"已知/未知"，未知走 PIMC。
3. **预算与体验**：搜索会抢 CPU/内存。缓解：帧率压力信号 + 内存区域预算 + 并行度上限。
4. **版本漂移**：游戏更新会改结算语义。缓解：镜像注册表 + 覆盖目录 + 版本锁定，未支持即失败。
5. **过度工程**：不要在没有对拍的情况下先做 NNUE/CFR/自对弈。

### 4.6 可复用的开源资产

| 项目 | 可复用部分 | 许可 |
|---|---|---|
| [OpenSpiel](https://github.com/google-deepmind/open_spiel) | `Game/State/chance/infoset` 接口设计、PIMC/CFR 参考实现 | Apache-2.0 |
| [TexasSolver](https://github.com/bupticybee/TexasSolver) | "树构建配置 + 收敛日志 + JSON 导出"的产品形态 | AGPL-3.0（商用注意） |
| [spire-mentor](https://github.com/tfriedel/spire-mentor) | lockstep parity + fuzz 的测试方法论（Rust 实现可读） | MIT |
| [sts2-rl-agent](https://github.com/zhiyue/sts2-rl-agent) | 引擎/内容表/环境三层分离的结构 | 见仓库 |
| [Random Foreseer](https://github.com/hotwords123/StS2.RandomForeseer) | RNG 前推与"不改状态的前瞻"思路 | MIT |
| [KataGo](https://github.com/lightvector/KataGo) | 虚拟损失、树复用、辅助训练目标、GTP 扩展命令 | MIT |
| [Stockfish](https://github.com/official-stockfish/Stockfish) | 时间管理（fallingEval/instability）、TT 的 bound 设计 | GPL-3.0 |
| [SabberStone](https://github.com/HearthSim/SabberStone) | "每卡自动生成单测"的工程实践 | 见仓库 |
| [ocgcore/EDOPro](https://github.com/edo9300/edopro) | "卡牌 = 数据 + Lua 脚本"的效果注册表分层 | 见仓库 |

---

## 5. 实现落地（本仓库）

调研结论已落地为可运行的引擎与真机对拍管线：

| 能力 | 命令 | 产物 |
|---|---|---|
| 数据基线（M0） | `python tools/export_sts2_data.py --out data/sts2` | `data/sts2/*.json` + `manifest.json` |
| 值 oracle（M1） | `dotnet run --project src/Sts2.Oracle -- --probe all` | `tests/vectors/game_values.json` |
| 真机轨迹（M2） | `python harness/run_scenario.py harness/scenarios/ironclad_nibbits.json` | `tests/vectors/real/*.jsonl` |
| 引擎重放（M3/M5） | `dotnet run --project src/Sts2.Cli -- run --scenario … --pack data/sts2 --out …` | 与真机逐字段一致 |
| 对拍门禁（M4） | `python tools/compare_snapshots.py --a real.jsonl --b engine.jsonl` | CI 失败即回归 |
| 单文件回放（M6） | `dotnet run --project src/Sts2.Cli -- html --transcript real.jsonl --out replay.html` | 内嵌数据的离线 HTML |
| 进阶对拍（M7） | `… run --scenario harness/scenarios/ironclad_nibbits_a10.json …` | A4/A5/A9/A10 逐字段一致 |
| 性能与 AOT（M7） | `dotnet publish src/Sts2.Cli -c Release -r win-x64 -p:PublishAot=true` | 21.6 µs/场（约 46k 场/秒） |
| 五角色（M5） | `… run --scenario harness/scenarios/silent_nibbits_auto.json …` | Ironclad、Silent、Regent、Defect、Necrobinder 起手牌组均 0 差异 |
| 会话/长序列（M5） | `… run --scenario harness/scenarios/ironclad_session.json …` | 5 角色 × 16 场（4 个弱遭遇 × 4 轮），每局 204 个动作、220 帧 |
| 覆盖冒烟（M5） | `… smoke --pack data/sts2` | 432 张结构化卡全部可打出（0 失败）、64 个怪物房遭遇全部可启动 |
| 随机策略（M5） | `… run --scenario harness/scenarios/defect_random_toadpoles.json …` | 5 角色 × 4 弱遭遇共 20 个随机序列场景，逐字段 0 差异 |
| 会话对拍（M5） | `… run --scenario harness/scenarios/necrobinder_session.json …` | 5 角色各 16 场连续遭遇，200–380 动作/局，逐字段 0 差异 |
| 本地 Web（M8） | `dotnet run --project src/Sts2.Cli -- web --port 5080` | 浏览器人机对弈；`python tools/check_web.py` 冒烟 7 项 |

回放页只读快照：回合/HP/格挡/能量、敌人意图、手牌与五牌堆、逐步动作日志；方向键翻页，
无外部 CSS/JS/字体，可直接双击打开或放进静态站点。

## 附录 A：三条技术线的选型速查

| 你的场景 | 选它 | 代表 |
|---|---|---|
| 单机、可读状态、要可解释路线、CPU 预算 | 确定性束搜索 + Pareto + 多通道保留 | CombatSolver、spire-mentor（MCTS 变体） |
| 单机、隐藏信息只有牌序 | 上述 + PIMC/determinization | hearthstone-ai（多观察者 MCTS） |
| 竞技对抗、对手会针对你、有离线算力 | CFR + 抽象 + 子博弈求解 | Libratus、DeepStack、ReBeL、TexasSolver |
| 整局决策、有 GPU 与数天训练预算 | 自对弈 RL（PPO/MaskablePPO/DMC） | DouZero、Mortal、spire-mentor（整局 PPO）、sts2-rl-agent |
| 只想快速出个能打的 bot | 手写优先级 + 小规模穷举 | bottled_ai、ClawTheSpire |

## 附录 B：未核实项汇总

- DeepStack 训练算力、PioSOLVER 内部分层、Pluribus 硬件、ReBeL 训练成本（扑克线）。
- Monster Train 无可核实 AI 项目；XMage 的 raw README 404（仅引仓库首页）；Suphx 仅经 kanachan 引用 arXiv 编号。
- NNUE "原型节点率减半"、Pachi 的 KGS 段位、SF 的 TT 32TB 与 Fishtest 累计量、AlphaZero 比分细节均为维基/README 口径，未与原始论文交叉核对。
- GTP 规范原文未取到（维基条目限流）；"Elo 与访问数呈对数关系"为定性归纳。
- 所有 star 数、版本号、Elo 为 2026-10-24 抓取值，会随时间变化。

## 附录 C：证据文件索引

| 文件 | 内容 |
|---|---|
| `_research/notes/combatsolver.md` | CombatSolver 源码级深挖笔记（本报告第 1 章的底稿） |
| `_research/notes/cards-deck.md` | 卡牌模拟器/自对弈 AI 详细表（第 2.1/2.3/2.4 章底稿） |
| `_research/notes/cards-poker.md` | CFR/扑克求解器详细表（第 2.2 章底稿） |
| `_research/notes/engines.md` | 象棋/围棋引擎详细表（第 3 章底稿） |
| `_research/raw/*.txt` | 各项目 README/源码/维基页面的抓取原文（逐 URL 分段） |
| `_research/raw/spire-mentor-benchmarks-page.html` | §2.5 的基准页原文（2026-10-14，含三方横评与游戏本体对比） |
| `_research/raw/spire-mentor-benchmarks-writeup.md` | §2.5 的完整测试记录（未取整数值、复现命令、同工作量校验） |
| 上游 [Torch1230/CombatSolver](https://github.com/Torch1230/CombatSolver) | 本报告核对的上游快照为 commit `5c773ca`；本地浅克隆已删除，可按该提交号重新获取 |

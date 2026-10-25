# 卡牌 / Roguelike 卡牌构筑：模拟器与自对弈 AI 调研（cards-deck）

抓取时间 2026-10-24。star 数均取自当次 GitHub 搜索/仓库页；证据见 `_research/raw/cards-deck-sources.txt`。

## 结论摘要

1. 与 CombatSolver 同构的公开项目（StS2 侧至少 4 个）几乎都走"**自己重实现规则 + 搜索/学习**"，官方/Mod API 只用来读状态和下发动作，没有一个是靠调游戏内部接口做反事实推演的。最强的两个证据是 [spire-mentor](https://github.com/tfriedel/spire-mentor)（Rust 全量移植）和 [sts2-rl-agent](https://github.com/zhiyue/sts2-rl-agent)（Python 无头模拟器 + C# 桥接）。
2. 模拟器保真度不靠"看起来对"，靠**逐动作 lockstep 对拍 + 大 seed fuzz + 每卡单测**：spire-mentor 对游戏自身 C# 代码逐动作对拍，5000 seed fuzz，Ironclad 6,104/6,104 场干净；反面案例 sts-ironclad-agent 自认 simulator parity incomplete 仍报 50% 胜率。
3. 官方协议会**泄露隐藏信息**：CommunicationMod 的 JSON 直接带 `draw_pile` 顺序与敌人意图；做"合法 AI"必须自己裁剪可见域（bottled_ai 明确只用玩家可见信息）。
4. 成本量级差别极大：单战斗 MCTS 1000 次模拟 ≈14 ms（Rust，单线程）；整局 PPO 单卡 960 局/迭代 ≈208 s（1×RTX 3090），150 迭代 ≈9 h；TCG 自对弈到可用要 28M 参数、32×H20 约 3 天。
5. TCG 模拟器最值得抄的是"**引擎 + 卡牌脚本/效果注册表**"分层（ocgcore 的 Lua 卡脚本、Forge 的 Java 卡实现、SabberStone 的附魔层），AI 永远在引擎之上单独一层。

## 一、同游戏族：Slay the Spire / StS2 / Balatro

| 名称 | 链接 | 类型 | 技术栈 | 核心做法 | 可借鉴点 | 局限 / 维护 |
|---|---|---|---|---|---|---|
| Random Foreseer | [hotwords123/StS2.RandomForeseer](https://github.com/hotwords123/StS2.RandomForeseer) | 游戏内预测 Mod | C# / Godot / Harmony | 不推进真实 RNG 就预演后续随机结果（变牌、事件、抽牌、洗牌、球、目标），局内+局外分目录 | 产品定位="不改状态的前瞻"；`InCombat/` 与 `OutOfCombat/` 分离 | README 只讲预测功能，未公开"模拟核心"章节；MIT，v0.13.19，最低游戏 0.111.0 |
| spire-mentor | [tfriedel/spire-mentor](https://github.com/tfriedel/spire-mentor) | 模拟器 + 训练/AI | Rust 引擎 + Python/C# + TS | 全角色战斗与整局规则 Rust 移植；约 1500× 快于游戏自身无头战斗（单核 ~60k combats/s）；MCTS 战斗 + PPO 整局 | **lockstep parity + fuzz**；MCTS 走 1,000 sims≈14 ms；PPO 960 局/迭代≈208 s / 150 迭代≈9 h | Ironclad A0 90%、A9 39%、A10 11%；非 Ironclad parity 99.4–99.8%；MIT |
| sts2-rl-agent | [zhiyue/sts2-rl-agent](https://github.com/zhiyue/sts2-rl-agent) | 模拟器 + RL | Python + C#/Godot 桥 | 无头引擎（combat/hooks/damage/rng）+ 内容表（577 卡、260 Power、121 怪物、290 遗物、63 药水、5 角色） | 内容与引擎解耦；obs 131 维、动作空间 61/100；MaskablePPO 50 万步；~1,200 combats/s、~28,000 steps/s；TCP JSON 回实机 | ~92% Act 1 Ironclad 战斗胜率；133 源文件约 5 万行 |
| ClawTheSpire | [dbyrne/ClawTheSpire](https://github.com/dbyrne/ClawTheSpire) | 整局 bot | Python + LLM + HTTP 桥 | 战斗用 DFS 穷举打分；局外（卡牌奖励、地图、商店、事件）交本地 Ollama LLM | **搜索负责算术、LLM 只负责开放权衡** | 依赖第三方 [STS2-Agent](https://github.com/CharTyr/STS2-Agent) mod；5 star 级小项目 |
| bottled_ai | [xaved88/bottled_ai](https://github.com/xaved88/bottled_ai) | 整局 bot（StS1） | Python | 手写优先级 + 图遍历模拟手牌所有出牌顺序，按约 40 个加权值打分 | 不训练也能到 Watcher 52% 胜率；显式拒绝隐藏信息 | 策略手工维护，换版本成本高；80 star |
| sts_lightspeed | [gamerpuppy/sts_lightspeed](https://github.com/gamerpuppy/sts_lightspeed) | 模拟器/搜索 | C++17 | 追求 100% RNG 一致；已知 RNG 下做树搜索；可从存档载入（仅战斗） | 100 万随机 playout / 5 s / 16 线程的吞吐口径 | 只实现 Ironclad 与无色卡；后续无大动作 |
| sts-ironclad-agent | [destire-mio/sts-ironclad-agent](https://github.com/destire-mio/sts-ironclad-agent) | 整局 agent | Python（基于 lightspeed） | 局外用一张冻结网络，战斗内保留搜索 | **真实游戏 501 局 Heart 胜率 50.1%**（95% CI 45.7–54.5）的验收写法 | 自标 simulator parity incomplete——保真度缺口与胜率并存 |
| BalatroBot | [coder/balatrobot](https://github.com/coder/balatrobot) | 游戏 API Mod | Lua Mod + JSON-RPC 2.0/HTTP | 对外暴露完整游戏状态与控制（选牌、商店、盲注） | 外部进程当大脑、Mod 只当 IO；配套 BalatroLLM/BalatroBench | 79 star；生态偏 LLM/Agent 评测而非搜索 |

**同游戏族的共性**：StS2 这一代已出现"Rust/C++ 高性能重实现 + 游戏内桥"的标准组合，且都把**吞吐**当一等指标（spire-mentor、sts2-rl-agent 都在 README 首屏报 combats/s）。同时，两个最强项目都没有用"读内存 + 游戏内推演"：它们读一次真实状态，然后在自己引擎里 Fork。

## 二、斗地主 / 麻将 与 RLCard 框架

| 名称 | 链接 | 类型 | 核心做法 | 可借鉴点 / 数字 | 开源程度 |
|---|---|---|---|---|---|
| DouZero | [kwai/DouZero](https://github.com/kwai/DouZero) | 训练框架 | Deep Monte-Carlo + 动作编码 + 并行 actor；三方合作/对抗 | 单服务器 4 GPU、数天训练即超过当时所有程序；Botzone 344 个 AI 中第一（ICML 2021，[arXiv:2106.06135](https://arxiv.org/abs/2106.06135)） | Apache-2.0，含训练/评测代码 |
| RLCard | [datamllab/rlcard](https://github.com/datamllab/rlcard) | 环境集合 | `env.make()` → `set_agents()` → `env.run()`，统一 `num_actions` / `state_shape` / `action_shape` | 每游戏给出状态/动作空间量级：麻将 10^121/10^2、斗地主 10^53~10^83/10^4；[arXiv:1910.04376](https://arxiv.org/abs/1910.04376) | MIT |
| Mortal | [Equim-chan/Mortal](https://github.com/Equim-chan/Mortal) | 训练/推理 | 立直麻将深度 RL，Rust 实现，配在线文档 | "权重不进仓库"的发布纪律，避免版权/作弊争议 | AGPL-3.0，代码全开、权重另发 |
| kanachan | [Cryolite/kanachan](https://github.com/Cryolite/kanachan) | 数据 + 训练 | 牌谱标注工具 + 纯 token 特征（几乎无人工特征）+ curriculum fine-tuning | 数据规模论证：天凤凤凰卓 11 年 1,700 万局 vs 雀魂 2021-08 约 6,500 万局；明确不提供爬虫/数据/权重 | 代码开源 |
| Suphx | 论文 [arXiv:2003.13590](https://arxiv.org/abs/2003.13590)（经 kanachan 引用） | 研究系统 | 论文报告的高水平麻将 AI，被 kanachan 列为要对标对象（作者/单位未核实） | 说明"数据规模 + 大模型"路线的上限 | 未开源（细节未核实） |

**对模拟器/自对弈的直接启发**：斗地主/麻将的难点是**动作空间随局面剧烈变化**（合法动作集每步都不同），因此两家都用"动作编码 + 合法动作 mask"而不是固定动作维。CombatSolver 的候选动作同样是"手牌 × 目标 × 药剂 × 结束回合"的动态集合，这一点同构：**动作必须按局面枚举并编码，而不是预留大动作表**。RLCard 的价值在接口：`state_shape`/`action_shape` 让算法与游戏解耦，换卡池只换环境不改训练循环。

## 三、TCG 规则引擎与 AI 层

| 名称 | 链接 | 类型 | 规则引擎抽象 | AI 在哪一层 | 状态/维护 |
|---|---|---|---|---|---|
| Forge | [Card-Forge/forge](https://github.com/Card-Forge/forge) | 规则引擎 + AI 对手 | Java，可扩展架构，卡牌以代码实现；README 列多种"AI Formats" | 引擎内置电脑对手，与规则同栈 | 社区长期维护、多平台（桌面/安卓/iOS） |
| XMage | [magefree/mage](https://github.com/magefree/mage) | 规则引擎 + 电脑对手 | Java 全规则执行，客户端/服务器分离 | 仓库页自述"against other players or computer AI" | 2.4k star，长期维护 |
| SabberStone | [HearthSim/SabberStone](https://github.com/HearthSim/SabberStone) | 模拟器 | C#；"洋葱层"处理光环/附魔/触发 | `SabberStoneBasicAI` + 计分式树搜索独立成层 | 每张卡自动生成单测；含 Blackrock 等卡池进度表 |
| hearthstone-ai | [peter1591/hearthstone-ai](https://github.com/peter1591/hearthstone-ai) | 模拟器 + MCTS | header-only C++17 引擎 | 多观察者 MCTS 处理隐藏信息 + TensorFlow 策略/默认网络 | 334 star；自称可稳胜练习模式 AI |
| EDOPro / YGOPro | [edo9300/edopro](https://github.com/edo9300/edopro) | 自动决斗引擎 | `ocgcore`：卡牌效果写成 Lua 脚本（[CardScripts](https://github.com/ProjectIgnis/CardScripts) 卡脚本库） | 引擎无 AI；bot 在外部（[WindBot](https://github.com/IceYGO/windbot) 走服务器协议） | 社区活跃，客户端/核心分仓 |
| MirrorForce | [tommy85/MirrorForce](https://github.com/tommy85/MirrorForce) | 自对弈 agent | 复用 YGOPro 规则引擎 | 28M 参数 transformer，自对弈约 3 天 / 32×H20，iteration 1,084 | 首个 YGO agent 比赛冠军；只训 Sky Striker 单卡组 |

**关键结论**：TCG 的卡牌数量上千、效果互相覆盖，所以成熟引擎都把"卡牌"做成**数据 + 脚本/类**的注册表，而不是 if-else 堆叠；AI 一律在引擎外/上层。WindBot 这种"脚本 bot 通过服务器协议接入"的形态，等价于 CombatSolver 的"外部决策 + 原生入口执行"。

## 四、模拟器保真度方案对照

| 方案 | 代表项目 | 成本 | 主要风险（实测教训） |
|---|---|---|---|
| 官方 / Mod API 读真实状态 | [CommunicationMod](https://github.com/ForgottenArbiter/CommunicationMod)、[spirecomm](https://github.com/ForgottenArbiter/spirecomm)、BalatroBot、STS2-Agent | 低：只需实现 JSON 解码与命令下发 | ① 协议**泄露隐藏信息**（`draw_pile` 顺序、敌人意图），不裁剪就是作弊；② 只能拿"当前稳定态"，无法反事实；③ 动作经原生入口重放，有帧延迟/超时与状态竞态（spirecomm 2019 年后基本停更） |
| 规则重实现 | spire-mentor、sts2-rl-agent、sts_lightspeed、Forge、XMage、ocgcore | 高：spire-mentor 全角色全卡，sts2-rl-agent 约 5 万行/577 卡 | 语义漂移。spire-mentor 自述初版"和真实游戏有大量细小差异"，靠 parity 测试才收敛；sts-ironclad-agent 干脆承认 parity incomplete |
| 录像 / 检查点回放 | kanachan（牌谱标注）、CombatSolver `docs/CHECKPOINT_REPLAY.md` | 最低：不需要实时桥 | 只能覆盖"已经发生过"的轨迹，无法评估被剪掉的候选动作；适合回归与差分，不能单独当保真度证明 |

**控制漂移的可复用手段**（按性价比排序）：① 同一动作在 real/sim 两边的**严格差分**（CombatSolver 已有 actual/simulated 差分协议，方向正确）；② 大 seed 集 fuzz（spire-mentor 5,000 seed 分层）；③ 每卡/每效果一条单测（SabberStone 的自动化单测生成）；④ 覆盖率登记表（CombatSolver 的 `MethodMirrorRegistryDescriptor` + CoverageCatalog 已是这种形态）。

**CombatSolver 在这张表里的特殊位置**：它既不是纯 API 读取（不经 JSON 序列化，而是在同进程内读真实 `Model` 再克隆影子状态），也不是纯离线重写（镜像直接调原版方法链）。好处是没有协议损耗、能拿到最细的领域对象；代价是**缺少外部真值**——游戏自身就是唯一裁判，一旦镜像实现和原版分叉，没有第二份实现来对拍。spire-mentor 的做法正好补上这一点：用"headless C# harness 跑真实游戏代码"当对拍基准，这正是 CombatSolver 可以复用的最接近形态（本地已有 `tools/testing` 与 `-VerifyIncrementalSearch` 一类入口）。

**成本参考**（据公开数字）：做"可用整局 agent"大致需要 5 万行级模拟器 + 单卡 3090 数天到 9 小时训练（spire-mentor、sts2-rl-agent）；只做战斗搜索不做训练，则只需模拟器 + 评估函数（bottled_ai 约 40 个加权值、ClawTheSpire 的 DFS 打分），工程量小一个数量级，但泛化靠人工策略维护。

## 五、对 CombatSolver 式项目的可借鉴条目（抄什么 / 别抄什么）

1. **抄** spire-mentor 的"逐动作 lockstep + 大 seed fuzz"门禁，把"每效果一条 actual/simulated 差分"从测试提升为 CI 硬门禁；这比增加 Beam 宽度划算得多。
2. **抄** SabberStone 的"每张卡生成单测"与 ocgcore 的"卡牌=脚本"：把卡牌效果收敛到注册表，未登记即失败。CombatSolver 已有 `MethodMirrorRegistryDescriptor`，可进一步做到"未登记 = CI 红"。
3. **抄** sts2-rl-agent 的"引擎 / 内容表 / 环境"三层分离（577 cards、260 powers 等独立表）：新卡池按表接入，不动搜索与部署编排。
4. **抄** "吞吐是一等指标"：spire-mentor 单核 ~60k combats/s、sts2-rl-agent ~28,000 steps/s 都写在 README 首屏；`tools/search/OfflineSearchHarness` 应固定吞吐基线，防止镜像改动悄悄变慢。
5. **抄** "主线程读根、后台 Fork 子树"的边界：与 CombatSolver 现状同构；补充一点来自 lightspeed 的教训——**根构造范围要显式声明**（它只支持"载入到战斗内"），否则跨回合/局外状态会混进快照。
6. **别抄** 把 LLM 放进战斗循环：ClawTheSpire 让 LLM 只管局外，[AttemorySystem/spire-agent](https://github.com/AttemorySystem/spire-agent) 明确"确定性优先，只把开放的权衡交给 LLM"。战斗内应保持可复现的搜索。
7. **别抄** "回放通过 = 模拟正确"，也**别抄**用更大预算掩盖偏差：sts-ironclad-agent 在 parity 未完成时仍发布 50.1% 胜率，正是这种反例；这与 CombatSolver `AGENTS.md` 的禁令一致。
8. **抄** BalatroBot 的接口形态：Mod 内只读快照 + JSON 接口 + 外部进程决策。CombatSolver 的"战前预测 API（面向 Mod 开发者）"可扩展为"只读快照导出/导入"，用于离线差分复现。

## 六、来源清单与未核实项

**已核实（本次抓取成功，正文论断均由此支撑）**
- [hotwords123/StS2.RandomForeseer](https://github.com/hotwords123/StS2.RandomForeseer) README（主分支，中文）；CombatSolver `THIRD_PARTY_NOTICES.md` 记录其战斗模拟核心改造复用关系。
- [tfriedel/spire-mentor](https://github.com/tfriedel/spire-mentor)、[zhiyue/sts2-rl-agent](https://github.com/zhiyue/sts2-rl-agent)、[dbyrne/ClawTheSpire](https://github.com/dbyrne/ClawTheSpire)、[xaved88/bottled_ai](https://github.com/xaved88/bottled_ai)、[gamerpuppy/sts_lightspeed](https://github.com/gamerpuppy/sts_lightspeed)、[destire-mio/sts-ironclad-agent](https://github.com/destire-mio/sts-ironclad-agent)、[coder/balatrobot](https://github.com/coder/balatrobot)、[AttemorySystem/spire-agent](https://github.com/AttemorySystem/spire-agent) 的 README。
- [kwai/DouZero](https://github.com/kwai/DouZero)、[datamllab/rlcard](https://github.com/datamllab/rlcard)、[Equim-chan/Mortal](https://github.com/Equim-chan/Mortal)、[Cryolite/kanachan](https://github.com/Cryolite/kanachan) README。
- [Card-Forge/forge](https://github.com/Card-Forge/forge)、[HearthSim/SabberStone](https://github.com/HearthSim/SabberStone)、[peter1591/hearthstone-ai](https://github.com/peter1591/hearthstone-ai)、[edo9300/edopro](https://github.com/edo9300/edopro)、[IceYGO/windbot](https://github.com/IceYGO/windbot)、[tommy85/MirrorForce](https://github.com/tommy85/MirrorForce) README。
- [ForgottenArbiter/CommunicationMod](https://github.com/ForgottenArbiter/CommunicationMod) README（含完整状态 JSON 示例）。
- GitHub 仓库搜索页（HTML，服务器渲染）用于发现项目与 star 数：`slay the spire ai`、`slay the spire reinforcement learning`、`slay the spire simulator`、`balatro ai`、`balatro bot`、`monster train ai`、`xmage`、`hearthstone ai`、`ygopro windbot`、`yu-gi-oh ai`。

**未核实 / 明确缺口**
- **Monster Train**：GitHub 仓库搜索只返回 0–0 star 的疑似模板/营销仓库，**未找到可核实 AI 项目**。
- **XMage**：`raw.githubusercontent.com/magefree/mage/main|master/README.md` 均 404；正文只引用仓库首页简介（"full rules-enforcement engine … computer AI"）与 2.4k star，未读 README 全文。
- **Random Foreseer 的"模拟核心"内部结构**：其 README 只描述 RNG 预测功能，未列模拟器模块；"CombatSolver 模拟核心源自它"依据是 CombatSolver 的 `THIRD_PARTY_NOTICES.md`（2026-08-28 作者授权记录），非该项目 README。
- **Suphx**：仅由 kanachan README 引用 arXiv 编号，论文正文、十段等具体成绩未抓取核实。
- **Mortal** 的训练成本、推理延迟：README 未给数字，未写。
- 所有 star 数、版本号均为 2026-10-24 抓取值，可能变化。

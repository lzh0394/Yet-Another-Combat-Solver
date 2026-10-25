# 不完全信息博弈求解：CFR 家族、扑克求解器与框架

> 供「新卡牌游戏造 CombatSolver 同类求解器」参考。本文件所有数字均来自 `_research/raw/cards-poker-sources.txt` 中实际抓取的页面；未能核实的项在文末单列。

## 结论摘要

1. **CFR 家族解决的是「有对手私有信息且对手会针对你」的问题**：它的收敛目标是纳什均衡（平均策略收敛，见 OpenSpiel `cfr.py` docstring），代价是必须按信息集（infoset）枚举或抽样。RLCard 给出的规模表说明这条路在整局尺度上不可行：无限注德州 infoset 约 10^162、斗地主 10^53~10^83、麻将 10^121。
2. **可工程化的做法都是「抽象 + 子博弈求解」**：Libratus 用 CFR+ 加 endgame solving（比赛期间每晚在超算上重解，另耗 4M 核心小时），DeepStack 用深度有限搜索 + 价值网络做叶子评估，ReBeL 用公共信念状态递归。三者都不是「整局一次性求解」。
3. **开源产品的现实形态是「树构建配置 + 迭代日志 + 策略表导出」**：TexasSolver 在 flop、SPR=10、6 线程的设置下用 172s 收敛到 0.275% exploitability（同为 6 线程的 PioSOLVER 1.0 为 242s、0.29%），输出 `output_result.json`。这套「预算—收敛度—可读输出」的结构与 CombatSolver 的只读路线快照最接近。
4. **框架层面差距很大**：OpenSpiel（102 条游戏记录、C++ 核心 + Python 绑定、显式 chance player 与 information state）适合做算法试验台；PokerKit 只是纯 Python 规则/评估引擎（99% 覆盖率），不含搜索；Pgx 用 JAX 原生 `step` 换 GPU 上的 `vmap` 并行，但要求把规则写成纯函数。
5. **对 CombatSolver 这类项目，主结论是「先 PIMC，后局部 CFR，慎上神经网络」**：若隐藏信息只是未知牌序/随机数，把现有确定性 Beam Search 当单世界求解器做 determinization 最划算；只有当敌方持有私有信息且会博弈时才需要 CFR，且只能限定在小抽象子博弈内。

## 项目清单

| 名称 | 链接 | 类型 | 核心算法 | 预算量级（来自抓取内容） | 可借鉴点 | 局限 |
|---|---|---|---|---|---|---|
| CFR / CFR+ / DCFR（OpenSpiel 实现） | [OpenSpiel](https://github.com/google-deepmind/open_spiel) | 算法库（C++/Python） | 表格型 CFR：由 regret 算当前策略→算价值→更新 regret；CFR+ 用 RM+ 把累计 regret 截到 ≥0；DCFR 对正/负 regret 乘 `t^α/(t^α+1)`、`t^β/(t^β+1)`，平均策略乘 `t^γ` | 无预算参数；只在 Kuhn/Leduc 等小 infoset 上「充分测试」 | API 极简：`CFRSolver.evaluate_and_update_policy()`、`average_policy()`、`current_policy()` | 表格式需枚举 infoset，整局扑克/卡牌战斗不可行 |
| Deep CFR | [OpenSpiel v1.1.0 源码](https://raw.githubusercontent.com/google-deepmind/open_spiel/v1.1.0/open_spiel/python/algorithms/deep_cfr.py) | 神经网络 CFR | advantage 网络做 regret matching + strategy 网络拟合平均策略，样本进 reservoir buffer | 训练量级未核实（原文件用 TF1） | 用网络替代 infoset 枚举，是「大状态空间」唯一可行变体 | master 分支已无此文件；训练重、推理要 NN |
| TexasSolver | [GitHub](https://github.com/bupticybee/TexasSolver) | 开源求解器（C++/Qt，AGPLv3） | CFR 家族迭代求解：前身 Java 版 README 列出 `cfr`/`cfr_plus`/`discounted_cfr` 三种算法（作者称其后提出的 discounted CFR++ 更快）；按迭代打印双方与总 exploitability，树构建由输入脚本参数化 | 6 线程 / 1600MB / 172s / 0.275%；示例配置 `set_accuracy 0.5`、`set_max_iteration 200`、8 线程；README 称另有 GPU 版「>20×」 | ①树构建 DSL ②收敛日志 ③`dump_result output_result.json` ④树极大时可开 `public` chance sampling（MCCFR 变体）⑤多线程 fork 配置（action/chance fork 概率、按深度与最小规模 fork） | 只解 2 人零和；内存开销大；AGPL 商用需授权 |
| PioSOLVER 1.0 | [对比数据](https://github.com/bupticybee/TexasSolver/blob/master/benchmark/benchmark_piosolver.txt) | 商业求解器 | CFR 类迭代求解（Java 版 README 称与 PioSOLVER 的细微差异来自树构建逻辑不同与双方「CFR 收敛不足」）；同一 flop 配置（牌面 Qs Jh 2h、底池 10、有效筹码 95、flop 下注 100%、加注 50%、含 all-in） | 6 线程 / 492MB / 242s / 0.29% | 输入即「树构建配置」：用下注尺度集合 + all-in 阈值把树压到可解规模 | 闭源；内部抽象与分层策略未核实 |
| DeepStack | [Wikipedia](https://en.wikipedia.org/wiki/DeepStack) | 超人类 AI（2017 Science） | 深度有限搜索 + 价值网络 + continual resolving：lookahead 内用 CFR 迭代，叶子用网络估剩余博弈 | 训练算力未核实；对局侧为「单次决策一次搜索」 | 用网络替代深搜尾部；每步重解保证策略一致 | 需预训练价值网，实时建议依赖 GPU |
| Libratus | [Wikipedia](https://en.wikipedia.org/wiki/Libratus) | 超人类 AI（2017） | CFR+ 的新变体 + endgame solving（去掉了 action mapping） | 构建用 >15M 核心小时（Bridges 超算），比赛期间另用 4M 核心小时每晚重解 | 「离线蓝图 + 在线子博弈重解」的分层结构 | 蓝图层成本在集群级，不是单机方案 |
| Pluribus | [Wikipedia](https://en.wikipedia.org/wiki/Pluribus_(poker_bot)) | 超人类 AI（2019，6 人桌） | 离线自博弈出基础策略 + 在线继续学习；多人非零和无强理论保证 | 基础策略 8 天、按市价约 $144；对 5 位职业选手平均 >30 mbb/局 | 证明 >2 人场景可经验性奏效；搜索只看前几步 | 需海量自对弈；无均衡保证；源码未公开 |
| ReBeL | [facebookresearch/ReBeL](https://github.com/facebookresearch/ReBeL) | 递归信念搜索（2020 NeurIPS） | 公共信念状态上递归 + 价值网络 + 子博弈 CFR | 官方实现支持多 GPU 自博弈（每 GPU 8 个 CPU 生成线程）；求解参数示例 `mdp_depth 2`、`subgame_iters 1024`、`num_threads 10` | 「信念状态 + 浅层递归 + 网络叶子」这一套可迁移到隐藏手牌战斗 | 官方只开源 Liar's Dice；训练成本未核实 |
| OpenSpiel | [GitHub](https://github.com/google-deepmind/open_spiel) | 框架 | 102 条游戏记录；算法表含 CFR/CFR-BR/MCCFR/Deep CFR/PIMC/IS-MCTS/Regret Matching | 核心 C++、算法 Python/C++ 双实现 | `Game`/`State`/chance player/infoset 四件套；chance 显式建模随机性 | 大游戏性能不足；PIMC 仅「轻测」 |
| PokerKit | [GitHub](https://github.com/uoftcprg/pokerkit) | 扑克模拟器 | 纯 Python 规则引擎 + 手牌评估 + 统计 | 需 Python 3.11+；99% 代码覆盖率；不含求解器 | 统一高层 API（`Automation`、`Mode`），自定义变体成本低 | 只做模拟/评估，无搜索与均衡求解 |
| Pgx | [GitHub](https://github.com/sotetsuk/pgx) | JAX 模拟器 | JAX-native `init/step`，`jit` + `vmap` 批量并行 | 示例一次并行 1024 局；支持 Kuhn/Leduc/桥牌叫牌/麻雀麻将等 | 把随机性显式作为 `key` 传入，避免规划时信息泄漏 | 需写纯函数式规则，改造成本高 |
| RLCard | [GitHub](https://github.com/datamllab/rlcard) | 卡牌 RL 工具包 | 环境 + DMC/DQN/NFSP/CFR（chance sampling） | 规模表：无限注德州 infoset 10^162、斗地主 10^53~10^83、麻将 10^121、UNO 10^163 | 卡牌环境抽象与预训练模型清单 | 面向 RL 训练，不是均衡求解器 |

补充参照：Cepheus 用 CFR+「基本弱解」单挑限注德州，分析了 3.19×10^14 个决策点（Wikipedia「Computer poker player」）。这说明即使是被「解决」的最小扑克变体，也是 10^14 量级的离线工程。

## PIMC vs CFR vs 确定性完全信息搜索

| 维度 | PIMC / determinization | CFR 家族 | 确定性完全信息搜索（CombatSolver 现路线） |
|---|---|---|---|
| 核心假设 | 隐藏信息可由一个与观察一致的「世界」代表；每个世界内按完全信息处理 | 对手有私有信息且会针对你的策略；求近似均衡 | 状态全可见；随机性由种子/期望处理 |
| 做法 | 采样 K 个一致世界，对每个世界跑完全信息搜索，按世界权重汇总 | 迭代更新 infoset 上的后悔值，输出平均策略（或子博弈内重解） | Beam Search + 预算剪枝，直接对节点做启发式估值 |
| 适用条件 | 隐藏信息主要是「发牌/牌序」，对手不建模你的策略 | 需要不可利用性（对抗强手/竞技） | 单机、确定性战斗、需要可解释路线 |
| 算力 | K × 单次搜索（线性可调，最容易塞进 120s/12 万节点预算） | 表格式随 infoset 指数增长；子博弈式需抽象 + 每步重解 | 单次搜索，预算最好控 |
| 实现复杂度 | 低（复用现有搜索器 + 采样器） | 高（infoset 抽象、regret 存储、收敛判定） | 低（已有） |
| 可解释性 | 中（可给「最坏世界下的路线」） | 低（输出是概率策略，不是一条路线） | 高（一条具体路线 + 战损） |
| 主要风险 | 策略融合：同一动作在不同世界的最优理由不同，PIMC 会高估（领域共识，原始文献未抓取，标为未核实） | 抽象会带来可利用性；多人无保证 | 对隐藏信息「假装看得见」，路线偏乐观 |

## 对 CombatSolver 式项目的判断

1. **主线不必换：确定性完全信息搜索仍是正确基座。** CombatSolver 的 `SimulateAsync` 已用样本种子替换怪物组成、开局洗牌与战斗 RNG，说明随机性是可重放的单一随机流，不是对手私有信息。这类问题用 CFR 属于过度工程。
2. **只有「敌方私有信息 + 敌方会博弈」才值得上 CFR。** 若新游戏的隐藏信息只是抽牌顺序/牌堆，用 determinization + PIMC（判断 3）即可；只有出现「对手手牌隐藏且其出牌针对你的策略」时，才需要 regret 类方法，并应把范围压到单回合或有限回合的战术子博弈。
3. **PIMC 的具体落地方式**：把现有 Beam Search 当「单世界求解器」，从剩余牌池按已知信息约束重排 K 个世界，每个世界分配 1/K 预算（例如 8 个世界 × 15s），输出跨世界的期望/中位/最坏战损，并明确标注「最坏世界」路线。这能直接复用只读路线快照与 1–16 线程并行，改动只在采样层与汇总层。
4. **CFR 只能进小抽象子博弈，且必须先做抽象。** TexasSolver 的输入脚本本质是动作抽象（下注尺度集合、all-in 阈值），PioSOLVER 用同类配置把树压到 6 线程 4 分钟内可解。若新游戏要解「一回合内的出牌博弈」，得先定义信息集抽象（按手牌强度分桶）与动作抽象，否则参照 RLCard 的 10^162 量级，整局 CFR 无意义。
5. **神经网络路线（DeepStack/Libratus/ReBeL）不适合单机 CPU 实时场景。** 它们的共同前提是离线大规模训练：Libratus 蓝图 >15M 核心小时、Pluribus 8 天自对弈、ReBeL 多 GPU 自博弈。若确实要「叶子估值」，可行的是离线蒸馏一个极小网络 + ReBeL 式浅层递归（`mdp_depth 2` 量级），但这会把交付形态从「纯 CPU 单机」变成「需 GPU 推理」。
6. **可直接复用的产品化部件**：①以 exploitability/收敛度做质量指标（TexasSolver 每次迭代打印，CombatSolver 目前只报战损）；②树/分支配置化（把 Beam 的动作分支集合外置成配置）；③策略与 EV 的 JSON 导出（对应现有只读路线快照的序列化）；④树规模爆炸时的 `public` chance sampling（MCCFR 变体）与多线程 fork 参数，可给「1–16 线程 + 预算」提供现成的并行切分思路。
7. **需要显式兜底**：不完全信息下的「最优」是相对假设的，预算不足时 PIMC 的结论会剧烈波动。建议保留「最坏世界退化路径」，并在输出中标明世界数与置信区间，避免把采样结果包装成确定最优。

## 来源清单（实际抓取成功，均可复核）

- [Wikipedia: Libratus](https://en.wikipedia.org/wiki/Libratus) — 15M/4M 核心小时、CFR+、endgame solving、120,000 手、14.7 bb/100。（正文经 `en.wikipedia.org/w/api.php?action=query&prop=extracts&explaintext=1&titles=Libratus` 抓取）
- [Wikipedia: DeepStack](https://en.wikipedia.org/wiki/DeepStack) — 深度有限搜索 + 价值网络 + continual resolving；44,000 手、11 位职业选手、49 bb/100。
- [Wikipedia: Pluribus (poker bot)](https://en.wikipedia.org/wiki/Pluribus_(poker_bot)) — 6 人桌、基础策略 8 天/约 $144、>30 mbb/局、多人无强理论保证。
- [Wikipedia: Computer poker player](https://en.wikipedia.org/wiki/Computer_poker_player) — Cepheus 用 CFR+ 分析 3.19×10^14 决策点；PioSOLVER/PokerSnowie 作为 RTA 求解器的定位。
- [Wikipedia: Noam Brown](https://en.wikipedia.org/wiki/Noam_Brown) — Libratus（Science 2018）、Pluribus（Science 2019）、ReBeL（NeurIPS 2020）的论文出处与对局设置。
- [facebookresearch/ReBeL README](https://github.com/facebookresearch/ReBeL) — 仅开源 Liar's Dice、多 GPU 自博弈、`mdp_depth/subgame_iters/num_threads` 参数。
- [TexasSolver README（master）](https://github.com/bupticybee/TexasSolver/blob/master/README.md) — 172s vs 242s、1600MB vs 492MB、0.275% vs 0.29%、AGPLv3、GPU 版「>20×」。
- [TexasHoldemSolverJava README](https://github.com/bupticybee/TexasHoldemSolverJava/blob/master/README.md) — `cfr`/`cfr_plus`/`discounted_cfr` 三种算法选项、`public` chance sampling 与 `threads`/fork 参数、与 PioSOLVER 差异来源的解释（"different tree construction logic and insufficient CFR converge"）。
- [TexasSolver console 分支 README](https://github.com/bupticybee/TexasSolver/blob/console/README.md) — 输入脚本语法、迭代 exploitability 日志、`output_result.json`、`set_accuracy/set_max_iteration`。
- [TexasSolver benchmark 的 PioSOLVER 配置](https://github.com/bupticybee/TexasSolver/blob/master/benchmark/benchmark_piosolver.txt) — 牌面/底池/筹码/下注尺度。
- [OpenSpiel README](https://github.com/google-deepmind/open_spiel/blob/master/README.md)、[concepts.md](https://github.com/google-deepmind/open_spiel/blob/master/docs/concepts.md)、[games.md](https://github.com/google-deepmind/open_spiel/blob/master/docs/games.md)、[algorithms.md](https://github.com/google-deepmind/open_spiel/blob/master/docs/algorithms.md) — 框架定位、Game/State/chance/infoset、102 条游戏记录、算法与文献清单。
- [OpenSpiel cfr.py](https://github.com/google-deepmind/open_spiel/blob/master/open_spiel/python/algorithms/cfr.py)、[discounted_cfr.py](https://github.com/google-deepmind/open_spiel/blob/master/open_spiel/python/algorithms/discounted_cfr.py)、[deep_cfr.py（v1.1.0）](https://github.com/google-deepmind/open_spiel/blob/v1.1.0/open_spiel/python/algorithms/deep_cfr.py) — CFR 三步骤、RM+ 截断、DCFR 的 α/β/γ 公式、Deep CFR 双网络 + reservoir buffer。
- [PokerKit README.rst](https://github.com/uoftcprg/pokerkit/blob/main/README.rst) — 纯 Python、Python 3.11+、99% 覆盖率、`Automation`/`Mode` API。
- [Pgx README](https://github.com/sotetsuk/pgx/blob/main/README.md) — JAX-native、`jit`+`vmap`、1024 并行、随机性显式 `key`。
- [RLCard README](https://github.com/datamllab/rlcard/blob/master/README.md) — infoset 规模表（10^162 等）、DMC/DQN/NFSP/CFR。

## 未核实清单

- DeepStack 的训练算力（GPU 数量/天数）：抓取页面只写「网络在小数量状态上训练」，无硬件数字。
- PioSOLVER 的内部分层/抽象设计：仅从 TexasSolver 的对比 benchmark 得到外部行为数据，未取得官方文档。
- Pluribus 的硬件与总训练算力：只有「8 天、约 $144」与「>30 mbb/局」，无 GPU 数。
- ReBeL 的训练成本与扑克规模结果：README 仅覆盖 Liar's Dice，未给算力数字。
- SD-CFR（Single Deep CFR）：OpenSpiel 无对应实现，本文件未展开，相关论断不作数。
- PIMC 的「策略融合/非局部性」批评：未抓到原始文献，按领域共识陈述并已标注。
- TexasSolver GPU 版细节：README 仅一句「>20× faster」，未核验其算法与硬件要求。

# 博弈引擎技术栈调研：国际象棋与围棋，及其对卡牌战斗搜索的迁移判断

> 调研对象：Stockfish、Leela Chess Zero、AlphaZero、Ethereal（国际象棋）；AlphaGo / AlphaGo Zero / AlphaZero、KataGo、Leela Zero、Pachi（围棋）。数字均标注来源，无法核实者文末单列。"源码"指实际抓取的仓库文件（URL 见来源清单）。

## 结论摘要

1. **两条路线的分野是"每个节点携带多少信息"，不是搜索速度**。AlphaZero 下象棋每秒约搜 8 万个局面，Stockfish 约 7000 万个，差近三个数量级；但 1000 局正式比赛是 AlphaZero 155 胜 6 负 839 和，且 Stockfish 需 10:1 时间让步才能打平。决定棋力的是单位预算换来的决策质量。
2. **可迁移的是四件基础设施而非具体算法**：确定性工作量预算、带深度/边界标记的状态表、并行提交与虚拟损失、SPRT 回归门禁。这些机制在棋种间形式几乎相同，说明与博弈结构无关。
3. **alpha-beta 与 MCTS 的核心假设（完全信息、零和、交替行动、转移已知）在卡牌里全部失效**，不能整体移植；但 PUCT 的"按访问计数分配预算"与"根并行"可嫁接在束搜索上。
4. **NNUE 的可迁移点是"增量评估 + CPU 友好"，不是网络本身**。前提是"一步只动一个棋子"，卡牌不满足这种局部性，需退化为"特征桶 + 浅层模型"。
5. **自对弈不是唯一冷启动路径**：KataGo 用约 1.4 GPU 年达到 ELF 约 74 GPU 年的水平（约 50 倍效率）；"高预算搜索生成数据 → 蒸馏低预算策略"比无限自对弈更适合单机预算。

## 一、国际象棋引擎

| 机制 | 代表实现 | 为什么有效 |
| --- | --- | --- |
| PVS / 零窗口 | Stockfish 19 步有序搜索（`src/search.cpp`） | 零宽窗口先验证"这步是否更好"，失败才全窗口重搜；多数走法在验证阶段被否掉，省下的节点远多于重搜成本 |
| 置换表（TT） | Stockfish（上限 32 TB，generation 老化）、Ethereal（建议 64 MB/线程/分钟） | 同局面经不同顺序到达时复用结论；必须同存 value、depth、bound，否则浅搜结论会冒充深搜 |
| LMR | Stockfish：减深量随深度、走法序号、improving 增长（源码 1181 行），按 1/1024 深度单位保存以便给 futility/SEE 阈值复用 | 赌"靠后的走法大概率不最优"；赌错时 fail-high 触发全深度重搜，故是"期望无损"：只损失被忽略走法的机会成本 |
| Null move / ProbCut | Stockfish：probCutBeta = beta + 241 − 64×improving，用 depth−3/−5 缩减搜索预判 | Null move 赌"让对手先走都赢不了，这局面够好"；对 zugzwang 失效，故高深度加 verification search。ProbCut 赌"超界大概率真超界" |
| Singular extension | Stockfish：singularBeta = ttValue − (59 + 66×ttPv)×depth/63 | 排除置换表最佳走法做缩减搜索，若其余走法都明显更差，则该走法唯一，于是加深而非剪枝 |
| 预算再分配 | Stockfish（584–632 行）：fallingEval、timeReduction（距上次最优走法改变的深度）、bestMoveInstability = 1.077 + 2.229×改动数/线程数 | 用搜索自身的不稳定度决定要不要继续：评估在掉、最优走法反复变说明没看透，加时；稳定则提前收手 |
| NNUE | Stockfish 12 起；2023-07 删除手工评估 | 首层稀疏线性，一步只改少量输入，只加减变化项即得新累加器，推理成本摊到近零；代价是节点率下降，棋力净增极大 |
| SPRT + 分布式回归 | Fishtest（累计 20,700 CPU 年、103 亿局）、OpenBench | 序贯概率比检验在"提升 0"与"提升 δ"间尽早停机，配合卡方校验筛噪声 |

### 1.1 搜索-评估分工

Stockfish 是"便宜评估 × 极多节点"：叶节点用 NNUE（整数、SIMD、增量更新）把单次评估压到极低，于是每秒数千万节点。AlphaZero 是"昂贵评估 × 极少节点"：一次 4 TPU 推理给出策略先验与胜率，用先验把搜索压到少数分支。两者战绩接近，说明**搜索广度与评估深度可互换**，选哪侧取决于硬件与状态空间形状——CPU 单机更适合前者。

NNUE 架构（SF 16.1 源码）：特征集 HalfKAv2_hm 把"王的位置"折叠成桶（`KingBuckets` 最大索引 31），每桶 384 维输入（64 格 × 12 棋型 ÷ 2 横向镜像），各视角一个 2560 维累加器，后接 L2=15、L3=32，按 8 个 layer stack 分堆输出。当前 master 换为 K32Q2 + PP_3Wide + FullThreats，首层 1024 维、L2=L3=32、32 个 layer stack。**"按局面类型分堆选输出层"比网络宽度更值得记**：等价于用状态特征选局部模型。

### 1.2 评测生态

进步速度来自评测基础设施：TCEC / CCRL / CCC 提供外部排名（Stockfish 截至 2026-10 在 CCRL 40/15 约 3650 Elo），Fishtest 提供内部回归；UCI（2000-11）解耦引擎与界面，使批量自动对局可行。LC0 走规模化路线：截至 2025-06 自对弈超 25 亿局、约 100 万局/天，2022 起改用 transformer。

## 二、围棋引擎

| 组件 | 代表实现 | 为什么有效 |
| --- | --- | --- |
| MCTS + PUCT | AlphaZero：argmax Q(a) + c·P(a)·√ΣN(b)/(1+N(a))，c≈1.1（KataGo 论文） | 分母 1+N 让"访问少"的子节点天然获补偿，分子 √ΣN 让补偿随总预算缓增；策略先验 P 把有限补偿集中到合理分支，故 19×19 的巨分支因子下仍可用 |
| Dirichlet 根噪声 | AlphaZero：α=0.3/0.15/0.03（象棋/将棋/围棋） | 只加根节点，防自对弈反复走同一条线；按合法走法数反比缩放，避免小分支局面被噪声主导 |
| 虚拟损失 | KataGo `cpp/search/search.cpp`（`virtualLosses.fetch_add`，选子后先加再评估） | 多线程同时下降会重复选中同一节点；先记一笔"假访问"临时压低其 PUCT，把其他线程赶到别处。代价是值估计偏悲观，回传时撤销 |
| 批量推理 | KataGo JSON 分析引擎可批量评估多盘；Leela Zero 把"改进 GPU batching"列为待办；KataGo 六种后端 | 单次推理固定开销远大于边际开销，凑批能把 GPU 利用率从个位数提到高位；工程上须把搜索与推理队列解耦 |
| 树复用 | KataGo：`treeReuseCarryOverTimeFactor`；`clear_cache` 同时清树与 NN 缓存 | 对手走完后其子树仍是有效结果，直接继承省重复搜索；需显式清理，否则旧树污染新局面统计 |
| 辅助训练目标 | KataGo：score、ownership、6/16/50 回合短期价值头 | 终局胜负每局只有一个二值噪声信号，方差极大；短期目标提供低方差梯度，主头收敛更快（偏差-方差权衡），并支撑"不确定性加权" |
| 动态 cPUCT / 不确定性加权 | KataGo v1.9.0：按节点效用方差缩放 cPUCT；按网络自报误差给每个 playout 加权 | 常数 cPUCT 隐含"效用差值的量纲固定"，但局面间胜率差异尺度差很多；按方差缩放等于自动调探索强度。两项合计约 +75 Elo |
| 子树价值偏差修正 | KataGo：λ=0.35、α=0.8，按局部棋形分桶回灌"网络相对子树的误差"，约 30–60 Elo | 在线学习"网络在某类局部战术上系统性偏乐观/悲观"；只学相对误差非绝对值，故不会退化成 RAVE 的粗糙启发式 |
| 固定访问数作预算 | Pachi：`-t =5000`、`max_tree_size` 以 MB 计 | 把"想多久"换成"想多少"，棋力与硬件速度解耦：慢机器只是更慢，不会更弱。这是预算账本的最简形式 |

### 2.1 训练范式与算力账

AlphaGo（2015–2016）是"监督策略网 + 价值网 + MCTS"，分布式版用 1202 CPU + 176 GPU。AlphaGo Zero 去掉人类棋谱，输入 17 个平面（8+8 历史 + 执子方），ResNet 20/40 块 × 256 通道，双头（策略、价值），64 GPU worker + 19 CPU 参数服务器，3 天自对弈 490 万局。AlphaZero 推广到象棋/将棋：700k 步 × 4096 批、5000 个一代 TPU 生成 + 64 个二代 TPU 训练，象棋 9 小时、将棋 12 小时、围棋 34 小时，象棋自对弈 4400 万局。

算力对比很说明问题：AlphaGo Zero 围棋主跑约 41 TPU 年；ELF OpenGo 约 2000 张 V100 跑 13–14 天（约 74 GPU 年）；KataGo 最多 28 张 V100、19 天（约 1.4 GPU 年、420 万局、2.41 亿样本）即超过 ELF 最终模型。效率差 50 倍，主要来自与领域无关的工程改进：playout 上限随机化、强制 playout + 策略目标剪枝、辅助目标、权重滑动平均与门禁。

### 2.2 "访问数 → 棋力"的量级关系

AlphaZero 训练时每手 800 次模拟，评估按根访问数贪心选点；KataGo 用 c_PUCT=1.1、满搜索上限 600→1000 次访问。Pachi 的对照更直观：带策略网络时 5000–15000 playouts 约 KGS 3d，无 dcnn 约 KGS 2k，64 台 × 20 核集群到 KGS 5d。即**同预算下策略质量决定档位，策略固定后加访问数只带来对数级增长**；缺排序先验时，加大 Beam 只是把对数曲线拉平。

## 三、跨引擎工程共性

| 维度 | 国际象棋 | 围棋 | 共性结论 |
| --- | --- | --- | --- |
| 搜索树内存 | TT 可配到 32 TB | 树节点 + NN 缓存；Pachi 用 MB 级硬上限 | 都需要"容量硬上限 + 老化/淘汰"，否则长会话必然内存爆炸；容量与棋力收益递减 |
| 并行 | 根并行 + 共享 TT + 主线程投票 | 树并行 + 虚拟损失；或根并行（多棵独立树，根层合并） | 根并行天然确定性（只在根层合并），树并行省树但需处理写冲突与重复投入 |
| 推理批处理 | CPU 端 NNUE 靠 SIMD 与小整数，无批概念 | GPU/TPU 必须凑批，批量决定吞吐 | 批量是降低单位推理开销的手段；CPU 场景应改为降低单位状态评估开销（缓存、增量、特征复用） |
| 预算控制 | 时间管理 + `Move Overhead` + 节点上限 | 固定 playouts/访问数 | 时间不可复现，工作量（节点/访问/扩展）才可复现；两者分层：外层租约用时间，内层记账用工作量 |
| 外部知识缓存 | Syzygy：7 子从 140 TB 压到 18.4 TB，WDL 小到可放 SSD 供搜索中查，DTZ 只在根用 | 无残局库，靠网络与搜索本身 | 缓存按"查询频率 × 体积"分层：高频小表进内存通路，低频大表只在根用；AlphaZero 明确不用开局库/残局库，说明缓存是加速器而非必需 |
| 自对弈规模 | LC0：25 亿局，约 100 万局/天 | AGZ 490 万局/3 天；KataGo 420 万局/19 天 | 数据量与"每局信息密度"可互换；门禁测试（200 局赢 100 局才换网）比绝对数量更重要 |
| 评测 | SPRT、Elo + 置信区间、TCEC/CCRL 外部榜 | 门禁对局 + 分布式 rating 对局 | 没有统计门禁的"变强了"不可信；评测必须能在有限算力下早停 |

协议层：UCI 与 GTP 把引擎变成可替换黑盒，KataGo 的 `kata-set-rules`、`clear_cache` 更把"规则/参数版本"与"搜索结果"绑定成可审计单元。

## 四、对 CombatSolver 式卡牌求解器的 8 条迁移建议

1. **预算定义成"唯一工作量"而非时间（直接可用）**。Pachi 用固定 playouts 让"慢机器只是慢"，Stockfish 同时提供时间管理与节点上限。CombatSolver 已有 `SearchBudgetLedger`，应强化为展开次数/评估次数/分配字节三类计数各自单调可复现，时间只做外层租约。验收：同根、同政策、同计数预算下路线逐字节一致。
2. **预算再分配跟随"不稳定度"（需改造）**。Stockfish 用 fallingEval、最优走法改动次数、距上次改动的深度缩放思考时间。卡牌侧对应量是：最优路线被替换的次数、最优与次优的质量差、前沿 Pareto 覆盖度变化。要点是**分配必须基于已提交的只读快照**，不能用线程时序或 wall-clock 抖动。
3. **状态表带"预算层级 + 边界类型"（需改造）**。TT 存 value + depth + bound + generation，浅搜结论绝不冒充深搜。卡牌里"同状态不同预算""同状态不同政策"值不同，状态键须纳入政策/预算层级，并区分精确值/上界/下界；同时引入 generation 老化。`ContinuationStamp` 与转置支配已在做类似事，缺的是**老化与容量硬上限**。
4. **并行扩展用"虚拟损失"替代事后去重（直接可用）**。KataGo 在选子后、评估前先记假访问，防止多线程重复投入。CombatSolver 已有"固定 worker lane + 确定性提交"，可给"已准入未提交"的前沿节点打临时惩罚，让其他 lane 转向别处，提交时撤销——纯记账改动，按准入顺序而非完成顺序施加即天然确定。
5. **做"分桶浅层评估"而非直接上 NNUE（需改造）**。NNUE 前提是首层线性 + 一步只改少量输入。卡牌一次出牌同时改能量、手牌、牌堆、Power、格挡、敌人意图，增量更新只在 HP/格挡/能量成立。路径：手工特征 → 按局面类型/敌人/职业分桶（对应 layer stack）→ 每桶一个极小线性模型，离线用高预算搜索结果蒸馏。
6. **用 PUCT 的"访问计数"改造 Beam 保留（需改造）**。当前 Beam 是 top-k + Pareto，问题是每个候选只被看一次。可引入"每节点已投入预算"字段，对同分候选按 1/(1+投入) 加权分配下一轮预算，把静态宽度换成"预算随边际收益流动"。注意随机性：单次模拟只是采样，节点值必须存均值与方差，否则 PUCT 会追噪声。
7. **建 SPRT 风格的多目标回归门禁（直接可用）**。Fishtest 用 SPRT + 卡方校验，在 103 亿局规模上仍能早停。卡牌侧目标是向量（胜率、平均战损、回合数、必保资源），SPRT 只检验单变量，做法是固定其余指标对主指标做序贯检验，或用"帕累托支配对数"做复合统计量。关键是**预设接受/拒绝边界并记录样本量**。
8. **自对弈冷启动替换为"求解器自博弈 + 蒸馏"（需改造）**。围棋自对弈成立是因为转移确定、对手即自己；卡牌有隐藏信息（抽牌堆、敌人 AI），真自对弈需要信念状态与对手模型，成本极高。更现实的是 KataGo 路线：高预算求解器在固定种子集上生成路线，把"状态特征 → 首选动作"蒸馏成低预算策略，再用门禁对局验收。直接照搬 AlphaZero 式自对弈（不适用）。

另两条明确"不适用"：**alpha-beta 整体框架**（要求完全信息、零和、交替行动，且依赖"对手最优"；卡牌里敌人 AI 是确定但不可知的程序）；**null move / 静态搜索类启发式**（"跳过一手"在卡牌里等于结束回合，通常直接输）。

## 来源清单

| URL | 支撑论断 |
| --- | --- |
| https://en.wikipedia.org/wiki/Stockfish_(chess) | CCRL 40/15 约 3650 Elo（2026-10）、TCEC 19 冠、Fishtest 20,700 CPU 年/103 亿局、NNUE 自 SF12 起与 2023-07 删除手工评估、TT 上限 32 TB |
| https://en.wikipedia.org/wiki/AlphaZero | 5000 一代 TPU + 64 二代 TPU、700k×4096、9 小时、80k vs 70M 局面/秒、1000 局 155-6-839、10:1 时间让步 |
| https://en.wikipedia.org/wiki/Leela_Chess_Zero | 25 亿局自对弈（2025-06）、约 100 万局/天、2022 起 transformer、多后端 |
| https://en.wikipedia.org/wiki/NNUE | 特征相对王位置、首层线性故可增量更新、SIMD 整数推理、以节点率换棋力 |
| https://en.wikipedia.org/wiki/Syzygy_tablebase | 7 子 18.4 TB（对比 Lomonosov 140 TB）、WDL 可放 SSD、DTZ 用于根、8 子约 2 PB 的可行性估计 |
| https://en.wikipedia.org/wiki/Universal_Chess_Interface | 2000-11 发布、长代数记谱、`uci_elo`/`uci_limitstrength`、USI/UCCI 变体 |
| https://en.wikipedia.org/wiki/AlphaGo | 分布式版 1202 CPU + 176 GPU、策略网/价值网 + MCTS |
| https://en.wikipedia.org/wiki/AlphaGo_Zero | 17 平面输入、20/40 块×256 通道、64 GPU worker + 19 CPU 参数服务器、3 天 490 万局 |
| https://en.wikipedia.org/wiki/KataGo | 首版 2019-02、辅助目标（score/ownership）、任意贴目与让子、b28c512（2024-08）、19 天/≤28 V100/420 万局 |
| https://en.wikipedia.org/wiki/Monte_Carlo_tree_search | UCT 收敛到 minimax、根/叶/树三种并行化定义、"选择性展开会漏看陷阱" |
| https://raw.githubusercontent.com/official-stockfish/Stockfish/master/src/search.cpp | 19 步搜索顺序、LMR 减深与 lmrDepth 复用、ProbCut beta 公式、singular 公式、fallingEval/timeReduction/bestMoveInstability、Move Overhead |
| https://raw.githubusercontent.com/official-stockfish/Stockfish/master/src/nnue/nnue_architecture.h | 当前架构：K32Q2/PP_3Wide/FullThreats、L1=1024、L2=L3=32、32 layer stack |
| https://raw.githubusercontent.com/official-stockfish/Stockfish/sf_16.1/src/nnue/nnue_architecture.h | SF16.1：HalfKAv2_hm、Big 网 2560 维累加器、L2=15、L3=32、8 layer stack |
| https://raw.githubusercontent.com/official-stockfish/Stockfish/sf_16.1/src/nnue/features/half_ka_v2_hm.h | 特征维度定义（64 格×12 棋型÷2）、KingBuckets 最大索引 31、MaxActiveDimensions=32 |
| https://raw.githubusercontent.com/lightvector/KataGo/master/docs/KataGoMethods.md | 子树价值偏差修正（λ=0.35/α=0.8，30–60 Elo）、动态方差缩放 cPUCT、不确定性加权（合计约 +75 Elo）、短期价值目标 |
| https://raw.githubusercontent.com/lightvector/KataGo/master/cpp/search/search.cpp | 虚拟损失实现（`virtualLosses` 原子增减、选子后先加）、`treeReuseCarryOverTimeFactor` 树复用 |
| https://raw.githubusercontent.com/lightvector/KataGo/master/README.md | 六种后端、v1.17 支持 transformer、JSON 分析引擎批量评估、单卡消费级 GPU 数月可达超人类 |
| https://raw.githubusercontent.com/lightvector/KataGo/master/docs/GTP_Extensions.md | GTP 扩展命令、`kata-set-rules` 运行时换规则、`clear_cache` 清树与 NN 缓存 |
| https://raw.githubusercontent.com/pasky/pachi/master/README.md | UCT+RAVE+Moggy playout、dcnn 只用于根、固定 playouts 档位（5000–15000→KGS 3d；无 dcnn→2k）、64 台×20 核→KGS 5d、`max_tree_size` 以 MB 计 |
| https://raw.githubusercontent.com/AndyGrant/Ethereal/master/README.md | alpha-beta + NNUE 参照实现、OpenBench（Fishtest 派生）为测试平台、哈希 64 MB/线程/分钟建议、MultiPV 仅用于分析 |
| https://raw.githubusercontent.com/LeelaChessZero/lc0/master/README.md | 多后端（CBLAS/OpenBLAS/DNNL/CUDA/cuDNN/HIP/ROCm/ONNX/Metal/SYCL） |
| https://raw.githubusercontent.com/leela-zero/leela-zero/master/README.md | 搜索中的 GPU batching 列为待改进项（说明批量推理是独立工程问题） |
| https://ar5iv.labs.arxiv.org/html/1712.01815 | AlphaZero 论文：800 simulations/手、Dirichlet α={0.3,0.15,0.03}、700k 步×4096、象棋 4400 万局/9 小时、4 TPU + 44 核、按根访问数贪心选点 |
| https://ar5iv.labs.arxiv.org/html/1902.10565 | KataGo 论文：41 TPU 年（AGZ）vs 74 GPU 年（ELF）vs 1.4 GPU 年（KataGo）、c_PUCT=1.1、playout 上限随机化 0.25/600/100、批大小 256、420 万局/2.41 亿样本、门禁 200 局赢 100 局 |

### 未验证 / 需注意

- **"NNUE 原型节点率减半"**：来自维基百科叙述，未见原始测量。
- **Pachi 的 KGS 段位**：README 自述，未经第三方评级核实；同 README 内不同配置不构成严格对照。
- **TT 上限 32 TB、Fishtest 20,700 CPU 年/103 亿局**：维基百科"截至 2026 年 10 月"口径，未与官方站点交叉核对。
- **Stockfish 当前 master 的 NNUE 输入索引空间总大小**：文中只给可推导结构（桶数、单桶维度、累加器宽度），未断言完整维度。
- **AlphaZero 1000 局比分与 10:1 时间让步**：源自维基百科转述 Science 论文，未读原文表格。
- **"Elo 与访问数呈对数关系"**：由 Pachi 档位表与 AlphaZero/KataGo 固定访问设置归纳的定性判断，无单一论文给出拟合曲线。
- **GTP 规范原文**：该维基条目在本会话反复限流（429），命令集与语义仅由 KataGo 文档侧证。
- **环境限制**：本会话 `web_search` / `web_fetch` 失效（HTTP 401），全部抓取经 `_research/fetch.ps1` 完成；`api.github.com` 403 未使用。

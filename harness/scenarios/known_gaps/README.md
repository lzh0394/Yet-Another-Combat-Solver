# 已知缺口（不参与 CI 对拍）

这些场景已经用真机录制（`tests/vectors/real/known_gaps/`），但引擎尚未复刻对应的机制，
逐字段对拍会失败。保留轨迹是为了修好机制后可以直接回归，不必重新录制。

回归命令：

```bash
dotnet run --project src/Sts2.Cli -c Release -- run \
  --scenario harness/scenarios/known_gaps/<name>.json \
  --pack data/sts2 --out artifacts/engine/<name>.jsonl
python tools/compare_snapshots.py \
  --a tests/vectors/real/known_gaps/<name>.jsonl \
  --b artifacts/engine/<name>.jsonl
```

| 场景 | 缺失机制 |
|---|---|
| `ironclad_thieving_hopper_weak` | `CardDebuffIntent` 偷牌（按稀有度从抽/弃牌堆选卡移出战斗），逃脱时带走 |
| `ironclad_corpse_slugs_weak` | `RavenousPower`：同侧怪物死亡时存活者 +4 力量并眩晕一回合（`CreatureCmd.Stun`） |
| `ironclad_punch_off` | 玩家侧 Debuff 的层数递减时机（真机在玩家回合结束时递减，引擎目前在敌方回合结束统一递减） |
| `ironclad_flyconid` | 同上（Frail/Weak 在玩家侧的持续时间），以及 Flyconid 的孢子能力层数增长 |
| `ironclad_strangler` | 同上（玩家侧 Debuff 持续时间导致伤害差异） |
| `ironclad_ovicopter` | Ovicopter 的召唤（ToughEgg）与 `Rng.Niche` 消耗顺序 |

# 覆盖报告

- 数据包：schema 2，游戏 v0.111.0 （commit 41cef1ea）

## 卡牌

- 总数 596，结构化（`effect_complete: true`）**557**
- 其中可打出 526、不可打出 31
- 引擎冒烟：526 张可打出、31 张不可打出、0 张失败
- op 分布：`power` 258、`damage` 188、`block` 72、`draw` 46、`add_card` 32、`channel` 24、`energy` 21、`upgrade` 11、`hp_loss` 11、`move_card` 11、`stars` 9、`summon` 8、`discard` 7、`auto_play` 7、`evoke` 6、`exhaust_random` 4、`exhaust` 3、`orb_passive` 3、`exhaust_hand` 3、`put_back` 3、`max_hp` 2、`orb_slots` 2、`play_top` 2、`duplicate_pick` 2、`heal` 2、`apply_keyword` 2、`return_from_discard` 1、`add_from_draw` 1、`kill_osty` 1、`orb_remove` 1、`discard_hand_draw` 1、`energy_current` 1、`draw_retained` 1、`lose_block` 1、`remove_power` 1、`gold` 1、`draw_until` 1、`discard_drawn` 1、`discard_all` 1、`discard_hand` 1、`end_turn` 1、`stun` 1

## 怪物招式

- 招式总数 389，完整解析 334，含数值效果 304

## 能力与遗物

- 能力 265 个（带 `vars` 的 37）
- 遗物 299 个（带 `vars` 的 221）

## 遭遇

- 遭遇总数 90，数据内已列出怪物 77
- 引擎冒烟：64 个可启动、0 个显式不支持、0 个失败

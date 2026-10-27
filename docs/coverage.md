# 覆盖报告

- 数据包：schema 2，游戏 v0.111.0 （commit 41cef1ea）

## 卡牌

- 总数 596，结构化（`effect_complete: true`）**488**
- 其中可打出 457、不可打出 31
- 引擎冒烟：457 张可打出、31 张不可打出、0 张失败
- op 分布：`power` 249、`damage` 147、`block` 68、`draw` 36、`add_card` 24、`channel` 22、`energy` 19、`move_card` 11、`discard` 9、`upgrade` 9、`stars` 9、`hp_loss` 9、`summon` 8、`evoke` 6、`exhaust_random` 4、`exhaust` 3、`orb_slots` 2、`play_top` 2、`heal` 2、`draw_to` 1、`discard_all` 1

## 怪物招式

- 招式总数 389，完整解析 334，含数值效果 304

## 能力与遗物

- 能力 265 个（带 `vars` 的 37）
- 遗物 299 个（带 `vars` 的 221）

## 遭遇

- 遭遇总数 90，数据内已列出怪物 77
- 引擎冒烟：64 个可启动、0 个显式不支持、0 个失败

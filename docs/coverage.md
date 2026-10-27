# 覆盖报告

- 数据包：schema 2，游戏 v0.111.0 （commit 41cef1ea）

## 卡牌

- 总数 596，结构化（`effect_complete: true`）**470**
- 其中可打出 439、不可打出 31
- 引擎冒烟：439 张可打出、31 张不可打出、0 张失败
- op 分布：`power` 249、`damage` 140、`block` 66、`draw` 35、`add_card` 24、`channel` 22、`energy` 19、`discard` 9、`stars` 9、`hp_loss` 9、`summon` 8、`upgrade` 8、`evoke` 6、`exhaust` 3、`orb_slots` 2、`move_card` 2、`heal` 2、`discard_all` 1

## 怪物招式

- 招式总数 389，完整解析 334，含数值效果 304

## 能力与遗物

- 能力 265 个（带 `vars` 的 37）
- 遗物 299 个（带 `vars` 的 221）

## 遭遇

- 遭遇总数 90，数据内已列出怪物 77
- 引擎冒烟：64 个可启动、0 个显式不支持、0 个失败

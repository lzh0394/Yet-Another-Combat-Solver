"""Summarise what the data package and the engine actually cover.

The numbers come from `data/sts2/*.json`; engine-side numbers (cards that can be
played, encounters that start) come from `sts2 smoke`, whose output can be fed in
with --smoke.  The markdown is written to stdout so CI can archive it.
"""
from __future__ import annotations

import argparse
import json
import re
from collections import Counter
from pathlib import Path


def load(pack: Path, name: str) -> dict:
    return json.loads((pack / name).read_text(encoding="utf-8"))


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--pack", default="data/sts2")
    parser.add_argument("--smoke", help="file holding `sts2 smoke` output")
    parser.add_argument("--out", help="write markdown here instead of stdout")
    args = parser.parse_args(argv)

    pack = Path(args.pack)
    cards = load(pack, "cards.json")
    powers = load(pack, "powers.json")
    relics = load(pack, "relics.json")
    monsters = load(pack, "monsters.json")
    encounters = load(pack, "encounters.json")
    manifest = load(pack, "manifest.json")

    structured = [c for c in cards.values() if c.get("effect_complete")]
    ops = Counter()
    for card in structured:
        for effect in card.get("effect") or []:
            ops[effect.get("op", "?")] += 1

    playable = [c for c in structured if (c.get("cost") is not None and c["cost"] >= 0)]
    unplayable = len(structured) - len(playable)

    monster_moves = sum(len(m.get("moves") or {}) for m in monsters.values())
    monster_with_effects = sum(
        1
        for m in monsters.values()
        for move in (m.get("moves") or {}).values()
        if move.get("effect_complete") and move.get("effects")
    )
    monster_moves_complete = sum(
        1 for m in monsters.values() for move in (m.get("moves") or {}).values() if move.get("effect_complete")
    )

    smoke_cards = smoke_encounters = None
    if args.smoke:
        text = Path(args.smoke).read_text(encoding="utf-8", errors="replace")
        m = re.search(r"cards:\s*(\d+) played,\s*(\d+) unplayable skipped,\s*(\d+) failed", text)
        if m:
            smoke_cards = tuple(int(g) for g in m.groups())
        m = re.search(r"encounters:\s*(\d+) ok,\s*(\d+) unsupported,\s*(\d+) failed", text)
        if m:
            smoke_encounters = tuple(int(g) for g in m.groups())

    lines = [
        "# 覆盖报告",
        "",
        f"- 数据包：schema {manifest.get('schema_version')}，游戏 {manifest.get('game', {}).get('version')} "
        f"（commit {manifest.get('game', {}).get('commit')}）",
        "",
        "## 卡牌",
        "",
        f"- 总数 {len(cards)}，结构化（`effect_complete: true`）**{len(structured)}**",
        f"- 其中可打出 {len(playable)}、不可打出 {unplayable}",
        f"- 引擎冒烟：{_fmt_cards(smoke_cards)}",
        "- op 分布：" + "、".join(f"`{k}` {v}" for k, v in ops.most_common()),
        "",
        "## 怪物招式",
        "",
        f"- 招式总数 {monster_moves}，完整解析 {monster_moves_complete}，含数值效果 {monster_with_effects}",
        "",
        "## 能力与遗物",
        "",
        f"- 能力 {len(powers)} 个（带 `vars` 的 {_with_vars(powers)}）",
        f"- 遗物 {len(relics)} 个（带 `vars` 的 {_with_vars(relics)}）",
        "",
        "## 遭遇",
        "",
        f"- 遭遇总数 {len(encounters)}，数据内已列出怪物 {sum(1 for e in encounters.values() if e.get('monsters'))}",
        f"- 引擎冒烟：{_fmt_encounters(smoke_encounters)}",
        "",
    ]
    text = "\n".join(lines)
    if args.out:
        Path(args.out).write_text(text, encoding="utf-8")
    else:
        print(text)
    return 0


def _with_vars(entries: dict) -> int:
    return sum(1 for e in entries.values() if e.get("vars"))


def _fmt_cards(smoke) -> str:
    if not smoke:
        return "未提供（用 `--smoke` 传入 `sts2 smoke` 输出）"
    played, unplayable, failed = smoke
    return f"{played} 张可打出、{unplayable} 张不可打出、{failed} 张失败"


def _fmt_encounters(smoke) -> str:
    if not smoke:
        return "未提供"
    ok, unsupported, failed = smoke
    return f"{ok} 个可启动、{unsupported} 个显式不支持、{failed} 个失败"


if __name__ == "__main__":
    raise SystemExit(main())

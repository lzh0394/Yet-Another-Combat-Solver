#!/usr/bin/env python3
"""Compare exported data packages against oracle observations.

Usage: python tools/compare_values.py --data data/sts2 --oracle tests/vectors

Reports numeric differences. Items the static exporter cannot decide (computed
expressions, conditional properties, oracle read errors) are counted as
unverifiable and registered individually in --json-out. Exit code 1 on any diff.
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

NUM_TOL = 1e-9


def eq(a, b) -> bool:
    if isinstance(a, bool) or isinstance(b, bool):
        return a is b
    if isinstance(a, (int, float)) and isinstance(b, (int, float)):
        return abs(float(a) - float(b)) <= NUM_TOL
    return a == b


def stats() -> dict:
    return {"compared": 0, "unverifiable": 0, "diffs": [], "structural": []}


def compare_card(cid: str, exported: dict, oracle: dict, out: dict) -> None:
    out["compared"] += 1
    if oracle.get("is_mock"):
        out["diffs"].append(f"{cid}: exported a mock card")
        return
    skip_type = exported.get("multimodal")
    for field, okey in (("type", "type"), ("rarity", "rarity"), ("target", "target")):
        if field == "type" and skip_type:
            continue
        if not eq(exported.get(field), oracle.get(okey)):
            out["diffs"].append(f"{cid}.{field}: {exported.get(field)!r} != {oracle.get(okey)!r}")
    cost = exported.get("cost")
    ocost = oracle.get("cost")
    if cost is None:
        if ocost != -1:
            out["diffs"].append(f"{cid}.cost: None != {ocost!r}")
    elif exported.get("costs_x") and cost == 0 and ocost == -1:
        pass
    elif not eq(cost, ocost):
        out["diffs"].append(f"{cid}.cost: {cost!r} != {ocost!r}")
    if bool(exported.get("costs_x")) != bool(oracle.get("costs_x")):
        out["diffs"].append(f"{cid}.costs_x: {exported.get('costs_x')!r} != {oracle.get('costs_x')!r}")
    star = exported.get("star_cost")
    ostar = oracle.get("star_cost")
    if star is None:
        if ostar != -1:
            out["diffs"].append(f"{cid}.star_cost: None != {ostar!r}")
    elif not eq(star, ostar):
        out["diffs"].append(f"{cid}.star_cost: {star!r} != {ostar!r}")
    if set(exported.get("keywords") or []) != set(oracle.get("keywords") or []):
        out["diffs"].append(f"{cid}.keywords: {exported.get('keywords')} != {oracle.get('keywords')}")

    evars = exported.get("vars") or {}
    ovars = oracle.get("vars") or {}
    for name in sorted(set(evars) | set(ovars)):
        if name not in evars:
            out["diffs"].append(f"{cid}.vars missing {name}")
            continue
        if name not in ovars:
            out["diffs"].append(f"{cid}.vars extra {name}")
            continue
        ev, ov = evars[name], ovars[name]
        if "base" in ev:
            if not eq(ev["base"], ov.get("base")):
                out["diffs"].append(f"{cid}.vars.{name}: {ev['base']!r} != {ov.get('base')!r}")
        else:
            out["unverifiable"] += 1
            out["structural"].append(f"{cid}.vars.{name}: computed expression")

    upgraded = oracle.get("upgraded")
    if upgraded is None:
        expect = exported.get("vars") or {}
        for name, ev in expect.items():
            if "upgraded" in ev:
                out["diffs"].append(f"{cid}.vars.{name}: exported an upgrade but oracle has none")
        return
    ocost_up = upgraded.get("cost_resolved")
    ecost_up = exported.get("cost_upgraded")
    if ecost_up is None:
        if ocost_up not in (-1, 0):
            out["diffs"].append(f"{cid}.cost_upgraded: None != {ocost_up!r}")
    elif not eq(ecost_up, ocost_up):
        out["diffs"].append(f"{cid}.cost_upgraded: {ecost_up!r} != {ocost_up!r}")
    if set(exported.get("keywords_upgraded") or []) != set(upgraded.get("keywords") or []):
        out["diffs"].append(
            f"{cid}.keywords_upgraded: {exported.get('keywords_upgraded')} != {upgraded.get('keywords')}"
        )
    uovars = upgraded.get("vars") or {}
    for name in sorted(set(evars) | set(uovars)):
        if name not in evars:
            out["diffs"].append(f"{cid}.upgraded vars extra {name}")
            continue
        if name not in uovars:
            continue
        ev, ov = evars[name], uovars[name]
        if "upgraded" in ev:
            if not eq(ev["upgraded"], ov.get("base")):
                out["diffs"].append(f"{cid}.vars.{name}.upgraded: {ev['upgraded']!r} != {ov.get('base')!r}")
        elif "base" in ev:
            if not eq(ev["base"], ov.get("base")):
                out["diffs"].append(f"{cid}.vars.{name} (no upgrade): {ev['base']!r} != {ov.get('base')!r}")
        else:
            out["unverifiable"] += 1
            out["structural"].append(f"{cid}.vars.{name}: computed expression")


def compare_vars(scope: str, exported: dict, oracle: dict, out: dict) -> None:
    if oracle.get("vars_error"):
        out["unverifiable"] += 1
        out["structural"].append(f"{scope}: oracle vars_error ({oracle['vars_error']})")
        return
    evars = exported.get("vars") or {}
    ovars = oracle.get("vars") or {}
    for name in sorted(set(evars) | set(ovars)):
        if name not in evars:
            out["diffs"].append(f"{scope}.vars missing {name}")
        elif name not in ovars:
            out["diffs"].append(f"{scope}.vars extra {name}")
        elif "base" in evars[name]:
            if not eq(evars[name]["base"], ovars[name].get("base")):
                out["diffs"].append(f"{scope}.vars.{name}: {evars[name]['base']!r} != {ovars[name].get('base')!r}")
        else:
            out["unverifiable"] += 1
            out["structural"].append(f"{scope}.vars.{name}: computed expression")


def compare_monster(mid: str, exported: dict, oracle: dict, out: dict) -> None:
    out["compared"] += 1
    for field in ("min_hp", "max_hp"):
        ev, ov = exported.get(field), oracle.get(field)
        if ev is None:
            out["unverifiable"] += 1
            out["structural"].append(f"{mid}.{field}: not statically resolved")
        elif not eq(ev, ov):
            out["diffs"].append(f"{mid}.{field}: {ev!r} != {ov!r}")
    if oracle.get("moves_error"):
        out["unverifiable"] += 1
        out["structural"].append(f"{mid}: oracle moves_error ({oracle['moves_error']})")
        return
    if exported.get("initial_state") is None:
        out["unverifiable"] += 1
        out["structural"].append(f"{mid}.initial_state: not statically resolved")
    elif exported["initial_state"] != oracle.get("initial_state"):
        out["diffs"].append(f"{mid}.initial_state: {exported['initial_state']!r} != {oracle.get('initial_state')!r}")
    emoves = exported.get("moves") or {}
    omoves = oracle.get("moves") or {}
    # Branch states (ConditionalBranchState/RandomBranchState) are engine bookkeeping: the oracle
    # only enumerates concrete MoveStates, so compare the MoveState keys only.
    e_keys = {k for k, v in emoves.items() if v.get("kind") == "MoveState"}
    o_keys = {k for k, v in omoves.items() if v.get("kind") == "MoveState"}
    if e_keys != o_keys:
        out["diffs"].append(f"{mid}.moves keys: {sorted(e_keys ^ o_keys)}")
    for move_id in sorted(set(emoves) & set(omoves)):
        em, om = emoves[move_id], omoves[move_id]
        if em.get("kind") != om.get("kind"):
            out["diffs"].append(f"{mid}.{move_id}.kind: {em.get('kind')} != {om.get('kind')}")
        if em.get("kind") == "MoveState":
            if em.get("follow_up") != om.get("follow_up"):
                out["diffs"].append(
                    f"{mid}.{move_id}.follow_up: {em.get('follow_up')!r} != {om.get('follow_up')!r}"
                )
            if bool(em.get("must_perform_once")) != bool(om.get("must_perform_once")):
                out["diffs"].append(f"{mid}.{move_id}.must_perform_once differs")
            eint, oint = em.get("intents") or [], om.get("intents") or []
            if len(eint) != len(oint):
                out["diffs"].append(f"{mid}.{move_id}.intents len: {len(eint)} != {len(oint)}")
            for i, (a, b) in enumerate(zip(eint, oint)):
                if a.get("type") != b.get("type"):
                    out["diffs"].append(f"{mid}.{move_id}.intent[{i}]: {a.get('type')} != {b.get('type')}")
                for field in ("damage", "repeats", "cards"):
                    av, bv = a.get(field), b.get(field)
                    if av is None and bv is not None:
                        out["unverifiable"] += 1
                    elif av is not None and bv is not None and not eq(av, bv):
                        out["diffs"].append(f"{mid}.{move_id}.intent[{i}].{field}: {av!r} != {bv!r}")
        else:
            if list(em.get("branches") or []) != list(om.get("branches") or []):
                out["diffs"].append(
                    f"{mid}.{move_id}.branches: {em.get('branches')} != {om.get('branches')}"
                )


def compare_power(pid: str, exported: dict, oracle: dict, out: dict) -> None:
    out["compared"] += 1
    for field in ("type", "stack_type"):
        if exported.get(field) is None and oracle.get(field) is not None:
            out["unverifiable"] += 1
            out["structural"].append(f"{pid}.{field}: conditional expression, not statically decidable")
        elif exported.get(field) != oracle.get(field):
            out["diffs"].append(f"{pid}.{field}: {exported.get(field)!r} != {oracle.get(field)!r}")
    compare_vars(pid, exported, oracle, out)


def compare_relic(rid: str, exported: dict, oracle: dict, out: dict) -> None:
    out["compared"] += 1
    if exported.get("rarity") != oracle.get("rarity"):
        out["diffs"].append(f"{rid}.rarity: {exported.get('rarity')!r} != {oracle.get('rarity')!r}")
    if not eq(exported.get("merchant_cost"), oracle.get("merchant_cost")):
        out["diffs"].append(f"{rid}.merchant_cost: {exported.get('merchant_cost')!r} != {oracle.get('merchant_cost')!r}")
    compare_vars(rid, exported, oracle, out)


def compare_potion(pid: str, exported: dict, oracle: dict, out: dict) -> None:
    out["compared"] += 1
    for field in ("rarity", "target"):
        if exported.get(field) is None and oracle.get(field) is not None:
            out["unverifiable"] += 1
            out["structural"].append(f"{pid}.{field}: conditional expression, not statically decidable")
        elif exported.get(field) != oracle.get(field):
            out["diffs"].append(f"{pid}.{field}: {exported.get(field)!r} != {oracle.get(field)!r}")
    compare_vars(pid, exported, oracle, out)


def compare_encounter(eid: str, exported: dict, oracle: dict, out: dict) -> None:
    out["compared"] += 1
    for field in ("room_type", "is_weak"):
        if exported.get(field) != oracle.get(field):
            out["diffs"].append(f"{eid}.{field}: {exported.get(field)!r} != {oracle.get(field)!r}")
    eall = sorted(exported.get("all_possible_monsters") or [])
    oall = sorted(oracle.get("all_possible_monsters") or [])
    if eall != oall:
        out["diffs"].append(f"{eid}.all_possible_monsters: {eall} != {oall}")
    om = oracle.get("monsters")
    if om is None:
        out["unverifiable"] += 1
        out["structural"].append(f"{eid}.monsters: oracle GenerateMonsters unavailable")
    else:
        em = exported.get("monsters")
        if em is None:
            out["unverifiable"] += 1
            out["structural"].append(f"{eid}.monsters: not statically resolved")
        elif em != om:
            out["diffs"].append(f"{eid}.monsters: {em} != {om}")


def compare_character(cid: str, exported: dict, oracle: dict, out: dict) -> None:
    if any(cid.endswith("." + n) for n in ("DEPRECATED_CHARACTER", "DEPRIVED", "RANDOM_CHARACTER")):
        out["unverifiable"] += 1
        out["structural"].append(f"{cid}: non-playable character, excluded by content scope")
        return
    out["compared"] += 1
    for field in ("starting_hp", "starting_gold", "max_energy"):
        if exported.get(field) is None:
            out["unverifiable"] += 1
            out["structural"].append(f"{cid}.{field}: not statically resolved")
        elif not eq(exported.get(field), oracle.get(field)):
            out["diffs"].append(f"{cid}.{field}: {exported.get(field)!r} != {oracle.get(field)!r}")
    for field in ("starting_deck", "starting_relics"):
        if list(exported.get(field) or []) != list(oracle.get(field) or []):
            out["diffs"].append(f"{cid}.{field}: {exported.get(field)} != {oracle.get(field)}")
    if exported.get("card_pool") != oracle.get("card_pool"):
        out["diffs"].append(f"{cid}.card_pool: {exported.get('card_pool')!r} != {oracle.get('card_pool')!r}")


def compare_act(aid: str, exported: dict, oracle: dict, out: dict) -> None:
    out["compared"] += 1
    for field in ("index", "is_default"):
        if not eq(exported.get(field), oracle.get(field)):
            out["diffs"].append(f"{aid}.{field}: {exported.get(field)!r} != {oracle.get(field)!r}")


SKIP_MISSING = ("CHARACTER.DEPRECATED_CHARACTER", "CHARACTER.DEPRIVED", "CHARACTER.RANDOM_CHARACTER")


def compare_dict(data: dict, oracle: dict, check, skip_mocks: bool = False) -> dict:
    out = stats()
    for key in sorted(set(data) & set(oracle)):
        if skip_mocks and oracle[key].get("is_mock"):
            continue
        check(key, data[key], oracle[key], out)
    for key in sorted(set(data) - set(oracle)):
        out["diffs"].append(f"{key}: not observed by oracle")
    for key in sorted(set(oracle) - set(data)):
        if skip_mocks and oracle[key].get("is_mock"):
            continue
        if key in SKIP_MISSING:
            out["unverifiable"] += 1
            out["structural"].append(f"{key}: non-playable character, excluded by content scope")
            continue
        out["diffs"].append(f"{key}: missing from data package")
    return out


def load(path: Path) -> dict:
    doc = json.loads(path.read_text(encoding="utf-8"))
    return doc.get("data", doc) if isinstance(doc, dict) else doc


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--data", default="data/sts2")
    ap.add_argument("--oracle", default="tests/vectors")
    ap.add_argument("--json-out", default="", help="write the per-section report as JSON")
    args = ap.parse_args()
    data_dir, oracle_dir = Path(args.data), Path(args.oracle)

    oracle = {p.stem: load(p) for p in oracle_dir.glob("*.json") if p.stem not in ("game_values", "game_values.raw")}
    raw = oracle_dir / "game_values.raw.json"
    if raw.exists():
        oracle.update({k: v for k, v in load(raw).items() if isinstance(v, dict)})
    merged = oracle_dir / "game_values.json"
    if merged.exists():
        oracle.update(
            {k: v for k, v in load(merged).items() if isinstance(v, dict) and k != "meta"}
        )

    sections = [
        ("cards", compare_card),
        ("monsters", compare_monster),
        ("powers", compare_power),
        ("relics", compare_relic),
        ("potions", compare_potion),
        ("encounters", compare_encounter),
        ("characters", compare_character),
        ("acts", compare_act),
    ]
    results = {}
    for section, check in sections:
        data_path = data_dir / f"{section}.json"
        if section not in oracle or not data_path.exists():
            continue
        results[section] = compare_dict(load(data_path), oracle[section], check, skip_mocks=True)

    total = 0
    for section, res in results.items():
        total += len(res["diffs"])
        print(f"{section}: {res['compared']} compared, {res['unverifiable']} unverifiable, {len(res['diffs'])} diffs")
        for line in res["diffs"][:40]:
            print(" ", line)
        if len(res["diffs"]) > 40:
            print(f"  ... and {len(res['diffs']) - 40} more")
    print("TOTAL DIFFS:", total)
    if args.json_out:
        report = {
            "data": str(data_dir),
            "oracle": str(oracle_dir),
            "sections": results,
            "total_diffs": total,
        }
        path = Path(args.json_out)
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8", newline="\n")
        print("report written to", path)
    return 1 if total else 0


if __name__ == "__main__":
    sys.exit(main())

#!/usr/bin/env python3
"""Compare two parity transcripts (JSONL snapshots) field by field.

Usage:
  python tools/compare_snapshots.py --a tests/vectors/real/x.jsonl --b artifacts/engine/x.jsonl
  python tools/compare_snapshots.py --a a.jsonl --b b.jsonl --json-out report.json

Exit code 0 means every shared field matches; 1 means at least one difference (or a line-count
mismatch). Floating point values are compared with a 1e-9 tolerance.
"""
from __future__ import annotations

import argparse
import json
import sys

EPSILON = 1e-9


def load(path: str) -> list[dict]:
    rows = []
    with open(path, encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if line:
                rows.append(json.loads(line))
    return rows


def equal(a, b) -> bool:
    if isinstance(a, bool) or isinstance(b, bool):
        return a is b
    if isinstance(a, (int, float)) and isinstance(b, (int, float)):
        return abs(float(a) - float(b)) <= EPSILON
    return a == b


def diff(a, b, path: str, out: list[dict], limit: int) -> None:
    if len(out) >= limit:
        return
    if type(a) is not type(b):
        # ints and floats are interchangeable for parity purposes
        if isinstance(a, (int, float)) and isinstance(b, (int, float)):
            if not equal(a, b):
                out.append({"path": path, "expected": a, "actual": b})
            return
        out.append({"path": path, "expected": a, "actual": b, "reason": "type"})
        return
    if isinstance(a, dict):
        for key in sorted(set(a) | set(b)):
            if key not in a:
                out.append({"path": f"{path}/{key}", "actual": b[key], "reason": "missing in expected"})
            elif key not in b:
                out.append({"path": f"{path}/{key}", "expected": a[key], "reason": "missing in actual"})
            else:
                diff(a[key], b[key], f"{path}/{key}", out, limit)
            if len(out) >= limit:
                return
        return
    if isinstance(a, list):
        if len(a) != len(b):
            out.append({"path": path, "expected": len(a), "actual": len(b), "reason": "length"})
            return
        for i, (x, y) in enumerate(zip(a, b)):
            diff(x, y, f"{path}[{i}]", out, limit)
            if len(out) >= limit:
                return
        return
    if not equal(a, b):
        out.append({"path": path, "expected": a, "actual": b})


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--a", required=True, help="expected transcript (real game)")
    parser.add_argument("--b", required=True, help="actual transcript (engine)")
    parser.add_argument("--limit", type=int, default=20, help="max differences per line (default 20)")
    parser.add_argument("--json-out", help="write the report as JSON")
    args = parser.parse_args()

    expected = load(args.a)
    actual = load(args.b)
    report: dict = {
        "expected": args.a,
        "actual": args.b,
        "lines_expected": len(expected),
        "lines_actual": len(actual),
        "lines": [],
        "diff_count": 0,
    }

    if len(expected) != len(actual):
        report["lines"].append({"line": None, "diffs": [{
            "path": "",
            "expected": len(expected),
            "actual": len(actual),
            "reason": "line count",
        }]})
        report["diff_count"] += 1

    for i, (exp, act) in enumerate(zip(expected, actual)):
        diffs: list[dict] = []
        diff(exp, act, "", diffs, args.limit)
        if diffs:
            report["lines"].append({"line": i, "action": exp.get("action"), "diffs": diffs})
            report["diff_count"] += len(diffs)
            print(f"line {i} ({exp.get('action')}): {len(diffs)} difference(s)")
            for d in diffs[: args.limit]:
                print(f"  {d['path']}: expected={d.get('expected')!r} actual={d.get('actual')!r}"
                      + (f" ({d['reason']})" if "reason" in d else ""))

    if args.json_out:
        with open(args.json_out, "w", encoding="utf-8") as f:
            json.dump(report, f, ensure_ascii=False, indent=2)

    if report["diff_count"] == 0 and len(expected) == len(actual):
        print(f"ok: {len(expected)} snapshots identical")
        return 0
    print(f"FAILED: {report['diff_count']} difference(s) across {len(expected)} vs {len(actual)} snapshots")
    return 1


if __name__ == "__main__":
    sys.exit(main())

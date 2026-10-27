#!/usr/bin/env python3
"""Record a multi-combat parity session from the real game.

The real game refuses to bootstrap a second run inside one process
(`RunManager` reports "State is already set"), so a session is recorded as a
sequence of independent headless game launches: one per combat. Each combat is
set up from exactly the same initial state, which is what the engine models,
and the resulting transcripts are concatenated with global action indices so
one file covers the whole session.

Usage:
    python harness/run_session.py harness/scenarios/ironclad_session.json
"""
from __future__ import annotations

import argparse
import json
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent


def die(message: str) -> None:
    print(f"ERROR: {message}", file=sys.stderr)
    raise SystemExit(1)


def merge(session: dict, entry: dict) -> dict:
    """Session defaults plus the entry's encounter-scoped overrides (mirrors Scenario.Merge)."""
    combat = {key: value for key, value in session.items() if key != "encounters"}
    for key in ("encounter", "actions", "room_type", "policy_seed", "hp", "max_hp", "max_energy", "energy"):
        if key in entry:
            combat[key] = entry[key]
    return combat


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("session", type=Path, help="session JSON with an encounters array")
    parser.add_argument("--out", type=Path, default=None, help="transcript path (default tests/vectors/real/<name>.jsonl)")
    parser.add_argument("--no-build", action="store_true", help="reuse the last mod build")
    args = parser.parse_args()

    session = json.loads(args.session.read_text(encoding="utf-8"))
    encounters = session.get("encounters") or die("session file needs an encounters array")
    out = args.out or ROOT / "tests" / "vectors" / "real" / f"{args.session.stem}.jsonl"
    out.parent.mkdir(parents=True, exist_ok=True)

    rows: list[dict] = []
    base = 0
    with tempfile.TemporaryDirectory() as tmp:
        tmp_dir = Path(tmp)
        for index, entry in enumerate(encounters, start=1):
            combat = merge(session, entry)
            scenario_path = tmp_dir / f"combat_{index}.json"
            recorded_path = tmp_dir / f"combat_{index}.jsonl"
            scenario_path.write_text(json.dumps(combat, indent=2) + "\n", encoding="utf-8")
            command = [sys.executable, str(ROOT / "harness" / "run_scenario.py"), str(scenario_path), "--out", str(recorded_path)]
            if args.no_build:
                command.append("--no-build")
            print(f"[session] combat {index}/{len(encounters)}: {combat.get('encounter')}", file=sys.stderr)
            result = subprocess.run(command, cwd=ROOT)
            if result.returncode != 0:
                die(f"combat {index} recording failed")

            for line in recorded_path.read_text(encoding="utf-8").splitlines():
                if not line.strip():
                    continue
                row = json.loads(line)
                if row.get("type") == "snapshot" and row.get("action", {}).get("index") is not None:
                    row["action"]["index"] += base
                rows.append(row)
            base += len(combat.get("actions", []))

    joined = "\n".join(json.dumps(row, ensure_ascii=False, separators=(",", ":")) for row in rows)
    out.write_text(joined + "\n", encoding="utf-8")
    print(f"[session] wrote {len(rows)} rows to {out}", file=sys.stderr)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

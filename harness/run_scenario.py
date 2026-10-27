#!/usr/bin/env python3
"""Build, install, and run one parity scenario against the real game.

This is the only script that launches Slay the Spire 2. It:
  1. builds harness/Sts2.ParityMod,
  2. installs the DLL-only mod into <game>/mods/sts2parity/,
  3. makes sure the game can find its Steam app id,
  4. moves the Steam Workshop content directory aside (the game otherwise loads
     every subscribed workshop mod, which would change combat behavior),
  5. launches `SlayTheSpire2.exe --headless` with the scenario,
  6. checks the environment sentinels and copies the JSONL transcript into
     tests/vectors/real/ (committed for CI).

Usage:
    python harness/run_scenario.py harness/scenarios/ironclad_nibbits.json
    python harness/run_scenario.py --no-build harness/scenarios/ironclad_nibbits.json
"""

from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
HARNESS = ROOT / "harness"
MOD_PROJECT = HARNESS / "Sts2.ParityMod"
MANIFEST = HARNESS / "sts2parity" / "mod_manifest.json"
DEFAULT_OUT_DIR = ROOT / "tests" / "vectors" / "real"
LOG_DIR = ROOT / "artifacts" / "parity" / "logs"

GAME_DIR = Path(
    os.environ.get(
        "STS2_GAME_DIR",
        r"C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2",
    )
)
APP_ID = "2868840"
WORKSHOP_DIR = GAME_DIR.parent.parent / "workshop" / "content" / APP_ID
WORKSHOP_DISABLED = WORKSHOP_DIR.with_name(APP_ID + ".disabled-by-parity")

SENTINELS = (
    "[parity] initializer",
    "RUNNING MODDED!",
    "[parity] scenario complete",
)
FORBIDDEN = ("[parity] scenario failed", "[parity] missing STS2_PARITY_")


def die(message: str) -> "NoReturn":  # type: ignore[name-defined]
    print(f"error: {message}", file=sys.stderr)
    raise SystemExit(1)


def check_installation() -> dict:
    release_info = GAME_DIR / "release_info.json"
    exe = GAME_DIR / "SlayTheSpire2.exe"
    if not exe.exists():
        die(f"game not found at {GAME_DIR} (set STS2_GAME_DIR)")
    info = json.loads(release_info.read_text(encoding="utf-8"))
    manifest = json.loads((ROOT / "data" / "sts2" / "manifest.json").read_text(encoding="utf-8"))
    game = manifest.get("game", {})
    for key in ("version", "commit", "main_assembly_hash"):
        if key in game and str(game[key]) != str(info.get(key)):
            die(
                f"game {key} is {info.get(key)!r} but the data package was exported from {game[key]!r}; "
                "re-export data/sts2 before recording"
            )
    return info


def build_mod() -> None:
    print("[run] building parity mod")
    result = subprocess.run(
        ["dotnet", "build", str(MOD_PROJECT), "-c", "Release", "-v", "quiet", "-nologo"],
        cwd=ROOT,
        text=True,
        capture_output=True,
    )
    if result.returncode != 0:
        print(result.stdout[-4000:])
        print(result.stderr[-4000:])
        die("dotnet build failed")


def install_mod() -> None:
    mod_dir = GAME_DIR / "mods" / "sts2parity"
    mod_dir.mkdir(parents=True, exist_ok=True)
    dll = MOD_PROJECT / "bin" / "Release" / "net9.0" / "sts2parity.dll"
    if not dll.exists():
        die(f"built mod dll missing: {dll}")
    shutil.copy2(dll, mod_dir / "sts2parity.dll")
    shutil.copy2(MANIFEST, mod_dir / "mod_manifest.json")
    print(f"[run] installed mod into {mod_dir}")


def prepare_steam() -> None:
    app_id_file = GAME_DIR / "steam_appid.txt"
    if not app_id_file.exists() or app_id_file.read_text(encoding="utf-8").strip() != APP_ID:
        app_id_file.write_text(APP_ID, encoding="utf-8")
        print(f"[run] wrote {app_id_file}")


def disable_workshop() -> None:
    if not WORKSHOP_DIR.exists():
        return
    if WORKSHOP_DISABLED.exists():
        die(
            f"both {WORKSHOP_DIR.name} and {WORKSHOP_DISABLED.name} exist under "
            f"{WORKSHOP_DIR.parent}; move the live directory aside manually"
        )
    try:
        WORKSHOP_DIR.rename(WORKSHOP_DISABLED)
    except OSError as exc:
        die(f"cannot move workshop directory aside (is Steam writing to it?): {exc}")
    print(f"[run] workshop content disabled: {WORKSHOP_DIR.name} -> {WORKSHOP_DISABLED.name}")


def run_game(scenario: Path, out: Path, timeout: int) -> str:
    out.parent.mkdir(parents=True, exist_ok=True)
    if out.exists():
        out.unlink()
    LOG_DIR.mkdir(parents=True, exist_ok=True)
    log_path = LOG_DIR / (scenario.stem + ".stdout.log")
    env = dict(os.environ)
    env["STS2_PARITY_SCENARIO"] = str(scenario.resolve())
    env["STS2_PARITY_OUT"] = str(out.resolve())
    print(f"[run] launching {GAME_DIR / 'SlayTheSpire2.exe'} --headless")
    started = time.time()
    with log_path.open("w", encoding="utf-8", errors="replace") as log:
        try:
            proc = subprocess.run(
                [str(GAME_DIR / "SlayTheSpire2.exe"), "--headless"],
                cwd=GAME_DIR,
                env=env,
                stdout=log,
                stderr=subprocess.STDOUT,
                timeout=timeout,
            )
        except subprocess.TimeoutExpired:
            die(f"game did not exit within {timeout}s; see {log_path}")
    print(f"[run] game exited with {proc.returncode} in {time.time() - started:.1f}s")
    text = log_path.read_text(encoding="utf-8", errors="replace")
    for sentinel in SENTINELS:
        if sentinel not in text:
            print(text[-6000:])
            die(f"sentinel missing from game log: {sentinel!r} (log: {log_path})")
    for forbidden in FORBIDDEN:
        if forbidden in text:
            print(text[-6000:])
            die(f"parity driver failed: {forbidden!r} (log: {log_path})")
    return text


def validate_transcript(out: Path, scenario: dict) -> int:
    if not out.exists():
        die(f"driver wrote no transcript to {out}")
    lines = [json.loads(line) for line in out.read_text(encoding="utf-8").splitlines() if line.strip()]
    errors = [line for line in lines if line.get("type") == "error"]
    if errors:
        die(f"transcript contains a driver error: {errors[0].get('error', '')[:500]}")
    snapshots = [line for line in lines if line.get("type") == "snapshot"]
    if not snapshots:
        die("transcript has no snapshots")
    if snapshots[0].get("action", {}).get("type") != "start":
        die("first snapshot is not the start state")
    if len(snapshots) != len(scenario.get("actions", [])) + 1:
        die(
            f"expected {len(scenario.get('actions', [])) + 1} snapshots, got {len(snapshots)} "
            "(driver stopped early)"
        )
    last = snapshots[-1]
    if not last.get("combat_over"):
        die("scenario did not finish the combat; the last snapshot is not combat_over")
    return len(snapshots)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("scenario", type=Path, help="scenario JSON")
    parser.add_argument("--out", type=Path, default=None, help="transcript path (default tests/vectors/real/<name>.jsonl)")
    parser.add_argument("--no-build", action="store_true", help="reuse the last build")
    parser.add_argument("--timeout", type=int, default=240)
    parser.add_argument("--skip-workshop-check", action="store_true")
    args = parser.parse_args()

    scenario_path = args.scenario if args.scenario.is_absolute() else ROOT / args.scenario
    if not scenario_path.exists():
        die(f"scenario not found: {scenario_path}")
    scenario = json.loads(scenario_path.read_text(encoding="utf-8"))
    out = args.out or (DEFAULT_OUT_DIR / (scenario_path.stem + ".jsonl"))

    info = check_installation()
    print(f"[run] game {info.get('version')} commit {info.get('commit')} hash {info.get('main_assembly_hash')}")
    if not args.no_build:
        build_mod()
    install_mod()
    prepare_steam()
    if not args.skip_workshop_check:
        disable_workshop()
    run_game(scenario_path, out, args.timeout)
    count = validate_transcript(out, scenario)
    print(f"[run] OK: {count} snapshots -> {out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())

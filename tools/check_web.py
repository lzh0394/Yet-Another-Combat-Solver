#!/usr/bin/env python3
"""Smoke-test the local web UI (M8): boots `sts2 web`, then drives the JSON API end to end.

Usage: python tools/check_web.py [--port 5099] [--scenario harness/scenarios/ironclad_nibbits.json]
Exit code 0 when the page and every endpoint behave.
"""
from __future__ import annotations

import argparse
import json
import subprocess
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent


def request(url: str, body: dict | None = None) -> tuple[int, dict | str]:
    data = None if body is None else json.dumps(body).encode()
    req = urllib.request.Request(url, data=data, method="GET" if body is None else "POST",
                                 headers={"content-type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=10) as response:
            payload = response.read().decode()
            return response.status, payload if body is None and "text/html" in response.headers.get(
                "content-type", "") else json.loads(payload)
    except urllib.error.HTTPError as err:
        return err.code, json.loads(err.read().decode() or "{}")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, default=5099)
    parser.add_argument("--scenario", default="harness/scenarios/ironclad_nibbits.json")
    args = parser.parse_args()
    base = f"http://127.0.0.1:{args.port}"

    server = subprocess.Popen(
        ["dotnet", "run", "--project", "src/Sts2.Cli", "-c", "Release", "--no-build", "--",
         "web", "--port", str(args.port), "--pack", "data/sts2", "--scenario", args.scenario],
        cwd=ROOT, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    checks: list[str] = []
    try:
        deadline = time.time() + 30
        status, page = 0, ""
        while time.time() < deadline:
            try:
                status, page = request(base + "/")
                if status == 200:
                    break
            except (urllib.error.URLError, ConnectionError):
                pass
            time.sleep(0.4)
        assert status == 200, f"page did not come up (status {status})"
        checks.append("page served")
        assert 'id="msg"' in page, "page is missing the #msg status element (script aborts before rendering)"
        checks.append("page declares #msg before first fetch")
        assert "Slay the Spire 2" in page and "<script>" in page, "page is not the replay UI"
        checks.append("page is self-contained HTML")

        status, new = request(base + "/api/new", {})
        assert status == 200 and "session" in new, new
        sid, state = new["session"], new["state"]
        assert len(state["piles"]["hand"]) == 5, "opening hand should be five cards"
        checks.append("new combat hands five cards")

        playable = next((i for i, c in enumerate(state["piles"]["hand"]) if c.get("can_play")), None)
        assert playable is not None, "no playable card in opening hand"
        enemy_hp = state["enemies"][0]["hp"]
        energy = state["player"]["energy"]
        status, played = request(f"{base}/api/play?id={sid}", {"slot": playable, "target": "0"})
        assert status == 200 and played["state"]["player"]["energy"] <= energy, played
        assert played["state"]["enemies"][0]["hp"] <= enemy_hp, "card had no effect on the enemy"
        assert played["log"], "action was not logged"
        checks.append("play endpoint applies a card")

        status, ended = request(f"{base}/api/end?id={sid}", {})
        assert status == 200 and ended["state"]["round"] == 2, ended
        assert len(ended["state"]["piles"]["hand"]) == 5, "new turn should draw five cards"
        checks.append("end-turn endpoint advances the round")

        status, current = request(f"{base}/api/state?id={sid}")
        assert status == 200 and current["state"]["round"] == 2, current
        checks.append("state endpoint is consistent")

        status, err = request(f"{base}/api/state?id=missing")
        assert status == 404 and "error" in err, (status, err)
        checks.append("unknown session returns 404")
    finally:
        server.terminate()
        try:
            server.wait(timeout=10)
        except subprocess.TimeoutExpired:
            server.kill()

    for name in checks:
        print(f"ok   {name}")
    print(f"web checks passed: {len(checks)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())

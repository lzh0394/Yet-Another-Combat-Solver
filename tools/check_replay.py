#!/usr/bin/env python3
"""Validate a single-file HTML replay produced by `sts2 html`.

Checks that the page is self-contained (no external script/style/link references) and that it
embeds exactly one snapshot object per transcript line.

Usage: python tools/check_replay.py <replay.html> <transcript.jsonl>
"""

from __future__ import annotations

import pathlib
import re
import sys


def main(argv: list[str]) -> int:
    if len(argv) != 3:
        print(__doc__.strip().splitlines()[-1])
        return 2
    html_path, jsonl_path = pathlib.Path(argv[1]), pathlib.Path(argv[2])
    html = html_path.read_text(encoding="utf-8")
    lines = [line for line in jsonl_path.read_text(encoding="utf-8").splitlines() if line.strip()]

    failures: list[str] = []
    if not html.startswith("<!DOCTYPE html>"):
        failures.append("missing doctype")
    for pattern in (r'<script[^>]+src=', r'<link[^>]+href=', r'@import'):
        if re.search(pattern, html):
            failures.append(f"external reference: {pattern}")
    if "const DATA = [" not in html:
        failures.append("missing embedded DATA array")
    embedded = html.count('"type":"snapshot"')
    if embedded != len(lines):
        failures.append(f"embedded snapshots {embedded} != transcript lines {len(lines)}")

    if failures:
        print(f"FAILED {html_path}: " + "; ".join(failures))
        return 1
    print(f"ok {html_path.name}: {embedded} snapshots, {len(html)} bytes")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))

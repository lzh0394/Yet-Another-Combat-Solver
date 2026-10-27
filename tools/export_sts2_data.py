#!/usr/bin/env python3
"""Export the Slay the Spire 2 data baseline from the ilspycmd decompiled tree.

The exporter is a static reader for the subset of C# the game's model classes use:
constructors with constant arguments, `CanonicalVars` initializers, `OnUpgrade`
mutations, `AscensionHelper` ternaries, monster move-state machines, and simple
`ModelDb.X<T>()` references.

It never loads sts2.dll; tools/Sts2.Oracle is the runtime oracle that validates
these numbers (see tools/compare_values.py).

Usage:
    python tools/export_sts2_data.py \
        --tree _research/decompiled-v0.111.0 \
        --game-dir "C:/Program Files (x86)/Steam/steamapps/common/Slay the Spire 2" \
        --out data/sts2
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import sys
from datetime import datetime, timezone
from decimal import Decimal, InvalidOperation
from pathlib import Path

SCHEMA_VERSION = 2

CATEGORY_DIRS = {
    "cards": ("Models/Cards", "CardModel"),
    "monsters": ("Models/Monsters", "MonsterModel"),
    "powers": ("Models/Powers", "PowerModel"),
    "relics": ("Models/Relics", "RelicModel"),
    "potions": ("Models/Potions", "PotionModel"),
    "encounters": ("Models/Encounters", "EncounterModel"),
    "characters": ("Models/Characters", "CharacterModel"),
    "acts": ("Models/Acts", "ActModel"),
}

VAR_DEFAULT_NAMES = {
    "DamageVar": "Damage",
    "BlockVar": "Block",
    "HpLossVar": "HpLoss",
    "SummonVar": "Summon",
    "HealVar": "Heal",
    "CardsVar": "Cards",
    "RepeatVar": "Repeat",
    "MaxHpVar": "MaxHp",
    "ExtraDamageVar": "ExtraDamage",
    "OstyDamageVar": "OstyDamage",
    "GoldVar": "Gold",
    "StarsVar": "Stars",
    "EnergyVar": "Energy",
    "ForgeVar": "Forge",
    "CalculatedDamageVar": "CalculatedDamage",
    "CalculatedBlockVar": "CalculatedBlock",
    "CalculationBaseVar": "CalculationBase",
    "CalculationExtraVar": "CalculationExtra",
}

POWER_VAR_ALIASES = {
    "Dexterity": "DexterityPower",
    "Doom": "DoomPower",
    "Poison": "PoisonPower",
    "Strength": "StrengthPower",
    "Vulnerable": "VulnerablePower",
    "Weak": "WeakPower",
}

UNPLAYABLE_CHARACTERS = {"DeprecatedCharacter", "Deprived", "RandomCharacter"}

COMPUTED_VARS = {
    "CalculatedDamageVar",
    "CalculatedBlockVar",
    "CalculatedVar",
}

ENUM_TYPES = {
    "CardType",
    "CardRarity",
    "TargetType",
    "CardKeyword",
    "PowerType",
    "PowerStackType",
    "RelicRarity",
    "PotionRarity",
    "RoomType",
    "IntentType",
}


# --------------------------------------------------------------------------- text


class Source:
    """A .cs file with comments and string bodies masked out of `code`."""

    def __init__(self, path: Path, text: str):
        self.path = path
        self.text = text
        self.literals: dict[int, str] = {}
        self.code = self._mask(text)

    def _mask(self, src: str) -> str:
        out = list(src)
        n = len(src)
        i = 0
        while i < n:
            c = src[i]
            if c == "/" and i + 1 < n and src[i + 1] == "/":
                j = src.find("\n", i)
                j = n if j < 0 else j
                for k in range(i, j):
                    out[k] = " "
                i = j
            elif c == "/" and i + 1 < n and src[i + 1] == "*":
                j = src.find("*/", i + 2)
                j = n if j < 0 else j + 2
                for k in range(i, j):
                    out[k] = " "
                i = j
            elif c == '"':
                verbatim = i > 0 and src[i - 1] == "@"
                start = i + 1
                j = start
                while j < n:
                    if verbatim:
                        if src[j] == '"':
                            if j + 1 < n and src[j + 1] == '"':
                                j += 2
                                continue
                            break
                        j += 1
                    else:
                        if src[j] == "\\":
                            j += 2
                            continue
                        if src[j] == '"':
                            break
                        j += 1
                self.literals[i] = src[start:j]
                token = "\x01" + str(i)
                span = j + 1 - i
                if len(token) > span:
                    token = token[:span]
                out[i : j + 1] = list(token + "\x02" * (span - len(token)))
                i = j + 1
            elif c == "'":
                j = i + 1
                while j < n:
                    if src[j] == "\\":
                        j += 2
                        continue
                    if src[j] == "'":
                        break
                    j += 1
                for k in range(i, min(j + 1, n)):
                    out[k] = " "
                i = j + 1
            else:
                i += 1
        return "".join(out)

    def literal(self, pos: int) -> str | None:
        return self.literals.get(pos)

    def render(self, masked: str) -> str:
        """Restore masked string markers to readable quoted literals."""

        def repl(m: re.Match) -> str:
            value = self.literals.get(int(m.group(1)), "")
            value = value.replace("\\", "\\\\").replace('"', '\\"')
            return '"' + value + '"'

        return re.sub(r"\x01(\d+)\x02*", repl, masked)

    def string_at(self, start: int) -> str | None:
        """Return the literal whose opening quote is at `start`."""
        return self.literals.get(start)


def match_brace(code: str, open_pos: int) -> int:
    depth = 0
    for i in range(open_pos, len(code)):
        c = code[i]
        if c == "{":
            depth += 1
        elif c == "}":
            depth -= 1
            if depth == 0:
                return i
    return len(code) - 1


def match_paren(code: str, open_pos: int) -> int:
    depth = 0
    for i in range(open_pos, len(code)):
        c = code[i]
        if c == "(":
            depth += 1
        elif c == ")":
            depth -= 1
            if depth == 0:
                return i
    return len(code) - 1


def split_top(text: str, sep: str = ",") -> list[str]:
    parts: list[str] = []
    depth = 0
    last = 0
    for i, ch in enumerate(text):
        if ch in "([{":
            depth += 1
        elif ch in ")]}":
            depth -= 1
        elif ch == sep and depth == 0:
            parts.append(text[last:i])
            last = i + 1
    parts.append(text[last:])
    return [p.strip() for p in parts if p.strip()]


def find_top_level(text: str, needle: str) -> int:
    depth = 0
    i = 0
    while i <= len(text) - len(needle):
        ch = text[i]
        if ch in "([{":
            depth += 1
        elif ch in ")]}":
            depth -= 1
        elif depth == 0 and text.startswith(needle, i):
            return i
        i += 1
    return -1


NUM_RE = re.compile(r"^-?\d+(?:\.\d+)?[mMfFdD]?$")


def parse_number(expr: str) -> Decimal | None:
    e = expr.strip().rstrip(";").strip()
    e = re.sub(r"\((?:int|decimal|float|double|long|uint|short|byte)\)", "", e).strip()
    if not NUM_RE.match(e):
        return None
    e = e[:-1] if e[-1] in "mMfFdD" else e
    try:
        return Decimal(e)
    except InvalidOperation:
        return None


def num_json(value: Decimal) -> int | float:
    if value == value.to_integral_value():
        return int(value)
    return float(value)


# -------------------------------------------------------------------- class model


class ClassInfo:
    def __init__(self, src: Source, name: str, base: str, start: int, body_start: int, end: int):
        self.src = src
        self.name = name
        self.base = base
        self.start = start
        self.body_start = body_start
        self.end = end
        self.body = src.code[body_start + 1 : end]
        self.body_offset = body_start + 1
        self._member_cache: dict[str, str | None] = {}

    def prop_expr(self, name: str) -> str | None:
        """`Name => expr;` or a get-only property block's first return expression."""
        if name in self._member_cache:
            return self._member_cache[name]
        m = re.search(r"\b" + re.escape(name) + r"\s*=>\s*", self.body)
        if m:
            value = self._expr_until_semicolon(m.end())
            self._member_cache[name] = value
            return value
        m = re.search(r"\b" + re.escape(name) + r"\s*\{", self.body)
        if m:
            close = match_brace(self.body, m.end() - 1)
            block = self.body[m.end() : close]
            r = re.search(r"\breturn\b", block)
            if r:
                value = self._expr_until_semicolon(m.end() + r.end())
                self._member_cache[name] = value
                return value
        self._member_cache[name] = None
        return None

    def prop_block(self, name: str) -> str | None:
        m = re.search(r"\b" + re.escape(name) + r"\s*\{", self.body)
        if not m:
            return None
        close = match_brace(self.body, m.end() - 1)
        return self.body[m.end() : close]

    def field_expr(self, name: str) -> str | None:
        m = re.search(
            r"\b(?:private|public|protected|internal)\s+(?:const\s+|static\s+|readonly\s+)*"
            r"[\w<>\[\],\.\?\s]+?\s+" + re.escape(name) + r"\s*=\s*",
            self.body,
        )
        if not m:
            return None
        return self._expr_until_semicolon(m.end())

    def method_body(self, name: str) -> str | None:
        pat = re.compile(
            r"\b(?:public|private|protected|internal)\s+(?:override\s+|virtual\s+|async\s+|static\s+)*"
            r"[\w<>\[\],\.\?\(\)\s]+?\s+" + re.escape(name) + r"\s*\("
        )
        m = pat.search(self.body)
        if not m:
            return None
        close_paren = match_paren(self.body, m.end() - 1)
        brace = self.body.find("{", close_paren)
        if brace < 0:
            return None
        close = match_brace(self.body, brace)
        return self.body[brace + 1 : close]

    def ctor_base_args(self) -> list[str]:
        pat = re.compile(r"\bpublic\s+" + re.escape(self.name) + r"\s*\(")
        m = pat.search(self.body)
        if not m:
            return []
        close = match_paren(self.body, m.end() - 1)
        rest = self.body[close + 1 :]
        b = re.search(r":\s*base\s*\(", rest)
        if not b:
            return []
        open_pos = close + 1 + b.end() - 1
        end = match_paren(self.body, open_pos)
        return split_top(self.body[open_pos + 1 : end])

    def initializer(self, var_name: str) -> str | None:
        """Object initializer attached to `X varName = new ... { ... };`"""
        m = re.search(r"\b" + re.escape(var_name) + r"\s*=\s*new\s+[^;{]*\{", self.body)
        if not m:
            return None
        open_pos = m.end() - 1
        close = match_brace(self.body, open_pos)
        return self.body[open_pos + 1 : close]

    def _expr_until_semicolon(self, start: int) -> str:
        depth = 0
        i = start
        while i < len(self.body):
            c = self.body[i]
            if c in "([{":
                depth += 1
            elif c in ")]}":
                depth -= 1
            elif c == ";" and depth == 0:
                return self.body[start:i].strip()
            i += 1
        return self.body[start:].strip()


class ClassTable:
    def __init__(self, tree: Path):
        self.classes: list[ClassInfo] = []
        self.by_name: dict[str, list[ClassInfo]] = {}
        self.by_namespace: dict[str, list[ClassInfo]] = {}
        for path in sorted(tree.rglob("*.cs")):
            rel = path.relative_to(tree).as_posix()
            if not rel.startswith("MegaCrit/sts2/Core/Models/"):
                continue
            src = Source(path, path.read_text(encoding="utf-8", errors="replace"))
            ns = ""
            m = re.search(r"namespace\s+([\w\.]+)", src.code)
            if m:
                ns = m.group(1)
            for m in re.finditer(r"\bclass\s+([A-Za-z_]\w*)", src.code):
                name = m.group(1)
                brace = src.code.find("{", m.end())
                if brace < 0:
                    continue
                # base clause between class name and `{`
                between = src.code[m.end() : brace]
                base = ""
                cm = re.search(r":\s*([A-Za-z_]\w*)", between)
                if cm:
                    base = cm.group(1)
                end = match_brace(src.code, brace)
                info = ClassInfo(src, name, base, m.start(), brace, end)
                info.namespace = ns
                info.relpath = rel
                info.is_abstract = "abstract" in src.code[max(0, m.start() - 40) : m.start()]
                self.classes.append(info)
                self.by_name.setdefault(name, []).append(info)
                self.by_namespace.setdefault(ns, []).append(info)

    def pick(self, category: str) -> list[ClassInfo]:
        dir_part, base = CATEGORY_DIRS[category]
        candidates = []
        for info in self.classes:
            if not info.relpath.startswith("MegaCrit/sts2/Core/" + dir_part + "/"):
                continue
            if info.name == base or getattr(info, "is_abstract", False):
                continue
            if category == "characters" and info.name in UNPLAYABLE_CHARACTERS:
                continue
            if self._derives(info, base):
                candidates.append(info)
        return candidates

    def _parent(self, cur: ClassInfo) -> ClassInfo | None:
        options = [c for c in self.by_name.get(cur.base, []) if c is not cur]
        if not options:
            return None
        same = [c for c in options if c.namespace == cur.namespace]
        return same[0] if same else options[0]

    def _derives(self, info: ClassInfo, base: str) -> bool:
        seen: set[int] = set()
        cur: ClassInfo | None = info
        while cur is not None and cur.base:
            if cur.base == base:
                return True
            if id(cur) in seen:
                return False
            seen.add(id(cur))
            cur = self._parent(cur)
        return False

    def counts(self, base: str) -> tuple[int, int]:
        concrete = [i for i in self.classes if i.name != base and not i.is_abstract and self._derives(i, base)]
        mocks = sum(1 for i in concrete if i.name.startswith("Mock"))
        return len(concrete), mocks


# ------------------------------------------------------------------ value parsing


class ExprResolver:
    """Resolves class member expressions into {base, ascension, level} or a raw expr."""

    def __init__(self, table: ClassTable):
        self.table = table

    def _find_member(self, info: ClassInfo, name: str) -> tuple[ClassInfo | None, str | None]:
        cur: ClassInfo | None = info
        seen: set[str] = set()
        while cur is not None and cur.name not in seen:
            seen.add(cur.name)
            member = cur.prop_expr(name)
            if member is None:
                member = cur.field_expr(name)
            if member is not None:
                return cur, member
            options = [c for c in self.table.by_name.get(cur.base, []) if c.namespace == cur.namespace]
            cur = options[0] if options else None
        return None, None

    def resolve(self, expr: str, info: ClassInfo, seen: set[str] | None = None) -> dict:
        seen = seen or set()
        e = expr.strip().rstrip(";").strip()
        if not e:
            return {"base": None}

        n = parse_number(e)
        if n is not None:
            return {"base": num_json(n)}

        m = re.fullmatch(r"(?:AscensionHelper\.)?GetValueIfAscension\s*\((.*)\)", e)
        if m:
            args = split_top(m.group(1))
            if len(args) == 3:
                level = args[0].split(".")[-1]
                base = self.resolve(args[2], info, seen)
                asc = self.resolve(args[1], info, seen)
                return {
                    "base": base.get("base"),
                    "ascension": asc.get("base") if "base" in asc else asc,
                    "level": level,
                }

        m = re.fullmatch(r"([A-Za-z_]\w*)\.([A-Za-z_]\w*)", e)
        if m:
            type_name, member = m.groups()
            if type_name in ENUM_TYPES:
                return {"enum": member, "enum_type": type_name}
            # static const table reference
            return {"expr": e}

        if re.fullmatch(r"[A-Za-z_]\w*", e):
            if e in seen:
                return {"expr": e}
            owner, member = self._find_member(info, e)
            if member is not None:
                return self.resolve(member, owner or info, seen | {e})
            return {"expr": e}

        # ternary of two resolvable sides
        q = find_top_level(e, "?")
        if q > 0:
            colon = find_top_level(e[q + 1 :], ":")
            if colon > 0:
                then = e[q + 1 : q + 1 + colon]
                other = e[q + 2 + colon :]
                a = self.resolve(then, info, seen)
                b = self.resolve(other, info, seen)
                if a.get("base") == b.get("base"):
                    return a
                return {"expr": e}

        return {"expr": e}


# ----------------------------------------------------------------------- model db


class Exporter:
    def __init__(self, tree: Path, game_dir: Path):
        self.tree = tree
        self.game_dir = game_dir
        self.table = ClassTable(tree)
        self.resolver = ExprResolver(self.table)
        self.errors: list[dict] = []

    # -- helpers ---------------------------------------------------------

    def error(self, scope: str, message: str) -> None:
        self.errors.append({"scope": scope, "error": message})

    def parse_vars(self, info: ClassInfo, scope: str) -> dict:
        expr = info.prop_expr("CanonicalVars")
        if expr is None:
            return {}
        if "CanonicalVars" in expr:
            # block-bodied property whose return was captured by prop_expr; handled above
            pass
        out: dict[str, dict] = {}
        for m in re.finditer(r"\bnew\s+([A-Za-z_]\w*)\s*(?:<([\w\.\,\s]+)>)?\s*\(", expr):
            var_type = m.group(1)
            generic = (m.group(2) or "").strip()
            if not var_type.endswith("Var"):
                continue
            open_pos = m.end() - 1
            close = match_paren(expr, open_pos)
            args = split_top(expr[open_pos + 1 : close])
            full = expr[m.start() : close + 1]
            # chained calls, e.g. `.WithMultiplier(...)`
            tail_end = close + 1
            while tail_end < len(expr) and expr[tail_end] in " \t\r\n.":
                if expr[tail_end] == ".":
                    nm = re.match(r"\.\s*([A-Za-z_]\w*)\s*\(", expr[tail_end:])
                    if not nm:
                        break
                    p_open = tail_end + nm.end() - 1
                    p_close = match_paren(expr, p_open)
                    tail_end = p_close + 1
                else:
                    tail_end += 1
            computed_text = info.src.render(expr[m.start() : tail_end].strip())

            args_text = [a.strip() for a in args]
            name = None
            if args_text and self._string_literal(args_text[0], info.src) is not None:
                name = self._string_literal(args_text[0], info.src)
            if name is None:
                if generic:
                    name = re.split(r"[,\s]", generic)[0].split(".")[-1]
                else:
                    name = VAR_DEFAULT_NAMES.get(var_type)

            entry: dict
            if var_type in COMPUTED_VARS:
                entry = {"expr": computed_text}
            else:
                value_expr = args_text[0] if args_text else ""
                if self._string_literal(value_expr, info.src) is not None and len(args_text) > 1:
                    value_expr = args_text[1]
                value_expr = re.sub(r"^\w+\s*:\s*", "", value_expr)
                resolved = self.resolver.resolve(value_expr, info)
                if "base" not in resolved or resolved["base"] is None:
                    entry = {"expr": computed_text}
                else:
                    entry = resolved

            if name is None:
                self.error(scope, f"cannot name var {var_type} in {computed_text}")
                name = var_type
            if name in out:
                self.error(scope, f"duplicate var {name}")
            out[name] = entry
        return out

    @staticmethod
    def _string_literal(arg: str, src: Source) -> str | None:
        m = re.fullmatch(r"\x01(\d+)\x02*", arg.strip())
        return None if m is None else src.literals.get(int(m.group(1)))

    def parse_keywords(self, info: ClassInfo) -> list[str]:
        expr = info.prop_expr("CanonicalKeywords")
        if not expr:
            return []
        # Keep declaration order: the real game exposes keywords in the order the card lists them.
        return list(dict.fromkeys(re.findall(r"CardKeyword\.([A-Za-z_]\w*)", expr)))

    def parse_upgrade(self, info: ClassInfo, scope: str) -> dict:
        body = info.method_body("OnUpgrade") or ""
        result = {"cost_delta": 0, "var_deltas": {}, "add_keywords": [], "remove_keywords": []}
        for m in re.finditer(r"EnergyCost\.UpgradeBy\(([^;]*)\)\s*;", body):
            n = self.resolver.resolve(m.group(1), info)
            if "base" in n and isinstance(n["base"], (int, float)):
                result["cost_delta"] += n["base"]
            else:
                self.error(scope, f"non-constant EnergyCost.UpgradeBy: {m.group(1)}")
        for m in re.finditer(
            r"DynamicVars(?:\.([A-Za-z_]\w*)|\s*\[\s*(\d+*)\s*\])\s*\.UpgradeValueBy\(([^;]*)\)\s*;",
            body,
        ):
            name, arg = m.group(1), m.group(3)
            if name is None:
                name = self._string_literal(m.group(2), info.src) or ""
            n = self.resolver.resolve(arg, info)
            if name in POWER_VAR_ALIASES:
                name = POWER_VAR_ALIASES[name]
            if "base" in n and isinstance(n["base"], (int, float)):
                result["var_deltas"][name] = result["var_deltas"].get(name, 0) + n["base"]
            else:
                self.error(scope, f"non-constant UpgradeValueBy {name}: {arg}")
        result["add_keywords"] = re.findall(r"AddKeyword\(CardKeyword\.([A-Za-z_]\w*)\)", body)
        result["remove_keywords"] = re.findall(r"RemoveKeyword\(CardKeyword\.([A-Za-z_]\w*)\)", body)
        return result

    # -- cards -----------------------------------------------------------

    def cards(self) -> dict:
        out = {}
        for info in self.table.pick("cards"):
            if info.name.startswith("Mock"):
                continue
            cid = "CARD." + camel_to_screaming(info.name)
            try:
                out[cid] = self.card(info, cid)
            except Exception as e:  # keep exporting the rest
                self.error(cid, f"{type(e).__name__}: {e}")
                out[cid] = {"id": cid, "name": info.name, "export_error": f"{type(e).__name__}: {e}"}
        return dict(sorted(out.items()))

    def card(self, info: ClassInfo, cid: str) -> dict:
        args = info.ctor_base_args()
        cost = 0
        ctype = ""
        rarity = ""
        target = ""
        if len(args) >= 4:
            cost = self._int_arg(args[0], info, cid, "cost")
            ctype = self._enum_arg(args[1])
            rarity = self._enum_arg(args[2])
            target = self._enum_arg(args[3])
        else:
            self.error(cid, f"unexpected ctor args: {args}")

        overrides = {}
        multimodal = False
        for prop, enum_type in (("Type", "CardType"), ("Rarity", "CardRarity"), ("TargetType", "TargetType")):
            expr = info.prop_expr(prop)
            if expr is None:
                continue
            resolved = self.resolver.resolve(expr, info)
            if "enum" in resolved:
                overrides[prop] = resolved["enum"]
            else:
                multimodal = True
                self.error(cid, f"dynamic {prop}: {expr}")
        ctype = overrides.get("Type", ctype)
        rarity = overrides.get("Rarity", rarity)
        target = overrides.get("TargetType", target)

        have_x = info.prop_expr("HasEnergyCostX")
        costs_x = bool(have_x and "true" in have_x)
        vars_ = self.parse_vars(info, cid)
        keywords = self.parse_keywords(info)
        upgrade = self.parse_upgrade(info, cid)

        cost_upgraded = cost + upgrade["cost_delta"] if cost >= 0 else cost
        if costs_x and cost < 0:
            cost = cost_upgraded = 0
        vars_upgraded = {}
        for name, value in vars_.items():
            if "base" in value and isinstance(value["base"], (int, float)):
                delta = upgrade["var_deltas"].get(name)
                if delta is not None:
                    base = value["base"]
                    vars_upgraded[name] = {
                        **value,
                        "upgraded": num_json(Decimal(str(base)) + Decimal(str(delta))),
                    }
                else:
                    vars_upgraded[name] = dict(value)
            else:
                vars_upgraded[name] = dict(value)
        for name, delta in upgrade["var_deltas"].items():
            if name not in vars_upgraded:
                self.error(cid, f"upgrade references unknown var {name}")
            else:
                base = vars_upgraded[name].get("base")
                if isinstance(base, (int, float)):
                    vars_upgraded[name]["upgraded"] = num_json(Decimal(str(base)) + Decimal(str(delta)))

        keywords_up = set(keywords)
        keywords_up.update(upgrade["add_keywords"])
        keywords_up.difference_update(upgrade["remove_keywords"])

        star_cost = self._star_cost(info)
        effects, effects_complete = self._card_effects(info)

        entry = {
            "id": cid,
            "name": info.name,
            "type": ctype,
            "rarity": rarity,
            "target": target,
            "cost": None if cost < 0 else cost,
            "cost_upgraded": None if cost_upgraded < 0 else cost_upgraded,
            "costs_x": costs_x,
            "star_cost": None if star_cost < 0 else star_cost,
            "keywords": keywords,
            "keywords_upgraded": sorted(keywords_up),
            "vars": vars_ or None,
            "multimodal": multimodal,
            "effect_complete": effects_complete,
            "effect": effects or [],
        }
        for name, value in vars_upgraded.items():
            if name in entry["vars"]:
                entry["vars"][name] = value
        return entry

    def _star_cost(self, info: ClassInfo) -> int:
        expr = info.prop_expr("CanonicalStarCost")
        if not expr:
            return -1
        resolved = self.resolver.resolve(expr, info)
        if "base" in resolved and isinstance(resolved["base"], (int, float)):
            return int(resolved["base"])
        return -1

    def _int_arg(self, arg: str, info: ClassInfo, scope: str, label: str) -> int:
        arg = re.sub(r"^\w+\s*:\s*", "", arg.strip())
        resolved = self.resolver.resolve(arg, info)
        if "base" in resolved and isinstance(resolved["base"], (int, float)):
            return int(resolved["base"])
        self.error(scope, f"non-constant {label}: {arg}")
        return 0

    @staticmethod
    def _enum_arg(arg: str) -> str:
        m = re.search(r"([A-Za-z_]\w*)\.([A-Za-z_]\w*)", arg)
        return m.group(2) if m else ""


    # -- monsters --------------------------------------------------------

    def _find_method(self, info: ClassInfo, name: str) -> tuple[ClassInfo | None, str | None]:
        cur = info
        seen = set()
        while cur is not None and cur.name not in seen:
            seen.add(cur.name)
            body = cur.method_body(name)
            if body is not None:
                return cur, body
            options = [c for c in self.table.by_name.get(cur.base, []) if c.namespace == cur.namespace]
            cur = options[0] if options else None
        return None, None

    def _find_method_body(self, info: ClassInfo, name: str) -> str | None:
        return self._find_method(info, name)[1]

    def _literal_str(self, info: ClassInfo, token: str) -> str | None:
        token = token.strip()
        m = re.fullmatch(r"\x01(\d+)\x02*", token)
        return None if m is None else info.src.literals.get(int(m.group(1)))

    def _bool_expr(self, info: ClassInfo, prop: str) -> bool | None:
        owner = self._find_prop(info, prop) or info
        expr = owner.prop_expr(prop)
        if expr is None:
            return None
        e = expr.strip()
        if e == "true":
            return True
        if e == "false":
            return False
        return None

    def _int_prop(self, info: ClassInfo, prop: str) -> dict:
        owner = self._find_prop(info, prop) or info
        expr = owner.prop_expr(prop)
        if expr is None:
            return {"expr": None}
        return self.resolver.resolve(expr, info)

    def _entry_value(self, resolved: dict):
        if "base" in resolved and isinstance(resolved["base"], (int, float)):
            return resolved["base"]
        return None

    def monsters(self) -> dict:
        out = {}
        for info in self.table.pick("monsters"):
            if info.name.startswith("Mock"):
                continue
            mid = "MONSTER." + camel_to_screaming(info.name)
            try:
                out[mid] = self.monster(info, mid)
            except Exception as e:  # keep going, record the failure
                self.error(mid, f"{type(e).__name__}: {e}")
                out[mid] = {"id": mid, "name": info.name, "export_error": f"{type(e).__name__}: {e}"}
        return dict(sorted(out.items()))

    def monster(self, info: ClassInfo, mid: str) -> dict:
        entry = {"id": mid, "name": info.name}
        min_hp = self._int_prop(info, "MinInitialHp")
        max_hp = self._int_prop(info, "MaxInitialHp")
        entry["min_hp"] = self._entry_value(min_hp)
        entry["max_hp"] = self._entry_value(max_hp)
        if "level" in min_hp and "ascension" in min_hp:
            entry["hp_ascension"] = {
                "level": camel_to_screaming(min_hp["level"]),
                "min": min_hp["ascension"],
                "max": max_hp.get("ascension", max_hp.get("base")),
            }

        owner, body = self._find_method(info, "GenerateMoveStateMachine")
        if body is None:
            self.error(mid, "no GenerateMoveStateMachine")
            entry["moves"] = None
            return entry

        states: dict[str, dict] = {}
        var_to_id: dict[str, str] = {}
        nested_follow: list[tuple[str, str]] = []
        for m in re.finditer(r"new\s+(MoveState|ConditionalBranchState|RandomBranchState)\s*\(", body):
            kind = m.group(1)
            open_pos = m.end() - 1
            close = match_paren(body, open_pos)
            args = split_top(body[open_pos + 1 : close])
            sid = self._literal_str(owner or info, args[0]) if args else None
            if sid is None:
                continue
            stmt_start = body.rfind(";", 0, m.start()) + 1
            prefix = body[stmt_start : m.start()]
            assigns = [a.group(1) for a in re.finditer(r"(\w+)\s*=\s*", prefix) if a.group(1) != "FollowUpState"]
            if assigns:
                var_to_id[assigns[0]] = sid
            for pm in re.finditer(r"(\w+)\.FollowUpState\s*=\s*", prefix):
                nested_follow.append((pm.group(1), sid))
            state: dict = {"kind": kind}
            if kind == "MoveState":
                intents = []
                for token in args[2:]:
                    try:
                        intents.append(self._intent(owner or info, token))
                    except Exception as e:
                        self.error(mid, f"intent {token}: {type(e).__name__}: {e}")
                state["intents"] = intents
                state["follow_up"] = None
                semi = body.find(";", close)
                tail = body[close : semi if semi > 0 else len(body)]
                fm = re.search(r"FollowUpStateId\s*=\s*(\d+*)", tail)
                if fm:
                    state["follow_up"] = self._literal_str(owner or info, fm.group(1))
                state["must_perform_once"] = "MustPerformOnceBeforeTransitioning = true" in tail
                move_name = args[1].strip() if len(args) > 1 else ""
                effects, complete = self._move_effects(owner or info, move_name)
                state["effects"] = effects
                state["effect_complete"] = complete
            else:
                state["branches"] = []
            states[sid] = state

        for src_var, dst_id in nested_follow:
            if src_var in var_to_id:
                states[var_to_id[src_var]]["follow_up"] = dst_id
            else:
                self.error(mid, f"unresolved nested follow up {src_var}")

        for m in re.finditer(r"(\w+)\.FollowUpState\s*=\s*(\w+)\s*;", body):
            src_id, dst_id = var_to_id.get(m.group(1)), var_to_id.get(m.group(2))
            if src_id in states and dst_id:
                states[src_id]["follow_up"] = dst_id
            elif src_id in states:
                self.error(mid, f"unresolved follow up {m.group(2)}")

        conditional_branches = False
        for m in re.finditer(r"(\w+)\.Add(?:State|Branch)\(\s*(\w+)", body):
            src_id, dst_id = var_to_id.get(m.group(1)), var_to_id.get(m.group(2))
            if src_id not in states or not dst_id:
                if src_id in states:
                    self.error(mid, f"unresolved branch {m.group(2)}")
                continue
            cond = self._enclosing_if(body, m.start())
            if cond is not None:
                verdict = self._static_bool(cond)
                if verdict is False:
                    if "branches_conditional" not in states[src_id]:
                        states[src_id]["branches_conditional"] = []
                    states[src_id]["branches_conditional"].append(dst_id)
                    continue
                if verdict is None:
                    conditional_branches = True
            states[src_id]["branches"].append(dst_id)

        if conditional_branches:
            for st in states.values():
                if "branches" in st:
                    st["branches_conditional"] = True
        registered = self._registered_states(body, var_to_id)
        if registered:
            states = {sid: st for sid, st in states.items() if sid in registered}

        # Predicate lambdas and weights attached to branch states, in source order.
        for m in re.finditer(r"(\w+)\.Add(State|Branch)\s*\(", body):
            src_id = var_to_id.get(m.group(1))
            if src_id not in states:
                continue
            open_pos = m.end() - 1
            close = match_paren(body, open_pos)
            args = split_top(body[open_pos + 1 : close])
            if len(args) < 2:
                continue
            dst_id = var_to_id.get(args[0].strip()) or self._literal_str(owner or info, args[0])
            if not dst_id:
                continue
            st = states[src_id]
            if m.group(2) == "State":
                lambda_src = args[1].strip()
                lm = re.match(r"\(\)\s*=>\s*", lambda_src)
                expr = lambda_src[lm.end() :].strip() if lm else lambda_src
                expr = (owner or info).src.render(expr)
                st.setdefault("conditions", []).append({"branch": dst_id, "expr": expr})
            else:
                weight = 1.0
                repeat = "CanRepeat"
                for arg in args[1:]:
                    arg_s = arg.strip()
                    wm = re.search(r"()\s*=>\s*([0-9.]+)f", arg_s)
                    fm = re.fullmatch(r"([0-9.]+)f", arg_s)
                    im = re.fullmatch(r"[0-9]+", arg_s)
                    if wm:
                        weight = float(wm.group(2))
                    elif fm:
                        weight = float(fm.group(1))
                    elif im:
                        # AddBranch(state, int maxRepeats): weight stays 1, repeat is capped.
                        repeat = "CanRepeatXTimes"
                    elif "CannotRepeat" in arg_s:
                        repeat = "CannotRepeat"
                    elif "CanRepeatXTimes" in arg_s:
                        repeat = "CanRepeatXTimes"
                st.setdefault("weights", []).append(weight)
                st.setdefault("repeat_types", []).append(repeat)

        s_owner, s_body = self._find_method(info, "AfterAddedToRoom")
        if s_body is not None:
            try:
                start_effects, start_complete = self._move_effects(info, "AfterAddedToRoom")
            except Exception as e:
                start_effects, start_complete = None, False
                self.error(mid, f"start effects: {type(e).__name__}: {e}")
            entry["start_effects"] = start_effects
            entry["start_effect_complete"] = start_complete

        entry["initial_state"] = self._initial_state(info, body, var_to_id)
        initial_states = self._initial_states(info, body, var_to_id)
        if initial_states is not None:
            entry["initial_states"] = initial_states
        entry["moves"] = states
        return entry

    @staticmethod
    def _enclosing_if(body: str, pos: int) -> str | None:
        for m in re.finditer(r"\bif\s*\(", body):
            if m.start() > pos:
                continue
            open_paren = m.end() - 1
            close_paren = match_paren(body, open_paren)
            brace = body.find("{", close_paren)
            if brace < 0:
                continue
            close = match_brace(body, brace)
            if brace < pos < close:
                return body[open_paren + 1 : close_paren]
        return None

    @staticmethod
    def _static_bool(cond: str) -> bool | None:
        c = cond.strip()
        if re.fullmatch(r"![A-Za-z_]\w*", c):
            return True
        if re.fullmatch(r"[A-Za-z_]\w*", c):
            return False
        if c in ("true", "false"):
            return c == "true"
        return None

    def _registered_states(self, body: str, var_to_id: dict[str, str]) -> set[str] | None:
        ids: set[str] = set()
        found = False
        for m in re.finditer(r"\b\w+\s*\.\s*Add\(\s*(\w+)\s*\)", body):
            found = True
            sid = var_to_id.get(m.group(1))
            if sid:
                ids.add(sid)
        im = re.search(r"new\s+List<MonsterState>\s*\{", body)
        if im:
            close = match_brace(body, im.end() - 1)
            for token in split_top(body[im.end() : close]):
                found = True
                sid = var_to_id.get(token.strip())
                if sid:
                    ids.add(sid)
        return ids if found else None

    def _initial_state(self, info: ClassInfo, body: str, var_to_id: dict[str, str]) -> str | None:
        returns = list(re.finditer(r"return\s+new\s+MonsterMoveStateMachine\s*\(", body))
        if len(returns) != 1:
            return None
        rm = returns[0]
        open_pos = rm.end() - 1
        close = match_paren(body, open_pos)
        args = split_top(body[open_pos + 1 : close])
        if len(args) < 2:
            return None
        second = args[1].strip()
        if second in var_to_id:
            return var_to_id[second]
        if second.startswith("new "):
            inner = second[second.find("(") + 1 :]
            return self._literal_str(info, split_top(inner)[0]) if inner else None
        tm = re.search(
            r"\b" + re.escape(second) + r"\s*=\s*\(?[^;?]*\?\s*(\w+)\s*:\s*(\w+)\)?\s*;", body
        )
        if tm:
            return var_to_id.get(tm.group(2)) or var_to_id.get(tm.group(1))
        return None

    def _initial_states(self, info: ClassInfo, body: str, var_to_id: dict[str, str]) -> list[str] | None:
        """Switch-expression initial states: `return (StarterMoveIdx % 3) switch { 0 => new ..., ... }`,
        including the form wrapped in `new MonsterMoveStateMachine(list, (X % 3) switch { 0 => state, ... })`."""
        m = re.search(
            r"return\s+(?:new\s+MonsterMoveStateMachine\s*\(\s*\w+\s*,\s*)?"
            r"\(\s*\(?[^)]*StarterMove\w*[^)]*\)?\s*%\s*\d+\s*\)?\s*switch\s*\{",
            body,
        )
        if not m:
            m = re.search(r"\(\s*StarterMove\w*\s*%\s*\d+\s*\)\s*switch\s*\{", body)
        if not m:
            return None
        open_pos = body.find("{", m.end() - 1)
        close = match_brace(body, open_pos)
        arms = split_top(body[open_pos + 1 : close])
        by_index: dict[int, str] = {}
        next_index = 0
        for arm in arms:
            arm = arm.strip()
            if not arm:
                continue
            am = re.match(r"(\d+|_)\s*=>\s*(.+)$", arm, re.S)
            if not am:
                return None
            value = am.group(2).strip()
            if value.startswith("new "):
                open_paren = value.find("(")
                close_paren = match_paren(value, open_paren)
                args = split_top(value[open_paren + 1 : close_paren])
                if len(args) < 2:
                    return None
                state_var = args[1].strip()
            else:
                state_var = value
            state = var_to_id.get(state_var)
            if state is None:
                return None
            if am.group(1) == "_":
                by_index[next_index] = state
            else:
                by_index[int(am.group(1))] = state
            next_index += 1
        if not by_index:
            return None
        size = max(by_index) + 1
        return [by_index.get(i, by_index[max(by_index)]) for i in range(size)]

    def _intent(self, info: ClassInfo, token: str) -> dict:
        m = re.match(r"new\s+([A-Za-z_]\w*)\s*\(", token.strip())
        if not m:
            return {"type": token.strip()}
        cls = m.group(1)
        open_pos = m.end() - 1
        close = match_paren(token, open_pos)
        args = split_top(token[open_pos + 1 : close])
        tmap = {
            "SingleAttackIntent": "Attack",
            "MultiAttackIntent": "Attack",
            "DeathBlowIntent": "DeathBlow",
            "DefendIntent": "Defend",
            "BuffIntent": "Buff",
            "DebuffIntent": "Debuff",
            "StatusIntent": "StatusCard",
            "CardDebuffIntent": "CardDebuff",
            "HealIntent": "Heal",
            "SummonIntent": "Summon",
            "EscapeIntent": "Escape",
            "SleepIntent": "Sleep",
            "StunIntent": "Stun",
            "HiddenIntent": "Hidden",
            "UnknownIntent": "Unknown",
            "AttackIntent": "Attack",
        }
        it: dict = {"type": tmap.get(cls, cls.replace("Intent", ""))}
        if cls in ("SingleAttackIntent", "DeathBlowIntent", "AttackIntent") and args:
            it["damage"] = self._entry_value(self.resolver.resolve(args[0], info))
            it["repeats"] = 1
        elif cls == "MultiAttackIntent" and args:
            it["damage"] = self._entry_value(self.resolver.resolve(args[0], info))
            it["repeats"] = self._entry_value(self.resolver.resolve(args[1], info)) if len(args) > 1 else 1
        elif cls == "StatusIntent" and args:
            it["cards"] = self._entry_value(self.resolver.resolve(args[0], info))
        elif cls == "DebuffIntent" and args and args[0].split(":")[-1].strip() == "true":
            it["type"] = "DebuffStrong"
        return it

    # -- monster move effects -------------------------------------------

    EFFECT_CMDS = {
        "DamageCmd",
        "CreatureCmd",
        "PowerCmd",
        "CardPileCmd",
        "PlayerCmd",
        "SummonCmd",
        "OrbCmd",
        "PotionCmd",
        "CardCmd",
        "CardSelectCmd",
    }

    def _move_effects(self, info: ClassInfo, method: str) -> tuple[list | None, bool]:
        owner, body = self._find_method(info, method) if method else (None, None)
        if body is None:
            return None, False
        ops: list[dict] = []
        handled: list[tuple[int, int]] = []
        locals_: dict[str, object] = {}
        for lm in re.finditer(r"(?:int|decimal|var)\s+(\w+)\s*=\s*([^;]+);", body):
            try:
                locals_[lm.group(1)] = self._effect_amount(lm.group(2), owner or info)
            except Exception:
                pass
        pat = re.compile(r"\b(DamageCmd|CreatureCmd|PowerCmd|CardPileCmd)\s*\.\s*(\w+)\s*(<[^<>]*>)?\s*\(")
        for m in pat.finditer(body):
            cmd, name = m.group(1), m.group(2)
            open_pos = m.end() - 1
            close = match_paren(body, open_pos)
            if close < 0:
                continue
            args = split_top(body[open_pos + 1 : close])
            try:
                if cmd == "DamageCmd" and name == "Attack" and args:
                    ops.append(
                        {"op": "damage", "target": "player", "amount": self._effect_amount(args[0], owner)}
                    )
                elif cmd == "CreatureCmd" and name == "GainBlock" and len(args) >= 2:
                    ops.append(
                        {"op": "block", "target": "self", "amount": self._effect_amount(args[1], owner)}
                    )
                elif cmd == "CreatureCmd" and name == "Heal" and len(args) >= 2:
                    ops.append(
                        {"op": "heal", "target": "self", "amount": self._effect_amount(args[1], owner)}
                    )
                elif cmd == "CardPileCmd" and name in ("AddToCombatAndPreview", "AddToCombat") and len(args) >= 3:
                    card_type = (m.group(3) or "").strip("<> ")
                    pile = "Discard"
                    for a in args:
                        if "PileType." in a:
                            pile = a.split("PileType.")[-1].strip()
                    ops.append(
                        {
                            "op": "add_card",
                            "card": "CARD." + camel_to_screaming(card_type),
                            "pile": pile,
                            "count": self._effect_amount(args[2], owner),
                        }
                    )
                elif cmd == "CardPileCmd" and name in ("AddToCombatAndPreview", "AddToCombat") and len(args) >= 3:
                    card_type = (m.group(3) or "").strip("<> ")
                    pile = "Discard"
                    for a in args:
                        if "PileType." in a:
                            pile = a.split("PileType.")[-1].strip()
                    ops.append(
                        {
                            "op": "add_card",
                            "card": "CARD." + camel_to_screaming(card_type),
                            "pile": pile,
                            "count": self._effect_amount(args[2], owner),
                        }
                    )
                elif cmd == "PowerCmd" and name == "Apply" and len(args) >= 3:
                    power = (m.group(3) or "").strip("<> ")
                    target = args[1]
                    if "base.Creature" in target or "this.Creature" in target:
                        tgt = "self"
                    elif "targets" in target or "Player" in target:
                        tgt = "player"
                    else:
                        tgt = "unknown"
                    ops.append(
                        {
                            "op": "power",
                            "target": tgt,
                            "power": "POWER." + camel_to_screaming(power.split(".")[-1]),
                            "amount": self._effect_amount(args[2], owner),
                        }
                    )
                handled.append((m.start(), close))
            except Exception:
                ops.append({"op": f"{cmd}.{name}", "unresolved": True, "error": str(sys.exc_info()[1])})

        unsupported: list[str] = []
        for m in re.finditer(r"\b(\w+Cmd)\s*\.\s*(\w+)\s*\(", body):
            cmd = m.group(1)
            if cmd not in self.EFFECT_CMDS:
                continue
            if any(start <= m.start() <= end for start, end in handled):
                continue
            unsupported.append(f"{cmd}.{m.group(2)}")
        complete = not unsupported
        return ops or None, complete

    def _card_effects(self, info: ClassInfo) -> tuple[list | None, bool]:
        owner, body = self._find_method(info, "OnPlay")
        if body is None:
            return None, True
        ctx = owner or info
        local_amounts: dict[str, object] = {}
        for lm in re.finditer(r"(?:int|decimal|var)\s+(\w+)\s*=\s*([^;]+);", body):
            try:
                local_amounts[lm.group(1)] = self._effect_amount(lm.group(2), ctx)
            except Exception:
                pass

        def amount(arg: str):
            key = arg.strip()
            if key in local_amounts:
                return local_amounts[key]
            return self._effect_amount(arg, ctx)

        ops: list[dict] = []
        handled: list[tuple[int, int]] = []
        pat = re.compile(r"(DamageCmd|CreatureCmd|PowerCmd|CardPileCmd|PlayerCmd|CardSelectCmd|CardCmd|OrbCmd)\s*\.\s*(\w+)\s*(<[^<>]*>)?\s*\(")
        for m in pat.finditer(body):
            cmd, name = m.group(1), m.group(2)
            open_pos = m.end() - 1
            close = match_paren(body, open_pos)
            if close < 0:
                continue
            args = split_top(body[open_pos + 1 : close])
            semi = body.find(";", close)
            tail = body[close : semi if semi > 0 else close]
            try:
                if cmd == "DamageCmd" and name == "Attack" and args:
                    op = {
                        "op": "damage",
                        "target": "all_enemies" if "TargetingAllOpponents" in tail else "target",
                        "amount": amount(args[0]),
                    }
                    hm = re.search(r"\.WithHitCount\(([^)]*)\)", tail)
                    if hm:
                        op["hits"] = amount(hm.group(1))
                    ops.append(op)
                elif cmd == "CreatureCmd" and name == "GainBlock" and len(args) >= 2:
                    ops.append({"op": "block", "target": "self", "amount": amount(args[1])})
                elif cmd == "CreatureCmd" and name == "Heal" and len(args) >= 2:
                    ops.append({"op": "heal", "target": "self", "amount": amount(args[1])})
                elif cmd == "CardPileCmd" and name in ("AddToCombatAndPreview", "AddToCombat") and len(args) >= 3:
                    card_type = (m.group(3) or "").strip("<> ")
                    pile = "Discard"
                    for a in args:
                        if "PileType." in a:
                            pile = a.split("PileType.")[-1].strip()
                    ops.append(
                        {
                            "op": "add_card",
                            "card": "CARD." + camel_to_screaming(card_type),
                            "pile": pile,
                            "count": self._effect_amount(args[2], owner),
                        }
                    )
                elif cmd == "PowerCmd" and name == "Apply" and len(args) >= 3:
                    power = (m.group(3) or "").strip("<> ")
                    tgt_expr = args[1]
                    if "cardPlay.Target" in tgt_expr:
                        tgt = "target"
                    elif "base.Owner.Creature" in tgt_expr or "this.Creature" in tgt_expr:
                        tgt = "self"
                    elif "HittableEnemies" in tgt_expr or "Enemies" in tgt_expr:
                        tgt = "all_enemies"
                    else:
                        tgt = "unknown"
                    ops.append(
                        {
                            "op": "power",
                            "target": tgt,
                            "power": "POWER." + camel_to_screaming(power.split(".")[-1]),
                            "amount": amount(args[2]),
                        }
                    )
                elif cmd == "CardPileCmd" and name == "Draw" and len(args) >= 2:
                    ops.append({"op": "draw", "amount": amount(args[1])})
                elif cmd == "PlayerCmd" and name == "GainEnergy" and args:
                    ops.append({"op": "energy", "amount": amount(args[0])})
                elif cmd == "OrbCmd" and name == "Channel" and m.group(3):
                    ops.append({"op": "channel", "orb": m.group(3).strip("<> ").split(".")[-1].replace("Orb", "").upper()})
                elif cmd == "OrbCmd" and name == "EvokeNext" and args:
                    dequeue = "dequeue: false" not in body[open_pos:close]
                    ops.append({"op": "evoke", "count": 1, "dequeue": dequeue})
                elif cmd == "PlayerCmd" and name == "GainStars" and args:
                    ops.append({"op": "stars", "amount": amount(args[0])})
                elif cmd == "CardSelectCmd" and name in ("FromHandForDiscard", "FromHandForExhaust") and len(args) >= 3:
                    # CardSelectorPrefs(<prompt>, N) -- N cards picked; the harness always picks the
                    # first eligible card, so the op just needs the count.
                    count = 1
                    pm = re.search(r"CardSelectorPrefs\([^,]*,\s*(\d+)", args[2])
                    if pm:
                        count = int(pm.group(1))
                    ops.append({"op": "exhaust" if "Exhaust" in name else "discard", "count": count, "pick": "first"})
                elif cmd == "CreatureCmd" and name in ("TriggerAnim", "PlayVfx", "PlaySound", "TriggerAnimations"):
                    # Presentation-only commands; they carry no combat state.
                    pass
                elif cmd == "Cmd" and name == "CustomScaledWait":
                    # Presentation-only commands; they carry no combat state.
                    pass
                elif cmd == "CardCmd" and name in ("Discard", "Exhaust") and any(
                    op.get("op") in ("discard", "exhaust") for op in ops
                ):
                    # The selected card is discarded/exhausted by the preceding selection op.
                    pass
                else:
                    continue
                handled.append((m.start(), close))
            except Exception:
                ops.append({"op": f"{cmd}.{name}", "unresolved": True})

        unsupported: list[str] = []
        for m in re.finditer(r"(\w+Cmd)\s*\.\s*(\w+)\s*\(", body):
            cmd = m.group(1)
            if cmd not in self.EFFECT_CMDS:
                continue
            if any(start <= m.start() <= end for start, end in handled):
                continue
            unsupported.append(f"{cmd}.{m.group(2)}")
        for op in ops:
            for field in ("amount", "hits"):
                val = op.get(field)
                if isinstance(val, dict) and "expr" in val:
                    unsupported.append(f"unresolved-{field}")
        if not ops and not unsupported:
            residue = re.sub(r"ArgumentNullException[^;]*;", "", body)
            residue = re.sub(r"//.*", "", residue).strip()
            if residue:
                unsupported.append("unparsed-body")
        return ops or None, not unsupported

    def _effect_amount(self, expr: str, info: ClassInfo):
        e = expr.strip()
        e = re.sub(r"(\d+)*", lambda mm: '"' + str(info.src.literals.get(int(mm.group(1)), mm.group(0))) + '"', e)
        e = re.sub(r"^\((?:int|decimal|float)\)\s*", "", e)
        vm = re.fullmatch(
            r"(?:base\.)?DynamicVars(?:\[\s*\"(\w+)\"\s*\]|\.(\w+))", e
        )
        if vm:
            name = vm.group(1) or vm.group(2)
            return {"var": POWER_VAR_ALIASES.get(name, name)}
        vm = re.fullmatch(
            r"base\.DynamicVars(?:\[\s*\"(\w+)\"\s*\]|\.(\w+))\.(?:BaseValue|IntValue|Value)", e
        )
        if vm:
            name = vm.group(1) or vm.group(2)
            return {"var": POWER_VAR_ALIASES.get(name, name)}
        if e.startswith("-"):
            inner = self._effect_amount(e[1:].strip(), info)
            if isinstance(inner, (int, float)):
                return -inner
            return inner
        resolved = self.resolver.resolve(e, info)
        if "level" in resolved and "ascension" in resolved:
            # Keep both sides: the game picks by run ascension level, the engine mirrors that.
            return {
                "base": resolved["base"],
                "ascension": resolved["ascension"],
                "level": camel_to_screaming(resolved["level"]),
            }
        if "base" in resolved and isinstance(resolved["base"], (int, float)):
            return resolved["base"]
        if "value" in resolved and isinstance(resolved["value"], (int, float)):
            return resolved["value"]
        return resolved

    # -- powers ----------------------------------------------------------

    def powers(self) -> dict:
        out = {}
        for info in self.table.pick("powers"):
            if info.name.startswith("Mock"):
                continue
            pid = "POWER." + camel_to_screaming(info.name)
            try:
                entry = {
                    "id": pid,
                    "name": info.name,
                    "type": self._enum_prop(info, "Type"),
                    "stack_type": self._enum_prop(info, "StackType"),
                    "vars": self.parse_vars(info, pid) or None,
                }
                out[pid] = entry
            except Exception as e:
                self.error(pid, f"{type(e).__name__}: {e}")
                out[pid] = {"id": pid, "name": info.name, "export_error": f"{type(e).__name__}: {e}"}
        return dict(sorted(out.items()))

    def _find_prop(self, info: ClassInfo, name: str) -> ClassInfo | None:
        cur = info
        seen = set()
        while cur is not None and cur.name not in seen:
            seen.add(cur.name)
            if cur.prop_expr(name) is not None or cur.prop_block(name) is not None:
                return cur
            options = [c for c in self.table.by_name.get(cur.base, []) if c.namespace == cur.namespace]
            cur = options[0] if options else None
        return None

    def _enum_prop(self, info: ClassInfo, prop: str) -> str | None:
        owner = self._find_prop(info, prop) or info
        block = owner.prop_block(prop)
        if block is not None and re.search(r"\bif\s*\(", block):
            return None
        expr = owner.prop_expr(prop)
        if not expr:
            return None
        resolved = self.resolver.resolve(expr, owner)
        return resolved.get("enum")

    # -- relics ----------------------------------------------------------

    RELIC_COSTS = {
        "Common": 175,
        "Uncommon": 225,
        "Rare": 275,
        "Shop": 200,
        "Ancient": 999999999,
        "Starter": 999999999,
        "Event": 999999999,
        "None": 1,
    }

    def relics(self) -> dict:
        out = {}
        for info in self.table.pick("relics"):
            if info.name.startswith("Mock"):
                continue
            rid = "RELIC." + camel_to_screaming(info.name)
            try:
                rarity = self._enum_prop(info, "Rarity")
                cost_expr = info.prop_expr("MerchantCost")
                if cost_expr:
                    resolved = self.resolver.resolve(cost_expr, info)
                    merchant_cost = self._entry_value(resolved)
                    if merchant_cost is None:
                        merchant_cost = self.RELIC_COSTS.get(rarity or "")
                else:
                    merchant_cost = self.RELIC_COSTS.get(rarity or "")
                out[rid] = {
                    "id": rid,
                    "name": info.name,
                    "rarity": rarity,
                    "vars": self.parse_vars(info, rid) or None,
                    "merchant_cost": merchant_cost,
                }
            except Exception as e:
                self.error(rid, f"{type(e).__name__}: {e}")
                out[rid] = {"id": rid, "name": info.name, "export_error": f"{type(e).__name__}: {e}"}
        return dict(sorted(out.items()))

    # -- potions ---------------------------------------------------------

    def potions(self) -> dict:
        out = {}
        for info in self.table.pick("potions"):
            if info.name.startswith("Mock"):
                continue
            pid = "POTION." + camel_to_screaming(info.name)
            try:
                out[pid] = {
                    "id": pid,
                    "name": info.name,
                    "rarity": self._enum_prop(info, "Rarity"),
                    "target": self._enum_prop(info, "TargetType"),
                    "vars": self.parse_vars(info, pid) or None,
                }
            except Exception as e:
                self.error(pid, f"{type(e).__name__}: {e}")
                out[pid] = {"id": pid, "name": info.name, "export_error": f"{type(e).__name__}: {e}"}
        return dict(sorted(out.items()))

    # -- encounters ------------------------------------------------------

    def encounters(self) -> dict:
        out = {}
        for info in self.table.pick("encounters"):
            if info.name.startswith("Mock"):
                continue
            eid = "ENCOUNTER." + camel_to_screaming(info.name)
            try:
                out[eid] = self.encounter(info, eid)
            except Exception as e:
                self.error(eid, f"{type(e).__name__}: {e}")
                out[eid] = {"id": eid, "name": info.name, "export_error": f"{type(e).__name__}: {e}"}
        return dict(sorted(out.items()))

    def encounter(self, info: ClassInfo, eid: str) -> dict:
        monsters = self._all_monster_ids(info)
        entry = {
            "id": eid,
            "name": info.name,
            "room_type": self._enum_prop(info, "RoomType"),
            "is_weak": bool(self._bool_expr(info, "IsWeak")),
            "all_possible_monsters": monsters,
        }
        body = self._find_method_body(info, "GenerateMonsters")
        entry["monsters"] = self._encounter_slots(info, body) if body else None
        starter_rng = self._starter_rng(body) if body else None
        if starter_rng is not None:
            entry["starter_rng"] = starter_rng
        return entry

    def _all_monster_ids(self, info: ClassInfo) -> list[str]:
        chunks: list[tuple[str, ClassInfo]] = []
        visited: set = set()

        def add_body(c: ClassInfo) -> None:
            key = ("body", c.name)
            if key in visited:
                return
            visited.add(key)
            chunks.append((c.body, c))

        add_body(info)
        cur = info
        while True:
            options = [c for c in self.table.by_name.get(cur.base, []) if c.namespace == cur.namespace]
            if not options:
                break
            cur = options[0]
            add_body(cur)

        ids: list[str] = []
        while chunks:
            text, cur = chunks.pop(0)
            for m in re.finditer(r"ModelDb\.Monster<([A-Za-z_]\w*)>\s*\(\s*\)", text):
                mid = "MONSTER." + camel_to_screaming(m.group(1))
                if mid not in ids:
                    ids.append(mid)
            for m in re.finditer(r"\b([A-Z][A-Za-z0-9_]*)\.([A-Za-z_]\w*)\b", text):
                cls, member = m.groups()
                if cls == "ModelDb":
                    continue
                key = ("static", cls, member)
                if key in visited:
                    continue
                visited.add(key)
                for cand in self.table.by_name.get(cls, []):
                    sub = cand.field_expr(member) or cand.prop_expr(member)
                    if sub:
                        chunks.append((sub, cand))
                        break
        return ids


    def _find_field(self, info: ClassInfo, name: str) -> str | None:
        cur = info
        seen = set()
        while cur is not None and cur.name not in seen:
            seen.add(cur.name)
            expr = cur.field_expr(name)
            if expr is not None:
                return expr
            options = [c for c in self.table.by_name.get(cur.base, []) if c.namespace == cur.namespace]
            cur = options[0] if options else None
        return None

    def _starter_rng(self, body: str) -> dict | None:
        """`num = (var.StarterMoveIdx = base.Rng.NextInt(N)); var2.StarterMoveIdx = (num + k) % N;`"""
        base = re.search(
            r"\(\s*(\w+)\.StarterMoveIdx\s*=\s*base\.Rng\.NextInt\((\d+)\)\s*\)", body
        )
        if not base:
            return None
        base_var, size = base.group(1), int(base.group(2))
        offsets: dict[str, int] = {base_var: 0}
        for m in re.finditer(r"(\w+)\.StarterMoveIdx\s*=\s*\(\s*num\s*\+\s*(\d+)\s*\)\s*%\s*(\d+)\s*;", body):
            offsets[m.group(1)] = int(m.group(2))
        nums = re.findall(r"\bnum\s*=\s*\(\s*(\w+)\.StarterMoveIdx", body)
        if nums:
            offsets[nums[0]] = 0
        return {"range": size, "offsets": list(offsets.values())}

    def _encounter_slots(self, info: ClassInfo, body: str) -> list | None:
        var_to_monster: dict[str, str] = {}
        for m in re.finditer(r"(\w+)\s*=\s*(?:\([^)]*\)\s*)?ModelDb\.Monster<([A-Za-z_]\w*)>", body):
            var_to_monster[m.group(1)] = "MONSTER." + camel_to_screaming(m.group(2))

        def monster_id(expr: str) -> str | None:
            expr = expr.strip()
            direct = re.search(r"ModelDb\.Monster<([A-Za-z_]\w*)>", expr)
            if direct:
                return "MONSTER." + camel_to_screaming(direct.group(1))
            if re.fullmatch(r"[A-Za-z_]\w*", expr):
                return var_to_monster.get(expr)
            return None

        ret = body.find("return")
        if ret < 0:
            return None
        tail = body[ret:]
        arr = re.search(r"\[\d*\]\s*\{", tail)
        if arr is not None:
            close = match_brace(tail, arr.end() - 1)
            items = split_top(tail[arr.end() : close])
        else:
            single = re.search(r"ReadOnlySingleElementList<[^>]*>\s*\(", tail)
            if single is None:
                return None
            outer_close = match_paren(tail, single.end() - 1)
            items = [tail[single.end() : outer_close].strip()]
        out: list = []
        for item in items:
            item = item.strip()
            if not item:
                continue
            if not item.startswith("("):
                return None
            inner_close = match_paren(item, 0)
            parts = split_top(item[1:inner_close])
            if len(parts) != 2:
                return None
            mid = monster_id(parts[0])
            if mid is None:
                return None
            slot = parts[1].strip()
            if slot == "null":
                slot_value = None
            else:
                slot_value = self._literal_str(info, slot)
                if slot_value is None:
                    return None
            out.append([mid, slot_value])
        return out or None

    # -- characters ------------------------------------------------------

    def characters(self) -> dict:
        out = {}
        for info in self.table.pick("characters"):
            if info.name.startswith("Mock"):
                continue
            cid = "CHARACTER." + camel_to_screaming(info.name)
            try:
                out[cid] = self.character(info, cid)
            except Exception as e:
                self.error(cid, f"{type(e).__name__}: {e}")
                out[cid] = {"id": cid, "name": info.name, "export_error": f"{type(e).__name__}: {e}"}
        return dict(sorted(out.items()))

    def character(self, info: ClassInfo, cid: str) -> dict:
        deck_expr = info.prop_expr("StartingDeck") or ""
        deck = ["CARD." + camel_to_screaming(m.group(1)) for m in re.finditer(r"ModelDb\.Card<(\w+)>", deck_expr)]
        relic_expr = info.prop_expr("StartingRelics") or ""
        relics = ["RELIC." + camel_to_screaming(m.group(1)) for m in re.finditer(r"ModelDb\.Relic<(\w+)>", relic_expr)]
        pool_expr = info.prop_expr("CardPool") or ""
        pool = None
        pm = re.search(r"ModelDb\.CardPool<(\w+)>", pool_expr)
        if pm:
            pool = "CARD_POOL." + camel_to_screaming(pm.group(1))
        energy = 3
        e = info.prop_expr("MaxEnergy")
        if e:
            v = self._entry_value(self.resolver.resolve(e, info))
            if isinstance(v, (int, float)):
                energy = int(v)
        return {
            "id": cid,
            "name": info.name,
            "starting_hp": self._entry_value(self._int_prop(info, "StartingHp")),
            "starting_gold": self._entry_value(self._int_prop(info, "StartingGold")),
            "max_energy": energy,
            "starting_deck": deck,
            "starting_relics": relics,
            "card_pool": pool,
        }

    # -- acts ------------------------------------------------------------

    def acts(self) -> dict:
        out = {}
        for info in self.table.pick("acts"):
            if info.name.startswith("Mock"):
                continue
            aid = "ACT." + camel_to_screaming(info.name)
            try:
                out[aid] = {
                    "id": aid,
                    "name": info.name,
                    "index": self._entry_value(self._int_prop(info, "Index")),
                    "is_default": bool(self._bool_expr(info, "IsDefault")),
                }
            except Exception as e:
                self.error(aid, f"{type(e).__name__}: {e}")
                out[aid] = {"id": aid, "name": info.name, "export_error": f"{type(e).__name__}: {e}"}
        return dict(sorted(out.items()))


def camel_to_screaming(name: str) -> str:
    """Slugify like StringHelper.Slugify: CamelCase -> UPPER_SNAKE."""
    s = re.sub(r"([a-z0-9])([A-Z])", r"\1_\2", name)
    s = re.sub(r"([A-Z]+)([A-Z][a-z])", r"\1_\2", s)
    s = re.sub(r"[^A-Za-z0-9]+", "_", s)
    return s.strip("_").upper()


# ------------------------------------------------------------------------ output


def game_meta(game_dir: Path, tree: Path) -> dict:
    release = {}
    release_path = game_dir / "release_info.json"
    if release_path.exists():
        release = json.loads(release_path.read_text(encoding="utf-8"))
    dll = game_dir / "data_sts2_windows_x86_64" / "sts2.dll"
    meta = {
        "version": release.get("version"),
        "commit": release.get("commit"),
        "main_assembly_hash": release.get("main_assembly_hash"),
        "dll_name": dll.name,
        "dll_size": dll.stat().st_size if dll.exists() else None,
        "dll_sha256": hashlib.sha256(dll.read_bytes()).hexdigest() if dll.exists() else None,
    }
    return meta


def tree_meta(tree: Path) -> dict:
    files = list(tree.rglob("*.cs"))
    newest = max((f.stat().st_mtime for f in files), default=0)
    stamp = os.environ.get("SOURCE_DATE_EPOCH")
    when = datetime.fromtimestamp(int(stamp), tz=timezone.utc) if stamp else datetime.now(timezone.utc)
    return {
        "decompiler": "ilspycmd 9.1.0.7988",
        "tree": tree.as_posix(),
        "files": len(files),
        "decompiled_at": datetime.fromtimestamp(newest, tz=timezone.utc).isoformat() if newest else None,
        "exported_at": when.isoformat(),
    }


def write_json(path: Path, data) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    text = json.dumps(data, indent=2, ensure_ascii=False, sort_keys=False) + "\n"
    tmp = path.with_suffix(path.suffix + ".tmp")
    tmp.write_text(text, encoding="utf-8", newline="\n")
    tmp.replace(path)


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--tree", default="_research/decompiled-v0.111.0")
    ap.add_argument(
        "--game-dir",
        default=r"C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2",
    )
    ap.add_argument("--out", default="data/sts2")
    ap.add_argument("--only", default="", help="comma separated categories")
    args = ap.parse_args(argv)

    tree = Path(args.tree)
    if not tree.exists():
        print(f"decompiled tree not found: {tree}", file=sys.stderr)
        return 2
    game_dir = Path(args.game_dir)
    out_dir = Path(args.out)

    exporter = Exporter(tree, game_dir)
    only = {s.strip() for s in args.only.split(",") if s.strip()}
    payload = {}
    for category in CATEGORY_DIRS:
        if only and category not in only:
            continue
        method = getattr(exporter, category)
        payload[category] = method()

    counts = {k: len(v) for k, v in payload.items()}
    if not only:
        assembly_counts = {}
        mock_counts = {}
        for category, (_dir_part, base) in CATEGORY_DIRS.items():
            total, mocks = exporter.table.counts(base)
            assembly_counts[category] = total
            mock_counts[category] = mocks
        manifest = {
            "schema_version": SCHEMA_VERSION,
            "game": game_meta(game_dir, tree),
            "source": tree_meta(tree),
            "counts": counts,
            "assembly_counts": assembly_counts,
            "mock_counts": mock_counts,
            "errors": exporter.errors,
        }
        write_json(out_dir / "manifest.json", manifest)
    for category, data in payload.items():
        write_json(out_dir / f"{category}.json", data)
    print(f"exported {counts} -> {out_dir}")
    if exporter.errors:
        print(f"{len(exporter.errors)} export errors; first 10:", file=sys.stderr)
        for e in exporter.errors[:10]:
            print(f"  {e['scope']}: {e['error']}", file=sys.stderr)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

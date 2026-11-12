#!/usr/bin/env python3
"""Check relative Markdown links in README.md, docs/ and shaders/README.md."""
from __future__ import annotations

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
LINK = re.compile(r"!?\[[^\]]*\]\(([^)]+)\)")
FENCE = re.compile(r"```.*?```", re.S)


def targets() -> list[Path]:
    files = [ROOT / "README.md", ROOT / "shaders" / "README.md", ROOT / "tools" / "zero-copy" / "README.md"]
    files += sorted((ROOT / "docs").rglob("*.md"))
    return [p for p in files if p.is_file()]


def main() -> int:
    missing: list[str] = []
    checked = 0
    for path in targets():
        text = FENCE.sub("", path.read_text(encoding="utf-8"))
        for raw in LINK.findall(text):
            target = raw.strip()
            if target.startswith(("http://", "https://", "mailto:", "#")):
                continue
            target = target.split("#", 1)[0]
            if not target:
                continue
            checked += 1
            candidate = (path.parent / target).resolve()
            if not candidate.exists():
                try:
                    display = path.relative_to(ROOT)
                except ValueError:
                    display = path
                missing.append(f"{display}: {raw}")
    if missing:
        print(f"missing links: {len(missing)} (checked {checked})")
        for item in missing:
            print(f"  {item}")
        return 1
    print(f"ok: {checked} links checked, 0 missing")
    return 0


if __name__ == "__main__":
    sys.exit(main())
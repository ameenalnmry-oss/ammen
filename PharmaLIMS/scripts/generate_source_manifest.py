#!/usr/bin/env python3
from __future__ import annotations

import hashlib
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
MANIFEST = ROOT / "SOURCE_MANIFEST_SHA256.txt"
EXCLUDED_PARTS = {".git", ".vs", "bin", "obj", "artifacts", "__pycache__"}
EXCLUDED_SUFFIXES = {".pyc", ".user", ".suo"}
EXCLUDED_LOCAL_SETTINGS = {
    "appsettings.Production.json",
    "appsettings.Development.json",
    "appsettings.Local.json",
}


def controlled_files() -> list[Path]:
    return sorted(
        path
        for path in ROOT.rglob("*")
        if path.is_file()
        and path != MANIFEST
        and path.name not in EXCLUDED_LOCAL_SETTINGS
        and not any(part in EXCLUDED_PARTS for part in path.relative_to(ROOT).parts)
        and path.suffix.lower() not in EXCLUDED_SUFFIXES
    )


def main() -> None:
    lines = ["# PharmaLIMS source integrity manifest"]
    for path in controlled_files():
        digest = hashlib.sha256(path.read_bytes()).hexdigest()
        relative = path.relative_to(ROOT).as_posix()
        lines.append(f"{digest}  {relative}")
    MANIFEST.write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")
    print(f"Wrote {len(lines) - 1} controlled file hashes to {MANIFEST.name}.")


if __name__ == "__main__":
    main()

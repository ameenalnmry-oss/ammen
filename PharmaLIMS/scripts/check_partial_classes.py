from __future__ import annotations
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parents[1]
PART_RE = re.compile(r"^(?P<base>.+)\.Part(?P<num>\d+)\.cs$", re.I)
CLASS_RE = re.compile(r"\bpartial\s+class\s+(?P<name>[A-Za-z_][A-Za-z0-9_]*)")

errors: list[str] = []
families: dict[str, list[pathlib.Path]] = {}

for path in ROOT.rglob("*.Part*.cs"):
    if any(part in {"bin", "obj", "tests", "tools"} for part in path.parts):
        continue
    match = PART_RE.match(path.name)
    if not match:
        continue
    base_name = match.group("base")
    base_path = path.with_name(base_name + ".cs")
    families.setdefault(str(base_path.relative_to(ROOT)), []).append(path)
    if not base_path.exists():
        errors.append(f"orphan partial: {path.relative_to(ROOT)} has no {base_path.name}")
        continue

    base_classes = set(CLASS_RE.findall(base_path.read_text(encoding="utf-8-sig")))
    part_classes = set(CLASS_RE.findall(path.read_text(encoding="utf-8-sig")))
    if not base_classes:
        errors.append(f"base file is not partial: {base_path.relative_to(ROOT)}")
    if not part_classes:
        errors.append(f"part file is not partial: {path.relative_to(ROOT)}")
    if base_classes.isdisjoint(part_classes):
        errors.append(
            f"class mismatch: {path.relative_to(ROOT)} declares {sorted(part_classes)} "
            f"but base declares {sorted(base_classes)}"
        )

if errors:
    print("\n".join(errors))
    sys.exit(1)

print(f"Partial class validation passed for {len(families)} source families.")

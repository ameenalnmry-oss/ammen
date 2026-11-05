"""Execute module-level source regressions alongside the unittest class suite."""
from __future__ import annotations

import ast
from pathlib import Path
import runpy
import unittest


def main() -> int:
    root = Path(__file__).resolve().parents[1]
    suite = unittest.TestSuite()
    for path in sorted((root / "tests").glob("test_*.py")):
        tree = ast.parse(path.read_text(encoding="utf-8-sig"))
        names = [node.name for node in tree.body
                 if isinstance(node, ast.FunctionDef) and node.name.startswith("test_")]
        if not names:
            continue
        scope = runpy.run_path(str(path))
        for name in names:
            # FunctionTestCase reports errors rather than silently omitting any
            # future test whose required fixture arguments are unsupported here.
            suite.addTest(unittest.FunctionTestCase(
                scope[name], description=f"{path.name}:{name}"))
    if not suite.countTestCases():
        raise RuntimeError("No module-level source regressions were discovered.")
    return 0 if unittest.TextTestRunner(verbosity=2).run(suite).wasSuccessful() else 1


if __name__ == "__main__":
    raise SystemExit(main())

"""Compile exact active production gates into the disposable SQL regression host."""
import pathlib
import re
import sys


def method(source, name):
    # Mask strings/comments before balancing braces; preserve offsets in the source.
    masked = re.sub(r'@"(?:""|[^"])*"|"(?:\\.|[^"\\])*"|\'(?:\\.|[^\'\\])*\'|//[^\r\n]*|/\*[\s\S]*?\*/',
                    lambda m: ' ' * len(m.group()), source)
    found = re.search(r'\b(?:private|internal)\s+static\s+(?:int|void)\s+' + re.escape(name) + r'\s*\(', masked)
    if not found:
        raise ValueError('Production method not found: ' + name)
    start = masked.index('{', found.start())
    depth = 1
    end = start + 1
    while depth and end < len(masked):
        depth += (masked[end] == '{') - (masked[end] == '}')
        end += 1
    if depth:
        raise ValueError('Unbalanced production method: ' + name)
    return source[found.start():end]


if __name__ == '__main__':
    root = pathlib.Path(sys.argv[1]).resolve()
    output = pathlib.Path(sys.argv[2])
    gate_name = 'EnsureEmApprovalQualityEventGateInTransaction'
    gate = method((root / 'DatabaseHelper.EnvironmentalMonitoring.cs').read_text(encoding='utf-8-sig'), gate_name)
    plan = (root / 'DatabaseHelper.EmSourcePlan.cs').read_text(encoding='utf-8-sig')
    before = (root / 'tests/fixtures/R9-EM-approval-gate-before-lifecycle-fix.cs.txt').read_text(encoding='utf-8-sig')
    before = before.replace(gate_name, gate_name + 'BeforeMergeFix', 1)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text('#nullable enable\nusing System;\nusing System.Data;\nusing System.Globalization;\nusing Microsoft.Data.SqlClient;\n'
                      'namespace PharmaLIMS { public static partial class DatabaseHelper {\n' +
                      gate + '\n' + before + '\n' + method(plan, 'LockEmSourcePlanInTransaction') + '\n' +
                      method(plan, 'EnsureEmSourcePlanInTransaction') + '\n}}\n', encoding='utf-8')

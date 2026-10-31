"""Compile actual certificate methods without WPF; never copy their behavior into tests."""
from pathlib import Path
import re
import sys

source = Path(sys.argv[1]).read_text(encoding="utf-8-sig")
names = {
    "FormatResultForCertificate", "FormatReportSpecification", "GetConformity",
    "TryGetDecimal", "IsAppearanceTest", "IsApprovedComplianceQualitative",
    "IsComplianceQualitativeTest", "ConciseApprovedQualitativeCriterion",
    "IsAbsencePresenceTest", "IsPhTest", "IsConductivityTest", "IsHardnessTest",
    "IsPotableWater", "IsSoftWaterPoint", "ExtractPointNumber",
    "IsResidualChlorineTest", "IsPathogenTest", "IsTotalBacterialCountTest",
    "IsMicrobialCountTest",
}
methods = []
found = set()
for match in re.finditer(r"^        private (?:static )?(?:bool|string|int) (\w+)\(", source, re.M):
    if match[1] not in names:
        continue
    start = source.index("{", match.end())
    depth, end = 1, start + 1
    while depth:
        depth += (source[end] == "{") - (source[end] == "}")
        end += 1
    methods.append(source[match.start():end])
    found.add(match[1])
if found != names:
    raise RuntimeError(f"Production methods missing: {names - found}")
output = Path(sys.argv[2])
output.parent.mkdir(parents=True, exist_ok=True)
output.write_text(
    "#nullable disable\nusing System.Globalization;\nusing System.Text.RegularExpressions;\n"
    "using PharmaLIMS.Services;\nnamespace PharmaLIMS;\ninternal partial class CertificateMethods\n{\n"
    # Only UI state used by an unrelated pre-existing hardness branch.
    'private string currentPointCodeForSpec = "";\n' + "\n".join(methods) + "\n}\n",
    encoding="utf-8",
)

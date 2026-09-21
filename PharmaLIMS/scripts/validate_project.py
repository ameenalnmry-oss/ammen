#!/usr/bin/env python3
from __future__ import annotations

import hashlib
import json
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
ERRORS: list[str] = []
PRODUCTION_PUBLISH = "--production-publish" in sys.argv[1:]
DELIVERY_PACKAGE = "--delivery-package" in sys.argv[1:]
EXCLUDED = {"bin", "obj", ".git", ".vs", "artifacts", "__pycache__", ".pytest_cache"}
XAML_NS = "http://schemas.microsoft.com/winfx/2006/xaml"
EVENT_ATTRIBUTES = {
    "Loaded", "Unloaded", "Click", "SelectionChanged", "TextChanged",
    "Checked", "Unchecked", "SelectedDateChanged", "PreviewKeyDown",
    "KeyDown", "KeyUp", "MouseDoubleClick", "MouseLeftButtonDown",
    "DropDownOpened", "Closing", "Closed", "CellEditEnding",
    "AutoGeneratingColumn", "CurrentCellChanged", "BeginningEdit", "PreparingCellForEdit",
}


def ignored(path: Path) -> bool:
    return any(part in EXCLUDED for part in path.parts)


def error(message: str) -> None:
    ERRORS.append(message)


def text(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig", errors="replace")


def source_family(path: Path) -> str:
    """Read a C# source file plus any controlled partial continuation files."""
    parts = [path]
    parts.extend(sorted(path.parent.glob(path.stem + ".Part*.cs")))
    return "\n".join(text(part) for part in parts if part.is_file())


def controlled_source_files() -> set[str]:
    excluded_parts = {
        ".git",
        ".vs",
        "bin",
        "obj",
        "artifacts",
        "__pycache__",
        ".pytest_cache",
    }
    excluded_suffixes = {
        ".pyc",
        ".user",
        ".suo",
    }
    excluded_local_settings = {
        "appsettings.Production.json",
        "appsettings.Development.json",
        "appsettings.Local.json",
    }

    # NuGet lock files are governed by the dedicated reproducibility/locked-restore
    # release gate. Restore/materialization may create them before this validator
    # runs, so they must not be treated as unmanifested SOURCE_MANIFEST files.
    # They remain valid repository/release artifacts and are still checked by the
    # NuGet lock/release-validation pipeline.
    excluded_generated_files = {
        "packages.lock.json",
    }

    return {
        path.relative_to(ROOT).as_posix()
        for path in ROOT.rglob("*")
        if path.is_file()
        and path.name != "SOURCE_MANIFEST_SHA256.txt"
        and path.name not in excluded_local_settings
        and path.name not in excluded_generated_files
        and not any(
            part in excluded_parts
            for part in path.relative_to(ROOT).parts
        )
        and path.suffix.lower() not in excluded_suffixes
    }


manifest_path = ROOT / "SOURCE_MANIFEST_SHA256.txt"
if not manifest_path.exists():
    error("Controlled source manifest is missing.")
else:
    listed_sources: dict[str, str] = {}
    for line_number, line in enumerate(text(manifest_path).splitlines(), start=1):
        if not line.strip() or line.startswith("#"):
            continue
        match = re.fullmatch(r"([0-9a-fA-F]{64})  (.+)", line)
        if not match:
            error(f"Invalid source manifest line {line_number}.")
            continue
        digest, relative = match.groups()
        if relative in listed_sources:
            error(f"Duplicate source manifest entry: {relative}")
            continue
        listed_sources[relative] = digest.lower()
        source_path = ROOT / relative
        if not source_path.is_file():
            error(f"Source manifest references a missing file: {relative}")
            continue
        actual = hashlib.sha256(source_path.read_bytes()).hexdigest()
        if actual != digest.lower():
            error(f"Source manifest hash mismatch: {relative}")

    actual_sources = controlled_source_files()
    unmanifested = sorted(actual_sources - set(listed_sources))
    if unmanifested:
        error("Unmanifested controlled source file(s): " + ", ".join(unmanifested))
    orphaned = sorted(set(listed_sources) - actual_sources)
    if orphaned:
        error("Source manifest contains non-controlled/missing file(s): " + ", ".join(orphaned))


# XML/XAML integrity, duplicate x:Name, and event handler coverage.
for path in ROOT.rglob("*.xaml"):
    if ignored(path):
        continue
    try:
        tree = ET.parse(path)
    except Exception as exc:
        error(f"Invalid XAML {path.relative_to(ROOT)}: {exc}")
        continue

    names: dict[str, int] = {}
    handlers: set[str] = set()
    for element in tree.iter():
        xname = element.attrib.get(f"{{{XAML_NS}}}Name") or element.attrib.get("Name")
        if xname:
            names[xname] = names.get(xname, 0) + 1
        for attr, value in element.attrib.items():
            local = attr.rsplit("}", 1)[-1]
            if local in EVENT_ATTRIBUTES and re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*", value or ""):
                handlers.add(value)

    for name, count in names.items():
        if count > 1:
            error(f"Duplicate x:Name '{name}' ({count}) in {path.relative_to(ROOT)}")

    codebehind = path.with_suffix(path.suffix + ".cs")
    if handlers and codebehind.exists():
        code = source_family(codebehind)
        for handler in sorted(handlers):
            if not re.search(rf"\b{re.escape(handler)}\s*\(", code):
                error(f"Missing handler {handler} referenced by {path.relative_to(ROOT)}")

# JSON and project XML.
for path in ROOT.rglob("*.json"):
    if ignored(path):
        continue
    try:
        json.loads(text(path))
    except Exception as exc:
        error(f"Invalid JSON {path.relative_to(ROOT)}: {exc}")

try:
    ET.parse(ROOT / "PharmaLIMS.csproj")
except Exception as exc:
    error(f"Invalid project XML: {exc}")

# Lightweight C# lexical checks intended to catch packaging regressions before CI build.
for path in ROOT.rglob("*.cs"):
    if ignored(path):
        continue
    source = text(path)
    scrub = re.sub(
        r'@"(?:""|[^"])*"|"(?:\\.|[^"\\])*"|\'(?:(?:\\.)|[^\'\\])\'|//.*?$|/\*.*?\*/',
        "",
        source,
        flags=re.M | re.S,
    )
    for left, right in [("(", ")"), ("{", "}"), ("[", "]")]:
        if scrub.count(left) != scrub.count(right):
            error(f"Unbalanced {left}{right}: {path.relative_to(ROOT)}")

    duplicate_decl = re.search(
        r"\b(?:int|string|bool|decimal|double|DateTime|DataTable|SqlCommand|var)\s+([A-Za-z_]\w*)\s*=\s*"
        r"(?:int|string|bool|decimal|double|DateTime|DataTable|SqlCommand|var)\s+\1\s*=",
        scrub,
    )
    if duplicate_decl:
        error(f"Duplicate declaration assignment for '{duplicate_decl.group(1)}' in {path.relative_to(ROOT)}")

    if re.search(r"catch\s*\([^)]*\)\s*\{\s*\{", scrub):
        error(f"Duplicate opening block immediately after catch in {path.relative_to(ROOT)}")

# SQL packaging checks.
for path in ROOT.rglob("*.sql"):
    if ignored(path):
        continue
    source = text(path)
    if re.search(r"\bINSERT\s+(?:INTO\s+)?dbo\.LIMS_SchemaVersions[^;\n]*\n\s*INSERT\s+(?:INTO\s+)?dbo\.LIMS_SchemaVersions", source, re.I):
        error(f"Duplicate consecutive schema-version INSERT in {path.relative_to(ROOT)}")

# Version consistency.
project_text = text(ROOT / "PharmaLIMS.csproj")
if "<UseWPF>true</UseWPF>" not in project_text:
    error("UseWPF must remain enabled.")
if "<EnableDefaultApplicationDefinition>false</EnableDefaultApplicationDefinition>" not in project_text:
    error("WPF ApplicationDefinition must use the explicit clean-build mapping.")
if "<EnableDefaultPageItems>false</EnableDefaultPageItems>" not in project_text:
    error("WPF Page items must use the explicit clean-build mapping.")
if '<ApplicationDefinition Include="App.xaml">' not in project_text:
    error("App.xaml must be explicitly compiled as ApplicationDefinition.")
if '<Page Include="**\\*.xaml" Exclude="App.xaml;bin\\**;obj\\**;tools\\**;tests\\**">' not in project_text:
    error("Application XAML views must be explicitly compiled as WPF Page items.")
if re.search(r"<RuntimeFrameworkVersion>[^<]+</RuntimeFrameworkVersion>", project_text):
    error("RuntimeFrameworkVersion must not be patch-pinned; source/F5 builds must accept any compatible installed .NET 8 Windows Desktop patch.")
if re.search(r"<RollForward>LatestPatch</RollForward>", project_text):
    error("RollForward LatestPatch must not be used with a patch-pinned source build; use the normal .NET 8 framework resolution for F5/debug.")
ci_workflow_paths = [ROOT / ".github/workflows/ci.yml"]
repository_ci_workflow = ROOT.parent / ".github/workflows/ci.yml"
if repository_ci_workflow.is_file():
    ci_workflow_paths.append(repository_ci_workflow)

for ci_workflow_path in ci_workflow_paths:
    ci_workflow = text(ci_workflow_path)
    for marker, message in (
        ("--self-contained true", "publishing a self-contained Windows package"),
        ("Authenticode-sign published first-party binaries", "Authenticode signing"),
        ("Smoke exact signed Production artifact", "Production artifact smoke"),
        ("Generate and verify publish hash manifest and provenance", "publish hash manifest verification"),
        ("Attest publish manifest provenance", "build provenance attestation"),
        ("Upload signed, smoke-tested package", "controlled package upload"),
    ):
        if marker not in ci_workflow:
            error(f"CI workflow {ci_workflow_path} is missing {message}.")

if repository_ci_workflow.is_file():
    active_ci_workflow = text(repository_ci_workflow)
    for marker in (
        "Remove site-specific Production configuration from distributable package",
        "Site-specific appsettings.json must not be included in the uploaded Production package.",
    ):
        if marker not in active_ci_workflow:
            error(f"Active repository CI workflow is missing Production configuration redaction control: {marker}")
match = re.search(r"<Version>([^<]+)</Version>", project_text)
if not match:
    error("Project Version is missing.")
else:
    version = match.group(1).strip()
    file_match = re.search(r"<FileVersion>([^<]+)</FileVersion>", project_text)
    if not file_match or file_match.group(1).strip() != version:
        error("Version and FileVersion do not match.")
    if "<GenerateAssemblyInfo>false</GenerateAssemblyInfo>" in project_text:
        assembly_info = text(ROOT / "AssemblyInfo.cs")
        for attribute_name in (
            "AssemblyVersion",
            "AssemblyFileVersion",
            "AssemblyInformationalVersion",
        ):
            attribute_match = re.search(
                rf'{attribute_name}\("([^"]+)"\)',
                assembly_info,
            )
            if not attribute_match or attribute_match.group(1).strip() != version:
                error(
                    f"{attribute_name} in AssemblyInfo.cs must match project "
                    f"version {version} when GenerateAssemblyInfo is false."
                )
    production = json.loads(text(ROOT / "appsettings.Production.example.json"))
    runtime = production.get("Runtime", {})
    if runtime.get("ApplyStartupDatabaseUpdates") is not False:
        error("Production example must disable startup database updates.")
    if runtime.get("DevelopmentAdminFullPermissions") is not False:
        error("Production example must disable development Admin overrides.")
    if runtime.get("AllowEarlyMicrobiologyResults") is not False:
        error("Production example must disable early microbiology results.")
    if runtime.get("AllowLegacyPrmSpecificationFallback") is not False:
        error("Production example must disable legacy PRM specification fallback.")
    if (not isinstance(runtime.get("SessionTimeoutMinutes"), int) or
            not 5 <= runtime.get("SessionTimeoutMinutes", 0) <= 60):
        error("Production example SessionTimeoutMinutes must be within the controlled 5..60 minute range.")

    sbom_path = ROOT / f"SBOM_{version}.cdx.json"
    release_notes_path = ROOT / f"RELEASE_NOTES_{version}.md"
    if not sbom_path.exists():
        error(f"Versioned SBOM is missing: {sbom_path.name}")
    else:
        sbom = json.loads(text(sbom_path))
        sbom_version = sbom.get("metadata", {}).get("component", {}).get("version")
        if sbom_version != version:
            error("SBOM application version does not match the project version.")
    if not release_notes_path.exists():
        error(f"Versioned release notes are missing: {release_notes_path.name}")

    migration_manifest = json.loads(text(ROOT / "Database/MigrationManifest.json"))
    if migration_manifest.get("applicationVersion") != version:
        error("Migration manifest application version does not match the project version.")
    if migration_manifest.get("baselineThrough") != "20260722_003":
        error("Migration manifest historical baseline boundary is missing or unexpected.")
    fresh_baseline = migration_manifest.get("freshInstallBaseline") or {}
    baseline_relative = fresh_baseline.get("file") or ""
    baseline_path = ROOT / "Database" / baseline_relative
    if fresh_baseline.get("versionKey") != "BASELINE_20260823_001":
        error("The controlled fresh-install baseline key is missing or unexpected.")
    if baseline_relative != "Baseline/20260823_001_PharmaLIMS_Core_Baseline.sql":
        error("The controlled fresh-install baseline file is missing or unexpected.")
    if not baseline_path.is_file():
        error("The controlled fresh-install baseline SQL file is missing.")
    else:
        baseline_hash = hashlib.sha256(baseline_path.read_bytes()).hexdigest()
        if baseline_hash.lower() != str(fresh_baseline.get("sha256") or "").lower():
            error("Fresh-install baseline hash mismatch.")
        baseline_source = text(baseline_path)
        for core_table in (
            "Users", "Tests", "WaterSamplingPoints", "SamplingPoints", "WaterTestProfiles",
            "WaterSpecifications", "Samples", "SampleTests", "LIMS_NumberSequences",
            "Certificates", "CertificatePrintHistory", "AuditTrail", "ElectronicSignatures", "EM_Areas",
            "EM_Events", "MediaNumberSequences", "CultureMediaLots", "MediaPreparations", "MediaQualifications",
        ):
            if f"CREATE TABLE dbo.{core_table}" not in baseline_source:
                error(f"Fresh-install baseline is missing core table dbo.{core_table}.")
        if re.search(r"(?im)^\s*GO\s*$", baseline_source):
            error("Fresh-install baseline contains unsupported GO batch separators.")
        if re.search(
            r"(?i)INSERT\s+(?:INTO\s+)?dbo\.(?:Users|Tests|WaterSamplingPoints|WaterSpecifications|EM_Areas|CultureMedia)\b",
            baseline_source,
        ):
            error("Fresh-install baseline must not auto-seed GMP master data or credentials.")
    migration_files = {item.get("file") for item in migration_manifest.get("migrations", [])}
    listed_migration_names = {
        Path(relative).name
        for relative in migration_files
        if isinstance(relative, str) and relative
    }
    packaged_migration_names = {
        path.name
        for path in (ROOT / "Database/Migrations").glob("*.sql")
        if path.is_file()
    }
    orphan_migrations = sorted(packaged_migration_names - listed_migration_names)
    if orphan_migrations:
        error(
            "Uncontrolled SQL migration file(s) are present but absent from "
            "MigrationManifest.json: " + ", ".join(orphan_migrations)
        )
    missing_packaged_migrations = sorted(listed_migration_names - packaged_migration_names)
    if missing_packaged_migrations:
        error(
            "MigrationManifest.json references SQL file(s) missing from the package: "
            + ", ".join(missing_packaged_migrations)
        )
    if "Migrations/20260722_003_Resolve_Project_Complexities.sql" not in migration_files:
        error("Migration manifest does not include the Batch 22 migration.")
    if "Migrations/20260722_004_PRM_Specification_Master.sql" not in migration_files:
        error("Migration manifest does not include the controlled PRM Specification Master migration.")
    if "Migrations/20260722_005_Link_PRM_Specifications_To_Items.sql" not in migration_files:
        error("Migration manifest does not include the item-linked PRM specification migration.")
    if "Migrations/20260723_002_Default_Oral_Tablet_Microbiology_Profiles.sql" not in migration_files:
        error("Migration manifest does not include the default oral-tablet profile repair.")
    if "Migrations/20260809_001_Protect_Compliance_Records.sql" not in migration_files:
        error("Migration manifest does not include append-only compliance-record protection.")
    if "Migrations/20260811_001_External_Trend_Area_Classification.sql" not in migration_files:
        error("Migration manifest does not include external-trend area classification.")
    if "Migrations/20260823_001_PRM_Specification_Remediation.sql" not in migration_files:
        error("Migration manifest does not include the controlled PRM specification remediation.")
    if "Migrations/20260823_002_Culture_Media_Requirement_Approval_Gate.sql" not in migration_files:
        error("Migration manifest does not include the Culture Media QA-activation gate.")
    if "Migrations/20260823_003_Quality_Event_Structured_Evidence_Schema.sql" not in migration_files:
        error("Migration manifest does not include the Quality Event structured-evidence schema.")
    if "Migrations/20260823_004_Water_Planning_Schema.sql" not in migration_files:
        error("Migration manifest does not include the controlled Water planning schema.")
    if "Migrations/20260823_005_External_Trend_Snapshot_Schema_Reconciliation.sql" not in migration_files:
        error("Migration manifest does not include the External Trend snapshot-schema reconciliation.")
    if "Migrations/20260826_001_PRM_Quality_Event_Current_Baseline.sql" not in migration_files:
        error("Migration manifest does not include the current PRM Quality Event baseline.")
    if "Migrations/20260826_002_PRM_Quality_Event_Operational_Readiness.sql" not in migration_files:
        error("Migration manifest does not include the PRM Quality Event operational-readiness hardening migration.")
    if "Migrations/20260826_003_EM_Investigation_Checklist_Master_Data.sql" not in migration_files:
        error("Migration manifest does not include the controlled EM investigation checklist master-data migration.")
    migration_entries = migration_manifest.get("migrations", [])
    migration_by_key = {item.get("versionKey"): item for item in migration_entries}
    current_prm_qe = migration_by_key.get("20260826_001") or {}
    if current_prm_qe.get("description") != "20260826_001_PRM_Quality_Event_Current_Baseline":
        error("Current PRM Quality Event baseline metadata is missing or unexpected.")
    prm_qe_runtime = migration_by_key.get("20260826_002") or {}
    if prm_qe_runtime.get("description") != "20260826_002_PRM_Quality_Event_Operational_Readiness":
        error("PRM Quality Event operational-readiness migration metadata is missing or unexpected.")
    for retired_key in ("20260825_002", "20260825_003", "20260825_004", "20260825_005", "20260825_006", "20260824_003"):
        retired = migration_by_key.get(retired_key) or {}
        if retired.get("supersededBy") != "20260826_001":
            error(f"Retired PRM Quality Event migration {retired_key} must remain superseded by checksum-controlled 20260826_001.")
    oral_profile_entries = [
        item for item in migration_manifest.get("migrations", [])
        if item.get("file") == "Migrations/20260723_002_Default_Oral_Tablet_Microbiology_Profiles.sql"
    ]
    if len(oral_profile_entries) != 1 or oral_profile_entries[0].get("versionKey") != "20260723_002R2":
        error("The revised full oral-tablet profile migration must use controlled key 20260723_002R2.")
    for item in migration_manifest.get("migrations", []):
        relative = item.get("file") or ""
        migration_path = ROOT / "Database" / relative
        if not migration_path.exists():
            error(f"Migration manifest references a missing file: {relative}")
            continue
        actual_hash = hashlib.sha256(migration_path.read_bytes()).hexdigest()
        if actual_hash.lower() != str(item.get("sha256") or "").lower():
            error(f"Migration manifest hash mismatch: {relative}")

        # EXEC(character_string) accepts string literals/variables joined by +, but not
        # an inline function expression such as + CONVERT(...). That form fails at
        # parse time on SQL Server with error 156. Protect current migrations from
        # reintroducing the v174 maintenance failure.
        if not item.get("supersededBy") and str(item.get("versionKey") or "") >= "20260825_007":
            migration_source = text(migration_path)
            if re.search(
                r"EXEC\s*\(\s*N'[^\n]*\+\s*(?:CONVERT|CAST|FORMAT|QUOTENAME|REPLACE|ISNULL|COALESCE)\s*\(",
                migration_source,
                flags=re.IGNORECASE,
            ):
                error(f"Current migration contains an invalid EXEC string expression: {relative}")

    legacy_identity_reconciliation = text(ROOT / "Database/Migrations/20260825_007_PRM_Quality_Event_Legacy_Identity_Reconciliation.sql")
    for required_identity_guard in (
        "DECLARE @AffectedResultIsIdentity BIT=0",
        "IF @AffectedResultIsIdentity=0",
        "DECLARE @ActionIdIsIdentity BIT=0",
        "IF @ActionIdIsIdentity=0",
        "DECLARE @AnswerIsIdentity BIT=0",
        "IF @AnswerIsIdentity=0",
        "DECLARE @PrintIsIdentity BIT=0",
        "IF @PrintIsIdentity=0",
        "Never UPDATE an existing IDENTITY column (SQL Server error 8102)",
    ):
        if required_identity_guard not in legacy_identity_reconciliation:
            error("20260825_007 is missing SQL 8102 identity-backfill protection: " + required_identity_guard)
    if "SET IDENTITY_INSERT" in legacy_identity_reconciliation.upper():
        error("20260825_007 must not use IDENTITY_INSERT to rewrite legacy surrogate keys.")
    for forbidden_static_binding in (
        "IF EXISTS (SELECT QualityEventPrintID FROM dbo.QualityEventPrintHistory",
        "SELECT @NextPrintBig=ISNULL(MAX(CONVERT(BIGINT,QualityEventPrintID))",
        "SELECT @NextPrintInt=ISNULL(MAX(CONVERT(BIGINT,QualityEventPrintID))",
    ):
        if forbidden_static_binding in legacy_identity_reconciliation:
            error("20260825_007 can bind QualityEventPrintID before ALTER TABLE completes: " + forbidden_static_binding)
    for required_dynamic_binding in ("@DuplicatePrintIdCount", "@NextValue=@NextPrintBig OUTPUT", "@NextValue=@NextPrintInt OUTPUT"):
        if required_dynamic_binding not in legacy_identity_reconciliation:
            error("20260825_007 is missing deferred QualityEventPrintID binding: " + required_dynamic_binding)

    for required_legacy_type_reconciliation in (
        "ALTER COLUMN ResultValue NVARCHAR(200) NULL",
        "ALTER COLUMN SpecificationLimit NVARCHAR(500) NULL",
        "ALTER COLUMN FailureType NVARCHAR(120) NULL",
    ):
        if required_legacy_type_reconciliation not in legacy_identity_reconciliation:
            error("20260825_007 is missing actual legacy affected-result type reconciliation: " + required_legacy_type_reconciliation)

    quality_event_model_source = text(ROOT / "Models/QualityEvent.cs")
    quality_event_repository_source = text(ROOT / "Repositories/QualityEventRepository.cs")
    for required_repository_contract in (
        "public int? SampleTestId",
        "public string? SourceModule",
        "public int? SourceResultId",
        "public int? TestId",
        "public string? ResultValue",
        "SourceModule, SourceResultID",
        'ResultValue = row.GetSafeString("ResultValue")',
        'new SqlParameter("@ResultValue", SqlDbType.NVarChar, 200)',
    ):
        if required_repository_contract not in quality_event_model_source + "\n" + quality_event_repository_source:
            error("Quality Event repository/model is not aligned to PRM qualitative source evidence: " + required_repository_contract)

    prm_readiness_source_for_types = text(ROOT / "Infrastructure/PrmSchemaReadinessService.cs")
    preflight_source_for_types = text(ROOT / "Infrastructure/SystemPreflightService.cs")
    for required_type_gate in (
        "name=N'ResultValue'",
        "system_type_id=TYPE_ID(N'nvarchar')",
        "name=N'SourceResultID'",
        "QualityEventAffectedResults.ResultValue NVARCHAR(200+)",
        "QualityEventAffectedResults.SourceResultID nullable INT",
    ):
        if required_type_gate not in prm_readiness_source_for_types + "\n" + preflight_source_for_types:
            error("PRM Quality Event affected-result schema type gate is missing: " + required_type_gate)

    integration_source = text(ROOT / "tests/PharmaLIMS.DatabaseIntegration/Program.cs")
    for required_rehearsal_control in (
        "VerifyLegacyQualityEventUpgradeAsync",
        "LegacyQualityEventSchemaSql",
        "PrintHistoryID int IDENTITY(1,1)",
        "ResultValue decimal(18,4)",
        'new[] { "20260825_007", "20260826_001", "20260826_002" }',
        "Multiple IDENTITY columns detected.",
        "Qualitative PRM affected-result evidence was not preserved.",
    ):
        if required_rehearsal_control not in integration_source:
            error("Legacy PRM Quality Event SQL integration rehearsal is incomplete: " + required_rehearsal_control)

    external_import_service = text(ROOT / "Services/ExternalTrendImportService.cs")
    external_import_dialog = text(ROOT / "ExternalTrendImportDialog.xaml.cs")
    for required_external_read_control in (
        "GetImportHistoryAsync",
        "GetBatchMethodsAsync",
        "GetBatchParametersAsync",
        "ExecuteReadWithRetryAsync",
        "ReadLockTimeoutMilliseconds",
        "IsTransientReadContention",
        "CancellationToken",
    ):
        if required_external_read_control not in external_import_service:
            error("External Trend controlled async read path is missing: " + required_external_read_control)
    for forbidden_external_read_pattern in (
        "Task.Run(importService.GetImportHistory)",
        "Task.Run(() => importService.GetBatchMethods",
        "Task.Run(() => importService.GetBatchParameters",
    ):
        if forbidden_external_read_pattern in external_import_dialog:
            error("External Trend UI still wraps database reads in Task.Run: " + forbidden_external_read_pattern)
    for required_external_ui_control in (
        "historySelectionCancellation",
        "parameterSelectionCancellation",
        "IsTransientDatabaseBusy",
        "Import history was not refreshed",
    ):
        if required_external_ui_control not in external_import_dialog:
            error("External Trend non-blocking lock-contention handling is missing: " + required_external_ui_control)

    current_prm_baseline_source = text(ROOT / "Database/Migrations/20260826_001_PRM_Quality_Event_Current_Baseline.sql")
    for required_legacy_reconciliation in (
        "@DuplicateAffectedSourceLinks",
        "EXEC(N'CREATE UNIQUE INDEX UX_QualityEventAffectedResults_Source_20260826",
        "EXEC(N'CREATE INDEX IX_QualityEventActions_Event_20260826",
        "@DuplicateChecklistAnswerLinks",
        "EXEC(N'CREATE UNIQUE INDEX UQ_QualityEventChecklistAnswers_20260826",
        "EXEC(N'CREATE INDEX IX_QualityEventPrintHistory_Event_20260826",
        "EXEC(N'CREATE INDEX IX_QualityEvents_SourceStatus_20260826",
    ):
        if required_legacy_reconciliation not in current_prm_baseline_source:
            error("20260826_001 is missing legacy schema reconciliation control: " + required_legacy_reconciliation)

    migrator_source = source_family(ROOT / "Infrastructure/StartupDatabaseMigrator.cs")
    if "ApplyManifestMigrationsAsync" not in migrator_source or "MigrationChecksum" not in migrator_source:
        error("Startup database migration execution is not linked to the controlled manifest and checksums.")
    for migration_control in (
        "ProvisionFreshDatabaseBaselineAsync",
        "AcquireMigrationSessionApplicationLockAsync",
        "sys.sp_getapplock",
        "Recorded migration has no controlled checksum and cannot be auto-stamped",
        "Historical migration is absent from the checksum-verified ledger",
    ):
        if migration_control not in migrator_source:
            error(f"Startup migration fail-closed control is missing: {migration_control}")
    development = json.loads(text(ROOT / "appsettings.Development.example.json"))
    if development.get("Runtime", {}).get("ApplyStartupDatabaseUpdates") is not False:
        error("Development configuration must not apply database schema updates automatically at application startup.")
    app_source = text(ROOT / "App.xaml.cs")
    if "ApplyRequiredUpdatesAsync" in app_source or "Preparing database. Controlled updates are being checked" in app_source:
        error("Normal application startup must not execute database schema migrations or hold the Login window behind DDL.")
    prm_readiness_source = text(ROOT / "Infrastructure/PrmSchemaReadinessService.cs")
    for forbidden_runtime_ddl in ("ApplyControlledMigrationAsync", "ApplyRequiredUpdatesAsync",
                                  "EnsureDevelopmentCurrentSchemaAsync", "StartupDatabaseMigrator"):
        if forbidden_runtime_ddl in prm_readiness_source:
            error(f"PRM workflow readiness must be strictly read-only; forbidden migration dependency: {forbidden_runtime_ddl}")
    if "commandTimeoutSeconds: ReadinessCommandTimeoutSeconds" not in prm_readiness_source:
        error("PRM schema-readiness probes must use the dedicated bounded read-only timeout.")
    if "Task.Run(() =>" in prm_readiness_source:
        error("PRM schema-readiness probes must use true async database I/O, not Task.Run around synchronous SQL.")
    if "catch (SqlException ex) when (ex.Number == -2 || ex.Number == 1222)" not in prm_readiness_source:
        error("PRM schema-readiness probes must absorb SQL timeout/lock timeout as a controlled busy state.")

    main_source = text(ROOT / "MainWindow.xaml.cs")
    main_xaml = text(ROOT / "MainWindow.xaml")
    preflight_source = text(ROOT / "Infrastructure/SystemPreflightService.cs")
    for control in ("BtnSystemPreflight", "BtnDatabaseMaintenance"):
        if control not in main_xaml:
            error(f"MainWindow controlled system lifecycle action is missing: {control}")
    for control in ("RefreshSystemReadinessAsync", "EnsureRuntimeReadyForWorkflow", "await migrator.ApplyRequiredUpdatesAsUserAsync(Login.CurrentUser);", "Development Database Maintenance"):
        if control not in main_source:
            error(f"MainWindow preflight/maintenance control is missing: {control}")
    for lifecycle_control in ("_preflightRunning", "_dashboardRefreshRunning", "maintenanceVerification: true"):
        if lifecycle_control not in main_source:
            error(f"MainWindow database lifecycle serialization control is missing: {lifecycle_control}")
    for lifecycle_control in (
        "APPLOCK_TEST(N'public', N'PharmaLIMS.SchemaMigration', N'Shared', N'Session')",
        "Database lifecycle coordination", "IsDatabaseMaintenanceActiveAsync", "_runGate"
    ):
        if lifecycle_control not in preflight_source:
            error(f"System Preflight read-only database-maintenance coordination control is missing: {lifecycle_control}")
    for forbidden_preflight_lease in (
        "CreateUnpooledConnection", "sys.sp_getapplock", "sys.sp_releaseapplock",
        "AsyncLocal<SqlConnection", "ReleasePreflightLeaseAsync"
    ):
        if forbidden_preflight_lease in preflight_source:
            error(f"System Preflight must not own a session-level application lock: {forbidden_preflight_lease}")
    for lifecycle_control in (
        "AcquireMigrationSessionApplicationLockAsync", "@LockMode=N'Exclusive'",
        "@LockOwner=N'Session'", "ApplyManifestMigrationEntryAsync", "ProgressChanged",
        "existingMaintenanceLease: maintenanceLease", "ReleaseMigrationSessionApplicationLockAsync",
        "sys.sp_releaseapplock", "SqlConnection.ClearPool"
    ):
        if lifecycle_control not in migrator_source:
            error(f"Database Maintenance serialization/restartability control is missing: {lifecycle_control}")
    if "AcquireMigrationApplicationLockAsync" in migrator_source:
        error("Database Maintenance must not reacquire the schema lifecycle application lock on a second SQL session during baseline inspection.")
    if "EnsureEnvironmentalMonitoringChecklistQuestions" in main_source:
        error("MainWindow Database Maintenance must not execute ad-hoc EM checklist DML outside the controlled migration ledger.")

    main_project = text(ROOT / "PharmaLIMS.csproj")
    for compile_exclusion in ('<Compile Remove="tools\\**\\*.cs" />', '<Compile Remove="tests\\**\\*.cs" />'):
        if compile_exclusion not in main_project:
            error(f"Main WPF project must isolate auxiliary C# sources from the SDK Compile glob: {compile_exclusion}")
    if 'Exclude="App.xaml;bin\\**;obj\\**;tools\\**;tests\\**"' not in main_project:
        error("Main WPF XAML Page glob must exclude tools/tests auxiliary project trees.")

    maintenance_runner = text(ROOT / "tools/PharmaLIMS.DatabaseMaintenance/Program.cs")
    for control in ("--confirm-development-maintenance", "AppConfig.IsDevelopment", "ApplyRequiredUpdatesAsync",
                    "ProgressChanged", "SystemPreflightService", "report.CanProceed"):
        if control not in maintenance_runner:
            error(f"Standalone Development database maintenance runner control is missing: {control}")

    operational_sources = (
        "EMResultsEntry.xaml.cs", "ExternalTrendThreeCycleReviewWindow.xaml.cs",
        "ProductionRawMaterialResults.xaml.cs", "ProductionRawMaterialSamples.xaml.cs",
        "QualityEventInvestigation.xaml.cs", "ResultsEntry.xaml.cs", "NewSampleDialog.xaml.cs",
    )
    for relative_source in operational_sources:
        source = text(ROOT / relative_source)
        for forbidden_runtime_ddl in ("ApplyControlledMigrationAsync", "ApplyRequiredUpdatesAsync",
                                      "EnsureDevelopmentCurrentSchemaAsync", "StartupDatabaseMigrator"):
            if forbidden_runtime_ddl in source:
                error(f"Operational workflow must not invoke database migrations ({forbidden_runtime_ddl}): {relative_source}")
    if "prmQualityEventLinkColumnsSql" in migrator_source or "prmQualityEventReconciliationColumnsSql" in migrator_source:
        error("Retired PRM Quality Event compatibility shims remain in the startup migrator.")
    current_prm_qe_path = ROOT / "Database/Migrations/20260826_001_PRM_Quality_Event_Current_Baseline.sql"
    if current_prm_qe_path.exists():
        current_prm_qe_sql = text(current_prm_qe_path)
        if re.search(r"(?is)ALTER\s+TABLE[^;]+ALTER\s+COLUMN", current_prm_qe_sql):
            error("Current PRM Quality Event baseline must not rewrite existing column definitions.")
        if re.search(r"(?i)\bDELETE\s+FROM\b|\bTRUNCATE\s+TABLE\b|\bDROP\s+(TABLE|COLUMN|CONSTRAINT|INDEX)\b", current_prm_qe_sql):
            error("Current PRM Quality Event baseline contains destructive schema/data operations.")
    prm_qe_operational_path = ROOT / "Database/Migrations/20260826_002_PRM_Quality_Event_Operational_Readiness.sql"
    if prm_qe_operational_path.exists():
        prm_qe_operational_sql = text(prm_qe_operational_path)
        if re.search(r"(?i)\bDELETE\s+FROM\b|\bTRUNCATE\s+TABLE\b|\bDROP\s+(TABLE|COLUMN|CONSTRAINT|INDEX)\b", prm_qe_operational_sql):
            error("PRM Quality Event operational-readiness migration contains destructive schema/data operations.")
        for protected_table in ("QualityEvents", "QualityEventAffectedResults", "QualityEventActions",
                                "QualityEventChecklistAnswers", "QualityEventPrintHistory"):
            if re.search(rf"(?is)\bUPDATE\s+(?:dbo\.)?{protected_table}\b", prm_qe_operational_sql):
                error(f"Operational-readiness migration must not rewrite existing compliance evidence rows: {protected_table}")
        for required_control in ("OUTPUT inserted.QualityEventID", "NEXT VALUE FOR",
                                 "Database Maintenance", "approved controlled deployment process"):
            combined = prm_qe_operational_sql + "\n" + source_family(ROOT / "ProductionRawMaterialResults.xaml.cs")
            if required_control not in combined:
                error(f"PRM Quality Event operational hardening control is missing: {required_control}")
    oral_profile_migration = text(
        ROOT / "Database/Migrations/20260723_002_Default_Oral_Tablet_Microbiology_Profiles.sql"
    )
    for organism_code in ("SALMONELLA", "ECOLI", "SAUREUS", "PAERUGINOSA", "CALBICANS"):
        if organism_code not in oral_profile_migration:
            error(f"The stricter oral-tablet specified-organism panel is missing {organism_code}.")
    for timing_column in ("AnalysisStartedDate", "AnalysisCompletedDate"):
        if timing_column not in oral_profile_migration:
            error(f"The controlled PRM analysis timing column is missing: {timing_column}.")

    prm_remediation = text(ROOT / "Database/Migrations/20260823_001_PRM_Specification_Remediation.sql")
    for required_profile_control in (
        "N'NMT 100 CFU/g'",
        "N'NMT 10 CFU/g'",
        "N'Draft',NULL,NULL,NULL,NULL,NULL,0,N'Controlled Remediation 20260823',0",
        "ApprovalStatus=N'Reviewed'",
    ):
        if required_profile_control not in prm_remediation:
            error(f"PRM oral-tablet remediation control is missing: {required_profile_control}")
    draft_profile_section = prm_remediation.split("DECLARE @Draft TABLE", 1)[-1]
    if "N'Raw Material'" in draft_profile_section or "N'Production / In-Process'" in draft_profile_section:
        error("Oral-tablet product limits must not be auto-assigned to Raw Material or In-Process categories.")

    media_gate = text(ROOT / "Database/Migrations/20260823_002_Culture_Media_Requirement_Approval_Gate.sql")
    for media_control in ("ApprovalStatus=N'Draft'", "IsActive=0", "ApprovedBy=NULL", "ApprovalStatus<>N'Approved'"):
        if media_control not in media_gate:
            error(f"Culture Media qualification approval gate is missing: {media_control}")

    quality_event_schema = text(ROOT / "Database/Migrations/20260823_003_Quality_Event_Structured_Evidence_Schema.sql")
    for required_table in (
        "QualityEventActions", "QualityEventChecklistQuestions", "QualityEventChecklistAnswers",
        "QualityEventRootCauseWhys", "QualityEventImpactAssessments", "QualityEventCAPAItems",
        "QualityEventRetesting", "QualityEventDistribution", "QualityEventRelatedItems",
        "QualityEventSignatures", "QualityEventPrintHistory",
    ):
        if f"CREATE TABLE dbo.{required_table}" not in quality_event_schema:
            error(f"Quality Event structured-evidence schema is missing dbo.{required_table}.")
    for required_trigger in (
        "TRG_QualityEventSignatures_AppendOnly",
        "TRG_QualityEventPrintHistory_AppendOnly",
    ):
        if required_trigger not in quality_event_schema:
            error(f"Quality Event evidence protection is missing {required_trigger}.")

    water_planning_schema = text(ROOT / "Database/Migrations/20260823_004_Water_Planning_Schema.sql")
    for required_table in (
        "Water_Plans", "Water_PlanSamples", "Water_PlanSampleTests",
        "Water_PlanSampleAttempts", "Water_PlanSignatures",
    ):
        if f"CREATE TABLE dbo.{required_table}" not in water_planning_schema:
            error(f"Controlled Water planning schema is missing dbo.{required_table}.")
    for water_control in (
        "PointCodeSnapshot", "TestNameSnapshot", "TRG_Water_PlanSignatures_AppendOnly",
    ):
        if water_control not in water_planning_schema:
            error(f"Controlled Water planning schema is missing: {water_control}")

    external_snapshot_reconciliation = text(
        ROOT / "Database/Migrations/20260823_005_External_Trend_Snapshot_Schema_Reconciliation.sql"
    )
    for snapshot_control in (
        "MethodName", "UnitName", "PeriodCount", "PeriodDefinitionJson",
        "SourceAnchorBatchID", "SnapshotHashSha256",
        "FK_EMTrendReviewSnapshots_SourceAnchorBatch",
        "CK_EMTrendReviewSnapshots_ControlledV2",
        "TR_EMTrendReviewSnapshots_Immutable",
    ):
        if snapshot_control not in external_snapshot_reconciliation:
            error(f"External Trend snapshot reconciliation is missing: {snapshot_control}")
    if re.search(r"(?i)\bUPDATE\s+dbo\.EMTrendReviewSnapshots\b", external_snapshot_reconciliation):
        error("External Trend snapshot reconciliation must not rewrite historical snapshot rows.")

    external_trend_service = text(ROOT / "Services/ExternalTrendThreeCycleService.cs")
    external_trend_window = text(ROOT / "ExternalTrendThreeCycleReviewWindow.xaml.cs")
    if "IsApprovalSnapshotSchemaReady" not in external_trend_service:
        error("External Trend approval schema readiness check is missing.")
    for approval_control in (
        "EnsureApprovalSnapshotSchemaAsync", "System Preflight", "Database Maintenance",
    ):
        if approval_control not in external_trend_window:
            error(f"External Trend approval schema gate is missing: {approval_control}")
    if "ApplyControlledMigrationAsync" in external_trend_window or "StartupDatabaseMigrator" in external_trend_window:
        error("External Trend operational approval must be read-only with respect to database schema.")

    em_results_source = source_family(ROOT / "EMResultsEntry.xaml.cs")
    em_evidence_source = text(ROOT / "Services/EmLimitEvidenceSql.cs")
    if "EmLimitEvidenceSql.Joins(forUpdate)" not in em_results_source:
        error("EM result save/load must use the shared effective historical evidence query.")
    em_load_plates = em_results_source.split("private void LoadPlates(int eventId)", 1)[1].split(
        "private bool HasEnteredResult", 1
    )[0]
    for frozen_limit_control in (
        'bool hasAlertSnapshot = row["AlertLimitSnapshot"] != DBNull.Value;',
        'bool hasActionSnapshot = row["ActionLimitSnapshot"] != DBNull.Value;',
        'bool hasAirVolumeSnapshot = !activeAir || row["AirVolumeLitersSnapshot"] != DBNull.Value;',
        'string frozenUnit = row.GetSafeString("ResultUnitSnapshot");',
        "PharmaLIMS will not substitute current master limits for historical evidence",
        "HasLimitSnapshot = hasAlertSnapshot && hasActionSnapshot && !string.IsNullOrWhiteSpace(frozenUnit) && hasAirVolumeSnapshot",
        "EM_LimitSnapshotReconciliations",
        "Signed Historical Reconciliation #",
        "Approved EM limits are incomplete for",
        "recorded approved alert and action limits used for this report",
    ):
        if frozen_limit_control not in em_results_source + em_evidence_source:
            error(f"Approved historical EM frozen-limit control is missing: {frozen_limit_control}")
    for prohibited_live_fallback in (
        "needsConfiguredLimits",
        "EMPlateLimitInfo configuredLimits",
        "GetEMPlateLimits(grade, method)",
        "configuredLimits.AlertLimit",
        "configuredLimits.ActionLimit",
        "configuredLimits.AirVolumeLiters",
    ):
        if prohibited_live_fallback in em_load_plates:
            error(f"Historical EM result loading still falls back to mutable master limits: {prohibited_live_fallback}")
    for removed_reconciliation_control in (
        "TryReconcileHistoricalLimitSnapshotsForDevelopment",
        "HistoricalLimitSnapshotCandidate",
        "EM Historical Limit Snapshot Reconciliation",
    ):
        if removed_reconciliation_control in em_results_source:
            error(f"Obsolete historical EM reconciliation flow is still present: {removed_reconciliation_control}")
    if "effective at the time of sampling" in em_results_source:
        error("Approved historical EM report still claims that fallback limits were effective at sampling time.")

    em_reconciliation_migration = text(ROOT / "Database/Migrations/20260828_003_EM_Limit_Snapshot_Reconciliation_And_Strict_Creation.sql")
    for reconciliation_control in (
        "CREATE TABLE dbo.EM_LimitSnapshotReconciliations",
        "TRG_EM_LimitSnapshotReconciliations_AppendOnly_20260828",
        "SupersedesReconciliationID",
        "THROW 53547",
        "Plate creation was rolled back",
        "AlertLimitSnapshot = approvedLimit.AlertLimitTotal",
        "ActionLimitSnapshot = approvedLimit.ActionLimitTotal",
    ):
        if reconciliation_control not in em_reconciliation_migration:
            error(f"v194 EM historical reconciliation control is missing: {reconciliation_control}")
    em_reconciliation_dialog = text(ROOT / "EMLegacySnapshotReconciliation.xaml.cs")
    for reconciliation_ui_control in (
        "EnsureUserPermissionInTransaction",
        '"CanApproveResults"',
        "Reconcile Historical EM Limit Snapshot",
        "ElectronicSignature",
    ):
        if reconciliation_ui_control not in em_reconciliation_dialog:
            error(f"v194 EM reconciliation QA/e-signature control is missing: {reconciliation_ui_control}")
    if "DefaultUnit(_selected.Method)" in em_reconciliation_dialog:
        error("Historical EM reconciliation must not auto-copy the current/default unit into evidence.")

    database_helper_source = "\n".join(text(path) for path in sorted(ROOT.glob("DatabaseHelper*.cs")))
    em_print_gate = database_helper_source.split("public static bool CanPrintEMResultReport", 1)[1].split(
        "public static DataTable GetEMEventSignatures", 1
    )[0]
    for obsolete_snapshot_gate in ("AlertLimitSnapshot", "ActionLimitSnapshot", "HasLimitSnapshot"):
        if obsolete_snapshot_gate in em_print_gate:
            error(f"Database EM print gate still requires a frozen snapshot: {obsolete_snapshot_gate}")

    compliance_protection = text(
        ROOT / "Database/Migrations/20260809_001_Protect_Compliance_Records.sql"
    )
    for protected_table in ("AuditTrail", "ElectronicSignatures", "CertificateLifecycleAudit"):
        if protected_table not in compliance_protection:
            error(f"Append-only database protection is missing {protected_table}.")

for obsolete_registration_file in ("SampleRegistration.xaml", "SampleRegistration.xaml.cs"):
    if (ROOT / obsolete_registration_file).exists():
        error(f"Obsolete uncontrolled workflow file must not be packaged: {obsolete_registration_file}")

if PRODUCTION_PUBLISH:
    # Production publish must use a site-specific controlled file. The example
    # is documentation only and is never copied to appsettings.json.
    production_config_path = ROOT / "appsettings.Production.json"
    if not production_config_path.exists():
        error(
            "Production publish blocked: appsettings.Production.json is missing. "
            "Create a site-specific controlled file from appsettings.Production.example.json."
        )
    else:
        effective = json.loads(text(production_config_path))
        effective_runtime = effective.get("Runtime", {})
        effective_database = effective.get("Database", {})
        effective_application = effective.get("Application", {})

        def is_placeholder(value: object) -> bool:
            normalized = str(value or "").strip().lower()
            if not normalized:
                return True
            return any(token in normalized for token in ("example", "placeholder", "changeme", "validated site name"))

        if effective.get("Environment") != "Production":
            error("Production publish blocked: appsettings.json Environment must be Production.")
        if is_placeholder(effective_application.get("SiteName")):
            error("Production publish blocked: Application:SiteName must be a real validated site name, not a placeholder.")
        if is_placeholder(effective_database.get("Server")):
            error("Production publish blocked: Database:Server must be a real controlled SQL Server target, not a placeholder.")
        if is_placeholder(effective_database.get("Database")):
            error("Production publish blocked: Database:Database must be a real controlled database name.")
        if effective_runtime.get("ApplyStartupDatabaseUpdates") is not False:
            error("Production publish blocked: startup database updates must be disabled.")
        if effective_runtime.get("DevelopmentAdminFullPermissions") is not False:
            error("Production publish blocked: development Admin permissions must be disabled.")
        if effective_runtime.get("AllowEarlyMicrobiologyResults") is not False:
            error("Production publish blocked: early microbiology results must be disabled.")
        if effective_runtime.get("AllowLegacyPrmSpecificationFallback") is not False:
            error("Production publish blocked: legacy PRM fallback must be disabled.")
        if not isinstance(effective_runtime.get("SessionTimeoutMinutes"), int) or effective_runtime.get("SessionTimeoutMinutes", 0) <= 0:
            error("Production publish blocked: a positive session inactivity timeout is required.")
        if effective_database.get("Encrypt") is not True:
            error("Production publish blocked: database encryption must be enabled.")
        if effective_database.get("TrustServerCertificate") is not False:
            error("Production publish blocked: TrustServerCertificate must be false.")

if DELIVERY_PACKAGE:
    prohibited_delivery_files: list[str] = []
    for path in ROOT.rglob("*"):
        if not path.is_file():
            continue
        relative = path.relative_to(ROOT)
        if (
            any(part in {".vs", "bin", "obj", "artifacts", "__pycache__"} for part in relative.parts)
            or path.name.endswith((".user", ".pyc"))
        ):
            prohibited_delivery_files.append(relative.as_posix())
    if prohibited_delivery_files:
        error(
            "Delivery package contains prohibited local/build artifact(s): "
            + ", ".join(sorted(prohibited_delivery_files))
        )

required = [
    ROOT / "AISystemReview.xaml",
    ROOT / "AISystemReview.xaml.cs",
    ROOT / "Database/Migrations/20260722_003_Resolve_Project_Complexities.sql",
    ROOT / "Database/Migrations/20260722_004_PRM_Specification_Master.sql",
    ROOT / "Database/Migrations/20260722_005_Link_PRM_Specifications_To_Items.sql",
    ROOT / "Database/Migrations/20260723_002_Default_Oral_Tablet_Microbiology_Profiles.sql",
    ROOT / "Database/Migrations/20260809_001_Protect_Compliance_Records.sql",
    ROOT / "Database/Migrations/20260811_001_External_Trend_Area_Classification.sql",
    ROOT / "Database/Baseline/20260823_001_PharmaLIMS_Core_Baseline.sql",
    ROOT / "Database/Migrations/20260823_001_PRM_Specification_Remediation.sql",
    ROOT / "Database/Migrations/20260823_002_Culture_Media_Requirement_Approval_Gate.sql",
    ROOT / "Database/Migrations/20260823_003_Quality_Event_Structured_Evidence_Schema.sql",
    ROOT / "Database/Migrations/20260823_004_Water_Planning_Schema.sql",
    ROOT / "Database/Migrations/20260823_005_External_Trend_Snapshot_Schema_Reconciliation.sql",
    ROOT / "Database/Verification/20260823_Phase1_Stabilization_Verification.sql",
    ROOT / "Database/Verification/20260722_Project_Complexity_Verification.sql",
    ROOT / "Database/Verification/20260822_Runtime_System_Preflight.sql",
    ROOT / "EMTrendReport.xaml",
    ROOT / "EMTrendReport.xaml.cs",
    ROOT / "Infrastructure/SystemPreflightService.cs",
    ROOT / "SystemPreflight.xaml",
    ROOT / "SystemPreflight.xaml.cs",
    ROOT / "tools/PharmaLIMS.DatabaseMaintenance/PharmaLIMS.DatabaseMaintenance.csproj",
    ROOT / "tools/PharmaLIMS.DatabaseMaintenance/Program.cs",
    ROOT / "scripts/Invoke-DevelopmentDatabaseMaintenance.ps1",
    ROOT / "tests/PharmaLIMS.DatabaseIntegration/PharmaLIMS.DatabaseIntegration.csproj",
    ROOT / "tests/PharmaLIMS.DatabaseIntegration/Program.cs",
    ROOT / "tests/PharmaLIMS.RuntimeSmoke/PharmaLIMS.RuntimeSmoke.csproj",
    ROOT / "tests/PharmaLIMS.RuntimeSmoke/Program.cs",
    ROOT / "scripts/Invoke-ReleaseValidation.ps1",
]
# Cross-module structural closure gates.
water_registration_source = text(ROOT / "NewSampleDialog.xaml.cs")
water_results_source = text(ROOT / "ResultsEntry.xaml.cs")
database_helper_source = "\n".join(text(path) for path in sorted(ROOT.glob("DatabaseHelper*.cs")))
em_results_source_for_atomicity = source_family(ROOT / "EMResultsEntry.xaml.cs")
preflight_source = text(ROOT / "Infrastructure/SystemPreflightService.cs")
prm_results_source = source_family(ROOT / "ProductionRawMaterialResults.xaml.cs")
ci_source = text(ROOT / ".github/workflows/ci.yml")
release_runner_source = text(ROOT / "scripts/Invoke-ReleaseValidation.ps1")

if '"Water Sample Registered"' not in water_registration_source or "tran.Commit();" not in water_registration_source:
    error("Water registration audit/commit controls are missing.")
elif water_registration_source.index('"Water Sample Registered"') > water_registration_source.index("tran.Commit();", water_registration_source.index('"Water Sample Registered"')):
    error("Water registration audit must be recorded before the registration transaction commits.")
if "Sample registration is blocked to prevent assigning guessed or hard-coded tests" not in database_helper_source:
    error("Water registration must fail closed when controlled Water Test Profiles are unavailable.")
if ("GetPersistedResultValue" not in water_results_source or
        "CreateOrUpdateQualityEventForOOS(" not in water_results_source or
        "con," not in water_results_source or "tran," not in water_results_source):
    error("Water result persistence/OOS Quality Event atomicity controls are missing.")
if "DatabaseHelper.ExecuteInTransaction" not in em_results_source_for_atomicity or "No EM ALERT/ACTION/OOS result was linked" not in em_results_source_for_atomicity:
    error("EM Quality Event creation must be atomic and require linked affected results.")
if "CheckCriticalSchemaColumnsAsync" not in preflight_source or "CheckControlledMasterDataAsync" not in preflight_source:
    error("System Preflight critical schema/master-data gates are incomplete.")
if "PRM_CertificateSnapshots" not in prm_results_source or "STRING_AGG" not in prm_results_source or "@Missing" not in prm_results_source:
    error("PRM certificate schema readiness must be comprehensive and fail closed.")
if "Invoke-ReleaseValidation.ps1 -RunDatabaseIntegration -RunRuntimeSmoke" not in ci_source:
    error("CI must invoke the authoritative release runner with SQL integration and WPF RuntimeSmoke.")
for runner_control in (
    "MSSQLLocalDB",
    "PharmaLIMS.DatabaseIntegration",
    "PharmaLIMS.DatabaseMaintenance",
    "PharmaLIMS.RuntimeSmoke",
    "Disposable SQL Server integration executable",
    "WPF RuntimeSmoke execution",
):
    if runner_control not in release_runner_source:
        error(f"Authoritative release runner control is missing: {runner_control}")

for path in required:
    if not path.exists():
        error(f"Required release artifact is missing: {path.relative_to(ROOT)}")

if ERRORS:
    print("\n".join("ERROR: " + item for item in ERRORS))
    sys.exit(1)

print("PASS: XAML/JSON/project XML, event handlers, source delimiters, release controls, and required artifacts.")

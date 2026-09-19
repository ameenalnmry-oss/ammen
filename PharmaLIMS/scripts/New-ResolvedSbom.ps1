param(
    [string]$Project = "./PharmaLIMS.csproj",
    [string]$PublishDirectory = "./artifacts/publish",
    [string]$OutputPath = "./artifacts/publish/SBOM.cdx.json",
    [string]$Version
)
$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$projectXml = Get-Content -LiteralPath $Project -Raw
    $Version = [string]$projectXml.Project.PropertyGroup.Version
}
$raw = dotnet list $Project package --include-transitive --format json | Out-String
if ($LASTEXITCODE -ne 0) { throw "dotnet list package failed while generating resolved SBOM." }
$report = $raw | ConvertFrom-Json
$packages = @{}
foreach ($projectEntry in $report.projects) {
    foreach ($framework in $projectEntry.frameworks) {
        foreach ($pkg in @($framework.topLevelPackages) + @($framework.transitivePackages)) {
            if ($null -eq $pkg -or [string]::IsNullOrWhiteSpace($pkg.id) -or [string]::IsNullOrWhiteSpace($pkg.resolvedVersion)) { continue }
            $key = "$($pkg.id.ToLowerInvariant())@$($pkg.resolvedVersion)"
            $packages[$key] = [ordered]@{
                type = "library"
                name = $pkg.id
                version = $pkg.resolvedVersion
                'bom-ref' = "pkg:nuget/$($pkg.id)@$($pkg.resolvedVersion)"
                purl = "pkg:nuget/$($pkg.id)@$($pkg.resolvedVersion)"
            }
        }
    }
}
if ($packages.Count -eq 0) { throw "Resolved dependency graph was empty; refusing to emit SBOM." }
$publishManifest = Join-Path $PublishDirectory "PUBLISH_MANIFEST_SHA256.txt"
if (-not (Test-Path -LiteralPath $PublishDirectory)) { throw "Publish directory not found: $PublishDirectory" }
$component = [ordered]@{ type="application"; name="PharmaLIMS"; version=$Version; 'bom-ref'="pkg:generic/PharmaLIMS@$Version" }
$document = [ordered]@{
    bomFormat="CycloneDX"; specVersion="1.5"; serialNumber="urn:uuid:$([guid]::NewGuid())"; version=1;
    metadata=[ordered]@{ timestamp=(Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"); component=$component; properties=@(
        [ordered]@{name="pharmalims:dependencySource";value="Generated in CI from dotnet restore/list package resolved graph for the current build."},
        [ordered]@{name="pharmalims:resolvedNuGetComponentCount";value=[string]$packages.Count},
        [ordered]@{name="pharmalims:artifact";value="Signed and smoke-tested win-x64 publish directory"}
    )};
    components=@($packages.Keys | Sort-Object | ForEach-Object { $packages[$_] })
}
$parent = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Force -Path $parent | Out-Null
$document | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $OutputPath -Encoding utf8NoBOM
# Self-check: every resolved package must be present exactly once.
$parsed = Get-Content -LiteralPath $OutputPath -Raw | ConvertFrom-Json
if (@($parsed.components).Count -ne $packages.Count) { throw "Generated SBOM component count does not match resolved dependency graph." }
Write-Host "Resolved CycloneDX SBOM generated: $OutputPath ($($packages.Count) NuGet components)."

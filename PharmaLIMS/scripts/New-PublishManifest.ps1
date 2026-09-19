[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$publish = (Resolve-Path $PublishDirectory).Path
$manifest = Join-Path $publish 'PUBLISH_MANIFEST_SHA256.txt'
$provenance = Join-Path $publish 'PUBLISH_PROVENANCE.json'
Remove-Item -LiteralPath $manifest,$provenance -Force -ErrorAction SilentlyContinue

# Provenance is created first so the final manifest cryptographically covers it.
$payload = [ordered]@{
    application = 'PharmaLIMS'
    generatedAtUtc = [DateTime]::UtcNow.ToString('o')
    gitCommit = $env:GITHUB_SHA
    githubRunId = $env:GITHUB_RUN_ID
    githubRepository = $env:GITHUB_REPOSITORY
    runnerName = $env:RUNNER_NAME
    integrityModel = 'PUBLISH_PROVENANCE.json is covered by PUBLISH_MANIFEST_SHA256.txt; the manifest itself is attested by CI.'
}
$payload | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $provenance -Encoding utf8NoBOM

$lines = Get-ChildItem -Path $publish -File -Recurse |
    Where-Object { $_.FullName -ne $manifest } |
    Sort-Object FullName |
    ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($publish, $_.FullName).Replace('\\','/')
        $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant()
        "$hash  $relative"
    }
[IO.File]::WriteAllLines($manifest, $lines, [Text.UTF8Encoding]::new($false))
Write-Host "Publish provenance: $provenance"
Write-Host "Publish manifest: $manifest"
Write-Host "Manifest SHA-256: $((Get-FileHash -Algorithm SHA256 -LiteralPath $manifest).Hash.ToLowerInvariant())"

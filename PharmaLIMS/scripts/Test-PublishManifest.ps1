[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$publish = (Resolve-Path $PublishDirectory).Path
$manifest = Join-Path $publish 'PUBLISH_MANIFEST_SHA256.txt'
if (-not (Test-Path -LiteralPath $manifest -PathType Leaf)) {
    throw "Publish hash manifest is missing: $manifest"
}

$seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
$lineNumber = 0
foreach ($line in [IO.File]::ReadAllLines($manifest)) {
    $lineNumber++
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    if ($line -notmatch '^([0-9a-fA-F]{64})  (.+)$') {
        throw "Malformed publish manifest line $lineNumber."
    }
    $expected = $Matches[1].ToLowerInvariant()
    $relative = $Matches[2].Replace('/', [IO.Path]::DirectorySeparatorChar)
    if (-not $seen.Add($relative)) { throw "Duplicate publish manifest entry: $relative" }
    if ([IO.Path]::IsPathRooted($relative) -or $relative.Contains('..')) {
        throw "Unsafe publish manifest path: $relative"
    }
    $full = [IO.Path]::GetFullPath((Join-Path $publish $relative))
    $prefix = $publish.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Publish manifest entry escapes publish directory: $relative"
    }
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
        throw "Publish manifest file is missing: $relative"
    }
    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $full).Hash.ToLowerInvariant()
    if ($actual -ne $expected) {
        throw "Publish manifest SHA-256 mismatch: $relative"
    }
}
if ($seen.Count -eq 0) { throw 'Publish hash manifest contains no files.' }


# Fail if the publish directory contains an unmanifested file other than the manifest itself.
$actualRelativeFiles = @(Get-ChildItem -Path $publish -File -Recurse |
    Where-Object { $_.FullName -ne $manifest } |
    ForEach-Object { [IO.Path]::GetRelativePath($publish, $_.FullName) })
$unlisted = @($actualRelativeFiles | Where-Object { -not $seen.Contains($_) })
if ($unlisted.Count -gt 0) {
    throw "Publish directory contains file(s) not covered by the hash manifest: $($unlisted -join ', ')"
}
if ($seen.Count -ne $actualRelativeFiles.Count) {
    throw "Publish manifest coverage mismatch: manifest=$($seen.Count), files=$($actualRelativeFiles.Count)."
}

Write-Host "Publish manifest verification PASS: $($seen.Count) files; no unlisted publish artifacts."

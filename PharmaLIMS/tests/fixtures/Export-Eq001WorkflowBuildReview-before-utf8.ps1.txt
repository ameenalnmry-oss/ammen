[CmdletBinding()]
param([Parameter(Mandatory=$true)][ValidatePattern('^[a-fA-F0-9]{40}$')][string]$SourceCommit)
$ErrorActionPreference='Stop'
$project='E:\PharmaLIMS-MicroEquipment-SINGLE-VERIFY-20261012\PharmaLIMS'
function Read-Source([string]$Path,[string]$Revision=$SourceCommit) {
    if($Revision -notmatch '^[a-fA-F0-9]{40}$' -or $Path -notmatch '^PharmaLIMS/[A-Za-z0-9_./-]+$' -or $Path.Contains('..')) { throw 'Invalid source reference.' }
    $lines=@(git -C $project show "${Revision}:$Path")
    if($LASTEXITCODE -ne 0) { throw "Cannot read reviewed source: $Path" }
    return ($lines -join "`n")+"`n"
}
function Source-Hash([string]$Value) {
    $value=$Value.TrimStart([char]0xFEFF).Replace("`r`n","`n").TrimEnd([char[]]"`r`n")+"`n"
    $hash=[Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($value))).Replace('-','') }
    finally { $hash.Dispose() }
}
$manifest=(Read-Source 'PharmaLIMS/scripts/Eq001-WaterAnalysisSourcePrerequisites.json')|ConvertFrom-Json
if($manifest.OriginalProject -cne $project) { throw 'Unexpected project.' }
$known=@{}; $install=@{}; $unknown=@{}; $missing=@()
foreach($entry in $manifest.Files) { $install[$entry.Path]=$entry }
foreach($entry in $manifest.BuildInputs) {
    $relative=[string]$entry.Path
    if($relative -notmatch '^[A-Za-z0-9_./-]+$' -or $relative.Contains('..') -or $known.ContainsKey($relative)) { throw 'Invalid build input.' }
    $known[$relative]=$true
    $path=Join-Path $project $relative
    if(-not (Test-Path -LiteralPath $path -PathType Leaf)) { if(-not $install.ContainsKey($relative)) { $missing+=$relative }; continue }
    # This export contains source text only. It never reads configuration, credentials,
    # certificates, private rollback records, database rows, or binary assets.
    if([IO.Path]::GetExtension($path) -notin @('.cs','.xaml','.csproj','.props','.targets')) { continue }
    $local=Source-Hash ([IO.File]::ReadAllText($path))
    $revision=if($entry.SourceCommit) { [string]$entry.SourceCommit } else { $SourceCommit }
    $expected=Source-Hash (Read-Source $entry.SourcePath $revision)
    if($local -eq $expected) { continue }
    if($install.ContainsKey($relative) -and $local -eq $install[$relative].ExpectedNormalizedSha256) { continue }
    $unknown[$relative]=$path
}
foreach($file in @(Get-ChildItem -LiteralPath $project -File -Recurse -Force)) {
    $relative=$file.FullName.Substring($project.Length).TrimStart('\').Replace('\','/')
    if($relative -match '(^|/)(bin|obj|tests|\.git)/' -or
       ($relative.StartsWith('tools/') -and $relative -notmatch '^tools/PharmaLIMS\.TrustedSigning(?:\.Host)?/')) { continue }
    if($file.Extension -in @('.cs','.xaml','.csproj','.props','.targets') -and -not $known.ContainsKey($relative)) { $unknown[$relative]=$file.FullName }
}
$text=[Text.StringBuilder]::new()
[void]$text.AppendLine('EQ001 workflow build-source review; source-only, no application/database/service change.')
[void]$text.AppendLine("Reviewed revision: $SourceCommit")
[void]$text.AppendLine("Unreviewed source files: $($unknown.Count)")
foreach($relative in @($missing|Sort-Object)) { [void]$text.AppendLine("MISSING: $relative") }
foreach($relative in @($unknown.Keys|Sort-Object)) {
    $file=Get-Item -LiteralPath $unknown[$relative] -Force
    if(($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or $file.Length -gt 2097152) { throw "Refusing unusual source file: $relative" }
    [void]$text.AppendLine("===== FILE: $relative =====")
    [void]$text.AppendLine([IO.File]::ReadAllText($file.FullName))
}
$output=Join-Path 'C:\Users\Public\Documents' ('EQ001-workflow-build-review-'+[guid]::NewGuid().ToString('N')+'.txt')
[IO.File]::WriteAllText($output,$text.ToString(),[Text.UTF8Encoding]::new($false))
Write-Host "Unreviewed source files: $($unknown.Count); missing baseline inputs: $($missing.Count)"
Write-Host "Attach this file: $output"
Start-Process explorer.exe -ArgumentList @('/select,',('"'+$output+'"'))

$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$scriptFile = Join-Path $project 'scripts/Export-Eq001WorkflowBuildReview.ps1'
$tokens=$null; $errors=$null
$tree = [Management.Automation.Language.Parser]::ParseFile($scriptFile,[ref]$tokens,[ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }
foreach ($name in @('Read-Source','Source-Hash')) {
    $function = @($tree.FindAll({param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
    },$false))
    if ($function.Count -ne 1) { throw "Expected one $name function." }
    . ([scriptblock]::Create($function[0].Extent.Text))
}
$SourceCommit = git -C $project rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Test requires the existing checked-out Git revision.' }
$expected = [IO.File]::ReadAllText((Join-Path $project 'AssemblyInfo.cs'))
if (-not $expected.Contains([char]0x00A9)) { throw 'The real source fixture must include its copyright character.' }
$savedEncoding = [Console]::OutputEncoding
try {
    # Native git writes UTF-8 bytes even when the PowerShell console uses ASCII.
    [Console]::OutputEncoding = [Text.Encoding]::ASCII
    $legacyFile = Join-Path $project 'tests/fixtures/Export-Eq001WorkflowBuildReview-before-utf8.ps1.txt'
    $legacyTree = [Management.Automation.Language.Parser]::ParseFile($legacyFile,[ref]$tokens,[ref]$errors)
    if ($errors.Count) { throw ($errors | Out-String) }
    $legacyFunction = @($legacyTree.FindAll({param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Read-Source'
    },$false))
    if ($legacyFunction.Count -ne 1) { throw 'Expected one frozen source-export function.' }
    $legacy = & {
        . ([scriptblock]::Create($legacyFunction[0].Extent.Text))
        Read-Source 'PharmaLIMS/AssemblyInfo.cs' $SourceCommit
    }
    if ((Source-Hash $legacy) -ceq (Source-Hash $expected)) {
        throw 'The frozen native-pipeline exporter did not reproduce the Unicode mismatch with an ASCII console.'
    }
    Write-Host 'PASS: Frozen exporter reproduces the false Unicode source mismatch.'
    $actual = Read-Source 'PharmaLIMS/AssemblyInfo.cs' $SourceCommit
    if ((Source-Hash $actual) -cne (Source-Hash $expected)) {
        throw 'The source exporter changed actual Git Unicode bytes through console decoding.'
    }
    if (-not $actual.Contains([char]0x00A9)) { throw 'Unicode source content was lost.' }
    $lf = $actual.Replace("`r`n","`n")
    $crlf = $lf.Replace("`n","`r`n")
    $bom = ([char]0xFEFF).ToString() + $crlf
    if ((Source-Hash $lf) -cne (Source-Hash $crlf) -or (Source-Hash $lf) -cne (Source-Hash $bom)) {
        throw 'Reviewed BOM/line-ending normalization changed.'
    }
    if ((Source-Hash $actual) -ceq (Source-Hash ($actual + '// unreviewed change'))) {
        throw 'Actual content changes must still fail the source hash comparison.'
    }
}
finally { [Console]::OutputEncoding = $savedEncoding }
Write-Host 'PASS: Actual source-export helpers preserve UTF-8 with an ASCII console, normalize BOM/CRLF, and reject real source drift. No site export body executed.'

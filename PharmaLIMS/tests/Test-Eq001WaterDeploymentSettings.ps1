$ErrorActionPreference = 'Stop'
$deployment = Join-Path $PSScriptRoot '../scripts/Deploy-Eq001WaterRegistration.ps1'
$tokens = $null
$errors = $null
$tree = [Management.Automation.Language.Parser]::ParseFile((Resolve-Path $deployment),[ref]$tokens,[ref]$errors)
if ($errors) { throw ($errors | Out-String) }
$helper = $tree.FindAll({param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-DbOption'
},$false)
. ([scriptblock]::Create($helper.Extent.Text))
$creation = @($tree.EndBlock.Statements | Where-Object { $_.Extent.Text -cmatch '^\$dbOptions\s*=' })
$assignment = @($tree.EndBlock.Statements | Where-Object { $_.Extent.Text -cmatch '^\$dbOptions(\.ConnectionString\s*=|\.set_ConnectionString\()' })
if ($creation.Count -ne 1 -or $assignment.Count -ne 1) { throw 'Expected one reviewed connection builder initialization.' }
# Execute only the actual pure builder initialization; never the site deployment body.
foreach ($connection in @(
    'Server=tcp:AMEEN,1433;Database=EQ001_UAT_AMEEN_20261024;Integrated Security=True;Encrypt=Mandatory;TrustServerCertificate=False',
    'Data Source=tcp:AMEEN,1433;Initial Catalog=EQ001_UAT_AMEEN_20261024;Integrated Security=SSPI;Encrypt=True;Trust Server Certificate=False'
)) {
    $hostConfig = [pscustomobject]@{ConnectionString=$connection}
    $initialize = $creation[0].Extent.Text + "`n" + $assignment[0].Extent.Text + "`nreturn ,`$dbOptions"
    $parsed = & ([scriptblock]::Create($initialize))
    if ((Get-DbOption $parsed @('Server','Data Source')) -ne 'tcp:AMEEN,1433' -or
        (Get-DbOption $parsed @('Database','Initial Catalog')) -ne 'EQ001_UAT_AMEEN_20261024' -or
        (Get-DbOption $parsed @('Integrated Security','Trusted_Connection')) -notin @('True','SSPI') -or
        (Get-DbOption $parsed @('Encrypt')) -notin @('True','Mandatory') -or
        (Get-DbOption $parsed @('TrustServerCertificate','Trust Server Certificate')) -ne 'False') {
        throw 'Actual deployment parser did not read the approved UAT connection options.'
    }
}
$hostConfig = [pscustomobject]@{ConnectionString='Server=wrong-host;Database=wrong-db;Integrated Security=False;Encrypt=False;TrustServerCertificate=True'}
$parsed = & ([scriptblock]::Create($initialize))
if ((Get-DbOption $parsed @('Server','Data Source')) -eq 'tcp:AMEEN,1433' -or
    (Get-DbOption $parsed @('Database','Initial Catalog')) -eq 'EQ001_UAT_AMEEN_20261024' -or
    (Get-DbOption $parsed @('Integrated Security')) -ne 'False' -or
    (Get-DbOption $parsed @('TrustServerCertificate')) -ne 'True') {
    throw 'Unexpected target/TLS values must remain visible to the fail-closed checks.'
}
Write-Host 'PASS: Actual Water deployment connection parsing, aliases and rejected-value preservation. No site access or mutation.'

$checks = @($tree.FindAll({param($node)
    ($node -is [Management.Automation.Language.IfStatementAst] -and $node.Extent.Text -cmatch '^if \(\$sqlCode -ne 0\)') -or
    ($node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'Assert-WaterStageResult')
},$true))
if ($checks.Count -ne 1) { throw 'Expected one SQL staging completion guard.' }
$guard = $tree.FindAll({param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Assert-WaterStageResult'
},$false)
if ($guard) { . ([scriptblock]::Create($guard.Extent.Text)) }
$temporaryLog = Join-Path ([IO.Path]::GetTempPath()) ('water-stage-proof-' + [guid]::NewGuid() + '.txt')
try {
    foreach ($case in @(
        @{Code=0;Text='Timeout expired';Accept=$false},
        @{Code=0;Text='';Accept=$false},
        @{Code=1;Text='PASS: Water SQL boundary staged; existing business rows preserved.';Accept=$false},
        @{Code=0;Text='PASS: Water SQL boundary staged; existing business rows preserved.';Accept=$true}
    )) {
        $sqlCode = $case.Code
        $sqlLog = $temporaryLog
        [IO.File]::WriteAllText($sqlLog,$case.Text)
        $accepted = $true
        try { & ([scriptblock]::Create($checks[0].Extent.Text)) }
        catch { $accepted = $false }
        if ($accepted -ne $case.Accept) { throw 'SQL staging cannot succeed on zero exit code without a committed completion marker.' }
    }
}
finally { if (Test-Path -LiteralPath $temporaryLog) { Remove-Item -LiteralPath $temporaryLog } }
Write-Host 'PASS: SQL staging rejects timeout/empty output even with exit code zero; committed proof is required.'

$completion = Join-Path $PSScriptRoot '../scripts/Complete-Eq001WaterSql.ps1'
$completionTree = [Management.Automation.Language.Parser]::ParseFile((Resolve-Path $completion),[ref]$tokens,[ref]$errors)
if ($errors) { throw ($errors | Out-String) }
$builder = @($completionTree.FindAll({param($n)
    $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'New-WaterCompletionBatch'
},$false))
if ($builder.Count -ne 1) { throw 'Expected a pure SQL-only completion builder.' }
. ([scriptblock]::Create($builder[0].Extent.Text))
$rawStage = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '../scripts/EQ001_UAT_Water_Registration.sql'))
$batch = New-WaterCompletionBatch $rawStage
if (-not $batch.Contains($rawStage.Replace('DECLARE @VerifiedIsolatedUat bit = 0;','DECLARE @VerifiedIsolatedUat bit = 1;')) -or
    -not $batch.Contains('SET LOCK_TIMEOUT 10000') -or
    $batch.LastIndexOf('COMMIT;') -gt $batch.LastIndexOf('PASS: Water SQL boundary staged; existing business rows preserved.')) {
    throw 'Completion must preserve the reviewed SQL bytes and prove commit only after preservation checks.'
}
$rejected=$false
try { New-WaterCompletionBatch 'wrong stage' | Out-Null } catch { $rejected=$true }
if (-not $rejected) { throw 'Completion must reject an unknown staging format.' }
Write-Host 'PASS: Actual SQL-only completion builder preserves reviewed stage text and rejects missing UAT opt-in.'

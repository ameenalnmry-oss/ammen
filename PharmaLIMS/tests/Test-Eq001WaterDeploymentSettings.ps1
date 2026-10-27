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

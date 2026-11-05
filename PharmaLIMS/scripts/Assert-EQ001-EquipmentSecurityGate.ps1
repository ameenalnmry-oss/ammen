param(
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$Database = 'EQ001_UAT_AMEEN_20261024',
    [string]$Server = 'lpc:.\SQLEXPRESS'
)
$ErrorActionPreference = 'Stop'
$failures = New-Object 'System.Collections.Generic.List[string]'
function Check([bool]$condition,[string]$name) {
    if($condition) { Write-Output "PASS $name" }
    else { Write-Output "FAIL $name"; $script:failures.Add($name) }
}
$expected = @(
    'EQ001_UAT_MicroEquipment_Draft.sql',
    'EQ001_UAT_MicroEquipment_Link.sql',
    'EQ001_UAT_MicroEquipment_ConfirmUse.sql',
    'EQ001_UAT_MicroEquipment_NonUseExecution.sql'
)
foreach($name in $expected) {
    $file = Join-Path $ProjectRoot ('scripts\' + $name)
    Check (Test-Path -LiteralPath $file) "Prototype exists: $name"
    if(Test-Path -LiteralPath $file) {
        $matches = @(Select-String -LiteralPath $file -Pattern 'THROW\s+57198[^\r\n]*SECURITY HOLD')
        Check ($matches.Count -eq 1) "Unconditional SECURITY HOLD: $name"
    }
}
$endpoint = Join-Path $ProjectRoot 'tools\PharmaLIMS.TrustedSigning.Host\TrustedApplicationEndpoint.cs'
if(Test-Path $endpoint) {
    $source = [IO.File]::ReadAllText($endpoint)
    $hasEquipmentRoute = $source -match 'EquipmentDraft|EquipmentLink|EquipmentConfirmUse|EquipmentSubmitExecution'
    Check $hasEquipmentRoute 'Trusted EquipmentDraft source route exists'
    Check ($source.Contains('if(equipmentDraft && !enableEquipmentDraftUat)')) 'Equipment draft route is guarded by explicit opt-in'
    Check ($source.Contains('if(equipmentLink && !enableEquipmentLinkUat)')) 'Equipment link route is guarded by explicit opt-in'
    Check ($source.Contains('if(equipmentConfirm && !enableEquipmentConfirmUat)')) 'Equipment confirm route is guarded by explicit opt-in'
}
else { Check $false 'TrustedApplicationEndpoint source present' }
$settings = Join-Path $ProjectRoot 'tools\PharmaLIMS.TrustedSigning\TrustedHostSettings.cs'
if (Test-Path $settings) {
    $settingSource = [IO.File]::ReadAllText($settings)
    Check ($settingSource.Contains('public bool EnableEquipmentDraftUat { get; set; }')) 'Equipment UAT is disabled by default in host settings'
    Check ($settingSource.Contains('public bool EnableEquipmentLinkUat { get; set; }')) 'Equipment Link UAT is disabled by default'
    Check ($settingSource.Contains('public bool EnableEquipmentConfirmUat { get; set; }')) 'Equipment Confirm UAT is disabled by default'
}
else { Check $false 'Trusted host settings source present' }
$installer = Join-Path $ProjectRoot 'scripts\EQ001_TrustedEquipmentDraft_Installer_REVIEW_ONLY.sql'
if (Test-Path $installer) {
    $holds = @(Select-String -LiteralPath $installer -Pattern 'THROW\s+57198')
    Check ($holds.Count -eq 1) 'New signer-only draft installer is under SECURITY HOLD'
}
else { Check $false 'New trusted equipment installer present' }
foreach($name in @('EQ001_TrustedEquipmentLink_Installer_REVIEW_ONLY.sql',
                   'EQ001_TrustedEquipmentConfirm_Installer_REVIEW_ONLY.sql')) {
    $candidate = Join-Path $ProjectRoot ('scripts\' + $name)
    if(Test-Path -LiteralPath $candidate) {
        $hold = @(Select-String -LiteralPath $candidate -Pattern 'THROW\s+57198')
        Check ($hold.Count -eq 1) ("Signer-only installer is held: " + $name)
    }
    else { Check $false ("Signer-only installer exists: " + $name) }
}
try {
    $connectionString = 'Server=' + $Server + ';Database=' + $Database + ';Integrated Security=SSPI;Encrypt=True;TrustServerCertificate=True;Connect Timeout=5;'
    $connection = New-Object System.Data.SqlClient.SqlConnection($connectionString)
    $connection.Open()
    try {
        $command = $connection.CreateCommand()
        $command.CommandText = @'
SELECT
 (SELECT COUNT(*) FROM sys.procedures WHERE name IN (
  N'EQ001_UAT_CreateMicroEquipmentDraft',
  N'EQ001_UAT_LinkMicroEquipmentDraft',
  N'EQ001_UAT_ConfirmMicroEquipmentUse',
  N'EQ001_UAT_SubmitMicroEquipmentExecution')) AS PrototypeProcedures,
 (SELECT COUNT_BIG(*) FROM dbo.MicroEquipmentActivities) AS EquipmentActivities;
'@
        $reader = $command.ExecuteReader()
        if($reader.Read()) {
            Check ($reader.GetInt32(0) -eq 0) 'No insecure prototype equipment procedures installed'
            Write-Output ('INFO Existing equipment activities: ' + $reader.GetInt64(1))
        }
        $reader.Close()
        $command.CommandText = @'
SELECT COUNT(*) FROM sys.procedures WHERE name IN
(N'EQ001_TrustedEquipmentDraft',N'EQ001_TrustedEquipmentLink',N'EQ001_TrustedEquipmentConfirm');
'@
        Check ([int]$command.ExecuteScalar() -eq 0) 'Unreviewed new signer-only equipment procedures remain uninstalled'
    }
    finally { $connection.Dispose() }
}
catch { Check $false ('Read-only database gate could not complete: ' + $_.Exception.GetType().Name + ': ' + $_.Exception.Message) }
Write-Output ('GATE_FAILURES=' + $failures.Count)
if($failures.Count -gt 0) { exit 1 }
Write-Output 'SECURITY HOLD VERIFIED: Equipment write path is intentionally not released.'
exit 0

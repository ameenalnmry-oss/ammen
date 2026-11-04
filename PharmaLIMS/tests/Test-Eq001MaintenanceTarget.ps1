[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$BootstrapPath)
$ErrorActionPreference='Stop'
$text=[IO.File]::ReadAllText($BootstrapPath)
$first=$text.IndexOf('$mode=if(')
$last=$text.IndexOf('$settings=$null;$connection=$null',$first)
if($first -lt 0 -or $last -le $first){throw 'Target validation block not found.'}
# Execute only the actual target-validation block, with synthetic settings.
# Never execute administrator checks, configuration reads, service or install code.
$block=[scriptblock]::Create($text.Substring($first,$last-$first))
function Test-Case([string]$Name,[string]$Server='AMEEN\SQLEXPRESS',[string]$Database='EQ001_UAT_AMEEN_20261024',
    [string]$Mode='UAT',[bool]$Approved=$false,[string]$EnabledFlag='',[string]$ExpectedFailure='') {
    $settings=[pscustomobject]@{ConnectionString=('Data Source='+$Server+';Initial Catalog='+$Database+';Integrated Security=True');DeploymentMode=$Mode;ProductionActivationApproved=$Approved;
        EnableEquipmentDraftUat=$false;EnableEquipmentLinkUat=$false;EnableEquipmentConfirmUat=$false}
    if($EnabledFlag){$settings.$EnabledFlag=$true}
    $failure=$null
    try{& $block}catch{$failure=$_.Exception.Message}
    if($ExpectedFailure){
        if(-not $failure -or -not $failure.StartsWith($ExpectedFailure)){throw "ASSERT $Name did not fail at the expected target gate."}
    }elseif($failure){throw "ASSERT $Name must accept isolated UAT: $failure"}
    Write-Host ('PASS maintenance target: '+$Name)
}
Test-Case 'named original UAT instance'
Test-Case 'TCP original UAT instance' -Server 'tcp:AMEEN,1433'
Test-Case 'original UAT TCP alias' -Server 'AMEEN,1433'
Test-Case 'Production mode blocked' -Mode 'Production' -ExpectedFailure 'Unexpected UAT service target.'
Test-Case 'Production activation blocked' -Approved $true -ExpectedFailure 'Unexpected UAT service target.'
Test-Case 'different database blocked' -Database 'PharmaLIMS_Production' -ExpectedFailure 'Unexpected UAT service target.'
Test-Case 'different machine blocked' -Server 'OTHER\SQLEXPRESS' -ExpectedFailure 'Unexpected UAT service target.'
Test-Case 'different instance blocked' -Server 'AMEEN\OTHER' -ExpectedFailure 'Unexpected UAT service target.'
Test-Case 'different port blocked' -Server 'tcp:AMEEN,1434' -ExpectedFailure 'Unexpected UAT service target.'
foreach($flag in @('EnableEquipmentDraftUat','EnableEquipmentLinkUat','EnableEquipmentConfirmUat')){
    Test-Case ($flag+' remains held') -EnabledFlag $flag -ExpectedFailure 'Held equipment features are enabled.'
}
Write-Host 'PASS12 maintenance target cases using actual bootstrap block. No configuration read or source/service/database changes.'

"""Guarded site deployment contracts; pure PowerShell helpers run without site access."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import unittest

ROOT = Path(__file__).resolve().parents[1]
DEPLOY = ROOT / 'scripts/Deploy-Eq001WaterAnalysis.ps1'
INSTALL = ROOT / 'scripts/Install-Eq001AmeenSource.ps1'
MANIFEST = ROOT / 'scripts/Eq001-WaterAnalysisSourcePrerequisites.json'
PWSH = shutil.which('pwsh') or (str(Path('/workspace/.cloud-setup/powershell-parser/runtime/pwsh'))
    if Path('/workspace/.cloud-setup/powershell-parser/runtime/pwsh').is_file() else None)


def ps_literal(value):
    return "'" + str(value).replace("'", "''") + "'"


def pure_powershell(body):
    prefix = f'''$ErrorActionPreference='Stop'
$t=$null; $e=$null
$tree=[Management.Automation.Language.Parser]::ParseFile({ps_literal(DEPLOY)},[ref]$t,[ref]$e)
if ($e.Count) {{ throw ($e|Out-String) }}
foreach ($name in @('Get-WaterWorkflowStage','New-WaterAnalysisBatch','Assert-WaterAnalysisSqlProof','Enable-JsonBooleanFlag')) {{
 $f=@($tree.FindAll({{param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name}},$false))
 if ($f.Count -ne 1) {{ throw 'Missing unique pure function' }}
 . ([scriptblock]::Create($f[0].Extent.Text))
}}
'''
    return subprocess.run([PWSH, '-NoLogo', '-NoProfile', '-NonInteractive', '-Command', prefix + body],
        text=True, capture_output=True, timeout=45,
        env={**os.environ, 'XDG_CACHE_HOME':'/tmp/eq001-ps-cache',
             'XDG_CONFIG_HOME':'/tmp/eq001-ps-config', 'XDG_DATA_HOME':'/tmp/eq001-ps-data'})


class DeploymentContracts(unittest.TestCase):
    def test_analysis_allowlist_preserves_site_baselines_and_is_separate(self):
        data=json.loads(MANIFEST.read_text())
        paths=[e['Path'] for e in data['Files']]
        self.assertEqual(len(paths), len(set(paths)))
        self.assertNotIn('scripts/EQ001_UAT_Water_Registration.sql', paths)
        self.assertNotIn('scripts/Deploy-Eq001WaterRegistration.ps1', paths)
        self.assertIn('ResultsEntry.xaml.Part6.cs', paths)
        self.assertIn('Services/WaterWorkflowValidator.Evidence.cs', paths)
        for e in data['Files']:
            self.assertFalse(e['Remove'])
            if e['Path'].startswith('ResultsEntry'):
                self.assertEqual('PharmaLIMS/scripts/site-overlays/AMEEN-'+e['Path']+'.txt', e['SourcePath'])
            if e['ExpectedNormalizedSha256']:
                self.assertRegex(e['ExpectedNormalizedSha256'], r'^[a-f0-9]{64}$')
        installer=INSTALL.read_text()
        self.assertIn('[switch]$WaterAnalysisOnly', installer)
        self.assertIn("'source-water-analysis-rollback-{0}.json'", installer)
        self.assertIn('Schema=1; SourceCommit=$SourceCommit; Files=$rollback', installer)

    def test_mutation_requires_verified_source_rollback_and_private_runtime_copy(self):
        source=DEPLOY.read_text()
        guard=source.index('if (-not $Apply)')
        self.assertLess(guard,source.index('Backup-Tree $desktop'))
        self.assertLess(source.index('Matching protected original-source rollback'),source.index('dotnet build'))
        self.assertLess(source.index('Backup-Tree $serviceRuntime'), source.index('dotnet build'))
        self.assertIn('Flush($true)', source)
        self.assertIn('Assert-NoReparsePoint',source)
        self.assertIn('Assert-BuildInputs $manifest',source)
        self.assertIn('Unlisted active build input',source)
        self.assertIn('Unqualified dependency/runtime difference', source)
        self.assertEqual(1,source.count('dotnet build'))
        self.assertNotIn('Deploy-Eq001WaterRegistration.ps1',source)
        self.assertNotIn('EQ001_UAT_Water_Registration.sql',source)

    def test_sql_commit_and_independent_metadata_precede_activation(self):
        source=DEPLOY.read_text()
        stage=source.index("Invoke-WaterAnalysisSql $batch")
        metadata=source.index("Invoke-WaterAnalysisSql $metadata")
        install=source.index('foreach ($copy in $copies) {',metadata)
        start=source.index('Start-Service $serviceName')
        self.assertLess(stage,metadata)
        self.assertLess(metadata,install)
        self.assertLess(install,start)
        for item in ['execute_as_principal_id=-2','TRG_EQ001_WaterWorkflowReceipts_AppendOnly',
                     "permission_name=N'EXECUTE'",'SUSER_SID',"IS_SRVROLEMEMBER(N'sysadmin'",
                     'EnableWaterAnalysisUat','original-certificate.pfx','ServerCertificateSha256']:
            self.assertIn(item,source)
        self.assertNotIn('Add-Member -NotePropertyName EnableWaterRegistrationUat',source)
        self.assertIn("$mutated[$i].Target,$mutated[$i].Before",source)

    @unittest.skipUnless(PWSH, 'pwsh is required for executable deployment helper contracts')
    def test_powershell_parser(self):
        body='\n'.join(f'''[Management.Automation.Language.Parser]::ParseFile({ps_literal(path)},[ref]$t,[ref]$e)>$null
if ($e.Count) {{ throw ($e|Out-String) }}''' for path in (DEPLOY,INSTALL))
        result=pure_powershell(body)
        self.assertEqual(0,result.returncode,result.stdout+result.stderr)

    @unittest.skipUnless(PWSH, 'pwsh is required for executable deployment helper contracts')
    def test_stage_proof_rejects_timeout_error_missing_or_premature_marker(self):
        result=pure_powershell(r'''
$ok='PASS: Water analysis outer transaction committed; all existing rows preserved.'
Assert-WaterAnalysisSqlProof 0 $ok $true
$cases=@(
 @{Code=1;Log=$ok;Done=$true}, @{Code=0;Log=$ok;Done=$false},
 @{Code=0;Log=($ok+"`nSqlcmd: Error: execution timeout");Done=$true},
 @{Code=0;Log=($ok+"`nMsg 57040, Level 16");Done=$true},
 @{Code=0;Log='PASS: Exact isolated Water analysis boundary staged; historical clinical rows and existing client rights are unchanged.';Done=$true},
 @{Code=0;Log=($ok+"`n"+$ok);Done=$true}, @{Code=0;Log='';Done=$true})
foreach ($c in $cases) {
 $caught=$false
 try { Assert-WaterAnalysisSqlProof $c.Code $c.Log $c.Done } catch { $caught=$true }
 if (-not $caught) { throw 'Unsafe SQL proof was accepted' }
}
''')
        self.assertEqual(0,result.returncode,result.stdout+result.stderr)

    @unittest.skipUnless(PWSH, 'pwsh is required for executable deployment helper contracts')
    def test_composition_equals_actual_csharp_contract_and_rejects_ambiguous_marker(self):
        contract=ROOT/'tools/PharmaLIMS.TrustedSigning/WaterWorkflowSqlContract.cs'
        template=ROOT/'scripts/EQ001_UAT_Water_Analysis.sql'
        body=f'''
$contract=[IO.File]::ReadAllText({ps_literal(contract)})
$template=[IO.File]::ReadAllText({ps_literal(template)})
Add-Type -TypeDefinition $contract
$actual=Get-WaterWorkflowStage $template $contract
$expected=[PharmaLIMS.TrustedSigning.WaterWorkflowSqlContract]::ComposeStage($template)
if ($actual -cne $expected) {{ throw 'PowerShell composition differs from compiled C# ComposeStage' }}
$crlf=$contract.Replace("`r`n","`n").Replace("`n","`r`n").Replace('namespace PharmaLIMS.TrustedSigning;','namespace CrLfProbe;')
Add-Type -TypeDefinition $crlf
if ((Get-WaterWorkflowStage $template $crlf) -cne [CrLfProbe.WaterWorkflowSqlContract]::ComposeStage($template)) {{ throw 'CRLF contract composition differs from C#' }}
foreach ($bad in @('no marker',($template+'/*__WATER_WORKFLOW_SNAPSHOT_SQL__*/'))) {{
 $caught=$false
 try {{ Get-WaterWorkflowStage $bad $contract >$null }} catch {{ $caught=$true }}
 if (-not $caught) {{ throw 'Ambiguous snapshot marker accepted' }}
}}
$batch=New-WaterAnalysisBatch $actual
$outer=$batch.LastIndexOf('COMMIT;')
$proof=$batch.LastIndexOf('PASS: Water analysis outer transaction committed; all existing rows preserved.')
if ($outer -lt 0 -or $outer -ge $proof) {{ throw 'Proof is not after outer commit' }}
if ($batch.Contains('DECLARE @VerifiedIsolatedUat bit = 0;')) {{ throw 'Reviewed stage opt-in was not applied' }}
if (-not $batch.Contains('COLLATE Latin1_General_100_BIN2')) {{ throw 'Row hash ordering is nondeterministic' }}
if (-not $batch.Contains('FROM sys.tables WHERE is_ms_shipped=0')) {{ throw 'Existing-table historical hash coverage is incomplete' }}
'''
        result=pure_powershell(body)
        self.assertEqual(0,result.returncode,result.stdout+result.stderr)

    @unittest.skipUnless(PWSH, 'pwsh is required for executable deployment helper contracts')
    def test_combined_prm_requires_separate_stage_opt_in_and_flag_selection(self):
        body=f'''$contract=[IO.File]::ReadAllText({ps_literal(ROOT/'tools/PharmaLIMS.TrustedSigning/WaterWorkflowSqlContract.cs')})
$water=Get-WaterWorkflowStage ([IO.File]::ReadAllText({ps_literal(ROOT/'scripts/EQ001_UAT_Water_Analysis.sql')})) $contract
$prm=[IO.File]::ReadAllText({ps_literal(ROOT/'scripts/EQ001_UAT_Prm_Registration.sql')})
$default=New-WaterAnalysisBatch $water
if ($default.Contains('dbo.EQ001_UAT_PrmRegistration')) {{ throw 'Default rollout stages PRM implicitly' }}
$combined=New-WaterAnalysisBatch $water $prm
foreach ($declaration in @('DECLARE @VerifiedIsolatedUat bit = 1;','DECLARE @VerifiedPrmIsolatedUat bit = 1;',
 'DECLARE @Procedure nvarchar(max)','DECLARE @PrmProcedure nvarchar(max)')) {{
 if ($combined.Split(@($declaration),[StringSplitOptions]::None).Count -ne 2) {{ throw 'Combined stage declaration is ambiguous' }}
}}
if ($combined.Contains('bit = 0;')) {{ throw 'Combined independent stage opt-in missing' }}
$proof='PASS: Water analysis outer transaction committed; all existing rows preserved.'
if ($combined.Split(@($proof),[StringSplitOptions]::None).Count -ne 2) {{ throw 'Combined stage has ambiguous outer completion proof' }}
$json='{{"ConnectionString":"private-value","EnableWaterAnalysisUat":false,"EnablePrmRegistrationUat":false}}'
$only=Enable-JsonBooleanFlag $json 'EnableWaterAnalysisUat'
if ($only -cne $json.Replace('"EnableWaterAnalysisUat":false','"EnableWaterAnalysisUat":true')) {{ throw 'Analysis activation changed unrelated bytes' }}
$both=Enable-JsonBooleanFlag $only 'EnablePrmRegistrationUat'
if ($both -cne $only.Replace('"EnablePrmRegistrationUat":false','"EnablePrmRegistrationUat":true')) {{ throw 'Independent PRM activation changed unrelated bytes' }}
$caught=$false
try {{ New-WaterAnalysisBatch $water $prm.Replace('DECLARE @VerifiedIsolatedUat bit = 0;','') >$null }} catch {{ $caught=$true }}
if (-not $caught) {{ throw 'Unreviewed PRM stage without its independent guard was accepted' }}
'''
        result=pure_powershell(body)
        self.assertEqual(0,result.returncode,result.stdout+result.stderr)

if __name__ == '__main__':
    unittest.main()

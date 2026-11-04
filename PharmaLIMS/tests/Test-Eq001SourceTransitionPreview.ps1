[CmdletBinding()]
param([string]$ToolPath=(Join-Path $PSScriptRoot '../scripts/Get-Eq001SourceTransitionPreview.ps1'),[string]$PinnedPlanPath)
$ErrorActionPreference='Stop'
function Assert([bool]$Condition,[string]$Message) { if(-not $Condition){throw "ASSERT: $Message"} }
if(-not(Test-Path -LiteralPath $ToolPath -PathType Leaf)){throw 'ASSERT: qualified read-only transition preview is not available.'}
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Resolve-Path -LiteralPath $ToolPath).Path,[ref]$tokens,[ref]$errors)
Assert (@($errors).Count -eq 0) 'Planner must parse.'
foreach($node in $ast.EndBlock.Statements){
 if($node -is [Management.Automation.Language.FunctionDefinitionAst]){. ([scriptblock]::Create($node.Extent.Text))}
}
function Hash([byte[]]$Bytes){
 $sha=[Security.Cryptography.SHA256]::Create()
 try{[BitConverter]::ToString($sha.ComputeHash($Bytes)).Replace('-','').ToLowerInvariant()}finally{$sha.Dispose()}
}
function NormalizedHash([byte[]]$Bytes){
 $text=([Text.UTF8Encoding]::new($false,$true)).GetString($Bytes).TrimStart([char]65279)
 Hash ([Text.Encoding]::UTF8.GetBytes($text.Replace(([string][char]13+[char]10),[string][char]10).TrimEnd([char[]]@([char]13,[char]10))+[char]10))
}
$updates=@('CultureMediaPreparation.xaml.Part2.cs','CultureMediaPreparation.xaml.Part3.cs','CultureMediaPreparation.xaml.cs',
 'DatabaseHelper.EnvironmentalMonitoring.cs','DatabaseHelper.QualityEvents.cs','EMResultsEntry.xaml.Part3.cs','EMResultsEntry.xaml.cs',
 'PRMQualityEventInvestigation.xaml.cs','ProductionRawMaterialResults.xaml.cs','QualityEventInvestigation.xaml.Part2.cs',
 'QualityEventInvestigation.xaml.cs','ReportCertificate.xaml.cs','SystemPreflight.xaml.cs',
 'tools/PharmaLIMS.TrustedSigning/TrustedCallerMap.cs','tools/PharmaLIMS.TrustedSigning/WindowsTrustedPipeServer.cs','DatabaseHelper.EmSourcePlan.cs')
$additions=@('Services/MediaPreparationAmendmentSnapshot.cs','Infrastructure/QualityEventInvestigationConcurrencyGuard.cs','tools/PharmaLIMS.TrustedSigning/TrustedPipeConnectionPool.cs')
function Add-Input($Fixture,[string]$Relative,[bool]$NewFile,[bool]$Database){
 [byte[]]$before=[Text.Encoding]::UTF8.GetBytes('original::'+$Relative)
 [byte[]]$after=[Text.Encoding]::UTF8.GetBytes('candidate::'+$Relative)
 $entry=[pscustomobject]@{Path=$Relative;OriginalRawSha256=$(if($NewFile){$null}else{Hash $before});
 OriginalNormalizedSha256=$(if($NewFile){$null}else{NormalizedHash $before});OriginalStatus='SOURCE_DIFFERENCE';
 Sha256=(NormalizedHash $before);Artifact='scripts/site-overlays/fixture.cs.txt'}
 if($Database){$entry.OriginalStatus='IMMUTABLE_SITE_DATABASE_CONTENT';$entry|Add-Member NoteProperty ExactSha256 (Hash $before)}
 if($NewFile -or $Relative -in $updates){$entry.Sha256=NormalizedHash $after;$Fixture.Candidates[$Relative]=$after}
 if(-not $NewFile){$path=Join-Path $Fixture.Project $Relative;[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path));[IO.File]::WriteAllBytes($path,$before)}
 $Fixture.Inputs.Add($entry)
}
function New-Fixture{
 $root=Join-Path ([IO.Path]::GetTempPath()) ('eq001-preview-tests-'+[guid]::NewGuid().ToString('N'))
 $f=[pscustomobject]@{Root=$root;Project=(Join-Path $root 'original');Receipt=(Join-Path $root 'protected-receipt.json');ReceiptHash=$null;Inputs=[Collections.Generic.List[object]]::new();Candidates=@{};Plan=$null}
 [void][IO.Directory]::CreateDirectory($f.Project)
 [IO.File]::WriteAllText($f.Receipt,'{"OriginalBase64":"private-sentinel-never-output","SourceCommit":"97b34729"}')
 $f.ReceiptHash=Hash ([IO.File]::ReadAllBytes($f.Receipt))
 foreach($path in $updates){Add-Input $f $path $false $false};foreach($path in $additions){Add-Input $f $path $true $false}
 for($i=0;$i -lt 215;$i++){Add-Input $f ("Build/Keep{0:D3}.cs" -f $i) $false $false}
 for($i=0;$i -lt 123;$i++){Add-Input $f ("Database/Original{0:D3}.sql" -f $i) $false $true}
 $f.Plan=[pscustomobject]@{InputCount=357;OriginalBuildInputCount=231;MigrationCount=102;Version='2026.10.19.306';ArchiveSha256='b9244b38b1d4cb72dbd0bacf913fb7e92e07802626057e5169a5c58d71b6309a';Inputs=$f.Inputs.ToArray()}
 return $f
}
function Snapshot([string]$Root){
 $records=@(Get-ChildItem -LiteralPath $Root -Recurse -File -Force|Sort-Object FullName|ForEach-Object{$_.FullName.Substring($Root.Length)+':'+(Hash ([IO.File]::ReadAllBytes($_.FullName)))})
 [string]::Join('|',$records)
}
function Invoke-Preview($Fixture){
 $argsForPreview=@{Plan=$Fixture.Plan;Candidates=$Fixture.Candidates;Project=$Fixture.Project;ReceiptPath=$Fixture.Receipt;ReceiptSha256=$Fixture.ReceiptHash}
 Get-Eq001SourceTransitionPreview @argsForPreview
}
function Expect-Rejection([string]$Label,[scriptblock]$Mutate){
 $f=New-Fixture
 try{& $Mutate $f;$before=Snapshot $f.Root;$rejected=$false
 try{$null=Invoke-Preview $f}catch{$rejected=$true}
 Assert $rejected "$Label must reject.";Assert ((Snapshot $f.Root) -ceq $before) "$Label must not write source or receipt."
 Write-Host "PASS rejection/no writes: $Label"
 }finally{Remove-Item -LiteralPath $f.Root -Recurse -Force}
}
$f=New-Fixture
try{
 $before=Snapshot $f.Root;$p=Invoke-Preview $f
 Assert ($p.SourceChangeCount -eq 19 -and $p.Updates -eq 16 -and $p.Additions -eq 3) 'Valid16/3 delta.'
 Assert ($p.OriginalFilesVerified -eq 354 -and $p.UnchangedOriginalInputs -eq 338) 'All354 raw originals and338 retained inputs.'
 Assert ($p.CandidateArtifactsVerified -eq 19 -and @($p.Changes).Count -eq 19) 'Every candidate hash.'
 Assert ($p.PreviewOnly -eq $true -and $p.CandidateInstalled -eq $false -and $p.LocalUatPassed -eq $false) 'No installation/UAT claim.'
 Assert ((Snapshot $f.Root) -ceq $before) 'Successful preview must not write.'
 Assert (($p|ConvertTo-Json -Depth 8) -notmatch 'private-sentinel-never-output|candidate::|original::') 'No body disclosure.'
 $row=@($p.Changes|Where-Object Path -eq 'ReportCertificate.xaml.cs')[0]
 Assert ($row.Operation -ceq 'update' -and $row.OriginalRawSha256 -ceq (Hash ([Text.Encoding]::UTF8.GetBytes('original::ReportCertificate.xaml.cs')))) 'Original byte witness retained.'
 Write-Host 'PASS valid19-file preview;354 originals;338 retained;19 candidates;receipt unchanged;no bodies/writes.'
}finally{Remove-Item -LiteralPath $f.Root -Recurse -Force}
Expect-Rejection 'changed original' {param($f)[IO.File]::WriteAllText((Join-Path $f.Project 'ReportCertificate.xaml.cs'),'different')}
Expect-Rejection 'normalized-only original' {param($f)$p=Join-Path $f.Project 'ReportCertificate.xaml.cs';[IO.File]::WriteAllBytes($p,[byte[]](@(239,187,191)+[IO.File]::ReadAllBytes($p)))}
Expect-Rejection 'missing original' {param($f)Remove-Item -LiteralPath (Join-Path $f.Project 'Build/Keep000.cs')}
Expect-Rejection 'database drift' {param($f)[IO.File]::WriteAllText((Join-Path $f.Project 'Database/Original000.sql'),'drift')}
Expect-Rejection 'receipt drift' {param($f)[IO.File]::WriteAllText($f.Receipt,'changed')}
Expect-Rejection 'missing receipt' {param($f)Remove-Item -LiteralPath $f.Receipt}
Expect-Rejection 'missing candidate' {param($f)$f.Candidates.Remove('ReportCertificate.xaml.cs')}
Expect-Rejection 'tampered candidate' {param($f)$f.Candidates['ReportCertificate.xaml.cs']=[Text.Encoding]::UTF8.GetBytes('tampered')}
Expect-Rejection 'invalid UTF8 candidate' {param($f)$f.Candidates['ReportCertificate.xaml.cs']=[byte[]]@(255,254,255)}
Expect-Rejection 'unexpected candidate' {param($f)$f.Candidates['Extra.cs']=[byte[]]@(1)}
Expect-Rejection 'existing new helper' {param($f)[void][IO.Directory]::CreateDirectory((Join-Path $f.Project 'Services'));[IO.File]::WriteAllText((Join-Path $f.Project $additions[0]),'existing')}
Expect-Rejection 'wrong count' {param($f)$f.Plan.InputCount=356}
Expect-Rejection 'wrong version' {param($f)$f.Plan.Version='2026.10.6.303'}
Expect-Rejection 'wrong archive' {param($f)$f.Plan.ArchiveSha256=('0'*64)}
Expect-Rejection 'unexpected scope' {param($f)
 $old=Join-Path $f.Project $f.Plan.Inputs[0].Path
 Move-Item -LiteralPath $old -Destination (Join-Path $f.Project 'Unreviewed.cs')
 $f.Plan.Inputs[0].Path='Unreviewed.cs'
}
Expect-Rejection 'duplicate metadata' {param($f)$f.Plan.Inputs[20].Path=$f.Plan.Inputs[19].Path.ToUpperInvariant()}
Expect-Rejection 'traversal' {param($f)$f.Plan.Inputs[0].Path='../escaped.cs'}
Expect-Rejection 'absolute path' {param($f)$f.Plan.Inputs[0].Path='C:/escaped.cs'}
Expect-Rejection 'dot alias' {param($f)$f.Plan.Inputs[0].Path='./CultureMediaPreparation.xaml.Part2.cs'}
Expect-Rejection 'undeclared change' {param($f)$f.Plan.Inputs[0].Sha256=$f.Plan.Inputs[0].OriginalNormalizedSha256}
foreach($name in @('Extra.cs','Extra.targets','Extra.props','Extra.rsp','NuGet.Config')){
 $extraName=$name;Expect-Rejection "unlisted build input $name" {param($f)[IO.File]::WriteAllText((Join-Path $f.Project $extraName),'extra')}
}
Expect-Rejection 'reparse source ancestor' {param($f)
 $outside=Join-Path $f.Root 'outside';[void][IO.Directory]::CreateDirectory($outside)
 $kind=if($env:OS -eq 'Windows_NT'){'Junction'}else{'SymbolicLink'}
 $null=New-Item -ItemType $kind -Path (Join-Path $f.Project 'Services') -Target $outside
}
Expect-Rejection 'unlisted reparse directory cannot hide build inputs' {param($f)
 $outside=Join-Path $f.Root 'outside';[void][IO.Directory]::CreateDirectory($outside)
 $kind=if($env:OS -eq 'Windows_NT'){'Junction'}else{'SymbolicLink'}
 $null=New-Item -ItemType $kind -Path (Join-Path $f.Project 'Additional') -Target $outside
}
Expect-Rejection 'unlisted shared signer compiler input' {param($f)
 [IO.File]::WriteAllText((Join-Path $f.Project 'tools/PharmaLIMS.TrustedSigning/Extra.cs'),'extra')
}
Expect-Rejection 'unlisted signer host compiler input' {param($f)
 $directory=Join-Path $f.Project 'tools/PharmaLIMS.TrustedSigning.Host'
 [void][IO.Directory]::CreateDirectory($directory)
 [IO.File]::WriteAllText((Join-Path $directory 'Extra.cs'),'extra')
}
if($PinnedPlanPath){[byte[]]$realPlan=[IO.File]::ReadAllBytes($PinnedPlanPath)}
else{$repo=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path;[byte[]]$realPlan=Get-Eq001RepositoryBytes $repo '1935ec8ae4a52c3a0cd3cb63d08e2bd7952ed39f' 'PharmaLIMS/scripts/site-cohort-inputs/AMEEN-cohort.json'}
$verified=Get-Eq001VerifiedCohortPlan $realPlan
Assert (@($verified.Inputs).Count -eq 357) 'Actual1935 plan verifies.'
$rejected=$false;try{$null=Get-Eq001VerifiedCohortPlan ([Text.Encoding]::UTF8.GetBytes('{}'))}catch{$rejected=$true}
Assert $rejected 'Arbitrary plan must not redefine trusted original hashes.'
Write-Host 'PASS immutable1935 plan pin and arbitrary metadata rejection.'
$repoFixture=Join-Path ([IO.Path]::GetTempPath()) ('eq001-git-bytes-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory((Join-Path $repoFixture 'PharmaLIMS'))
try{
 [byte[]]$raw=[byte[]](@(239,187,191)+[Text.Encoding]::UTF8.GetBytes('value='+[char]0x0645+[char]13+[char]10))
 [IO.File]::WriteAllBytes((Join-Path $repoFixture 'PharmaLIMS/Fixture.cs.txt'),$raw)
 & git -C $repoFixture init --quiet
 if($LASTEXITCODE -ne 0){throw 'Fixture init failed.'}
 & git -C $repoFixture -c core.autocrlf=false add -- PharmaLIMS/Fixture.cs.txt
 & git -C $repoFixture -c user.name=EQ001 -c user.email=eq001@example.invalid commit --quiet -m fixture
 if($LASTEXITCODE -ne 0){throw 'Fixture commit failed.'}
 $commit=(& git -C $repoFixture rev-parse HEAD).Trim();$oldEncoding=$OutputEncoding
 try{$OutputEncoding=[Text.Encoding]::ASCII;[byte[]]$actual=Get-Eq001RepositoryBytes $repoFixture $commit 'PharmaLIMS/Fixture.cs.txt';Assert ((Hash $actual) -ceq (Hash $raw)) 'Git raw UTF8,BOM,CRLF under ASCII.'}
 finally{$OutputEncoding=$oldEncoding}
 foreach($bad in @('PharmaLIMS/../escape.cs','PharmaLIMS/Absent.cs')){
  $rejected=$false;try{$null=Get-Eq001RepositoryBytes $repoFixture $commit $bad}catch{$rejected=$true};Assert $rejected 'Unsafe/missing object rejects.'
 }
 Write-Host 'PASS raw Git bytes and unsafe/missing object rejection.'
}finally{Remove-Item -LiteralPath $repoFixture -Recurse -Force}
Write-Host 'PASS read-only transition preview tests. No AMEEN installation or UAT acceptance.'
if ($env:COMPUTERNAME -ine 'AMEEN') {
 $rejected=$false
 try { & $ToolPath } catch { $rejected=$true }
 Assert $rejected 'Actual entrypoint must reject a different computer before original-source access.'
 Write-Host 'PASS actual entrypoint rejects offAMEEN.'
}
$rejected=$false
try { & $ToolPath -Apply } catch { $rejected=$true }
Assert $rejected 'There is no Apply parameter or source mutation capability.'
Write-Host 'PASS Apply is unavailable.'

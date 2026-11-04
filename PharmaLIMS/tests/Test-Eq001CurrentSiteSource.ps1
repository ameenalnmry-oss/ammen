[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$InstallerPath,[string]$NativeSourcePath)
$ErrorActionPreference='Stop'
function Assert([bool]$Condition,[string]$Message){if(-not $Condition){throw "ASSERT: $Message"}}
$tokens=$null;$parseErrors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($InstallerPath,[ref]$tokens,[ref]$parseErrors)
Assert (@($parseErrors).Count -eq 0) 'Installer syntax.'
foreach($statement in $ast.EndBlock.Statements){
 if($statement -is [Management.Automation.Language.FunctionDefinitionAst]){. ([scriptblock]::Create($statement.Extent.Text))}
}
Assert ([bool](Get-Command Get-CurrentSiteSourceManifest -ErrorAction SilentlyContinue)) 'Current-site source mode is missing.'
function Sha([byte[]]$Bytes){$s=[Security.Cryptography.SHA256]::Create();try{[BitConverter]::ToString($s.ComputeHash($Bytes)).Replace('-','').ToLowerInvariant()}finally{$s.Dispose()}}
function Norm([byte[]]$Bytes){Sha ([Text.Encoding]::UTF8.GetBytes([Text.Encoding]::UTF8.GetString($Bytes)+[char]10))}
$updatePaths=@('CultureMediaPreparation.xaml.Part2.cs','CultureMediaPreparation.xaml.Part3.cs','CultureMediaPreparation.xaml.cs',
'DatabaseHelper.EnvironmentalMonitoring.cs','DatabaseHelper.QualityEvents.cs','EMResultsEntry.xaml.Part3.cs','EMResultsEntry.xaml.cs',
'PRMQualityEventInvestigation.xaml.cs','ProductionRawMaterialResults.xaml.cs','QualityEventInvestigation.xaml.Part2.cs',
'QualityEventInvestigation.xaml.cs','ReportCertificate.xaml.cs','SystemPreflight.xaml.cs','DatabaseHelper.EmSourcePlan.cs',
'tools/PharmaLIMS.TrustedSigning/TrustedCallerMap.cs','tools/PharmaLIMS.TrustedSigning/WindowsTrustedPipeServer.cs')
$addPaths=@('Services/MediaPreparationAmendmentSnapshot.cs','Infrastructure/QualityEventInvestigationConcurrencyGuard.cs','tools/PharmaLIMS.TrustedSigning/TrustedPipeConnectionPool.cs')
function New-Fixture{
 $root=Join-Path ([IO.Path]::GetTempPath()) ('eq001-source-mode-'+[guid]::NewGuid().ToString('N'))
 [void][IO.Directory]::CreateDirectory($root)
 $f=[pscustomobject]@{Root=$root;Inputs=[Collections.Generic.List[object]]::new();Scope=[Collections.Generic.List[object]]::new();Candidates=@{};Sidecars=[Collections.Generic.List[object]]::new();Plan=$null}
 foreach($name in @($updatePaths+$addPaths)){
  [byte[]]$before=[Text.Encoding]::UTF8.GetBytes('before:'+ $name);[byte[]]$after=[Text.Encoding]::UTF8.GetBytes('after:'+ $name+[char]10)
  $new=$name -in $addPaths;$p=Join-Path $root $name;[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($p))
  if(-not $new){[IO.File]::WriteAllBytes($p,$before)}
  $artifact='scripts/site-overlays/'+$name.Replace('/','_')+'.txt';$f.Candidates[$artifact]=$after
  $f.Inputs.Add([pscustomobject]@{Path=$name;CurrentSiteRawSha256=$(if($new){$null}else{Sha $before});CurrentSiteNormalizedSha256=$(if($new){$null}else{Norm $before});CurrentSiteExpectedAbsent=$new;Sha256=(Sha $after)})
  $f.Scope.Add([pscustomobject]@{Path=$name;Operation=$(if($new){'add'}else{'update'});ExpectedCurrentRawSha256=$(if($new){$null}else{Sha $before});CandidateRawSha256=(Sha $after);CandidateNormalizedSha256=(Sha $after);Artifact=$artifact})
 }
 for($i=0;$i -lt 351;$i++){
  $name="Other/Keep$i.cs";$p=Join-Path $root $name;[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($p));$b=[Text.Encoding]::UTF8.GetBytes("retained:$i");[IO.File]::WriteAllBytes($p,$b)
  $f.Inputs.Add([pscustomobject]@{Path=$name;CurrentSiteRawSha256=(Sha $b);CurrentSiteNormalizedSha256=(Norm $b);CurrentSiteExpectedAbsent=$false;Sha256=(Norm $b)})
 }
 for($i=0;$i -lt 10;$i++){
  $name="scripts/review$i.sql";$p=Join-Path $root $name;[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($p));$b=[Text.Encoding]::UTF8.GetBytes("THROW 57198,'held',1;");[IO.File]::WriteAllBytes($p,$b)
  $f.Sidecars.Add([pscustomobject]@{Path=$name;ExactSha256=(Sha $b);Execute=$false})
 }
 $f.Plan=[pscustomobject]@{InputCount=370;OriginalBuildInputCount=231;CurrentSiteBuildInputCount=243;MigrationCount=103;Version='2026.10.19.306';ArchiveSha256='b9244b38b1d4cb72dbd0bacf913fb7e92e07802626057e5169a5c58d71b6309a';Inputs=$f.Inputs.ToArray();SourceUpdateScope=$f.Scope.ToArray();AdditionalReviewInputs=$f.Sidecars.ToArray()}
 return $f
}
function Snapshot([string]$Root){[string]::Join('|',@(Get-ChildItem -LiteralPath $Root -Recurse -File -Force|Sort-Object FullName|ForEach-Object{$_.FullName+':'+(Sha ([IO.File]::ReadAllBytes($_.FullName)))}))}
function Manifest($Fixture){
 $read={param($path) return ,$Fixture.Candidates[$path]}.GetNewClosure()
 Get-CurrentSiteSourceManifest -Plan $Fixture.Plan -ProjectPath $Fixture.Root -ReadCandidate $read
}
function Reject([string]$Name,[scriptblock]$Modify){
 $f=New-Fixture
 try{& $Modify $f;$before=Snapshot $f.Root;$rejected=$false;try{$null=Manifest $f}catch{$rejected=$true}
 Assert $rejected "$Name rejected.";Assert ((Snapshot $f.Root) -ceq $before) "$Name writes no files.";Write-Host "PASS current-source rejection/no writes: $Name"
 }finally{Remove-Item -LiteralPath $f.Root -Recurse -Force}
}
$f=New-Fixture
try{
 $before=Snapshot $f.Root;$m=Manifest $f
 Assert (@($m.Files).Count -eq 19 -and @($m.PreservedInputs).Count -eq 361) '19 source changes and361 retained inputs.'
 Assert (@($m.Files|Where-Object ExpectedRawSha256).Count -eq 16) 'All16 before-byte witnesses.'
 foreach($row in $m.Files){Assert (-not $row.Remove) 'No source deletion.';Assert ($row.SourcePath.StartsWith('PharmaLIMS/scripts/site-overlays/')) 'Inert reviewed source only.'}
 Assert ((Snapshot $f.Root) -ceq $before) 'Manifest creation has no source writes.'
 Write-Host 'PASS valid current-source manifest19/361 and unchanged files.'
}finally{Remove-Item -LiteralPath $f.Root -Recurse -Force}
Reject 'selected file drift' {param($f)[IO.File]::WriteAllText((Join-Path $f.Root 'SystemPreflight.xaml.cs'),'different')}
Reject 'retained file drift' {param($f)[IO.File]::WriteAllText((Join-Path $f.Root 'Other/Keep0.cs'),'different')}
Reject 'review SQL drift' {param($f)[IO.File]::WriteAllText((Join-Path $f.Root 'scripts/review0.sql'),'changed')}
Reject 'missing current file' {param($f)Remove-Item -LiteralPath (Join-Path $f.Root 'Other/Keep0.cs')}
Reject 'new helper already present' {param($f)[IO.File]::WriteAllText((Join-Path $f.Root $addPaths[0]),'existing')}
Reject 'candidate changed' {param($f)$f.Candidates[$f.Scope[0].Artifact]=[byte[]]@(1)}
Reject 'scope broadened' {param($f)$f.Scope[0].Path='Other/Keep0.cs'}
Reject 'scope incomplete' {param($f)$f.Plan.SourceUpdateScope=@($f.Plan.SourceUpdateScope|Select-Object -Skip 1)}
Reject 'case alias in metadata' {param($f)$f.Plan.Inputs[20].Path=$f.Plan.Inputs[19].Path.ToUpperInvariant()}
Reject 'traversal' {param($f)$f.Plan.Inputs[20].Path='../escape.cs'}
Reject 'unlisted compiler source' {param($f)[IO.File]::WriteAllText((Join-Path $f.Root 'Extra.cs'),'extra')}
Reject 'unlisted shared source' {param($f)[IO.File]::WriteAllText((Join-Path $f.Root 'tools/PharmaLIMS.TrustedSigning/Extra.cs'),'extra')}
Reject 'unlisted SQL' {param($f)[IO.File]::WriteAllText((Join-Path $f.Root 'scripts/Extra.sql'),'extra')}
Reject 'wrong version' {param($f)$f.Plan.Version='2026.10.6.303'}
Reject 'SQL marked executable' {param($f)$f.Plan.AdditionalReviewInputs[0].Execute=$true}
Reject 'directory reparse' {param($f)
 $outside=Join-Path $f.Root 'outside';[void][IO.Directory]::CreateDirectory($outside)
 $kind=if($env:OS -eq 'Windows_NT'){'Junction'}else{'SymbolicLink'}
 $null=New-Item -ItemType $kind -Path (Join-Path $f.Root 'Injected') -Target $outside
}
Write-Host 'PASS current-site source precondition tests; no AMEEN application or database changed.'

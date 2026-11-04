[CmdletBinding()]
param([string]$InstallerPath,[string]$InstallerText,[switch]$NativeFiles,[string]$NativeSourcePath)
$ErrorActionPreference='Stop'
function Assert([bool]$Value,[string]$Message){if(-not $Value){throw ('ASSERT: '+$Message)}}
$text=if($InstallerText){$InstallerText}else{[IO.File]::ReadAllText($InstallerPath)}
$tokens=$null;$errors=$null;$tree=[Management.Automation.Language.Parser]::ParseInput($text,[ref]$tokens,[ref]$errors)
Assert (@($errors).Count -eq 0) 'Installer syntax.'
foreach($statement in $tree.EndBlock.Statements){if($statement -is [Management.Automation.Language.FunctionDefinitionAst]){. ([scriptblock]::Create($statement.Extent.Text))}}
$anchor='$acl = Get-Acl -LiteralPath $privateDirectory'
$begin=$text.IndexOf($anchor)+$anchor.Length;$end=$text.IndexOf('foreach ($process in @(',$begin)
Assert ($begin -ge $anchor.Length -and $end -gt $begin) 'Actual root validation block present.'
$rootGate=[scriptblock]::Create($text.Substring($begin,$end-$begin))
if($env:OS -ne 'Windows_NT'){Write-Host 'SKIP actual Windows ACL descriptors; parser PASS.';return}
$trusted=@('S-1-5-18','S-1-5-32-544','S-1-5-21-3887410152-3473494482-1737934645-1009')
$actors=@('S-1-5-21-3887410152-3473494482-1737934645-1012','S-1-5-21-3887410152-3473494482-1737934645-1013','S-1-5-21-3887410152-3473494482-1737934645-1014')
$read=[Security.AccessControl.FileSystemRights]([int][Security.AccessControl.FileSystemRights]::ReadAndExecute -bor [int][Security.AccessControl.FileSystemRights]::Synchronize)
function New-Descriptor([bool]$File=$false){
 $d=if($File){[Security.AccessControl.FileSecurity]::new()}else{[Security.AccessControl.DirectorySecurity]::new()}
 $d.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'));$d.SetAccessRuleProtection($true,$false)
 foreach($sid in $trusted){
  $inherit=if($File){[Security.AccessControl.InheritanceFlags]::None}else{[Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'}
  $d.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),[Security.AccessControl.FileSystemRights]::FullControl,$inherit,[Security.AccessControl.PropagationFlags]::None,[Security.AccessControl.AccessControlType]::Allow))
 }
 return $d
}
function Add-Actor($Descriptor,[string]$Sid,[Security.AccessControl.FileSystemRights]$Rights=$read,
 [Security.AccessControl.InheritanceFlags]$Inheritance='None',[Security.AccessControl.PropagationFlags]$Propagation='None'){
 $Descriptor.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($Sid),$Rights,$Inheritance,$Propagation,[Security.AccessControl.AccessControlType]::Allow))
}
function Invoke-Gate($Descriptor,[bool]$Legacy=$false,[bool]$File=$false){
 if($File){Assert-CurrentSiteRollbackAcl -Acl $Descriptor;return}
 $acl=$Descriptor;$allowed=$trusted;$CurrentSiteOnly=-not $Legacy;& $rootGate
}
function Case([string]$Name,$Descriptor,[bool]$Reject=$false,[bool]$Legacy=$false,[bool]$File=$false){
 $before=[Convert]::ToBase64String($Descriptor.GetSecurityDescriptorBinaryForm());$failure=$null
 try{Invoke-Gate $Descriptor $Legacy $File}catch{$failure=$_}
 Assert (($null -ne $failure) -eq $Reject) ($Name+': unexpected acceptance/rejection. '+$(if($failure){$failure.Exception.Message}))
 Assert ([Convert]::ToBase64String($Descriptor.GetSecurityDescriptorBinaryForm()) -ceq $before) ($Name+': policy must not modify ACL.')
 Write-Host ('PASS rollback ACL: '+$Name)
}
Case 'original private root accepted' (New-Descriptor)
$site=New-Descriptor;foreach($sid in $actors){Add-Actor $site $sid}
Case 'exact three explicit non-inheriting site traversal rules accepted' $site
Case 'legacy root rule remains strict' $site -Reject $true -Legacy $true
foreach($sid in $actors){
 foreach($right in @('Write','Delete','DeleteSubdirectoriesAndFiles','ChangePermissions','TakeOwnership')){
  $d=New-Descriptor;Add-Actor $d $sid ([Security.AccessControl.FileSystemRights]([int]$read -bor [int][Security.AccessControl.FileSystemRights]$right));Case ($sid+' '+$right+' rejected') $d -Reject $true
 }
 foreach($flag in @('ObjectInherit','ContainerInherit','ObjectInherit,ContainerInherit')){
  $d=New-Descriptor;Add-Actor $d $sid -Inheritance $flag;Case ($sid+' '+$flag+' rejected') $d -Reject $true
 }
 $d=New-Descriptor;Add-Actor $d $sid -Inheritance 'ObjectInherit' -Propagation 'InheritOnly';Case ($sid+' inherit-only rejected') $d -Reject $true
 $d=New-Descriptor;Add-Actor $d $sid
 $sddl=$d.GetSecurityDescriptorSddlForm([Security.AccessControl.AccessControlSections]'Access,Owner')
 $sddl=$sddl.Replace('A;;0x1200a9;;;'+$sid,'A;ID;0x1200a9;;;'+$sid)
 $d.SetSecurityDescriptorSddlForm($sddl);Assert (@($d.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])|Where-Object{$_.IdentityReference.Value -eq $sid -and $_.IsInherited}).Count -eq 1) 'Inherited fixture.'
 Case ($sid+' inherited rule rejected') $d -Reject $true
 $f=New-Descriptor $true;Add-Actor $f $sid;Case ($sid+' file-level reader rejected') $f -Reject $true -File $true
}
$unknown=New-Descriptor;Add-Actor $unknown 'S-1-5-21-3887410152-3473494482-1737934645-1999';Case 'unknown root reader rejected' $unknown -Reject $true
$owner=New-Descriptor;$owner.SetOwner([Security.Principal.SecurityIdentifier]::new($actors[0]));Case 'reader owner rejected' $owner -Reject $true
$nullDacl=New-Descriptor;$nullDacl.SetSecurityDescriptorSddlForm('O:BAG:BAD:NO_ACCESS_CONTROL');Case 'null DACL rejected' $nullDacl -Reject $true
Case 'private rollback file accepted' (New-Descriptor $true) -File $true
if(-not $NativeFiles){Write-Host 'PASS Windows descriptor/root/file ACL gates. No project, service or live ACL changed.';return}
# Owned temporary ACL fixture on the hosted Windows runner only.
$root=Join-Path ([IO.Path]::GetTempPath()) ('eq001-acl-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($root)
try{
 Set-Acl -LiteralPath $root -AclObject $site
 $current=Get-Acl -LiteralPath $root
 Assert-CurrentSiteRollbackAcl -Acl $current -AllowSiteDirectoryTraversal
 $backupPath=Join-Path $root 'rollback.json'
 $stream=[IO.FileStream]::new($backupPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
 try{
  Assert-CurrentSiteRollbackAcl -Acl (Get-Acl -LiteralPath $backupPath)
  $inherited=@((Get-Acl -LiteralPath $backupPath).GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])|Where-Object{$_.IdentityReference.Value -in $actors})
  Assert ($inherited.Count -eq 0) 'Directory-only site actors cannot inherit file read permission.'
  $bytes=[Text.Encoding]::UTF8.GetBytes('owned rollback fixture');$stream.Write($bytes,0,$bytes.Length);$stream.Flush($true)
 }finally{$stream.Dispose()}
 Assert ([IO.File]::ReadAllText($backupPath) -ceq 'owned rollback fixture') 'Private rollback payload roundtrip.'
 Write-Host 'PASS real Windows child ACL inheritance and private readback while rollback stream is exclusive.'
 if(-not ('Eq001SiteSourceFile' -as [type])){Add-Type -TypeDefinition ([IO.File]::ReadAllText($NativeSourcePath))}
 $target=Join-Path $root 'owned-source.cs';[byte[]]$before=[Text.Encoding]::UTF8.GetBytes('original owned source');[byte[]]$after=[Text.Encoding]::UTF8.GetBytes('candidate owned source')
 [IO.File]::WriteAllBytes($target,$before)
 $change=[pscustomobject]@{Path='owned-source.cs';Target=$target;WasPresent=$true;Remove=$false;BeforeBytes=$before;AfterBytes=$after;BeforeSha256=(Get-ByteHash $before);AfterSha256=(Get-ByteHash $after);Handle=$null}
 $script:rejectedBackupPath=$null;$rejected=$false
 try{
  $null=Invoke-GuardedSourceChanges -Changes @($change) -BackupDirectory $root -Commit ('a'*40) -CurrentSiteRecord -ValidateRollbackAcl {
   param($path)
   $script:rejectedBackupPath=$path
   Assert (([IO.FileInfo]::new($path)).Length -eq 0) 'No rollback bytes before ACL verification.'
   $unsafe=Get-Acl -LiteralPath $path;Add-Actor $unsafe $actors[0];Set-Acl -LiteralPath $path -AclObject $unsafe
   Assert-CurrentSiteRollbackAcl -Acl (Get-Acl -LiteralPath $path)
  }
 }catch{
  if($_.Exception.Message -notlike 'Source rollback ACL grants unexpected access:*'){throw}
  $rejected=$true
 }
 Assert $rejected 'Unsafe child ACL rejects actual source transaction.'
 Assert ($null -ne $script:rejectedBackupPath -and ([IO.FileInfo]::new($script:rejectedBackupPath)).Length -eq 0) 'Rejected ACL never receives rollback source payload.'
 Assert ((Get-ByteHash ([IO.File]::ReadAllBytes($target))) -ceq (Get-ByteHash $before)) 'Source unchanged after rejected rollback ACL.'
 Write-Host 'PASS actual source transaction rejects unsafe child ACL before any rollback payload or source write.'
}finally{Remove-Item -LiteralPath $root -Recurse -Force}

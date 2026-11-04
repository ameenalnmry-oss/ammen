[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$InstallerPath,[Parameter(Mandatory=$true)][string]$NativeSourcePath)
$ErrorActionPreference='Stop'
function Assert([bool]$Condition,[string]$Message){if(-not $Condition){throw "ASSERT: $Message"}}
$tokens=$null;$errors=$null;$ast=[Management.Automation.Language.Parser]::ParseFile($InstallerPath,[ref]$tokens,[ref]$errors)
Assert (@($errors).Count -eq 0) 'Installer syntax.'
foreach($statement in $ast.EndBlock.Statements){if($statement -is [Management.Automation.Language.FunctionDefinitionAst]){. ([scriptblock]::Create($statement.Extent.Text))}}
Add-Type -TypeDefinition ([IO.File]::ReadAllText($NativeSourcePath))
Assert ([bool][Eq001SiteSourceFile].GetMethod('OpenQualified')) 'Qualified native source handles are missing.'
Assert ([bool](Get-Command Invoke-GuardedSourceChanges -ErrorAction SilentlyContinue)) 'Source transaction function is missing.'
if($env:OS -ne 'Windows_NT'){Write-Host 'PASS transaction/native API compilation; real file-handle tests require Windows.';return}
$root=Join-Path ([IO.Path]::GetTempPath()) ('eq001-transaction-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($root)
function New-Change([string]$Path,[bool]$New=$false){
 [byte[]]$before=[Text.Encoding]::UTF8.GetBytes('original:'+ $Path);[byte[]]$after=[Text.Encoding]::UTF8.GetBytes('candidate:'+ $Path)
 $target=Join-Path $root $Path;[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
 if(-not $New){[IO.File]::WriteAllBytes($target,$before)}else{$before=[byte[]]@()}
 [pscustomobject]@{Path=$Path;Target=$target;WasPresent=(-not $New);Remove=$false;BeforeBytes=$before;AfterBytes=$after;BeforeSha256=(Get-ByteHash $before);AfterSha256=(Get-ByteHash $after);Handle=$null}
}
function Apply($Changes,[bool]$Current=$true,[scriptblock]$Validate={}){
 Invoke-GuardedSourceChanges -Changes $Changes -BackupDirectory $root -Commit ('a'*40) -CurrentSiteRecord:$Current -WaterAnalysisRecord:(-not $Current) -ValidatePreserved $Validate
}
function Reject([scriptblock]$Action,[string]$Message){$no=$false;try{& $Action}catch{$no=$true};Assert $no $Message}
try{
 $native=Join-Path $root 'native.cs';[IO.File]::WriteAllText($native,'native')
 $h=[Eq001SiteSourceFile]::OpenQualified($native,$false)
 try{Reject {[IO.File]::WriteAllText($native,'bad')} 'Exclusive handle blocks second writer.';$h.WriteAll([byte[]]@(1,2));Assert ((Get-ByteHash ($h.ReadAll())) -eq (Get-ByteHash ([byte[]]@(1,2)))) 'Locked readback.'}finally{$h.Dispose()}
 $alias=Join-Path $root 'hardlink.cs';$null=New-Item -ItemType HardLink -Path $alias -Target $native
 Reject {$bad=[Eq001SiteSourceFile]::OpenQualified($native,$false);$bad.Dispose()} 'Hard-linked source rejected.'
 Remove-Item -LiteralPath $alias
 $outside=Join-Path $root 'outside';[void][IO.Directory]::CreateDirectory($outside);[IO.File]::WriteAllText((Join-Path $outside 'linked.cs'),'untouched')
 $junction=Join-Path $root 'junction';$null=New-Item -ItemType Junction -Path $junction -Target $outside
 Reject {$bad=[Eq001SiteSourceFile]::OpenQualified((Join-Path $junction 'linked.cs'),$false);$bad.Dispose()} 'Aliased parent rejected.'
 Reject {$bad=[Eq001SiteSourceFile]::OpenQualified((Join-Path $junction 'new-linked.cs'),$true);$bad.Dispose()} 'New helper through aliased parent rejected.'
 Assert (-not (Test-Path -LiteralPath (Join-Path $outside 'new-linked.cs'))) 'Rejected creation through alias leaves no helper.'
 [IO.Directory]::Delete($junction)
 Write-Host 'PASS native exclusive handles, readback, hardlink and parent-junction rejection.'
 $first=New-Change 'first.cs';$new=New-Change 'new.cs' $true
 $recordPath=Apply @($first,$new);$record=Get-Content -LiteralPath $recordPath -Raw|ConvertFrom-Json
 Assert ($record.Schema -eq 2 -and $record.SourceCommit -eq ('a'*40) -and @($record.Files).Count -eq 2) 'Separate schema2 rollback.'
 Assert ($record.ParentSourceReceiptSha256 -eq 'E71C9820185DACF70A988FD30D2F2AF6814ECBBAE9F945D6576F84FF450F81A2') 'Parent receipt identity retained.'
 Assert ((Get-ByteHash ([IO.File]::ReadAllBytes($first.Target))) -eq $first.AfterSha256) 'Existing source written.'
 Assert ((Get-ByteHash ([IO.File]::ReadAllBytes($new.Target))) -eq $new.AfterSha256) 'New helper written.'
 Assert ([Convert]::ToBase64String($first.BeforeBytes) -ceq $record.Files[0].OriginalBase64) 'Raw rollback bytes.'
 Write-Host 'PASS current-site success and durable source-only rollback provenance.'
 $legacy=New-Change 'legacy.cs';$legacyRecord=Get-Content -LiteralPath (Apply @($legacy) $false) -Raw|ConvertFrom-Json
 Assert ($legacyRecord.Schema -eq 1 -and -not $legacyRecord.PSObject.Properties['ParentSourceReceiptSha256']) 'Legacy water schema unchanged.'
 $drift=New-Change 'drift.cs';[IO.File]::WriteAllText($drift.Target,'newer work')
 Reject {Apply @($drift)} 'Changed source rejected before mutation.'
 Assert ([IO.File]::ReadAllText($drift.Target) -ceq 'newer work') 'Newer source preserved.'
 $rollbackFirst=New-Change 'rollback-first.cs';$rollbackNew=New-Change 'rollback-new.cs' $true
 $keep=Join-Path $root 'preserved.cs';[IO.File]::WriteAllText($keep,'retained')
 $keepWitness=[pscustomobject]@{Path='preserved.cs';ExpectedRawSha256=(Get-ByteHash ([IO.File]::ReadAllBytes($keep)))}
 $script:validationCalls=0
 Reject {Apply @($rollbackFirst,$rollbackNew) $true {
     $script:validationCalls++;if($script:validationCalls -eq 2){[IO.File]::WriteAllText($keep,'other writer newer data')}
     Test-CurrentSitePreservedInputs @($keepWitness) $root
 }} 'Actual retained-byte drift after source write rolls back transaction.'
 Assert ((Get-ByteHash ([IO.File]::ReadAllBytes($rollbackFirst.Target))) -eq $rollbackFirst.BeforeSha256) 'Existing source restored after post-write failure.'
 Assert (-not (Test-Path -LiteralPath $rollbackNew.Target)) 'New source removed after post-write failure.'
 Assert ([IO.File]::ReadAllText($keep) -ceq 'other writer newer data') 'Other writer data preserved by rollback.'
 $inventoryFirst=New-Change 'inventory-first.cs';$inventoryNew=New-Change 'inventory-new.cs' $true
 $known=@(Get-ChildItem -LiteralPath $root -Recurse -File|ForEach-Object{$_.FullName.Substring($root.Length+1).Replace('\','/')})+@('inventory-new.cs')
 $script:inventoryCalls=0
 Reject {Apply @($inventoryFirst,$inventoryNew) $true {
     $script:inventoryCalls++;if($script:inventoryCalls -eq 2){[IO.File]::WriteAllText((Join-Path $root 'ConcurrentExtra.cs'),'other writer source')}
     Test-CurrentSiteInputInventory $root $known
 }} 'Actual post-manifest compiler-input addition rolls back transaction.'
 Assert ((Get-ByteHash ([IO.File]::ReadAllBytes($inventoryFirst.Target))) -eq $inventoryFirst.BeforeSha256) 'Prior bytes restored after input graph change.'
 Assert (-not (Test-Path -LiteralPath $inventoryNew.Target)) 'New helper removed after input graph change.'
 Assert ([IO.File]::ReadAllText((Join-Path $root 'ConcurrentExtra.cs')) -ceq 'other writer source') 'Concurrent new source preserved.'
 $partial=New-Change 'partial.cs';$blocked=New-Change 'blocked/new.cs' $true
 $folder=Split-Path -Parent $blocked.Target;$originalAcl=Get-Acl -LiteralPath $folder
 $denyAcl=Get-Acl -LiteralPath $folder;$sid=[Security.Principal.WindowsIdentity]::GetCurrent().User
 $deny=[Security.AccessControl.FileSystemAccessRule]::new($sid,[Security.AccessControl.FileSystemRights]::CreateFiles,[Security.AccessControl.AccessControlType]::Deny)
 $denyAcl.AddAccessRule($deny);Set-Acl -LiteralPath $folder -AclObject $denyAcl
 try{Reject {Apply @($partial,$blocked)} 'Actual new-file creation denial rolls back prior write.'}finally{Set-Acl -LiteralPath $folder -AclObject $originalAcl}
 Assert ((Get-ByteHash ([IO.File]::ReadAllBytes($partial.Target))) -eq $partial.BeforeSha256) 'Prior source restored after actual I/O failure.'
 Assert (-not (Test-Path -LiteralPath $blocked.Target)) 'Denied helper not created.'
 Write-Host 'PASS real I/O rollback, retained-input failure, before-drift rejection and unchanged legacy schema.'
}finally{Remove-Item -LiteralPath $root -Recurse -Force}

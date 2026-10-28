param([Parameter(Mandatory=$true)][string]$StagePath,[Parameter(Mandatory=$true)][string]$OutputPath)
$ErrorActionPreference='Stop'
$tokens=$null; $errors=$null
$tree=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot '../scripts/Complete-Eq001WaterSql.ps1'),[ref]$tokens,[ref]$errors)
if ($errors) { throw ($errors | Out-String) }
$function=@($tree.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'New-WaterCompletionBatch'},$false))
if ($function.Count -ne 1) { throw 'Expected one pure completion batch builder.' }
. ([scriptblock]::Create($function[0].Extent.Text))
# Never execute the site completion body; only the actual pure batch builder.
[IO.File]::WriteAllText($OutputPath,(New-WaterCompletionBatch ([IO.File]::ReadAllText($StagePath))),[Text.UTF8Encoding]::new($false))

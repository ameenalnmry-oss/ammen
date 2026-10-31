[CmdletBinding()]
param([string]$SqlSettings)
$ErrorActionPreference='Stop'
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$previousArtifacts=$env:ArtifactsPath
$previousSettings=$env:EQ001_SQL_TEST_SETTINGS
$artifacts=Join-Path ([IO.Path]::GetTempPath()) ('pharmalims-workflow-regression-'+[guid]::NewGuid().ToString('N'))
try {
    $env:ArtifactsPath=$artifacts
    foreach($name in @('WaterWorkflowDomain','WaterWorkflowHost','WaterWorkflowHostInput','PrmRegistration')) {
        & dotnet run --project (Join-Path $project "tests/PharmaLIMS.${name}Regression/PharmaLIMS.${name}Regression.csproj") --configuration Release "-p:ArtifactsPath=$artifacts"
        if($LASTEXITCODE -ne 0) { throw "$name boundary regression failed." }
    }
    if($SqlSettings) {
        $env:EQ001_SQL_TEST_SETTINGS=[IO.Path]::GetFullPath($SqlSettings)
        foreach($name in @('WaterAnalysisSql','PrmRegistrationSql')) {
            & dotnet run --project (Join-Path $project "tests/PharmaLIMS.${name}Regression/PharmaLIMS.${name}Regression.csproj") --configuration Release "-p:ArtifactsPath=$artifacts" -- $project
            if($LASTEXITCODE -ne 0) { throw "$name restricted SQL regression failed." }
        }
    }
}
finally {
    if($null -eq $previousArtifacts) { Remove-Item Env:ArtifactsPath -ErrorAction SilentlyContinue }
    else { $env:ArtifactsPath=$previousArtifacts }
    if($null -eq $previousSettings) { Remove-Item Env:EQ001_SQL_TEST_SETTINGS -ErrorAction SilentlyContinue }
    else { $env:EQ001_SQL_TEST_SETTINGS=$previousSettings }
    if(Test-Path $artifacts) { Remove-Item -LiteralPath $artifacts -Recurse -Force }
}

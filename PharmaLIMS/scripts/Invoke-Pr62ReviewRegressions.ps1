[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$SqlSettings)
$ErrorActionPreference='Stop'
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$previousSettings=$env:EQ001_SQL_TEST_SETTINGS
try {
    $env:EQ001_SQL_TEST_SETTINGS=[IO.Path]::GetFullPath($SqlSettings)
    foreach ($name in @('InvestigationConcurrency','PrmReview','MediaAmendment','EmLifecycle')) {
        $test=Join-Path $project "tests/PharmaLIMS.${name}Regression/PharmaLIMS.${name}Regression.csproj"
        & dotnet run --project $test --configuration Release -- $project
        if ($LASTEXITCODE -ne 0) { throw "PR62 $name disposable SQL regression failed." }
    }
}
finally {
    if ($null -eq $previousSettings) { Remove-Item Env:\EQ001_SQL_TEST_SETTINGS -ErrorAction SilentlyContinue }
    else { $env:EQ001_SQL_TEST_SETTINGS=$previousSettings }
}

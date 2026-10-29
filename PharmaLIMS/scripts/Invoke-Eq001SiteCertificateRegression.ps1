[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false # The frozen defect must return exit 1; validate it explicitly below.
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$testProject = Join-Path $project 'tests/PharmaLIMS.WaterCertificateRegression/PharmaLIMS.WaterCertificateRegression.csproj'
$frozen = Join-Path $project 'tests/fixtures/AMEEN-ReportCertificate-20261029.cs.txt'
$corrected = Join-Path $project 'scripts/site-overlays/AMEEN-ReportCertificate.cs.txt'
$artifacts = Join-Path ([IO.Path]::GetTempPath()) ('eq001-site-certificate-' + [guid]::NewGuid().ToString('N'))
try {
    $runs = @(
        @{Name='frozen';Source=$frozen;ExpectedFailure=$true},
        @{Name='corrected';Source=$corrected;ExpectedFailure=$false}
    )
    foreach ($run in $runs) {
        $output = @(& dotnet run --project $testProject --configuration Release `
            "-p:CertificateSource=$($run.Source)" '-p:SitePrecisionOnly=true' `
            "-p:ArtifactsPath=$(Join-Path $artifacts $run.Name)" 2>&1)
        $code = $LASTEXITCODE
        $log = $output -join "`n"
        $summary = [regex]::Match($log, 'Site certificate precision: (\d+)/(\d+) checks passed; (\d+) failed\.')
        if (-not $summary.Success) {
            $output | Write-Host
            throw "Site certificate $($run.Name) did not execute the actual extracted methods."
        }
        $failures = [int]$summary.Groups[3].Value
        if ($run.ExpectedFailure) {
            if ($code -ne 1 -or $failures -le 0 -or $log -notmatch 'FAIL .*exact result 0\.0002') {
                $output | Write-Host
                throw 'The immutable uploaded source did not reproduce the scientific precision defect.'
            }
            Write-Host "PASS: Uploaded source reproduces the defect: $($summary.Value)"
        }
        else {
            if ($code -ne 0 -or $failures -ne 0) {
                $output | Write-Host
                throw 'Corrected site source failed exact scientific formatting or retained site semantics.'
            }
            Write-Host "PASS: Corrected complete site source: $($summary.Value)"
        }
    }
}
finally {
    if (Test-Path -LiteralPath $artifacts) { Remove-Item -LiteralPath $artifacts -Recurse -Force }
}

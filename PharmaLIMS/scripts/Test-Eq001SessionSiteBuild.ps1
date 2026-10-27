[CmdletBinding()]
param([string]$ExistingArtifactsPath)
$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$stage = Join-Path ([IO.Path]::GetTempPath()) ('eq001-session-build-' + [guid]::NewGuid())
$null = New-Item -ItemType Directory -Path $stage
$targets = Join-Path $stage 'session-site.targets'
$artifacts = if ($ExistingArtifactsPath) { [IO.Path]::GetFullPath($ExistingArtifactsPath) } else { Join-Path $stage 'artifacts' }
$pagePath = Join-Path $stage 'MainWindow.xaml'
[IO.File]::Copy((Join-Path $project 'scripts/site-overlays/AMEEN-MainWindow.xaml.txt'),$pagePath)
$overlay = [Security.SecurityElement]::Escape((Join-Path $project 'scripts/site-overlays'))
$template = @'
<Project>
 <ItemGroup Condition="'$(MSBuildProjectName)' == 'PharmaLIMS' or $([System.String]::Copy('$(MSBuildProjectName)').Contains('_wpftmp'))">
  <Compile Remove="App.xaml.cs" />
  <Compile Include="__OVERLAY__/AMEEN-App.xaml.cs.txt" />
  <Compile Remove="MainWindow.xaml.cs" />
  <Compile Include="__OVERLAY__/AMEEN-MainWindow.xaml.cs.txt" />
  <Page Remove="MainWindow.xaml" />
  <Page Include="__PAGE__"><Link>MainWindow.xaml</Link><Generator>MSBuild:Compile</Generator></Page>
 </ItemGroup>
</Project>
'@
try {
    [IO.File]::WriteAllText($targets,$template.Replace('__OVERLAY__',$overlay).Replace('__PAGE__',[Security.SecurityElement]::Escape($pagePath)),[Text.UTF8Encoding]::new($false))
    $buildArguments = @('build',(Join-Path $project 'PharmaLIMS.csproj'),'--configuration','Release','-maxcpucount:2',
        '-p:RestoreLockedMode=true',"-p:CustomAfterMicrosoftCommonTargets=$targets","-p:ArtifactsPath=$artifacts",'-p:EnableWindowsTargeting=true')
    if ($ExistingArtifactsPath) { $buildArguments += '--no-restore' }
    & dotnet @buildArguments
    if ($LASTEXITCODE -ne 0) { throw 'Complete original-site session overlays did not build.' }
    Write-Host 'PASS: Full original-site App/MainWindow overlays built without changing the project files.'
}
finally {
    Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
}

<#
    Publish-Build.ps1 -- builds WUGrid.exe and wugrid-cli.exe (Release) into builds\<version>\, ready to launch and test.

    The version comes from <Version> in Directory.Build.props: bump it there for each iteration, so every build gets its
    own folder. Tests run first; a failing test means no build. Rebuilding the same version replaces its folder.

    .\tools\Publish-Build.ps1
    .\tools\Publish-Build.ps1 -SkipTests
#>
param([switch] $SkipTests)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

[xml] $props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = @($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Version '$version' in Directory.Build.props is not MAJOR.MINOR.PATCH" }
$out = Join-Path $root "builds\$version"

if (-not $SkipTests) {
    dotnet test (Join-Path $root 'WUGrid.sln') -c Release -nologo -v q
    if ($LASTEXITCODE) { throw 'Tests failed: no build.' }
}

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
[void](New-Item -ItemType Directory -Path $out)
# Each project builds into its own bin and is copied over: building both straight into one folder lets MSBuild's
# incremental clean of one project delete the other's files
foreach ($project in 'WUGrid.Gui', 'WUGrid.Cli') {
    dotnet build (Join-Path $root "src\$project\$project.csproj") -c Release -nologo -v q
    if ($LASTEXITCODE) { throw "Build failed: $project" }
    Copy-Item (Join-Path $root "src\$project\bin\Release\net48\*") $out -Recurse -Force
}

$files = Get-ChildItem $out -File
"builds\$version  ($($files.Count) files, $([Math]::Round(($files | Measure-Object Length -Sum).Sum / 1KB)) KB)"
Get-ChildItem $out -Filter *.exe | ForEach-Object { "  $($_.Name)  $($_.VersionInfo.FileVersion)" }

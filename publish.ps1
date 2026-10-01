<#
.SYNOPSIS
    Builds ReBackup as one self-contained exe for Windows x64.

.DESCRIPTION
    Publishes src/ReBackup.App in Release as a single file that carries the .NET runtime, so it runs on any
    Windows 10/11 x64 machine without installing anything. The result is dist\ReBackup-<version>-win-x64.exe
    (the version comes from Directory.Build.props unless -Version is given).

.EXAMPLE
    .\publish.ps1
.EXAMPLE
    .\publish.ps1 -Version 1.0.1
.EXAMPLE
    .\publish.ps1 -FrameworkDependent   # small exe that needs the .NET 9 Desktop Runtime installed
#>
param(
    [string]$Version,
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',
    [switch]$FrameworkDependent
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

if (-not $Version) {
    [xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
    $Version = ($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
    if (-not $Version) { throw 'No <Version> in Directory.Build.props; pass -Version.' }
}

$publishDir = Join-Path $root "artifacts\publish\$Runtime"
$distDir = Join-Path $root 'dist'
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
New-Item -ItemType Directory -Force $distDir | Out-Null

$selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
$arguments = @(
    'publish', (Join-Path $root 'src\ReBackup.App\ReBackup.App.csproj'),
    '-c', 'Release',
    '-r', $Runtime,
    '--self-contained', $selfContained,
    '-o', $publishDir,
    "-p:Version=$Version",
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:DebugType=none',
    '-p:DebugSymbols=false'
)
if (-not $FrameworkDependent) { $arguments += '-p:EnableCompressionInSingleFile=true' }

Write-Host "Publishing ReBackup $Version ($Runtime, $(if ($FrameworkDependent) { 'framework-dependent' } else { 'self-contained' })) ..."
& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

$exe = Join-Path $publishDir 'ReBackup.App.exe'
if (-not (Test-Path $exe)) { throw "Publish produced no $exe." }
$suffix = if ($FrameworkDependent) { "-$Runtime-fd" } else { "-$Runtime" }
$target = Join-Path $distDir "ReBackup-$Version$suffix.exe"
Copy-Item $exe $target -Force

$leftovers = Get-ChildItem $publishDir -File | Where-Object { $_.Name -ne 'ReBackup.App.exe' }
if ($leftovers) { Write-Warning ("Files next to the exe (not needed at run time if empty): " + ($leftovers.Name -join ', ')) }

$size = [math]::Round((Get-Item $target).Length / 1MB, 1)
Write-Host "Done: $target ($size MB)"

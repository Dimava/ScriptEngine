<#
.SYNOPSIS
Builds all ScriptEngine release zip variants.

.DESCRIPTION
Restores/builds ScriptEngine once, then creates all release zips in this
repository's release directory:

- ScriptEngine-<version>.zip
- ScriptEngine-<version>-scripts.zip
- ScriptEngine-<version>-bepinex.zip
- ScriptEngine-<version>-scripts-bepinex.zip

.PARAMETER Version
Version label used in zip file names. Defaults to the Version property from
ScriptEngine.csproj.

.PARAMETER Configuration
Build configuration to use. Defaults to Release.

.PARAMETER ScriptsDir
Source Scripts directory used for script-inclusive packages.

.PARAMETER BepInExVersion
BepInEx 5 win x64 version to include. Defaults to latest.

.PARAMETER SkipRestore
Skips dotnet restore before building.

.PARAMETER SkipBuild
Skips dotnet build and packages the current output directory as-is.

.EXAMPLE
.\package-release.ps1 -ScriptsDir "C:\Games\Modulus\Scripts"
#>
param(
    [Parameter(Mandatory = $false)]
    [string]$Version = "",

    [Parameter(Mandatory = $false)]
    [string]$Configuration = "Release",

    [Parameter(Mandatory = $false)]
    [string]$ScriptsDir = "",

    [Parameter(Mandatory = $false)]
    [string]$BepInExVersion = "latest",

    [Parameter(Mandatory = $false)]
    [switch]$SkipRestore,

    [Parameter(Mandatory = $false)]
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$workspaceRoot = Split-Path -Parent $projectRoot
$projectFile = Join-Path $projectRoot "ScriptEngine\ScriptEngine.csproj"
$releaseDir = Join-Path $projectRoot "release"
$packageScript = Join-Path $PSScriptRoot "package-unzip.ps1"

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$projectXml = Get-Content -Raw $projectFile
    $Version = $projectXml.Project.PropertyGroup.Version | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($Version)) {
        throw "Version property not found in $projectFile."
    }
}

if ([string]::IsNullOrWhiteSpace($ScriptsDir)) {
    $ScriptsDir = Join-Path $workspaceRoot "Scripts"
}

if (-not $SkipRestore) {
    & dotnet restore $projectFile
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet restore failed."
    }
}

if (-not $SkipBuild) {
    $buildArgs = @("build", $projectFile, "-c", $Configuration)
    if ($SkipRestore) {
        $buildArgs += "--no-restore"
    }

    & dotnet @buildArgs
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed."
    }
}

New-Item -ItemType Directory -Force -Path $releaseDir | Out-Null

$packages = @(
    @{ Name = "ScriptEngine-$Version"; Scripts = $false; BepInEx = $false },
    @{ Name = "ScriptEngine-$Version-scripts"; Scripts = $true; BepInEx = $false },
    @{ Name = "ScriptEngine-$Version-bepinex"; Scripts = $false; BepInEx = $true },
    @{ Name = "ScriptEngine-$Version-scripts-bepinex"; Scripts = $true; BepInEx = $true }
)

foreach ($package in $packages) {
    $packageArgs = @{
        Version = $Version
        Configuration = $Configuration
        SkipRestore = $true
        SkipBuild = $true
        PackageName = $package.Name
    }

    if ($package.Scripts) {
        $packageArgs.IncludeScripts = $true
        $packageArgs.ScriptsDir = $ScriptsDir
    }

    if ($package.BepInEx) {
        $packageArgs.IncludeBepInEx = $true
        $packageArgs.BepInExVersion = $BepInExVersion
    }

    & $packageScript @packageArgs
    if ($LASTEXITCODE -ne 0) {
        throw "package-unzip.ps1 failed for $($package.Name)."
    }
}

Write-Host "Created all ScriptEngine $Version release zips in $releaseDir"

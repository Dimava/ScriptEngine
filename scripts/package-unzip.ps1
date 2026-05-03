<#
.SYNOPSIS
Packages ScriptEngine as a drop-in zip.

.DESCRIPTION
Creates a clean drop-in zip in this repository's release directory. The package
always contains BepInEx\plugins\ScriptEngine. Optional switches add Scripts\ and
a clean BepInEx loader payload.

.PARAMETER Version
Version label used in the zip file name. Defaults to the Version property from
ScriptEngine.csproj.

.PARAMETER Configuration
Build configuration to use. Defaults to Release.

.PARAMETER ScriptsDir
Source Scripts directory to include when IncludeScripts is set.

.PARAMETER IncludeScripts
Includes clean scripts and a default ScriptEngine.cfg.

.PARAMETER IncludeBepInEx
Includes a clean BepInEx loader payload.

.PARAMETER BepInExVersion
BepInEx 5 win x64 version to include. Defaults to latest.

.PARAMETER PackageName
Output package base name. Defaults to ScriptEngine-<version> plus suffixes.

.EXAMPLE
.\package-unzip.ps1 -IncludeScripts -ScriptsDir "C:\Games\Modulus\Scripts"
#>
param(
    [Parameter(Mandatory = $false)]
    [string]$Version = "",

    [Parameter(Mandatory = $false)]
    [string]$Configuration = "Release",

    [Parameter(Mandatory = $false)]
    [string]$ScriptsDir = "",

    [Parameter(Mandatory = $false)]
    [switch]$IncludeScripts,

    [Parameter(Mandatory = $false)]
    [switch]$IncludeBepInEx,

    [Parameter(Mandatory = $false)]
    [string]$BepInExVersion = "latest",

    [Parameter(Mandatory = $false)]
    [string]$PackageName = "",

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

function Get-ProjectVersion {
    [xml]$projectXml = Get-Content -Raw $projectFile
    $projectVersion = $projectXml.Project.PropertyGroup.Version | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($projectVersion)) {
        throw "Version property not found in $projectFile."
    }
    return $projectVersion
}

function Get-BepInExAsset {
    param([string]$RequestedVersion)

    if ([string]::IsNullOrWhiteSpace($RequestedVersion) -or $RequestedVersion -eq "latest") {
        $release = Invoke-RestMethod -Uri "https://api.github.com/repos/BepInEx/BepInEx/releases/latest"
        $asset = $release.assets | Where-Object { $_.name -like "BepInEx_win_x64_*.zip" } | Select-Object -First 1
        if ($null -eq $asset) {
            throw "Could not find BepInEx_win_x64 asset in latest BepInEx release."
        }

        return [pscustomobject]@{
            Version = $release.tag_name.TrimStart("v")
            Url = $asset.browser_download_url
            Name = $asset.name
        }
    }

    $assetName = "BepInEx_win_x64_$RequestedVersion.zip"
    return [pscustomobject]@{
        Version = $RequestedVersion
        Url = "https://github.com/BepInEx/BepInEx/releases/download/v$RequestedVersion/$assetName"
        Name = $assetName
    }
}

function Add-ScriptEnginePlugin {
    param([string]$SourceDir, [string]$StagingRoot)

    $pluginDir = Join-Path $StagingRoot "BepInEx\plugins\ScriptEngine"
    New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null

    $files = @(
        "ScriptEngine.dll",
        "ScriptEngine.deps.json",
        "ScriptEngine.pdb",
        "Microsoft.CodeAnalysis.dll",
        "Microsoft.CodeAnalysis.CSharp.dll",
        "System.Buffers.dll",
        "System.Collections.Immutable.dll",
        "System.Collections.NonGeneric.dll",
        "System.Collections.Specialized.dll",
        "System.ComponentModel.dll",
        "System.ComponentModel.Primitives.dll",
        "System.ComponentModel.TypeConverter.dll",
        "System.IO.FileSystem.Primitives.dll",
        "System.Linq.dll",
        "System.Memory.dll",
        "System.Numerics.Vectors.dll",
        "System.Reflection.Metadata.dll",
        "System.Reflection.TypeExtensions.dll",
        "System.Runtime.CompilerServices.Unsafe.dll",
        "System.Text.Encoding.CodePages.dll",
        "System.Threading.dll",
        "System.Threading.Tasks.Extensions.dll"
    )

    foreach ($file in $files) {
        $source = Join-Path $SourceDir $file
        if (-not (Test-Path $source)) {
            throw "Missing build output '$file' in $SourceDir."
        }

        Copy-Item -LiteralPath $source -Destination $pluginDir -Force
    }
}

function Add-CleanScripts {
    param([string]$SourceScriptsDir, [string]$StagingRoot)

    if ([string]::IsNullOrWhiteSpace($SourceScriptsDir)) {
        $SourceScriptsDir = Join-Path $workspaceRoot "Scripts"
    }

    $resolvedScriptsDir = (Resolve-Path -LiteralPath $SourceScriptsDir).Path
    $sourceDimava = Join-Path $resolvedScriptsDir "Dimava"
    if (-not (Test-Path $sourceDimava)) {
        throw "Scripts\Dimava folder not found at $sourceDimava. Pass -ScriptsDir <path-to-Scripts>."
    }

    $scriptsRoot = Join-Path $StagingRoot "Scripts"
    New-Item -ItemType Directory -Force -Path $scriptsRoot | Out-Null

    $defaultConfig = @"
[scripts]
enabled = true
enableNewScripts = false
enableNewEvalScripts = true

[scripts."Dimava/+ScriptEngineSettingsTab.cs"]
enabled = true
"@
    Set-Content -Path (Join-Path $scriptsRoot "ScriptEngine.cfg") -Value $defaultConfig -Encoding ASCII

    $excludeDirs = @(".git", "bin", "obj", "node_modules", "logs")
    foreach ($folderName in @("Dimava", "Eval")) {
        $sourceFolder = Join-Path $resolvedScriptsDir $folderName
        if ($folderName -eq "Eval" -and -not (Test-Path $sourceFolder)) {
            $sourceFolder = Join-Path $PSScriptRoot "Eval"
        }

        if (-not (Test-Path $sourceFolder)) {
            continue
        }

        $scriptsDest = Join-Path $scriptsRoot $folderName
        New-Item -ItemType Directory -Force -Path $scriptsDest | Out-Null
        $sourceFull = (Resolve-Path -LiteralPath $sourceFolder).Path.TrimEnd('\')

        Get-ChildItem -LiteralPath $sourceFolder -Recurse -Force | ForEach-Object {
            $rel = $_.FullName.Substring($sourceFull.Length).TrimStart('\')
            $segments = $rel -split '[\\/]+'
            foreach ($seg in $segments) {
                if ($excludeDirs -contains $seg -or $seg.StartsWith('.')) { return }
            }

            $target = Join-Path $scriptsDest $rel
            if ($_.PSIsContainer) {
                New-Item -ItemType Directory -Force -Path $target | Out-Null
            } else {
                $targetDir = Split-Path -Parent $target
                New-Item -ItemType Directory -Force -Path $targetDir | Out-Null
                Copy-Item -LiteralPath $_.FullName -Destination $target -Force
            }
        }
    }
}

function Add-CleanBepInEx {
    param([string]$StagingRoot, [string]$RequestedVersion)

    $asset = Get-BepInExAsset -RequestedVersion $RequestedVersion
    $zipPath = Join-Path $env:TEMP $asset.Name
    $extractDir = Join-Path $env:TEMP "ScriptEngine-BepInEx-$($asset.Version)-clean"

    if (-not (Test-Path $zipPath)) {
        Invoke-WebRequest -Uri $asset.Url -OutFile $zipPath
    }

    Remove-Item $extractDir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $extractDir | Out-Null
    Expand-Archive -LiteralPath $zipPath -DestinationPath $extractDir -Force

    foreach ($name in @("BepInEx", ".doorstop_version", "changelog.txt", "doorstop_config.ini", "winhttp.dll")) {
        $source = Join-Path $extractDir $name
        if (Test-Path $source) {
            Copy-Item -LiteralPath $source -Destination $StagingRoot -Recurse -Force
        }
    }

    Write-Host "Included BepInEx $($asset.Version)"
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = Get-ProjectVersion
}

if ([string]::IsNullOrWhiteSpace($PackageName)) {
    $suffix = ""
    if ($IncludeScripts) { $suffix += "-scripts" }
    if ($IncludeBepInEx) { $suffix += "-bepinex" }
    $PackageName = "ScriptEngine-$Version$suffix"
}

$outputDir = Join-Path $projectRoot "ScriptEngine\bin\$Configuration\netstandard2.1"
$stagingRoot = Join-Path $releaseDir $PackageName
$zipPath = Join-Path $releaseDir "$PackageName.zip"

if (-not $SkipRestore) {
    & dotnet restore $projectFile
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed." }
}

if (-not $SkipBuild) {
    $buildArgs = @("build", $projectFile, "-c", $Configuration)
    if ($SkipRestore) { $buildArgs += "--no-restore" }
    & dotnet @buildArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed." }
}

if (-not (Test-Path (Join-Path $outputDir "ScriptEngine.dll"))) {
    throw "Build output not found in $outputDir."
}

New-Item -ItemType Directory -Force -Path $releaseDir | Out-Null
Remove-Item $stagingRoot -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $stagingRoot | Out-Null

if ($IncludeBepInEx) {
    Add-CleanBepInEx -StagingRoot $stagingRoot -RequestedVersion $BepInExVersion
}

Add-ScriptEnginePlugin -SourceDir $outputDir -StagingRoot $stagingRoot

if ($IncludeScripts) {
    Add-CleanScripts -SourceScriptsDir $ScriptsDir -StagingRoot $stagingRoot
}

Compress-Archive -Path (Join-Path $stagingRoot "*") -DestinationPath $zipPath
Write-Host "Created $zipPath"

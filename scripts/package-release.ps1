<#
.SYNOPSIS
Builds and packages a ScriptEngine release zip for a target MelonLoader game.

.DESCRIPTION
Runs restore, build, and release packaging for ScriptEngine.

.PARAMETER Version
Version label used in the release folder and zip file name.

.PARAMETER Configuration
Build configuration to use. Defaults to Release.

.PARAMETER SkipRestore
Skips dotnet restore before building.

.PARAMETER SkipBuild
Skips dotnet build and packages the current output directory as-is.

.EXAMPLE
.\package-release.ps1 -Version 1.0.0
#>
param(
    [Parameter(Mandatory = $false)]
    [string]$Version = "1.1.1",

    [Parameter(Mandatory = $false)]
    [string]$Configuration = "Release",

    [Parameter(Mandatory = $false)]
    [switch]$SkipRestore,

    [Parameter(Mandatory = $false)]
    [switch]$SkipBuild
)

$projectRoot = Split-Path -Parent $PSScriptRoot
$projectFile = Join-Path $projectRoot "ScriptEngine\ScriptEngine.csproj"
$outputDir = Join-Path $projectRoot "ScriptEngine\bin\$Configuration\netstandard2.1"
$packageRoot = Join-Path $projectRoot "release\ScriptEngine-$Version"
$zipPath = Join-Path $projectRoot "release\ScriptEngine-$Version.zip"

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

if (-not (Test-Path (Join-Path $outputDir "ScriptEngine.dll"))) {
    throw "Build output not found in $outputDir."
}

$dependencies = @(
    "Microsoft.CodeAnalysis.dll",
    "Microsoft.CodeAnalysis.CSharp.dll",
    "System.Collections.Immutable.dll",
    "System.Reflection.Metadata.dll",
    "System.Memory.dll",
    "System.Buffers.dll",
    "System.Numerics.Vectors.dll",
    "System.Runtime.CompilerServices.Unsafe.dll",
    "System.Text.Encoding.CodePages.dll",
    "System.Threading.Tasks.Extensions.dll"
)

Remove-Item $packageRoot -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path (Join-Path $packageRoot "Mods"), (Join-Path $packageRoot "UserLibs") | Out-Null

Copy-Item (Join-Path $outputDir "ScriptEngine.dll") (Join-Path $packageRoot "Mods\")

foreach ($dependency in $dependencies) {
    $source = Join-Path $outputDir $dependency
    if (-not (Test-Path $source)) {
        throw "Missing dependency '$dependency' in $outputDir."
    }

    Copy-Item $source (Join-Path $packageRoot "UserLibs\")
}

Compress-Archive -Path (Join-Path $packageRoot "*") -DestinationPath $zipPath
Write-Host "Created $zipPath"

$unzipPackageScript = Join-Path $PSScriptRoot "package-unzip.ps1"
& $unzipPackageScript -Version $Version -Configuration $Configuration -SkipRestore -SkipBuild
if ($LASTEXITCODE -ne 0) {
    throw "package-unzip.ps1 failed."
}

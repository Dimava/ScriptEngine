<#
.SYNOPSIS
Packages the ScriptEngine bundle as a drop-in zip whose contents unzip directly
into the game install directory (no wrapping folder, no MelonLoader payload).

.DESCRIPTION
The resulting zip contains Mods\, UserLibs\, and Scripts\ at its top level.
MelonLoader itself (and version.dll) is expected to already be installed by the
user; this script only ships the mod, its managed dependencies, and the
Scripts/ tree.

.PARAMETER Version
Version label used in the zip file name.

.PARAMETER Configuration
Build configuration to use. Defaults to Release.

.PARAMETER SkipRestore
Skips dotnet restore before building.

.PARAMETER SkipBuild
Skips dotnet build and packages the current output directory as-is.

.EXAMPLE
.\package-unzip.ps1 -Version 1.1.0
#>
param(
    [Parameter(Mandatory = $false)]
    [string]$Version = "1.1.0",

    [Parameter(Mandatory = $false)]
    [string]$Configuration = "Release",

    [Parameter(Mandatory = $false)]
    [switch]$SkipRestore,

    [Parameter(Mandatory = $false)]
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$workspaceRoot = Split-Path -Parent $projectRoot
$projectFile = Join-Path $projectRoot "ScriptEngine\ScriptEngine.csproj"
$outputDir = Join-Path $projectRoot "ScriptEngine\bin\$Configuration\netstandard2.1"
$scriptsSource = Join-Path $workspaceRoot "Scripts"
$stagingRoot = Join-Path $workspaceRoot "release\Modulus-ScriptBundle-$Version-unzip"
$zipPath = Join-Path $workspaceRoot "release\Modulus-ScriptBundle-$Version-unzip.zip"

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

Remove-Item $stagingRoot -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path `
    (Join-Path $stagingRoot "Mods"), `
    (Join-Path $stagingRoot "UserLibs"), `
    (Join-Path $stagingRoot "Scripts") | Out-Null

Copy-Item (Join-Path $outputDir "ScriptEngine.dll") (Join-Path $stagingRoot "Mods\")

foreach ($dependency in $dependencies) {
    $source = Join-Path $outputDir $dependency
    if (-not (Test-Path $source)) {
        throw "Missing dependency '$dependency' in $outputDir."
    }
    Copy-Item $source (Join-Path $stagingRoot "UserLibs\")
}

if (-not (Test-Path $scriptsSource)) {
    throw "Scripts folder not found at $scriptsSource."
}

$excludeDirs = @(".git", "bin", "obj", "node_modules", "logs")
$scriptsDest = Join-Path $stagingRoot "Scripts"
$sourceFull = (Resolve-Path $scriptsSource).Path.TrimEnd('\')

Get-ChildItem -LiteralPath $scriptsSource -Recurse -Force | ForEach-Object {
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
        if (-not (Test-Path $targetDir)) {
            New-Item -ItemType Directory -Force -Path $targetDir | Out-Null
        }
        Copy-Item -LiteralPath $_.FullName -Destination $target -Force
    }
}

Compress-Archive -Path (Join-Path $stagingRoot "*") -DestinationPath $zipPath
Write-Host "Created $zipPath"

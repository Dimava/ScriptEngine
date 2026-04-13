<#
.SYNOPSIS
Decompiles a game's managed assembly into C# source using ilspycmd.

.DESCRIPTION
Finds the first <GameName>_Data directory under the specified game path, resolves
the requested managed assembly, and decompiles it into the selected output folder.

.PARAMETER GamePath
Path to the target game root directory.

.PARAMETER OutputDir
Destination directory for the decompiled source. Defaults to <GamePath>\Decompiled.

.PARAMETER AssemblyName
Managed assembly file name to decompile. Defaults to Assembly-CSharp.dll.

.EXAMPLE
.\decompile-game.ps1 -GamePath "C:\Games\MyGame"
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$GamePath,

    [Parameter(Mandatory = $false)]
    [string]$OutputDir,

    [Parameter(Mandatory = $false)]
    [string]$AssemblyName = "Assembly-CSharp.dll"
)

$resolvedGamePath = (Resolve-Path $GamePath).Path

if (-not $OutputDir) {
    $OutputDir = Join-Path $resolvedGamePath "Decompiled"
}

$resolvedOutputDir = [System.IO.Path]::GetFullPath($OutputDir)
$dataDir = Get-ChildItem -Path $resolvedGamePath -Directory -Filter "*_Data" | Select-Object -First 1
if (-not $dataDir) {
    throw "Could not find a '<GameName>_Data' directory under $resolvedGamePath."
}

$assemblyPath = Join-Path $dataDir.FullName "Managed\$AssemblyName"
if (-not (Test-Path $assemblyPath)) {
    throw "Could not find $AssemblyName at $assemblyPath."
}

$ilspy = Get-Command ilspycmd -ErrorAction SilentlyContinue
if (-not $ilspy) {
    throw "ilspycmd was not found on PATH. Install it with: dotnet tool install -g ilspycmd"
}

New-Item -ItemType Directory -Force -Path $resolvedOutputDir | Out-Null
& $ilspy.Source $assemblyPath -p -o $resolvedOutputDir
if ($LASTEXITCODE -ne 0) {
    throw "ilspycmd failed."
}

Write-Host "Decompiled $assemblyPath to $resolvedOutputDir"

<#
.SYNOPSIS
Links the current ScriptEngine build output into a BepInEx game's plugin folder.

.DESCRIPTION
Creates hard links for ScriptEngine.dll and its runtime dependencies from the local
build output into BepInEx\plugins\ScriptEngine under the target game directory.

.PARAMETER GamePath
Path to the target BepInEx game root directory.

.PARAMETER Configuration
Build configuration to link from. Defaults to Release.

.PARAMETER Watch
If set, watches for changes to ScriptEngine sources and rebuilds + relinks automatically.

.EXAMPLE
.\link-dev-build.ps1 -GamePath "C:\Games\MyGame"
.\link-dev-build.ps1 -GamePath "C:\Games\MyGame" -Watch
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$GamePath,

    [Parameter(Mandatory = $false)]
    [string]$Configuration = "Release",

    [Parameter(Mandatory = $false)]
    [switch]$Watch
)

$projectRoot = Split-Path -Parent $PSScriptRoot
$gameRoot = (Resolve-Path $GamePath).Path
$outputDir = Join-Path $projectRoot "ScriptEngine\bin\$Configuration\netstandard2.1"
$pluginDir = Join-Path $gameRoot "BepInEx\plugins\ScriptEngine"

$files = @(
    "ScriptEngine.dll",
    "ScriptEngine.pdb",
    "ScriptEngine.deps.json",
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
).ForEach({
    @{ Source = Join-Path $outputDir $_; Target = Join-Path $pluginDir $_ }
})

function Invoke-Link {
    New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null

    foreach ($file in $files) {
        if (-not (Test-Path $file.Source)) {
            Write-Warning "Missing build output: $($file.Source). Run the build first."
            return $false
        }

        if (Test-Path $file.Target) {
            Remove-Item $file.Target -Force
        }

        New-Item -ItemType HardLink -Path $file.Target -Target $file.Source | Out-Null
    }

    Write-Host "Linked dev build into $pluginDir"
    return $true
}

function Invoke-BuildAndLink {
    Write-Host "[$(Get-Date -Format 'HH:mm:ss')] Building..."
    $result = & dotnet build (Join-Path $projectRoot "ScriptEngine\ScriptEngine.csproj") -c $Configuration --nologo -v q 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Build failed:`n$result"
        return
    }
    Invoke-Link | Out-Null
    Write-Host "[$(Get-Date -Format 'HH:mm:ss')] Done."
}

Invoke-Link | Out-Null

if ($Watch) {
    $srcDir = Join-Path $projectRoot "ScriptEngine"
    $watcher = [System.IO.FileSystemWatcher]::new($srcDir, "*.cs")
    $watcher.IncludeSubdirectories = $true
    $watcher.NotifyFilter = [System.IO.NotifyFilters]::LastWrite

    Write-Host "Watching $srcDir for changes. Press Ctrl+C to stop."

    $pending = $false
    $lastChange = [datetime]::MinValue

    Invoke-BuildAndLink

    while ($true) {
        $event = $watcher.WaitForChanged([System.IO.WatcherChangeTypes]::Changed, 200)
        if (-not $event.TimedOut) {
            $lastChange = [datetime]::UtcNow
            $pending = $true
        }

        if ($pending -and ([datetime]::UtcNow - $lastChange).TotalMilliseconds -ge 500) {
            $pending = $false
            Invoke-BuildAndLink
        }
    }
}

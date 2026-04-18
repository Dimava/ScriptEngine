<#
.SYNOPSIS
Links the current ScriptEngine build output into a game's Mods and UserLibs folders.

.DESCRIPTION
Creates hard links for ScriptEngine.dll and its runtime dependencies from the local
build output into the target game's Mods and UserLibs directories.

.PARAMETER GamePath
Path to the target MelonLoader game root directory.

.PARAMETER Configuration
Build configuration to link from. Defaults to Release.

.PARAMETER Watch
If set, watches for changes to ScriptEngine.dll and rebuilds + relinks automatically.

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
$modsDir = Join-Path $gameRoot "Mods"
$userLibsDir = Join-Path $gameRoot "UserLibs"

$files = @(
    @{ Source = Join-Path $outputDir "ScriptEngine.dll"; Target = Join-Path $modsDir "ScriptEngine.dll" },
    @{ Source = Join-Path $outputDir "Microsoft.CodeAnalysis.dll"; Target = Join-Path $userLibsDir "Microsoft.CodeAnalysis.dll" },
    @{ Source = Join-Path $outputDir "Microsoft.CodeAnalysis.CSharp.dll"; Target = Join-Path $userLibsDir "Microsoft.CodeAnalysis.CSharp.dll" },
    @{ Source = Join-Path $outputDir "System.Collections.Immutable.dll"; Target = Join-Path $userLibsDir "System.Collections.Immutable.dll" },
    @{ Source = Join-Path $outputDir "System.Reflection.Metadata.dll"; Target = Join-Path $userLibsDir "System.Reflection.Metadata.dll" },
    @{ Source = Join-Path $outputDir "System.Memory.dll"; Target = Join-Path $userLibsDir "System.Memory.dll" },
    @{ Source = Join-Path $outputDir "System.Buffers.dll"; Target = Join-Path $userLibsDir "System.Buffers.dll" },
    @{ Source = Join-Path $outputDir "System.Numerics.Vectors.dll"; Target = Join-Path $userLibsDir "System.Numerics.Vectors.dll" },
    @{ Source = Join-Path $outputDir "System.Runtime.CompilerServices.Unsafe.dll"; Target = Join-Path $userLibsDir "System.Runtime.CompilerServices.Unsafe.dll" },
    @{ Source = Join-Path $outputDir "System.Text.Encoding.CodePages.dll"; Target = Join-Path $userLibsDir "System.Text.Encoding.CodePages.dll" },
    @{ Source = Join-Path $outputDir "System.Threading.Tasks.Extensions.dll"; Target = Join-Path $userLibsDir "System.Threading.Tasks.Extensions.dll" }
)

function Invoke-Link {
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

    Write-Host "Linked dev build into $modsDir and $userLibsDir"
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

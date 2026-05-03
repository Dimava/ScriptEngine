<#
.SYNOPSIS
Runs a deliberately inert ScriptEngine eval script once.

.DESCRIPTION
Eval scripts should use [ScriptEval] instead of [ScriptEntry] so ScriptEngine
cannot compile/load them by accident. This helper temporarily replaces
[ScriptEval] with [ScriptEntry], waits until ScriptEngine reports the expected
crash/unload signal, then restores the file back to [ScriptEval] and prints the
script's personal log.

The intended eval script shape is:

using ScriptEngine;

[ScriptEval]
public sealed class MyEval : ScriptMod
{
    protected override void OnEnable()
    {
        Log("whatever result");
        throw new System.Exception("EVAL_DONE");
    }
}

.EXAMPLE
.\Scripts\Eval\Invoke-Eval.ps1 -Script Eval/MyEval.cs
#>
param(
    [Parameter(Mandatory = $false)]
    [string]$Script = "Eval/EvalScratch.cs",

    [Parameter(Mandatory = $false)]
    [string]$CrashPattern = "Failed to load attribute script:",

    [Parameter(Mandatory = $false)]
    [int]$TimeoutSeconds = 30
)

$ErrorActionPreference = "Stop"

$scriptsRoot = Split-Path -Parent $PSScriptRoot
$scriptPath = Join-Path $scriptsRoot $Script.Replace("/", [System.IO.Path]::DirectorySeparatorChar)
$logRelative = [System.IO.Path]::ChangeExtension($Script.Replace("/", [System.IO.Path]::DirectorySeparatorChar), ".log")
$scriptLog = Join-Path (Join-Path $scriptsRoot "logs") $logRelative

if (-not (Test-Path $scriptPath)) {
    throw "Script not found: $scriptPath"
}

$original = Get-Content -Raw $scriptPath
if ($original -notmatch "\[ScriptEval\]") {
    throw "Expected [ScriptEval] marker in $scriptPath"
}

try {
    if (Test-Path $scriptLog) {
        Remove-Item -LiteralPath $scriptLog -Force
    }

    $entry = [regex]::Replace($original, "\[ScriptEval\]", "[ScriptEntry]", 1)
    Set-Content -Path $scriptPath -Value $entry -NoNewline

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    Write-Host "Activated $Script; waiting for '$CrashPattern' in $scriptLog"

    while ((Get-Date) -lt $deadline) {
        if (Test-Path $scriptLog) {
            $logText = Get-Content -Raw -Path $scriptLog
            if ($logText -like "*$CrashPattern*") {
                Write-Host "Matched crash signal: $CrashPattern"
                return
            }
        }

        Start-Sleep -Milliseconds 250
    }

    throw "Timed out after ${TimeoutSeconds}s waiting for '$CrashPattern' in $scriptLog"
}
finally {
    Set-Content -Path $scriptPath -Value $original -NoNewline
    Write-Host ""
    Write-Host "Script log:"
    if (Test-Path $scriptLog) {
        Get-Content -Path $scriptLog -Tail 120
    }
    else {
        Write-Host "No script log found at $scriptLog"
    }
}

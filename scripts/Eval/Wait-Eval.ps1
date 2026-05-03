<#
.SYNOPSIS
Waits for a ScriptEngine eval script to log a completion token.

.DESCRIPTION
Use this next to temporary Scripts\Eval\*.cs scripts. The helper reads the
per-script log under Scripts\logs and exits when it sees the requested token.
By default it scans existing log content too, which avoids missing fast OnEnable
scripts. Use unique done tokens, or pass -TailOnly to wait only for new lines.
It fails early if ScriptEngine writes compile, load, or runtime errors to that
same script log.

.EXAMPLE
.\Scripts\Eval\Wait-Eval.ps1 -Script Eval/MyEval.cs -Done "EVAL_DONE"
#>
param(
    [Parameter(Mandatory = $false)]
    [string]$Script = "Eval/EvalScratch.cs",

    [Parameter(Mandatory = $false)]
    [string]$Done = "EVAL_DONE",

    [Parameter(Mandatory = $false)]
    [int]$TimeoutSeconds = 30,

    [Parameter(Mandatory = $false)]
    [switch]$TailOnly
)

$ErrorActionPreference = "Stop"

$scriptsRoot = Split-Path -Parent $PSScriptRoot
$logRelative = [System.IO.Path]::ChangeExtension($Script.Replace("/", [System.IO.Path]::DirectorySeparatorChar), ".log")
$scriptLog = Join-Path (Join-Path $scriptsRoot "logs") $logRelative
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
$offset = 0L

if ((Test-Path $scriptLog) -and $TailOnly) {
    $offset = (Get-Item $scriptLog).Length
}

function Read-NewLogLines {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [ref]$Offset
    )

    if (-not (Test-Path $Path)) {
        return @()
    }

    $item = Get-Item $Path
    if ($item.Length -lt $Offset.Value) {
        $Offset.Value = 0L
    }

    if ($item.Length -eq $Offset.Value) {
        return @()
    }

    $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    try {
        $stream.Seek($Offset.Value, [System.IO.SeekOrigin]::Begin) | Out-Null
        $reader = New-Object System.IO.StreamReader($stream)
        try {
            $text = $reader.ReadToEnd()
            $Offset.Value = $stream.Position
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }

    if ([string]::IsNullOrEmpty($text)) {
        return @()
    }

    return $text -split "`r?`n" | Where-Object { $_ -ne "" }
}

$errorPattern = "\[Error\]|Compile errors:|OnEnable failed:|Failed to load|Unhandled .* exception|Disabling script after runtime exception"

Write-Host "Waiting for '$Done' in $scriptLog"

while ((Get-Date) -lt $deadline) {
    foreach ($line in Read-NewLogLines -Path $scriptLog -Offset ([ref]$offset)) {
        Write-Host $line

        if ($line -like "*$Done*") {
            Write-Host "Matched: $Done"
            exit 0
        }

        if ($line -match $errorPattern) {
            throw "ScriptEngine eval failed: $line"
        }
    }

    Start-Sleep -Milliseconds 250
}

throw "Timed out after ${TimeoutSeconds}s waiting for '$Done' in $scriptLog"

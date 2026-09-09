<#
.SYNOPSIS
    Points Sunshine's global_prep_cmd at DisplaySwitcher.

.DESCRIPTION
    Backs up sunshine.conf, then replaces the global_prep_cmd entry so that Sunshine runs
    'DisplaySwitcher.exe activate' when a stream starts and 'DisplaySwitcher.exe restore'
    when it ends. sunshine.conf lives under Program Files, so the script re-launches itself
    elevated if it is not already running as administrator.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\scripts\Install-SunshineHook.ps1
#>
[CmdletBinding()]
param(
    [string]$SunshineConf = 'C:\Program Files\Sunshine\config\sunshine.conf',
    [string]$ExePath
)

$ErrorActionPreference = 'Stop'

if (-not $ExePath) {
    $ExePath = Join-Path (Split-Path $PSScriptRoot -Parent) 'publish\DisplaySwitcher.exe'
}
$ExePath = [System.IO.Path]::GetFullPath($ExePath)

if (-not (Test-Path -LiteralPath $ExePath)) {
    throw "DisplaySwitcher.exe not found at '$ExePath'. Build it first with: dotnet publish src\DisplaySwitcher\DisplaySwitcher.csproj -c Release -o publish"
}
if (-not (Test-Path -LiteralPath $SunshineConf)) {
    throw "sunshine.conf not found at '$SunshineConf'."
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$isAdmin = ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Host "sunshine.conf is under Program Files, so this needs administrator rights. Prompting for elevation..."
    $childArgs = @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass',
        '-File', "`"$PSCommandPath`"",
        '-SunshineConf', "`"$SunshineConf`"",
        '-ExePath', "`"$ExePath`""
    )
    $proc = Start-Process -FilePath 'powershell.exe' -ArgumentList $childArgs -Verb RunAs -Wait -PassThru
    exit $proc.ExitCode
}

$backup = "$SunshineConf.$(Get-Date -Format 'yyyyMMdd-HHmmss').bak"
Copy-Item -LiteralPath $SunshineConf -Destination $backup -Force
Write-Host "Backed up existing config to: $backup"

# Backslashes are doubled for JSON, and the whole path is quoted because it contains spaces.
$escaped = $ExePath -replace '\\', '\\'
$newLine = 'global_prep_cmd = [{"do":"\"' + $escaped + '\" activate","undo":"\"' + $escaped + '\" restore","elevated":false}]'

$existing = [System.IO.File]::ReadAllLines($SunshineConf)
$output = New-Object 'System.Collections.Generic.List[string]'
$replaced = $false

foreach ($line in $existing) {
    if ($line -match '^\s*global_prep_cmd\s*=') {
        if (-not $replaced) { $output.Add($newLine) }
        $replaced = $true
    }
    else {
        $output.Add($line)
    }
}

if (-not $replaced) { $output.Add($newLine) }

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($SunshineConf, (($output -join "`r`n") + "`r`n"), $utf8NoBom)

if ($replaced) {
    Write-Host "Replaced the existing global_prep_cmd entry."
}
else {
    Write-Host "No global_prep_cmd entry existed; appended one."
}

Write-Host ""
Write-Host "New sunshine.conf contents:"
Get-Content -LiteralPath $SunshineConf | ForEach-Object { Write-Host "  $_" }
Write-Host ""
Write-Host "Restart Sunshine for this to take effect (it only reads sunshine.conf at startup)."

<#
.SYNOPSIS
    Publishes both executables and packs the portable ZIP that ships on the GitHub release.

.DESCRIPTION
    By default each exe is published self-contained and single-file, so the ZIP contains just two
    files and a user can unzip and run without installing the .NET runtime. That removes the most
    common support problem for a tool like this, at the cost of size: WPF plus its own copy of the
    runtime is around 63 MB per exe, and the bundles are already compressed so zipping them again
    saves almost nothing.

    -SharedRuntime publishes both exes into one folder so they share a single copy of the runtime,
    roughly halving the download. The trade is a folder of runtime DLLs sitting next to the two
    exes instead of two clean files.

.EXAMPLE
    ./scripts/publish.ps1 -Version 1.0.0

.EXAMPLE
    ./scripts/publish.ps1 -Version 1.0.0 -SharedRuntime
#>
[CmdletBinding()]
param(
    [string]$Version = '1.0.0',
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [switch]$SharedRuntime
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$stagingDir = Join-Path $repoRoot 'artifacts/staging'
$distDir = Join-Path $repoRoot 'dist'

Write-Host "Publishing DisplaySwitcher $Version ($Configuration, $Runtime)" -ForegroundColor Cyan

if (Test-Path $stagingDir) { Remove-Item $stagingDir -Recurse -Force }
New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null
New-Item -ItemType Directory -Path $distDir -Force | Out-Null

$projects = @(
    @{ Name = 'DisplaySwitcher'; Path = 'src/DisplaySwitcher/DisplaySwitcher.csproj' },
    @{ Name = 'Setup';           Path = 'src/Setup/Setup.csproj' }
)

foreach ($project in $projects) {
    Write-Host "  building $($project.Name)..." -ForegroundColor DarkGray

    if ($SharedRuntime) {
        # Publishing both into the same folder lets the second one reuse the first one's runtime.
        dotnet publish (Join-Path $repoRoot $project.Path) `
            -c $Configuration `
            -r $Runtime `
            --self-contained true `
            -p:PublishSingleFile=false `
            -p:Version=$Version `
            -o $stagingDir

        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $($project.Name)" }
    }
    else {
        $publishDir = Join-Path $repoRoot "artifacts/publish/$($project.Name)"

        dotnet publish (Join-Path $repoRoot $project.Path) `
            -c $Configuration `
            -r $Runtime `
            --self-contained true `
            -p:PublishSingleFile=true `
            -p:EnableCompressionInSingleFile=true `
            -p:Version=$Version `
            -o $publishDir

        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $($project.Name)" }

        Copy-Item (Join-Path $publishDir "$($project.Name).exe") $stagingDir -Force
    }
}

if ($SharedRuntime) {
    # The publish also drops each project's .pdb and the Core library's own host files.
    Get-ChildItem $stagingDir -Filter '*.pdb' | Remove-Item -Force
}

# settings.json ships alongside so a user can edit it without opening the app; the app also
# falls back to built-in defaults if it is missing.
Copy-Item (Join-Path $repoRoot 'settings.json') $stagingDir -Force
Copy-Item (Join-Path $repoRoot 'README.md') $stagingDir -Force
Copy-Item (Join-Path $repoRoot 'LICENSE') $stagingDir -Force

$zipPath = Join-Path $distDir "DisplaySwitcher-v$Version-$Runtime.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

Compress-Archive -Path (Join-Path $stagingDir '*') -DestinationPath $zipPath -CompressionLevel Optimal

$sizeMb = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
$fileCount = (Get-ChildItem $stagingDir -Recurse -File).Count

Write-Host ""
Write-Host "Packed $zipPath ($sizeMb MB, $fileCount files)" -ForegroundColor Green
Get-ChildItem $stagingDir -File | Sort-Object Length -Descending | Select-Object -First 8 | ForEach-Object {
    "  {0,-24} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB)
}

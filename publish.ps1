# Builds a self-contained copy of WuxAuto that runs on another PC without installing .NET.
# Output: dist\WuxAuto-win-x64.zip (to hand over) and dist\WuxAuto\ (the app folder).
# The build goes to dist\build\WuxAuto, which is always free; dist\WuxAuto is refreshed from it
# only while no WuxAuto.exe runs from there - the user's live copy lives in that folder and
# updates itself from the GitHub release (2026-10-27: a release failed with "Access to the path
# '...\dist\WuxAuto\Accessibility.dll' is denied" during a run; a build must never stop a run).
# -Version 1.2.3 stamps that version into the app (release.ps1 passes the release tag); without it
# the <Version> from WuxAuto.csproj is used.
param([string]$Version)
$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$dist = Join-Path $root 'dist'
$build = Join-Path $dist 'build\WuxAuto'
$app = Join-Path $dist 'WuxAuto'
$zip = Join-Path $dist 'WuxAuto-win-x64.zip'

if (Test-Path $build) { Remove-Item $build -Recurse -Force }
if (Test-Path $zip) { Remove-Item $zip -Force }

$versionArgs = @()
if ($Version) {
    if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 1.2.3 (got '$Version')." }
    $versionArgs = @("-p:Version=$Version")
}

dotnet publish (Join-Path $root 'src\WuxAuto\WuxAuto.csproj') `
    -c Release -r win-x64 --self-contained true -p:DebugType=none -o $build @versionArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

# OpenCV's video I/O plugin (27 MB) is only loaded by VideoCapture/VideoWriter, which the app does not use.
Get-ChildItem $build -Filter 'opencv_videoio_ffmpeg*.dll' | Remove-Item -Force

# The quick start for whoever gets the zip goes on top, where it is seen first.
Move-Item (Join-Path $build 'Assets\QuickStart.md') (Join-Path $build 'README.md') -Force

# The zip holds one WuxAuto\ folder (the in-app updater accepts the exe at the root or one level down).
Compress-Archive -Path $build -DestinationPath $zip
$size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "Done: $zip ($size MB)"

# dist\WuxAuto is the copy the user runs: refresh it only while nothing runs from it.
$running = @(Get-Process WuxAuto -ErrorAction SilentlyContinue | Where-Object {
        $_.Path -and $_.Path.StartsWith($app + '\', [System.StringComparison]::OrdinalIgnoreCase) })
if ($running.Count -gt 0) {
    Write-Host "dist\WuxAuto is in use (WuxAuto.exe PID $($running.Id -join ', ')) - left as it is; it updates itself from the release."
}
else {
    if (Test-Path $app) { Remove-Item $app -Recurse -Force }
    Copy-Item $build $app -Recurse
    Write-Host "Refreshed $app"
}

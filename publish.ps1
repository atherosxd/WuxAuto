# Builds a self-contained copy of WuxAuto that runs on another PC without installing .NET.
# Output: dist\WuxAuto\ (the app folder) and dist\WuxAuto-win-x64.zip (to hand over).
# -Version 1.2.3 stamps that version into the app (release.ps1 passes the release tag); without it
# the <Version> from WuxAuto.csproj is used.
param([string]$Version)
$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$dist = Join-Path $root 'dist'
$app = Join-Path $dist 'WuxAuto'
$zip = Join-Path $dist 'WuxAuto-win-x64.zip'

if (Test-Path $app) { Remove-Item $app -Recurse -Force }
if (Test-Path $zip) { Remove-Item $zip -Force }

$versionArgs = @()
if ($Version) {
    if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 1.2.3 (got '$Version')." }
    $versionArgs = @("-p:Version=$Version")
}

dotnet publish (Join-Path $root 'src\WuxAuto\WuxAuto.csproj') `
    -c Release -r win-x64 --self-contained true -p:DebugType=none -o $app @versionArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

# OpenCV's video I/O plugin (27 MB) is only loaded by VideoCapture/VideoWriter, which the app does not use.
Get-ChildItem $app -Filter 'opencv_videoio_ffmpeg*.dll' | Remove-Item -Force

# The quick start for whoever gets the zip goes on top, where it is seen first.
Move-Item (Join-Path $app 'Assets\QuickStart.md') (Join-Path $app 'README.md') -Force

Compress-Archive -Path $app -DestinationPath $zip
$size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "Done: $zip ($size MB)"

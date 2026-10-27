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

# Emgu CV's natives arrive twice in a win-x64 publish: NuGet puts the runtime packages' files next
# to the exe (WuxAuto.deps.json lists them there) and their build targets copy the same files again
# to runtimes\win-x64\native (cvextern.dll, the ffmpeg plugin, the MSVC runtime; ~50 MB). With both
# present the process loads cvextern.dll twice: Emgu's loader takes the subfolder copy, while .NET
# binds every P/Invoke to the one next to the exe. Without the subfolder Emgu loads that one too, so
# the subfolder copies go (only the files that also sit next to the exe).
$natives = Join-Path $build 'runtimes\win-x64\native'
if (Test-Path $natives) {
    Get-ChildItem $natives -File | Where-Object { Test-Path (Join-Path $build $_.Name) } | Remove-Item -Force
}

# OpenCV's video I/O plugin (opencv_videoio_ffmpeg*, ~30 MB) is only loaded by VideoCapture/VideoWriter,
# which the app does not use.
Get-ChildItem $build -Recurse -Filter 'opencv_videoio_ffmpeg*.dll' | Remove-Item -Force

$runtimes = Join-Path $build 'runtimes'
if ((Test-Path $runtimes) -and -not (Get-ChildItem $runtimes -Recurse -File)) { Remove-Item $runtimes -Recurse -Force }

# Exactly one OpenCV native: the win-x64 cvextern.dll next to the exe (the app is x64 only).
$cvextern = @(Get-ChildItem $build -Recurse -Filter 'cvextern.dll')
if ($cvextern.Count -ne 1 -or $cvextern[0].DirectoryName -ne $build) {
    throw "Expected one cvextern.dll, next to WuxAuto.exe; found: $(($cvextern | ForEach-Object FullName) -join ', ')"
}

# The quick start for whoever gets the zip goes on top, where it is seen first.
Move-Item (Join-Path $build 'Assets\QuickStart.md') (Join-Path $build 'README.md') -Force
# GPL-3.0: whoever gets the program gets the licence text with it.
Copy-Item (Join-Path $root 'LICENSE') (Join-Path $build 'LICENSE.txt')

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

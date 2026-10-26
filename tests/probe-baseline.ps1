<#
.SYNOPSIS
  Runs every offline probe of tests\WuxAuto.SelfTest over a frozen frame set and writes normalised
  text files, so two runs (before / after a refactor) can be diffed:

    git diff --no-index --stat <outA> <outB>      # empty = identical

.PARAMETER Out
  Output folder (created; existing *.txt in it are overwritten).
.PARAMETER Frames
  Frozen PNG folder. Default: %LOCALAPPDATA%\WuxAuto\parity-frames (a copy of the debug folder,
  never modified). The folder is only read; --frame probes run on copies in a temp folder because
  they write .scan.png / .crop.png next to the frame.
.PARAMETER Only
  Output names to (re)generate, e.g. -Only selftest.txt,selftest-verbose.txt. Default: all.
.PARAMETER SkipBuild
  Do not build the SelfTest project first.

Normalisation (run-specific noise only; scores, boxes, verdicts and OCR text are kept):
  * "<n> ms" / "<n>ms" timings                 -> "<ms>"
  * the SelfTest build folder and repo root     -> "<repo>" (assets: line differs per checkout)
  * kill-screenshot file names' wall-clock stamp -> "<ts>" (selftest "kill screenshots" line only)
  * %TEMP% paths (probe crops, scan copies)     -> "<tmp>"
  * the --frame working copy path               -> "<frame>"
  * lines that are only a timing / temp report  -> dropped
  * line endings -> LF, UTF-8 without BOM
#>
param(
    [Parameter(Mandatory = $true)][string]$Out,
    [string]$Frames = (Join-Path $env:LOCALAPPDATA 'WuxAuto\parity-frames'),
    [switch]$SkipBuild,
    [string[]]$Only = @()          # limit to these output names, e.g. selftest.txt (default: all)
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $PSScriptRoot 'WuxAuto.SelfTest\WuxAuto.SelfTest.csproj'

if (-not (Test-Path $Frames)) { throw "Frames folder not found: $Frames" }
$Frames = (Resolve-Path $Frames).Path
New-Item -ItemType Directory -Force $Out | Out-Null
$Out = (Resolve-Path $Out).Path

if (-not $SkipBuild) {
    Write-Host 'Building WuxAuto.SelfTest (Release)...'
    & dotnet build $project -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'build failed' }
}

$exe = Get-ChildItem (Join-Path $PSScriptRoot 'WuxAuto.SelfTest\bin') -Recurse -Filter 'WuxAuto.SelfTest.exe' |
    Where-Object { $_.FullName -match '\\Release\\' } |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $exe) { throw 'WuxAuto.SelfTest.exe (Release) not found' }
$exePath = $exe.FullName

$tmp = [System.IO.Path]::GetTempPath().TrimEnd('\')
$work = Join-Path $tmp 'wuxauto-probe-baseline-frame'

function Normalize([string[]]$lines, [string[]]$literals) {
    $result = New-Object System.Collections.Generic.List[string]
    foreach ($line in $lines) {
        $l = $line
        foreach ($pair in $literals) {            # literal path -> token (case-insensitive)
            $from, $to = $pair -split '=>', 2
            $l = [regex]::Replace($l, [regex]::Escape($from), $to, 'IgnoreCase')
        }
        $l = [regex]::Replace($l, '\d+(\.\d+)?\s*ms\b', '<ms>')
        $l = [regex]::Replace($l, '(?i)[a-z]:\\[^\r\n]*?wuxauto-[a-z0-9-]+', '<tmp>')
        # the kill-screenshot check names its files by wall-clock time (yyyyMMdd_HHmmss_fff_tag.jpg)
        if ($l -match 'kill screenshots') { $l = [regex]::Replace($l, '\d{8}_\d{6}_\d{3}_', '<ts>_') }
        # drop lines that only carry a timing or a temp path
        if ($l -match '^\s*(\w[\w ]*:\s*)?(<ms>|<tmp>[\\\w.-]*)\s*$') { continue }
        $result.Add($l.TrimEnd())
    }
    return , $result
}

function Run-Probe([string]$name, [string[]]$probeArgs, [string[]]$literals) {
    if ($Only.Count -gt 0 -and $Only -notcontains $name) { return }
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $prev = [Console]::OutputEncoding
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
    try { $raw = & $exePath @probeArgs 2>&1 | ForEach-Object { "$_" } }
    finally { [Console]::OutputEncoding = $prev }
    $code = $LASTEXITCODE
    $lines = Normalize $raw $literals
    $lines.Add("exit code: $code")
    $text = ($lines -join "`n") + "`n"
    [System.IO.File]::WriteAllText((Join-Path $Out $name), $text, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host ("{0,-28} {1,6} lines  exit {2}  {3:0}s" -f $name, $lines.Count, $code, $sw.Elapsed.TotalSeconds)
}

$common = @(
    ("$($exe.DirectoryName)=>" + '<selftest-bin>'),
    ("$repo=>" + '<repo>'),
    ("$tmp=>" + '<temp>')
)

Run-Probe 'selftest.txt'         @()                              $common
Run-Probe 'selftest-verbose.txt' @('--verbose')                   $common
foreach ($probe in 'ring', 'mapopen', 'lodparts', 'shura', 'runskill', 'titles', 'confirm') {
    Run-Probe "$probe.txt" @("--$probe", $Frames) (@(("$Frames=>" + '<frames>')) + $common)
}

if ($Only.Count -gt 0 -and $Only -notcontains 'frame-every-10th.txt') { return }

# --frame on every 10th PNG in name order (ordinal, like the probes), on temp copies
$names = [string[]](Get-ChildItem $Frames -Filter '*.png' |
    Where-Object { -not $_.Name.EndsWith('.scan.png') -and -not $_.Name.EndsWith('.crop.png') } |
    ForEach-Object { $_.Name })
[Array]::Sort($names, [StringComparer]::Ordinal)
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory $work | Out-Null
$frameOut = New-Object System.Collections.Generic.List[string]
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$prev = [Console]::OutputEncoding
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
try {
    for ($i = 0; $i -lt $names.Count; $i += 10) {
        $copy = Join-Path $work 'frame.png'
        Copy-Item (Join-Path $Frames $names[$i]) $copy -Force
        $frameOut.Add("===== $($names[$i])")
        $raw = & $exePath '--frame' $copy 2>&1 | ForEach-Object { "$_" }
        $frameOut.Add("exit code: $LASTEXITCODE (output follows)")
        foreach ($l in (Normalize $raw (@(("$copy=>" + '<frame>'), ("$work=>" + '<work>')) + $common))) { $frameOut.Add($l) }
    }
}
finally { [Console]::OutputEncoding = $prev }
Remove-Item $work -Recurse -Force
[System.IO.File]::WriteAllText((Join-Path $Out 'frame-every-10th.txt'), (($frameOut -join "`n") + "`n"), (New-Object System.Text.UTF8Encoding($false)))
Write-Host ("{0,-28} {1,6} lines  {2} frames  {3:0}s" -f 'frame-every-10th.txt', $frameOut.Count, [math]::Ceiling($names.Count / 10), $sw.Elapsed.TotalSeconds)

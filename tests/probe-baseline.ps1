<#
.SYNOPSIS
  Runs every offline probe of tests\WuxAuto.SelfTest over a frozen frame set and writes normalised
  text files, so two runs (before / after a refactor) can be diffed:

    git diff --no-index --stat <outA> <outB>      # empty = identical

  Exits 1 and prints "PROBE FAILURES: ..." if any probe exits non-zero or prints an unhandled
  exception / stack trace, so a crash that looks the same before and after cannot pass silently.

.PARAMETER Out
  Output folder (created; existing *.txt in it are overwritten).
.PARAMETER Frames
  Frozen PNG folder. Default: %LOCALAPPDATA%\WuxAuto\parity-frames (a copy of the debug folder,
  never modified). The folder is only read; --frame probes run on copies in a temp folder because
  they write .scan.png / .crop.png next to the frame.
.PARAMETER Only
  Output names to (re)generate, e.g. -Only selftest.txt,selftest-verbose.txt. Unknown names are an
  error. Default: all.
.PARAMETER SkipBuild
  Do not build the SelfTest project first (the fixed output folder must already exist).

The SelfTest is built into tests\WuxAuto.SelfTest\bin\Release\probe-baseline (git-ignored) and that
exe is run, so a stale exe from another build is never picked up.

Normalisation (run-specific noise only; scores, boxes, verdicts and OCR text are kept):
  * the SelfTest's own timing forms only: "(12 ms)", "in 12ms", "median 0.12ms", a trailing "12.3ms"
    -> "<ms>". Lines that echo OCR text ("N line(s): ...", "saw N word(s): ...", "ocr line: ...",
    "ocr match: ...") are never touched by this rule.
  * the SelfTest build folder and repo root     -> "<selftest-bin>" / "<repo>" (assets: line)
  * %TEMP% paths (probe crops, scan copies)     -> "<temp>" / "<tmp>"
  * the --frame working copy path               -> "<frame>"
  * kill-screenshot file names' wall-clock stamp -> "<ts>" (selftest "kill screenshots" line only)
  * lines that are only a timing / temp report  -> dropped
  * line endings -> LF, UTF-8 without BOM
#>
param(
    [Parameter(Mandatory = $true)][string]$Out,
    [string]$Frames = (Join-Path $env:LOCALAPPDATA 'WuxAuto\parity-frames'),
    [switch]$SkipBuild,
    [string[]]$Only = @()
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $PSScriptRoot 'WuxAuto.SelfTest\WuxAuto.SelfTest.csproj'
# Depth matters: the SelfTest finds src\WuxAuto\Assets five folders above its exe.
$binDir = Join-Path $PSScriptRoot 'WuxAuto.SelfTest\bin\Release\probe-baseline'
$exePath = Join-Path $binDir 'WuxAuto.SelfTest.exe'

$probeNames = @('ring', 'mapopen', 'lodparts', 'shura', 'runskill', 'titles', 'confirm')
$allOutputs = @('selftest.txt', 'selftest-verbose.txt') + ($probeNames | ForEach-Object { "$_.txt" }) + @('frame-every-10th.txt')
foreach ($o in $Only) {
    if ($allOutputs -notcontains $o) { throw "Unknown -Only name '$o'. Valid: $($allOutputs -join ', ')" }
}

if (-not (Test-Path $Frames)) { throw "Frames folder not found: $Frames" }
$Frames = (Resolve-Path $Frames).Path
New-Item -ItemType Directory -Force $Out | Out-Null
$Out = (Resolve-Path $Out).Path

if (-not $SkipBuild) {
    Write-Host 'Building WuxAuto.SelfTest (Release)...'
    & dotnet build $project -c Release -o $binDir --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'build failed' }
}
if (-not (Test-Path $exePath)) { throw "SelfTest exe not found: $exePath (run without -SkipBuild)" }

$tmp = [System.IO.Path]::GetTempPath().TrimEnd('\')
$work = Join-Path $tmp 'wuxauto-probe-baseline-frame'
$failures = New-Object System.Collections.Generic.List[string]

# Lines that echo OCR output: never touched by the timing rule.
$ocrEcho = '\b\d+ line\(s\): |\bsaw \d+ word\(s\):|^\s*ocr (line|match):'

function Normalize([string[]]$lines, [string[]]$literals) {
    $result = New-Object System.Collections.Generic.List[string]
    foreach ($line in $lines) {
        $l = $line
        foreach ($pair in $literals) {            # literal path -> token (case-insensitive)
            $from, $to = $pair -split '=>', 2
            $l = [regex]::Replace($l, [regex]::Escape($from), $to, 'IgnoreCase')
        }
        if ($l -notmatch $ocrEcho) {
            # the SelfTest's own timing forms (FrameProbe / Program / ScreenAnalyzer / VisionModels)
            $l = [regex]::Replace($l, '(?<= )\(\d+(\.\d+)? ms\)', '(<ms>)')            # "(12 ms)"
            $l = [regex]::Replace($l, '\bin \d+(\.\d+)?ms\b', 'in <ms>')               # "in 12.3ms"
            $l = [regex]::Replace($l, '\bmedian \d+(\.\d+)?ms\b', 'median <ms>')       # "median 0.12ms"
            $l = [regex]::Replace($l, '(?<=\s)\d+(\.\d+)?ms$', '<ms>')                 # "... 12.3ms" at end of line
        }
        $l = [regex]::Replace($l, '(?i)[a-z]:\\[^\s"'']*?wuxauto-[a-z0-9-]+', '<tmp>')
        # the kill-screenshot check names its files by wall-clock time (yyyyMMdd_HHmmss_fff_tag.jpg)
        if ($l -match 'kill screenshots') { $l = [regex]::Replace($l, '\d{8}_\d{6}_\d{3}_', '<ts>_') }
        # drop lines that only carry a timing or a temp path
        if ($l -match '^\s*(\w[\w ]*:\s*)?(<ms>|<tmp>[\\\w.-]*)\s*$') { continue }
        $result.Add($l.TrimEnd())
    }
    return , $result
}

# An unhandled exception prints "Unhandled exception." plus "   at Namespace.Method(" frames.
function Find-CrashMarker([string[]]$raw) {
    foreach ($line in $raw) {
        if ($line -match '^\s*Unhandled exception' -or $line -match '^\s+at [\w.<>`+\[\],]+\(') { return $line.Trim() }
    }
    return $null
}

function Note-Failure([string]$what, [int]$code, [string[]]$raw) {
    if ($code -ne 0) { $failures.Add("${what} exit=$code") }
    $marker = Find-CrashMarker $raw
    if ($marker) { $failures.Add("${what} crash marker: $marker") }
}

function Run-Probe([string]$name, [string[]]$probeArgs, [string[]]$literals) {
    if ($Only.Count -gt 0 -and $Only -notcontains $name) { return }
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $prev = [Console]::OutputEncoding
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
    try { $raw = & $exePath @probeArgs 2>&1 | ForEach-Object { "$_" } }
    finally { [Console]::OutputEncoding = $prev }
    $code = $LASTEXITCODE
    Note-Failure $name $code $raw
    $lines = Normalize $raw $literals
    $lines.Add("exit code: $code")
    $text = ($lines -join "`n") + "`n"
    [System.IO.File]::WriteAllText((Join-Path $Out $name), $text, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host ("{0,-28} {1,6} lines  exit {2}  {3:0}s" -f $name, $lines.Count, $code, $sw.Elapsed.TotalSeconds)
}

$common = @(
    ("$binDir=>" + '<selftest-bin>'),
    ("$repo=>" + '<repo>'),
    ("$tmp=>" + '<temp>')
)

Run-Probe 'selftest.txt'         @()                              $common
Run-Probe 'selftest-verbose.txt' @('--verbose')                   $common
foreach ($probe in $probeNames) {
    Run-Probe "$probe.txt" @("--$probe", $Frames) (@(("$Frames=>" + '<frames>')) + $common)
}

if ($Only.Count -eq 0 -or $Only -contains 'frame-every-10th.txt') {
    # --frame on every 10th PNG, in the probes' own order (default string comparer over full paths,
    # excluding .scan.png / .crop.png), on temp copies
    $paths = [string[]](Get-ChildItem $Frames -Filter '*.png' |
        Where-Object { -not $_.Name.EndsWith('.scan.png') -and -not $_.Name.EndsWith('.crop.png') } |
        ForEach-Object { $_.FullName })
    [Array]::Sort($paths)
    if (Test-Path $work) { Remove-Item $work -Recurse -Force }
    New-Item -ItemType Directory $work | Out-Null
    $frameOut = New-Object System.Collections.Generic.List[string]
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $prev = [Console]::OutputEncoding
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
    try {
        for ($i = 0; $i -lt $paths.Count; $i += 10) {
            $name = Split-Path -Leaf $paths[$i]
            $copy = Join-Path $work 'frame.png'
            Copy-Item $paths[$i] $copy -Force
            $frameOut.Add("===== $name")
            $raw = & $exePath '--frame' $copy 2>&1 | ForEach-Object { "$_" }
            $code = $LASTEXITCODE
            Note-Failure "frame:$name" $code $raw
            $frameOut.Add("exit code: $code (output follows)")
            foreach ($l in (Normalize $raw (@(("$copy=>" + '<frame>'), ("$work=>" + '<work>')) + $common))) { $frameOut.Add($l) }
        }
    }
    finally { [Console]::OutputEncoding = $prev }
    Remove-Item $work -Recurse -Force
    [System.IO.File]::WriteAllText((Join-Path $Out 'frame-every-10th.txt'), (($frameOut -join "`n") + "`n"), (New-Object System.Text.UTF8Encoding($false)))
    Write-Host ("{0,-28} {1,6} lines  {2} frames  {3:0}s" -f 'frame-every-10th.txt', $frameOut.Count, [math]::Ceiling($paths.Count / 10), $sw.Elapsed.TotalSeconds)
}

if ($failures.Count -gt 0) {
    Write-Host ''
    foreach ($f in $failures) { Write-Host "PROBE FAILURES: $f" -ForegroundColor Red }
    exit 1
}
Write-Host 'All probes ran clean (exit 0, no crash markers).'

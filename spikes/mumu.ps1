# Helpers for the M0 spike against the MuMu emulator. Dot-source it: . .\mumu.ps1
# Usage: Connect-MuMu; Install-Spike; Enable-Spike; Send-Spike SHOT -Extras '--ei','count','20'
# Extras are one string per am token: -Extras '--ei','dx','0','--ei','dy','-100'
$script:Adb = 'C:\Program Files\Netease\MuMuPlayerARM\shell\adb.exe'
$script:Device = '10.0.2.80:5555'
$script:Pkg = 'io.github.atherosxd.wuxauto.spike'
$script:Here = $PSScriptRoot

function Invoke-Adb { & $script:Adb -s $script:Device @args }

function Connect-MuMu { & $script:Adb connect $script:Device }

function Install-Spike {
    Push-Location (Join-Path $script:Here 'WuxAuto.MobileSpike')
    try {
        dotnet build -c Debug -f net10.0-android
        if ($LASTEXITCODE -ne 0) { throw 'build failed' }
        $apk = Get-ChildItem -Recurse -Filter "$script:Pkg-Signed.apk" -Path bin\Debug |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if (-not $apk) { throw 'signed apk not found' }
        Invoke-Adb install -r $apk.FullName
    } finally { Pop-Location }
}

function Enable-Spike {
    # Append to the existing list so other accessibility services stay on.
    $svc = "$script:Pkg/$script:Pkg.SpikeService"
    $cur = ((Invoke-Adb shell settings get secure enabled_accessibility_services) | Out-String).Trim()
    if ($cur -eq 'null') { $cur = '' }
    if (($cur -split ':') -notcontains $svc) {
        $list = if ($cur) { "${cur}:$svc" } else { $svc }
        Invoke-Adb shell settings put secure enabled_accessibility_services $list
    }
    Invoke-Adb shell settings put secure accessibility_enabled 1
}

function Send-Spike {
    # Extras go to am as-is, e.g. --es text hello --ei x 100
    param([Parameter(Mandatory)][string]$Action, [string[]]$Extras = @())
    Invoke-Adb shell am broadcast -a "$script:Pkg.$Action" -p $script:Pkg @Extras
}

function Get-SpikeFiles {
    # Default destination is spikes/.out (ignored by version control).
    param([string]$Dest = (Join-Path $script:Here '.out'))
    New-Item -ItemType Directory -Force $Dest | Out-Null
    Invoke-Adb pull "/sdcard/Android/data/$script:Pkg/files/shots/." $Dest
}

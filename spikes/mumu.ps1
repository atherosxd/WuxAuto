# Helpers for the M0 spike against the MuMu emulator. Dot-source it: . .\mumu.ps1
$script:Adb = 'C:\Program Files\Netease\MuMuPlayerARM\shell\adb.exe'
$script:Device = '10.0.2.95:5555'
$script:Pkg = 'io.github.atherosxd.wuxauto.spike'
$script:Here = $PSScriptRoot
$script:Out = 'C:\Users\cliof\AppData\Local\Temp\claude\C--Projects-personal-auto-wux\ce7934d6-9877-4c85-ae76-8e4413b6acc6\scratchpad\mumu'

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
    Invoke-Adb shell settings put secure enabled_accessibility_services "$script:Pkg/$script:Pkg.SpikeService"
    Invoke-Adb shell settings put secure accessibility_enabled 1
}

function Send-Spike {
    # Extras go to am as-is, e.g. --es text hello --ei x 100
    param([Parameter(Mandatory)][string]$Action, [string[]]$Extras = @())
    Invoke-Adb shell am broadcast -a "$script:Pkg.$Action" -p $script:Pkg @Extras
}

function Get-SpikeFiles {
    New-Item -ItemType Directory -Force $script:Out | Out-Null
    Invoke-Adb pull "/sdcard/Android/data/$script:Pkg/files/shots/." $script:Out
}

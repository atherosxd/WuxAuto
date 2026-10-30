# WuxAuto — project handoff

Single-file briefing for picking this project up in a new session. Written 2026-10-20 from the
state of `C:\Projects\personal\auto-wux`. Git since 2026-10-24 (public: github.com/atherosxd/WuxAuto);
earlier history exists only in this file.

---

## 1. What this is

`WuxAuto` is a **.NET 10 / WPF** pilot for the game window `Wuxen2.exe` (TwelveSky2, Unity port).
It captures the game with the **Desktop Duplication API**, finds UI elements with **OpenCV template
matching (through Emgu CV 5) + Windows.Media.Ocr**, and sends **keyboard/mouse input only to the
bound window**.

The automation goal:

1. Teleport into **[The Labyrinth (Adept)]** via the in-game teleport scroll.
2. Clear a **4-stage dungeon**, stage by stage, using the map window to pick the next boss.
3. After the last stage: **face north**, then **run to the world coordinate `3325,1932`** (the 5th/last boss; it is not in the map list, it is reached on foot)
   (tolerance 30).

Everything it does is visible: detections are drawn on the live preview and every step is logged.

Three more modes sit beside the Labyrinth (the Mode buttons above Start): **Auto LOD** (§4.5),
**Auto War** (§4.8, user 2026-10-27: an alt waits in a page-3 cave, joins every Bohai Outpost tribe war,
fights, comes back to the same cave and monster; one game per WuxAuto, no walking inside Bohai) and
**Auto Blossom** (§4.9, user 2026-10-29: Jaso Practitioners on a route in Blossom Garden; since v25 the user's recorded
83-waypoint route, walked there and back).

The user is non-technical about the internals but precise about the game behaviour — when they
report "it didn't click X", the fastest path is almost always (a) read the debug snapshot from
`%LOCALAPPDATA%\WuxAuto\debug`, and (b) run the offline frame probe over it.

---

## 2. Build, run, test

```powershell
# the Debug exe is locked only while *that* build runs. The user normally runs the installed copy
# (dist / self-update folder), which a Debug build does not touch - do NOT kill WuxAuto blindly, it
# stops their live run (2026-10-26, 13:07: a build killed a run at stage 2). Only when the build
# fails with a file lock, and after asking:
# Get-Process WuxAuto -ErrorAction SilentlyContinue | Stop-Process -Force

dotnet build .\WuxAuto.slnx
.\src\WuxAuto\bin\Debug\net10.0-windows10.0.19041.0\WuxAuto.exe
```

Another PC: `.\publish.ps1` → `dist\WuxAuto-win-x64.zip` (self-contained, no .NET install; Emgu CV's
native `cvextern.dll` and the MSVC runtime are kept once, next to the exe — the second copies the
runtime packages' build targets add under `runtimes\win-x64\native` made the process load
`cvextern.dll` twice and are dropped, as is the unused `opencv_videoio_ffmpeg` plugin; the script
fails unless exactly one `cvextern.dll` is left; `LICENSE` goes in as `LICENSE.txt`).
`App.OnStartup` runs `Core/StartupCheck` (desktop project) before the window: Media Foundation
DLLs (`MF.dll`, `MFPlat.DLL`, `MFReadWrite.dll`, imported by `cvextern.dll` — missing on Windows N and
trimmed "lite" builds such as Ghost Spectre; fatal), OpenCV load (fatal), English OCR and build
≥ 19041 (warnings). Report → `logs\startup_check_*.txt`, unhandled errors → `logs\crash_*.txt`.
`StartupUri` was removed: `App` creates `MainWindow` itself so a failed start exits instead of
leaving a windowless process (2026-10-21, a friend's Windows 10 "Spectre" PC would not start it).
The friend's second PC (Windows 10 build 19041.964) then crashed inside the runtime (`0x80131506`,
no app log) — cause unknown. The package ships `Diagnose.cmd` (copied from `src/WuxAuto`): stderr
capture + system facts + retries with `DOTNET_EnableWriteXorExecute=0`, then tiered JIT off, then
HW intrinsics / concurrent GC off → `diagnose.txt`. Whichever attempt first runs names the fix.

**Repo + self-update (2026-10-24, user):** public GitHub repo `atherosxd/WuxAuto`; versions are git
tags `vX.Y.Z` with a release carrying `WuxAuto-win-x64.zip`. `.\release.ps1 X.Y.Z "notes"` (main,
clean, pushed) → `publish.ps1 -Version` → `gh release create --target <HEAD>` (GitHub makes the tag). `Core/UpdateService` (desktop) reads
`releases/latest` on start (Yes/No with the notes) and on **Check for updates**; install = download to
`%TEMP%\WuxAuto-update`, hidden `install_update.cmd` waits for the PID, `robocopy /E` over the app folder,
restarts; log `logs\update_*.log`. Refused while running, from a build folder (no `coreclr.dll`) or a
read-only folder. Probes: `--update-check [owner/repo]`, `--update-install <appFolder> <zip>`.

Package for someone else (self-contained, no .NET needed; `src/WuxAuto/Assets/QuickStart.md` becomes
the zip's top-level `README.md`):

```powershell
.\publish.ps1     # -> dist\build\WuxAuto\ (the build), dist\WuxAuto-win-x64.zip (~76 MB) and dist\WuxAuto\
                  # (refreshed from the build only while no WuxAuto.exe runs from it - the user's live
                  # copy lives there and updates itself from the release; 2026-10-27 a release died on
                  # "Access to the path '...\dist\WuxAuto\Accessibility.dll' is denied" mid-run, and
                  # Remove-Item -Recurse had already wiped Assets\ (all 13 templates) of the live copy
                  # before the locked DLL stopped it - the run went on with the templates in memory)
```

Offline checks (no game needed):

```powershell
# synthetic-frame self test: template positions, OCR, compass at 5 angles, high level scan
dotnet run --project .\tests\WuxAuto.SelfTest                 # --verbose for per-angle scores

# run the whole "Test scan" pipeline over a saved PNG and annotate it
dotnet run --project .\tests\WuxAuto.SelfTest -- --frame <png> [--crop x y w h zoom]

# minimap zone title + Bohai/Cancel words for every PNG in a folder (zone guard check)
dotnet run --project .\tests\WuxAuto.SelfTest -- --titles "$env:LOCALAPPDATA\WuxAuto\debug"

# DXGI capabilities of the machine / capture real frames / script input
dotnet run --project .\tests\WuxAuto.SelfTest -- --dxgi
dotnet run --project .\tests\WuxAuto.SelfTest -- --capture
dotnet run --project .\tests\WuxAuto.SelfTest -- --input I
dotnet run --project .\tests\WuxAuto.SelfTest -- --do key:I --do rclick:661,285 --do wait:900
dotnet run --project .\tests\WuxAuto.SelfTest -- --do wheel:891,350,-10   # wheel x,y,notches (negative = down)
dotnet run --project .\tests\WuxAuto.SelfTest -- --do ctrlclick:960,400   # Ctrl+left click (walks there)
dotnet run --project .\tests\WuxAuto.SelfTest -- --do ctrlkey:1   # Ctrl + key tap (the manual attacks' Ctrl + skill key)
dotnet run --project .\tests\WuxAuto.SelfTest -- --do ctrlring --do ctrlbelt   # LOD: Ctrl+click the boss ring / belt found live

# selection ring (FindSelectionRing) for every PNG in a folder; crops -> %TEMP%\wuxauto-ring (the pilot no longer reads it, 2026-10-27)
dotnet run --project .\tests\WuxAuto.SelfTest -- --ring "$env:LOCALAPPDATA\WuxAuto\debug"

# map window open / shut for every PNG in a folder (the pilot's MapWindowVisibleAsync test, next to the old one)
dotnet run --project .\tests\WuxAuto.SelfTest -- --mapopen "$env:LOCALAPPDATA\WuxAuto\debug"

# LOD boss ring + belt for every PNG in a folder (fast, no OCR)
dotnet run --project .\tests\WuxAuto.SelfTest -- --lodparts "$env:LOCALAPPDATA\WuxAuto\debug"

# LOD Shura icon on the skill bar (score, box) for every PNG in a folder
dotnet run --project .\tests\WuxAuto.SelfTest -- --shura "$env:LOCALAPPDATA\WuxAuto\debug"

# Run skill icon + its gold ring (on/off) on the skill bar for every PNG; crops -> %TEMP%\wuxauto-runskill
dotnet run --project .\tests\WuxAuto.SelfTest -- --runskill "$env:LOCALAPPDATA\WuxAuto\debug"

# kill alert: plays the chime, shows a test notification, checks the notification centre kept it
dotnet run --project .\tests\WuxAuto.SelfTest -- --alert
# a sound file only (MP3/WAV/OGG): copies it into a scratch sounds folder (an OGG decoded to WAV),
# checks the copy opens and its position moves, plays it out loud (ffmpeg -f lavfi -i sine=... makes test files)
dotnet run --project .\tests\WuxAuto.SelfTest -- --alert <file>

# auto-confirm dialog (message line + OK button) for every PNG (absolute path; tests\frames holds the user's crop)
dotnet run --project .\tests\WuxAuto.SelfTest -- --confirm "$env:LOCALAPPDATA\WuxAuto\debug"

# Auto War probes (tests/WuxAuto.SelfTest/WarProbe.cs), each over a folder of PNGs
dotnet run --project .\tests\WuxAuto.SelfTest -- --hp "$env:LOCALAPPDATA\WuxAuto\debug"      # top-left "cur / max" HP read (white-text read, plain fallback)
dotnet run --project .\tests\WuxAuto.SelfTest -- --target "$env:LOCALAPPDATA\WuxAuto\debug"  # the fight's target read: which read decided (x1.5 / white / red / numbers / chest) + the old name read
dotnet run --project .\tests\WuxAuto.SelfTest -- --warwalk "$env:LOCALAPPDATA\WuxAuto\debug" # the walk out of the respawn point: goal name tag + Ctrl+click point per frame
dotnet run --project .\tests\WuxAuto.SelfTest -- --warui "$env:LOCALAPPDATA\WuxAuto\debug"   # where the UI text lies per client size (what WarWalkBands is measured from)
dotnet run --project .\tests\WuxAuto.SelfTest -- --warbar "$env:LOCALAPPDATA\WuxAuto\debug"  # BuffIcons + WeaponBuffIcons + ItemBuffIcons + Shura on the skill bar
dotnet run --project .\tests\WuxAuto.SelfTest -- --menu "$env:LOCALAPPDATA\WuxAuto\debug"    # the menu button and the MENU window
dotnet run --project .\tests\WuxAuto.SelfTest -- --tribe "$env:LOCALAPPDATA\WuxAuto\war-inputs"  # TRIBE STATUS: the Bohai row's countdown (the user's screenshot lives there, never in git)

# Auto Blossom: the two pillar bases (hits >= 0.30 in ScreenAnalyzer.PillarBand) + the pair / offset verdict per PNG
dotnet run --project .\tests\WuxAuto.SelfTest -- --pillars <dir>

# M2 parity: the pilot's detectors and x1.5 OCR reads on the frozen legacy vision (tests\...\Legacy,
# OpenCvSharp 4.13) vs Core's (Emgu CV), per frame; ends with two summary lines (see below)
dotnet run --project .\tests\WuxAuto.SelfTest -- --parity "$env:LOCALAPPDATA\WuxAuto\parity-frames"
```

`--parity` prints every difference line, the coverage per check, the pilot-view rules and then two
summary lines. `pilot-view: differences=… excluded=…` counts capped hit lists the way the pilot's
call site consumes them (best pick, count, the focused-row chevron, the fallback click point) and
leaves out detectors the pilot no longer calls (`compass`, `compass-angle` = Test scan only;
`selectionring`); the last line, `parity: frames=… checks=… differences=…`, is the strict view (every
check, hit lists hit by hit; exit code 1 when it is not 0). A difference line tagged `[strict-only]`
counts only in the strict line, `[pilot-view]` only in the pilot-view line. The OCR checks (`dest-*`,
`bossname-s*`, `lod-*`, `settingswin-*`, `attacklog`, `autohunt-*`, `gatetext*`, `stagewide*`) read each
region once per side at `Detection.OcrScale` and judge every needle with the pilot's own rules. Auto War
(merged from main 2026-10-28) is covered too: `warbar-*` (FindSkillBarIcons per buff icon, Shura, X buff),
`target-*`, `player-hp`, `menubutton`, `huntsettings-*`, `itemlist-*`, `warlist-x<scale>-*` (teleport list
at x1.5 / x2 / x1), `wargate`, `menutile`, `warwindow*`, `warcountdown`, `tribestatus-shows`, `wardialog-*`,
`warlog-*`, `warmap-*` (MonsterRows for the stage names) and `bossring-away`; the legacy side runs main's
OpenCvSharp code for them, frozen verbatim in `Legacy/ScreenAnalyzer.War.cs` (a `partial` of the legacy
analyzer). The saved frames hold no AUTO BUFF dialog, war gate or "Arrived" line, so those checks only
compare "not found" until such frames exist. There is no built-in pass criterion: whether the Emgu CV switch is accepted with the differences found is the
user's decision, recorded in the M2 PR.

`--do` frames land in `%TEMP%\wuxauto-step<N>-<verb>.png`. Check the frame before every click: an
Escape with a target selected only clears the target (no SETTINGS window), and the clicks meant for
the window then walk the character (2026-10-24). `WuxAuto.slnx` builds `tests\WuxAuto.SelfTest` too, but
into `bin\x64\Debug\…` (the solution maps it to x64), while `dotnet run --project tests\WuxAuto.SelfTest`
uses `bin\Debug\…` — run `dotnet build tests\WuxAuto.SelfTest` before `--no-build` runs, or the probe is stale.

The frame probe prints, per template, the best score, box, learned scale and timing, plus OCR
words and the map scroll-arrow result. `--crop` writes `<frame>.crop.png` next to the frame.

Core tests and the refactor parity gate (M1, 2026-10-27):

```powershell
dotnet test tests\WuxAuto.Core.Tests        # xUnit, platform-free Core only (614 tests, ~7.5 min; the pilot tests tick a
                                           # blank Mat with a scripted OCR scene - see tests/ in section 3)
# probe baseline: runs every SelfTest probe over a frozen, local-only frame set (never committed)
# and writes normalised output; run it before and after a refactor and diff the two
.\tests\probe-baseline.ps1 -Out <dir> [-Frames <dir>] [-SkipBuild] [-Only <probe,...>]
git diff --no-index --stat <a> <b>          # compare two runs; empty = identical
```

* Default frames: `%LOCALAPPDATA%\WuxAuto\parity-frames`, a one-off copy of `debug\*.png` (517 frames
  on 2026-10-26), never modified afterwards.
* A full run takes ~12-14 min; the script exits 1 and prints `PROBE FAILURES` if a probe fails.
* The probes read the live `settings.json` and any `templates\` overrides, so changes there show up as diffs.
* M1's move-only split was checked against the pre-M1 baseline after the seam step (Task 7) and after
  the move (Task 8), cumulatively; Task 6 had no run of its own.
* The M1 baseline (`baseline-a`) lived only in a session scratchpad; regenerate it from main or v0.11.0
  when M2 needs one.

The app itself:

| Control | Effect |
| --- | --- |
| process list + **Bind / Unbind** | attaches capture to that HWND, logs the client size |
| **Start / Stop** (F9 when the game is focused) | starts the pilot state machine |
| **F10** | panic: stop + unbind |
| **F11** | save the current frame, with overlays, to `%LOCALAPPDATA%\WuxAuto\debug\` |
| **Test scan now** | runs detections on one frame and logs everything |
| **Mini** (next to Stop) | `MiniWindow`: Laby / LOD, Start, Stop, status line, "On top" (`Ui.MiniAlwaysOnTop`, saved), "Full"; opens at `Ui.MiniPosition` of the work area (settings panel → "Mini mode" → Position: 9 spots, default **BottomLeft** (user corrected BottomRight), 8 px from the edges; checked live: TopLeft 12,12 / Middle 881,623 / BottomCenter 881,1233 / MiddleRight 1750,623 / BottomRight 1750,1233 on 2304×1488), the main window hides until it closes; the status line wraps in a fixed 3-line box so the window never resizes (user, 2026-10-23) |
| small screens | `MainWindow.FitToScreen`: the 1380×880 window shrinks to the work area, min 760×500 (user, 2026-10-23: it ran past a small screen and the title bar buttons were out of reach). Drop-downs: the `ComboBox` template draws its own box (border #3A4458) with the chevron in a right-hand column; the old one showed no box and the chevron on the left when narrow |
| **Dry run (no input)** | full detection, zero input — a "nothing happens" run is often dry-run |
| **Focus game**, **Reload templates**, **Templates folder**, **Debug snapshots**, **Save settings** | obvious |
| **Kill screenshots** (user, 2026-10-24: "to check whether it looted or not") | opens `%LOCALAPPDATA%\WuxAuto\kills`. `AutomationPilot.KillShots.cs` + `Vision/KillScreenshots` (both in WuxAuto.Core): plain JPEG (q 90, no overlays, newest 500 kept) when the pilot sees a boss die — Laby `WatchBoss` (banner / name gone) → `laby_s<N>_<boss>`, LOD death text → `lod_kill<N>`, target gone 2 s → `lod_gone` — and a second one when it moves on: LOD at the end of `LodLoot` (`_looted`); Laby 3 s later (`_after`, only in a zone-guarded phase) or earlier, right before `ClickMap` / `OpenInventory` act (the final boss's return teleport opens the inventory ~1.6 s after). Restarts drop a pending shot (`ResetRunState`). Not yet seen live |
| **Kill chime / Kill notification / Test alert** (user, 2026-10-26: friends run the pilot on a second PC next to their main one and LOD does not always loot everything - "a reminder to recheck"; "do the same for Laby") | `AutomationPilot.BossKilled` (string, loop thread) → `WuxEngine.BossKilled` → `MainViewModel.OnBossKilled` (UI thread) → `Core/KillAlert` (desktop project, `src/WuxAuto/Core/`). Raised in `OnLabyBossDead` (both `WatchBoss` kill paths, every stage incl. 5: "Stage 2: Spectral down") and at the **end** of `TickLodLoot` ("Bai Gu Great down" - after the loot, the reminder is to check what it left; both LOD kill paths go through it). Chime: `Sequence.KillSound` if set (**own sound, user 2026-10-27**: "Sound:" row under the check boxes, **Choose...** = `OpenFileDialog` *.mp3;*.wav;*.ogg → `KillAlert.ImportSoundAsync` copies it into `AppPaths.SoundsFolder` (`%LOCALAPPDATA%\WuxAuto\sounds`; same contents reused, same name with other contents → `name-2.ext`; **an .ogg is decoded to `name.wav`** by `Core/OggToWav` (user 2026-10-27 "also allow ogg"; NVorbis 0.10.5, MIT, managed - WPF `MediaPlayer` answers "Media file download failed" for OGG on this PC, Vorbis and Opus alike, and N / lite builds have even less): 16-bit PCM at the file's own rate and channels, decoded in `Task.Run` to `%TEMP%\wuxauto-ogg-*.wav`, then moved in; Ogg Opus ("OpusHead" in the first page), non-Ogg and damaged files and sounds over 5 min (`OggToWav.MaxLength`) are refused with a plain reason; a hand-edited `KillSound` pointing at an .ogg is not converted and falls back to the built-in chime), `ProbeAsync` must open it with sound within 5 s (else refused, the copy deleted), saved at once, played once; **Default** clears it; a set but missing / failing file → built-in + warning, the label shows "(missing)"), else `templates\chime.wav` if present (a bad file → built-in + warning), else `KillAlert.GenerateChime` (A5-C#6-E6, 1.6 s, 16-bit mono) written to `%LOCALAPPDATA%\WuxAuto\chime-builtin.wav` - **played from a file on purpose**: `SoundPlayer` from a stream hands a managed buffer to async `PlaySound` (pinned only during the call). Sound files (KillSound, chime.wav) play through WPF `MediaPlayer` (MP3 and every WAV kind; `SoundPlayer` only takes PCM WAV), kept open between kills, `Volume` 1.0 (it starts at 0.5), UI thread only (`PlayChimeAsync` waits for `MediaOpened` / `MediaFailed`; the SelfTest runs it on an STA thread with its own dispatcher); the built-in chime stays on `SoundPlayer`, so the fallback needs nothing from Windows Media. MP3, float WAV and names with `#` / `%20` verified with ffmpeg-made files (`--alert <file>`: opens, position moves, copy plays); OGG: mono 44.1 kHz, stereo 48 kHz and 5.1 convert and play, Opus and a 6 min file are refused; the SelfTest decodes `tests/WuxAuto.SelfTest/Sounds/tone.ogg` (1 s, 440 Hz left / 880 Hz right) and checks rate, channels and per-channel pitch; the **Choose...** dialog itself was not clicked in the real app. Notification: WinRT toast, silent, 2 h expiry, AUMID `atherosxd.WuxAuto` registered under `HKCU\Software\Classes\AppUserModelId` (DisplayName + `notify-icon.png` from the window icon) - an unpackaged app needs that; `ToastNotifier.Setting` throws "Element not found" here, so it is only read best-effort. `Sequence.KillChime` / `KillNotification` (on; check boxes under Loop). Status box + Mini window bottom row show `LastKillText`. A failure logs once per app start; **Test alert** always reports. Verified 2026-10-26 on this PC: chime plays, notification centre count for the AUMID 0 → 1, the button in the real app logs "chime OK" / "notification OK". Not yet seen during a live kill |
| checkboxes | `Loop`, `Focus on start`, `Only while game is focused`, `Restore cursor` |

**Phone app (M4, `src/WuxAuto.Mobile`, Android):** build **from `src\WuxAuto.Mobile`** — its `global.json` pins
SDK 10.0.402 (maui-android workload); the repo root resolves SDK 11 rc, which has no android workload:

```powershell
cd src\WuxAuto.Mobile; dotnet build ..\..\WuxAuto.Mobile.slnx -c Debug   # -> bin\Debug\net10.0-android\io.github.atherosxd.wuxauto-Signed.apk
$adb = 'C:\Program Files\Netease\MuMuPlayerARM\shell\adb.exe'; $dev = '10.0.2.80:5555'   # MuMu's IP changes on restart: Get-NetNeighbor -InterfaceAlias 'vEthernet (MuMuProVNet)'
& $adb connect $dev; & $adb -s $dev install -r bin\Debug\net10.0-android\io.github.atherosxd.wuxauto-Signed.apk
# enable the accessibility service (append to the existing list, never replace it)
$svc = 'io.github.atherosxd.wuxauto/io.github.atherosxd.wuxauto.PilotAccessibilityService'
$cur = ((& $adb -s $dev shell settings get secure enabled_accessibility_services) | Out-String).Trim(); if ($cur -eq 'null') { $cur = '' }
if (($cur -split ':') -notcontains $svc) { & $adb -s $dev shell settings put secure enabled_accessibility_services ($(if ($cur) { "${cur}:$svc" } else { $svc })) }
& $adb -s $dev shell settings put secure accessibility_enabled 1
& $adb -s $dev logcat -s WuxAuto                                           # every log line also goes to logcat
```

MuMu display (owner, 2026-10-30): with MuMu's default display the MuMu *window* on the PC turned portrait when
WuxAuto opened, though the app and the game inside stayed landscape (both activities are `SensorLandscape`). MuMu
settings → device **Phone**, display **portrait** keeps the window and both apps landscape - a MuMu setting, not code.
On a real phone the service is switched on by hand (Setup page; Android 13+: App info → ⋮ → Allow restricted
settings first). Data (settings.json, logs, debug, templates) lives in `/sdcard/Android/data/io.github.atherosxd.wuxauto/files`.
The round **W** button (accessibility overlay, default spot top strip 0.40/0.03) is the in-game control: tap =
panel, long-press = Panic; the notification's Stop and the Pilot page also stop. Debug builds take adb probes
(`DebugReceiver`, `am broadcast -a io.github.atherosxd.wuxauto.<PING|FRONT|SHOT|OCR|TAP|...> -p io.github.atherosxd.wuxauto`).

**Phone fixes 2026-10-30 (Xiaomi 17 Pro Max 2608x1200 + MuMu buff report):** the Xiaomi HUD scales with the height
exactly (every hit x1.11 = 1200/1080) - the "finds nothing" frames were login screens; its real failure was
`ShortcutCheck` deciding "missing" from the login / loading screens' cached absences after a re-login. Now: `Begin`
invalidates the `ShortcutMap`, the positions reset (every Start / restart) does too, and a shortcut is missing only
when two searches in a row on frames whose zone title reads miss it (`MaxTitleLooks` 20 without the title, then
anyway). ☰ zone anchored to the right edge (it sits 734 px from it at 1080). `phone_autohunt` = the yellow crossed
swords on the ring (HUNT; AUTO BUFF is the same swords in white - `ShortcutMap.YellowFraction` tells them apart, ≥ 0.15
yellow = HUNT); padlock templates cut to the gold lock (no see-through slot). A tap while a finger is held (Hold fight)
= lift, 150 ms, tap, 400 ms, press again (`TouchControls.LiftBeforeTapMs` / `TapBeforeRepressMs`). Phone buff / loot
log every tap ("Auto buff tapped at x,y", "Loot: N taps over M s"), warn once on "Force 90% and over required" and on
a missing AUTO BUFF / Pick up, and check a buff ~3 s later on the row under the HP bar (timers if they read, else the
row's pixels; unchanged = one more tap, then "Auto buff did not take"). Open: the Xiaomi ring shows a second padlock
(top left, reads open, no (+) shown) - what it does is the owner's word.

**Phone LOD, Xiaomi live test 2026-10-30 (`PhoneSettings`):** `LodArrivalWalkMs` 2000 - on arrival and back at the start
after dying the joystick is pushed up-left on the screen for 2 s before the sword taps ("northwest", the boss arena is left
of the arrival spot; the camera keeps the teleport's heading, there is no south turn on the phone). `LodBossTapAfterTabs` 3
- three sword taps without the boss targeted → a plain tap on its pink ring, else just below its belt (`FindApproachSpot`).
`HoldRepressMs` 2000 - the held attack is lifted and pressed again every 2 s (the player's own touch can end the held
finger unseen; user asked 5 s, then 2 s). Open from that test: the sword "unselects the boss" (cause unknown - debug bundle
pending) and a heads-up notification pausing the pilot. **Debug bundle 08:20 answered the sword one:** the minimap's
"1218 , 1824" read as "1218 , 824" (leading 1 dropped at 2608x1200) six times in one run → a false "killed by the boss"
(jump > `TeleportJump`) → fight left, sword tapped. The phone now needs a jump read twice (next read within 60,
`CheckLodJump`); the desktop is unchanged. Also: the phone's LodRun reads the frame before the sword tap (the sword
went out on its timer with the boss already targeted). Still open: on the phone the "pink ring" often hits the boss's
pink weapon trail (second walk-up click 3-5 radii out); the owner asks for font 120 % and interface scale 1.10 (+ button)
on the phone like the desktop's Settings visit (not ported yet).
**Phone Settings visit + reconnect (2026-10-30, MuMu 2560x1440, verified live):** the phone runs the shared FontSize
phase: ☰ → the "Settings (ESC)" tile (label read cut off as "ettings (ES"; the window counts as open only on a line that
is exactly "SETTINGS") → VISUAL → **Interface scale** to `PhoneSettings.InterfaceScale` 1.10 with its − / + (AUTO = 1.00,
steps of 0.01; first, because it resizes the window; the − is a ~2 px dash under `FindBrightBlobs`' 3 px floor, found by
its own flat-blob search - the AUTO button's border was tapped once) → Font size to exactly `FontSizePercent` 120 → TAB
target → **no SAVE** (the phone game keeps every change at once: scale and font survived a full game restart without
it, and the chat box's text field lies over SAVE and opens the keyboard at every tap height at scale 1.10) → the
window's X (`FindCloseBlobRightOf` reach 9, blobs ≥ 0.5 title heights - a 5x4 border speck was tapped once). Every
tap waits until the value read moves (`BeforeLastTap`, ≤ 2.5 s: the capture lags; it stopped at 1.09 and overshot to
1.11 before), and after a ☰ / tile tap fresh frames are read for 4 s before ☰ is tapped again (a second tap closed
a menu that had just opened). Minimap title: ML Kit read "[" as "(" after a MuMu restart → on the phone a leading
"(" / "{" counts as "[" (`WarText.PhoneTitleLine`). Reconnect: the pilot alone went server list → Online → CONNECT →
LOG IN → OK → START GAME → town in 15 s; while "Login OK" reads it taps OK again (≥ 3 s apart), never START GAME behind
the dialog. Two earlier "reconnect loops" were a hung game (gestures delivered, MuMu restarted itself after). Debug
builds take START / STOP adb probes. **1920x1080 (same day, verified live):** reconnect + visit work there too. Two
fixes: (1) the scale moves the shortcut rings - at 1.12 the left ring's Teleport Scroll slot sat off the screen edge and
the shortcut check (before the visit) opened the ring editor for it; on the phone, **in town the visit now runs before
the shortcut check** (`TickShortcutCheckAsync`, once per Start, not in a dry run; the check then starts over with
`ShortcutCheck.Begin`), and a scale change invalidates the `ShortcutMap`; (2) the font row's right chevron lay past
the 30-text-height band (label 14 px high, band end 1199, chevron at 1200) - the phone passes
`ReadSettingRowAsync(..., chevronReach: 10)` (10 heights past the value); the desktop keeps the 30-height band (its
same 1920x1080 cut-off is still open, §4.1). A menu button that is not found ends the visit after 10 tries.
Review round (same day): HUNT is **not** a required shortcut (used where found, never added) and the town autohunt-off
check does not run on the phone (its log read is unverified there; since M5 it does while HUNT shows the "+", below); a hit whose colour cannot be told (dark middle) is
neither HUNT nor AUTO BUFF; the buff check counts a miss only when timers that read did not go up **and** the first row
of buff icons (0,125 340x55 at 1080) did not change - then one more buff through `TryLodBuff` (Shura + 400 ms mid-fight),
else it only logs; no check in a dry run. The shortcut check's two misses are >= 1.5 s apart, and 20 looks without the
zone title stop the check ("the game screen (zone title) was not seen") instead of deciding. A tap during a hold does not
press the old hold again after a Halt / ReleaseAll / another hold (generation counter).

**Phone Laby, MuMu 1920x1080 live run (2026-10-30, M5):** stage 1 picked and the boss seen, then the pilot went silent:
(1) **attack icon cut off** - at interface scale 1.10 the hotbar's first slot spans 625-687 x 983-1045 and the old hotbar
search box (630, 975, 660, 105) cut it: the icon scored 0.83 (< 0.85), the held press before J was refused every tick
without a log line, so the autohunt never went on and the gate banner was never looked for. Box now (560, 935, 800, 145)
(room for scale 1.00-1.20; the icon reads 0.99 on all 9 game frames of the run). A refused skill press on the phone gives
up after `PhoneHoldTries` 10 (~3 s; `PhoneSkillGivenUp`, warning + `noskill_s<N>`): stages 1-4 turn the autohunt on
without the hold, stage 5 counts the press as made. (2) **HUNT shows the state** (user: "when autohunt button (yellow
blades) it changes to a cross button"): yellow swords = off, a "+" in a yellow glow = on (`phone_autohunt_on`, 34x34;
the glow = `ShortcutMap.GlowFraction` in the ring 1.6-2.6 half-widths out >= `OnGlowMin` 0.12 - 13 run frames: 0.23-0.37
on, <= 0.04 off, so a plain "+" is not HUNT). `ShortcutMap.AutohuntOn` re-reads both pictures every `AutohuntRecheckMs`
1 s (`HuntRereads`, not in `SearchCount`) and is null for `AutohuntSettleMs` 1.2 s after a tap (the capture lags);
`IGameControls.AutohuntOn` (desktop: always null). Every J of the pilot looks first and skips a tap that would undo the
wanted state; `EnsureAutohuntAsync` reads the button instead of the combat log; the town autohunt-off check runs on the
phone only while the button shows the "+" (the run's town frames had it on); a Start inside the Labyrinth takes the
autohunt for on only while the button does not show the yellow swords (a resume with it off took 14 min over a stage 1
boss at 1 %). (3) **Fast north turn** (user: "it is too
slow to rotate", 27 taps + 4 fine ~ 28 s): `ScreenAnalyzer.BeamAngle` turns the box round the searchlight in 15 degree
steps and matches the straight template in each (a turned, padded template scored a lined-up beam 0.60 - the minimap's
marker under it counted as black); `PhoneAimBeam` then drags `20 + 4.8 x degrees` px (<= 100 degrees, <= 3 turns, each
re-read) to leave the beam 15 degrees clockwise of the heading, and the usual taps finish. Offline: 0 degrees on a
lined-up run frame, exactly 90 / 180 / 270 on its turned copies. (4) **Gate timers** (Wuxen wiki, user): the first gate
opens 30 s after arriving (alone or a full party), each stays open 60 s, the boss comes 5 s after it closes, the next gate
opens 5 s after a kill, and after the last boss there are 40 s to loot. Phone: no banner read 3 s past the gate's timer
(`_gateDueAt`) -> the map anyway, after checking the old boss is gone. Seen live the same night (MuMu, stages 1-3): the
held press on the hotbar icon, J, the centre-click selection and the gate banner at every stage; a Bohai war dialog
cancelled mid-fight. (5) **Stall watchdog on stages 1-4 (phone):** the stage 3 boss stood at 13077973 HP for 26+ min,
the character inside it and the autohunt on, only "Defense failed" lines; HUNT off, a 3 s held press, HUNT on and a
centre tap (by adb) had it hitting at once. The stage 5 watchdog (`AutohuntStallMs` 15 s without a new attack line)
now runs on the phone's stages 1-4 too: J off, then the stage's hold + J + centre clicks again (`stall_s<N>`). (6)
**The map hides the minimap:** the phone's map window is full screen. Paging for the stage 4 boss (Ragesoul Heathen,
page 2; the ^ / v above "Spawns" are the page arrows) took ~10 s, the zone guard held ReadStage after 6 s and the loop
gate waited for the minimap title - the map stayed open, and 74 s after the gate opened the game put the character in
Piercing City (the gate closes after 60 s; whoever has not gone through is sent home - run 1's kick fits too). Now
ClickMap / ReadStage / AdvanceStage tick without the title (`PhoneLoopGate.RunsWithoutTitle`), and in them the zone
guard reads the map window's own title ("The Labyrinth (Adept)", `PhoneMapTitle`) when the minimap's does not read -
only a destination or home-town name counts. (7) **Teleport list paged one page too far** (user: "it is going to page 3
instead of page 2"): the captured frame lags the game by up to ~1 s. The first read came before the list had opened and
paged blind, the next read still showed page 1 and paged again, and the "Labyrinth" tap (aimed from a page 2 frame) hit
page 3. No teleport, and 2 minutes later "out of Temple of Fiends Keys" with 4 left. ML Kit reads the list's "n/N" counter:
`PhoneListPageReady` lets a read count only when the counter shows the page the pilot is on (`_pages` + 1), else it waits
400 ms (<= 8 reads, then the counter read, if any, is taken as the page). The Durations window (☰ -> Durations) shows the
real count: "[Temple of Fiends Key] 4 (time(s))" that evening. (8) **Map right after the kill** (user: "when the boss dies,
already try to open the map and select the next boss", "don't wait for 3s"): stage 3 was lost the same way as stage 4 -
the boss gone (13 s), +3 s timer grace, then the map read "still open" 5 s after the '>' tap on a stale frame, was tapped
again, then "gone - opening it again", "not open - clicking the map button" (which toggled it shut) until the gate closed.
Phone now: a boss death goes straight to the map (`NextStageOrFinish` -> `GateOpened`, no WaitForGate between stages,
no `GateToMapDelayMs`), the timer grace is 0, and every map decision after a map tap waits at least `PhoneUiLagMs` 1.5 s
(the map button, the window check, the '>'); a map gone after any '>' tap of this pick counts as the pick going through
(`_chevronTaps`). The first gate after arriving still waits for its banner or its 30 s timer. (9) **The turn gives way to
the first gate** (user: "it is so slow to turn, while turning keep track on the timer as per wiki and also keep track of
gate opened ... open the map and click the boss"): two entries the same evening were lost at stage 1 - the arrival turn
fell back to taps (30+ taps, ~60 s), the pick came 10-20 s before the first gate closed, and the walk from the entrance to
stage 1 takes longer than that. Phone now: during the turn on arrival the pilot watches the 30 s timer and the banner
(every 1.5 s, OCR every other look, `PhoneGateOpenWhileTurningAsync`); open -> the map and the boss at once, and the turn is
finished during the stage 1 boss wait (`_northPending` -> FaceNorth -> back to WaitBoss with its spawn clock kept,
`_northForWaitBoss`). And the angle read: at the entrance the marker sits on the minimap's bottom edge and the beam is cut
short - it read 95 degrees (right) at x1.2 with 0.79 but a wrong 5 degrees at x1.5 with 0.69, under the 0.85 gate. It now
reads at x1.1 / 1.3 / 1.5 on a half-size picture (~1.5 s on the PC) and trusts the best read at 0.85, or at 0.72 with a 0.04
lead over every read 30+ degrees away (`TrustedBeam`); the long turn is re-read after each drag. **Keys:** the pilot's restarts teleport in again on their own: that night
it entered the Labyrinth at 13:58, 14:07, 14:09 and 14:39 (a 16:14 click did not take); stop it before a restart when
the key budget is spent. Not yet seen live: the resume read, the long north turn, the gate timer, (5) and (6).

---

## 3. Repository map

```
src/WuxAuto.Core            net10.0 class library, platform-free (M1, 2026-10-27: move-only split so an Android
                            app can share the pilot). Holds everything that decides what to do:
  Automation/          AutomationPilot (+ .Lod / .RegularWar / .War / .WarTimer / .WarFight / .Blossom / .KillShots partials) - the whole state machine,
                       BlossomRoute (Auto Blossom's waypoint list, order with its loop, nearest start),
                       WarText (pure text rules of Auto War: countdown, war row, list entries, HP, target name, log lines),
                       TabTargetOptions (the four TAB target values, steps between them),
                       AutohuntLog
  Core/                AppSettings (all tunables; positions reset per run), SettingsStore + AppPaths
                       (load/save/migrate; `AppPaths.Configure(root, assetsFolder?)`), Geometry
                       (NormRect/PixelRect), LogEntry
  Platform/            the seams: IOcrEngine (desktop: OcrService), IGameInput (desktop: WindowInput),
                       MouseButton. IOcrEngine and IGameInput are in namespace `WuxAuto.Core`, MouseButton
                       in `WuxAuto.Input`. ScreenAnalyzer takes (and disposes) its IOcrEngine. The pilot takes
                       an IGameControls (M3, 2026-10-28): game actions (`GameAction` Press / Has, SkillTap,
                       WalkTo, Turn, TiltCamera, ZoomOut, ScrollList, UseItem, ...), never raw keys, so Android
                       can map them to on-screen buttons; desktop = `KeyboardMouseControls(settings, IGameInput)`
                       (each action = its settings key, read per call; `KeyboardMouseControlsTests` pin the raw
                       calls). `AutomationPilot.TickAsync(Mat, CancellationToken)` gets only the frame
  Vision/              TemplateMatcher, TemplateLibrary, ScreenAnalyzer, CompassDetector, OverlayRenderer,
                       KillScreenshots, TextMatcher, VisionModels. OpenCV through Emgu CV 5.0 (M2): Core
                       references only the managed `Emgu.CV`; each host adds the native runtime (desktop:
                       `Emgu.CV.runtime.mini.windows`). CvHelpers supplies the OpenCV default
                       arguments that Emgu leaves to the caller
  Phone/               the phone side's pure parts: TouchControls (IGameControls over ITouchInput), ShortcutMap
                       (on-screen buttons by phone_* template), PhoneRegions, FrameGate, PhoneLoopGate (per-pass
                       "tick or wait": capture, frame age, game in front, panel, zone title), FloatingSpot (W button
                       spot + edge snap), DebugBundleContents (what the debug zip packs)
  RULE: Core must stay OS-free - no WPF, WinRT, Win32/P-Invoke, System.Windows.*, registry. Anything
  that touches the OS goes behind a Platform/ interface and is implemented in the desktop project.
src/WuxAuto.Mobile          Android (MAUI, net10.0-android, minSdk 31), references Core; NOT in WuxAuto.slnx
                            (WuxAuto.Mobile.slnx, built from src\WuxAuto.Mobile - §2). Engine/MobileEngine (loop:
                            frame → PhoneLoopGate → ShortcutMap.Refresh → pilot.TickAsync, PlatformProfile.Phone),
                            EngineHost; Pages/ (Pilot, Setup, Settings, Log); Platforms/Android: CaptureService
                            (MediaProjection), PilotAccessibilityService + AndroidTouchInput (gestures), MlKitOcrEngine,
                            FloatingControl, PilotNotification, DebugBundle, AssetInstaller, DebugReceiver (Debug only)
src/WuxAuto                 WPF desktop app, references Core
  Assets/              shipped templates: teleportscroll, rightchevron, rightchevronfoc,
                       mapicon, flashlight, dungeongateopen, monstericon (.png)
  Automation/          AutoConfirm (Ctrl + right click dialog helper, ticked by the engine)
  Capture/             DesktopCaptureService - DXGI Desktop Duplication + frame pool
  Converters/          WPF value converters (LogLevelBrushConverter: LogEntry level -> brush)
  Core/                KillAlert, StartupCheck, UpdateService
  Engine/              WuxEngine - capture thread + vision loop + pilot tick
  Input/               WindowInput (implements IGameInput) - foreground-guarded SendInput, PostMessage mode, wheel
  Interop/             NativeMethods, window enumeration, global hotkeys, process info
  Vision/              OcrService (Windows.Media.Ocr, implements IOcrEngine)
  ViewModels/, MainWindow.*   WPF UI
tests/WuxAuto.SelfTest      offline probes (see above); Legacy/ = the frozen pre-port vision on OpenCvSharp
                            4.13, only for `--parity` (ScreenAnalyzer.War.cs: main's Auto War vision as of 28763e1)
tests/WuxAuto.Core.Tests    xUnit tests of Core; `dotnet test`. AppPathsTests (round-trips settings through
                            SettingsStore), TextMatcherTests, TemplateLibraryTests, ImageFileTests (non-ASCII
                            folders), CvHelpersTests (pixel-copy guards); the AppPaths-touching classes derive
                            from AppPathsScope, which puts AppPaths back and deletes its temp folders after
                            each test. Pilot and vision tests: ScreenAnalyzerTests;
                            StageBossSelectTests: ticks the real AutomationPilot from WaitBoss at stage 1 (`Start(WaitBoss,
                            stageIndex: 0)`) over a blank 1440×900 Mat with PilotTestDoubles - FakeOcrEngine returns a scripted
                            scene (minimap title, boss name, 8 attack-log lines whose numbers shift per click) for whatever area is
                            asked, FakeGameInput records every call - and asserts the input order: hold on → Tab, KeyDown/KeyUp 1,
                            Ctrl+1, J, centre click, "boss selected on click 1" → WatchBoss; hold off → J, Tab, click. No templates
                            (AppPaths points at an empty temp root), so the boss is found by name; needs the native OpenCV
                            (Emgu.CV.runtime.mini.windows) in the test project; LodStandStillTests: the same doubles from `Start(LodRun)` in AutoLod -
                            defaults: a walk click from 1195,1578, then at 1201,1760 "the boss spot" and only Tab;
                            `WalkToBossSpot` off → only Tab until "[LOD] Bai Gu Great" reads, then LodAttack;
                            SettingsMigrationTests: an edited settings.json loaded back (v22: LOD walk on, 1191,2413 → 1199,1782);
                            LodRingCheckTests: a pink ellipse drawn on the frame, the log gains a hit per skill tap - ring far →
                            2 Ctrl+clicks, then "Ring check 1" and a 3rd, each after Shura's key; ring underfoot → 1 click only;
                            `RingCheckEveryMs` 0 → 2 only; ring + a cyan "belt" gone / shown after the click → "under the boss", 1 click; a cyan "belt" after the
                            walk → no ring-check walk; RingDistanceTests: DistanceFromRing on 6 real ring boxes; the shared
                            runner is `PilotRun.RunAsync` in PilotTestDoubles
tests/probe-baseline.ps1    runs every SelfTest probe over a frozen frame set and writes normalised
                            output for before/after diffs (refactor parity check, section 2)
tests/WuxAuto.UiShot        renders the window to a PNG
branding/                   wuxen2-source.png + make_icon.py -> WuxAuto-logo.png (README) and
                            src/WuxAuto/WuxAuto.ico (exe + window icon; "AUTO" drawn over "ONLINE")
README.md                   user-facing documentation (kept up to date - read it too)
LICENSE                     GPL-3.0 (gnu.org text); Emgu CV's open-source licence is the GPL v3
```

Threading: `WuxEngine.VisionLoopAsync` owns capture → `AutomationPilot.TickAsync` → preview
publish. **All game-touching work happens on that one loop thread**; the UI only observes events.
`Stop()` therefore takes effect within one frame, and the pilot is a `switch` over `PilotPhase`
that is ticked, not a blocking script.

`PilotPhase` (+ `LodRun, LodAttack, LodApproach, LodRespawn, LodLoot`): `Idle, Focus, CheckZone, AutohuntOff, RunSkill, FontSize, OpenInventory, FindScroll, FindChevron, FindDestination, WaitForLoad,
WaitForGate, ClickMap, ReadStage, AdvanceStage, WaitBoss, SelectBoss, WatchBoss, FaceNorth,
RunToTarget, ReturnToTown, Done, Failed`.

---

## 4. How a run works

### 4.1 Teleport

| Step | Detail |
| --- | --- |
| Focus | activates the bound HWND (`FocusOnStart`), then waits `Input.FocusSettleMs` (800 ms) before the first key |
| CheckZone | **before any teleport** (run 28, user): reads the minimap title. Labyrinth → no teleport: the highest stage whose boss name is on screen (whole name, `IsFullName`; Tab up to 5×; the open map lists every name, so it waits for the map to close) → `SelectBoss` at that stage (`_autohuntPressed` = true, J is a toggle). No name, but the coordinates are within 400 of the arrival point (3595, 3415) → a late teleport: carry on from `ZoomOut` (run 31). Otherwise → fail, never teleport. Anywhere else → `OpenInventory`. A re-login puts the character back where it was (Labyrinth mid-fight), and Start after an Alt-Tab did the same; both ran the teleport inside the dungeon before. The probe prints `stage name '...'` for every stage |
| FontSize | once per Start, in town (user, 2026-10-21): **Escape** opens SETTINGS (Escape toggles it; the first press right after focusing was eaten once) → click the `VISUAL` tab → `ReadSettingRowAsync("Font size")` reads the value (`110%`) and the bright `<` / `>` glyphs beside it (1024×768: value 590,259, chevrons 541/671,263) → within `Sequence.FontSizePercent` … `FontSizeMaxPercent` (110 … 120, user 2026-10-23; min 0 = off) it is left alone, outside it the chevrons move it to the nearer end, ≤ 12 clicks → **SAVE** (exact word) if changed → Escape. Only text-found buttons are clicked — the window also has CHARACTER SELECT and EXIT. Anything unexpected just closes it and carries on. It was already 110 % when mapped. The VISUAL click right after Escape did not take once (still on SOUND, run 32): no "Font size" row → click VISUAL again (≤ 3), 900 ms after each. First live try read no value: the hit was the single word "Font" (< 60 % of "Font size") — the whole OCR line is judged now, and a miss saves `fontsize_unread`. **TAB target (user, 2026-10-24, `Sequence.TabTargetDefault` on, check box "TAB target: Default"):** same visit, after the font: the VISUAL list is first wheeled to the top (`TopStep`; the game reopens it at the top anyway), then down (10 notches, ≤ 3×) at a point 10 text heights under the VISUAL tab word until `ReadSettingRowAsync(…, numeric: false)` reads the row (`ValueText` = leftmost OCR line right of the label with 2+ letter words, must start in the left ¾ of the band). Options, in order: **Default → Nearest enemy → Enemy worth CP → Nearest monster**; the left chevron (found even when dimmed) is clicked until it reads Default (≤ 5) → one **SAVE** for both settings (SAVE leaves the window open) → Escape; a miss saves `tabtarget_unread`. 1920×1080: label (724,786), value ≈1063,784, chevrons 995 / 1178 x; all four values read right. Verified live 2026-10-24: Nearest monster → Default, saved, still Default after reopening. **Open:** at 1920×1080 the font row's right chevron (1178–1189) lies past the 30 h band end (label right 794 + 30×13 = 1184) and is dropped, so a font below the range cannot be raised there ("could not be set to 120%") |
| AutohuntOff | in town, **every run**, right after CheckZone (user, 2026-10-25: a restart, a closed stage 5, the LOD timer or a re-login can leave the autohunt running in town). `Sequence.AutohuntOffInTown` (on, check box "Autohunt off in town"). J is a toggle, so: baseline of the combat log's (`Regions.AttackMessage`) autohunt lines → tap `AutohuntKey` → a **new** line (multiset diff vs the baseline, `AutohuntLog.NewState`; keyed from "autohunt" onward, the tail compared fuzzily, bottom-most new line decides) reading `AutohuntEndedText` "Auto hunt ended" **or `AutohuntStoppedText` "Auto hunt stopped"** (town wording, seen 2026-10-27 at 1440×900: "Auto hunt stopped; Smart Farm journey cancelled." — J in town starts a *Smart Farm journey* "Returning to the hunting ground: [The Labyrinth (Adept)]", the second J cancels it; until then every town check ended "cannot tell", `autohunt_unread`; `AutohuntLog.Score` takes a rest that *starts with* the expected words as a full match) = it was on, off now → done; `AutohuntStartedText` "Auto hunt started" = it was off → tap again (≤ 3 taps, snapshot `autohunt_on` if it still reads started). Nothing new 2 s after the first tap → one more tap (the first key after focusing is sometimes eaten; two unread taps = parity kept), still nothing → warning + `autohunt_unread`, carry on → FontSize / OpenInventory (`AfterTownChecks`). Dry run skips it. Not yet seen live — the message wording comes from the user |
| RunSkill | in town, **every run**, after AutohuntOff (user, 2026-10-25: "before running LOD or Laby add a check if the Run skill is activated, it will have a ring on it"). `Sequence.RunSkillCheck` (on, check box "Run skill on in town"). `Assets/runskill.png` = the user's 54×57 crop of the active icon cut to the 33×33 inside the ring (the ring lies at radius 23–27 round its centre; the flame icon: red left, black right, yellow-gold flame bottom right). `FindRunSkill` matches it in `Regions.Hotbar` ≥ `RunSkillMatchScore` **0.88** (saved frames: 0.975–1.000 on every bar from 1024 to 2304 wide, 277 frames; a login screen hit 0.81 at ×0.5, nothing else above 0.74; frames whose bar is hidden read ≤ 0.70) → `ScreenAnalyzer.RingFraction`: 36 directions from the icon's centre, a gold pixel (H 15–30, S 60–150, V ≥ 150 scaled by the icon's brightest pixel / 255 — under a modal dialog the screen is dimmed, ring V 66–86, flame 102) at 0.68–0.85 of the box width out = that direction counts; ≥ `RunSkillRingMin` **0.25** = on. Every saved frame with the icon reads 0.47–0.56 (the icon covers parts of the ring; the user's crop 0.5, 20 of 36 directions) — **no frame with the skill off exists yet**, the "no ring" side is the user's word. No ring → click the icon, fresh frame 0.9 s later, ≤ 3 clicks → warning + `runskill_off`. Icon not on the bar → `RunSkillKey` **W** (UI "Run key") **once per Start** (`_runKeyPressed`): the key is a toggle and without the icon nothing says whether it was on, so a second press could turn it off; warning + `runskill_unread`. Dry run only reports. Not yet seen live |
| RegularWarOff | **Auto Laby only**, in town, **once per Start** (`_regularWarOffChecked`), after RunSkill, before FontSize (user, 2026-10-29: "check if the regular war is turned on in the autohunt, disable it if enabled, as it may get teleported to the war when the autohunt is on while attacking the boss"). `Sequence.RegularWarOffForLaby` (on, check box "Regular War off before Laby"). `AutomationPilot.RegularWarOff.cs`: `AutohuntSettingsKey` **S** (≤ 2 presses, 2.5 s apart - even, so a window that opened but never read is shut again) → whole-screen OCR (×2, ×1 when < 3 words) → AUTO BUFF title → its X (`FindCloseBlobRightOf`, never a guess; ≤ 3) → the "Regular War" line (`WarText.IsRegularWarLine`) → box (`FindCheckboxLeftOf`) → `IsCheckboxTicked` → ticked: click, fresh frame 0.9 s later, ≤ 2 clicks (even, so a box that only *reads* ticked ends as it was; → warning + `regularwar_on`) → `S` closes (first press blind, then the label must be gone 1.2 s later, ≤ 2, else `regularwar_open`). Window seen open, then nothing reads → re-read 5 s, never a blind `S` → warning + `regularwar_unread`. Unreadable from the start → warning + `regularwar_unread`, carry on. Dry run skips it. The keys-out "Regular War on" step (§4.5b) still ticks it back on. Pinned by `tests/WuxAuto.Core.Tests/RegularWarOffTests` (drawn gold box, `S` toggles the window). **The Regular War row has never been seen in a real frame** - not verified live |
| Inventory | taps `Input.InventoryKey` (default `I`) |
| Scroll | finds `teleportscroll.png` in `Regions.Scroll` (threshold 0.78: real ≈0.83, a town-screen look-alike scored 0.70 and made the pilot skip `I`) and **right-clicks** it (left click only picks it up in this client) |
| List | OCRs `Regions.TeleportList` for `Sequence.DestinationText` (`Labyrinth`, matches `[The Labyrinth (Adept)]` on page 2); pages with `rightchevron.png` while missing |
| Select | left-clicks the matched text row |
| Load | waits for the frame hash to settle (`LoadingMinMs` 1600, timeout 20 s) |
| Still in town? | `ZoomOut` fails with "Still in Piercing City" only after **20 s** (45 s while the top bar's ping reads `--ms`, `IsLaggingAsync`: "ms" without a digit before it). It was 5 s: with the server lagging the teleport went through later and the run had already failed (runs 26 08:07 and 31 11:42) |

### 4.1b On arrival (user, 2026-10-20)

`ArrivalDelayMs` (300) after the load check, then `ZoomOut` **waits for the zone name
(`Sequence.DestinationText`, "Labyrinth") on the minimap** (OCR of `Regions.Minimap` ×2 every 0.5 s;
**no zoom without it**, user 2026-10-22 — after max(60 s, `LabyKeyWaitSeconds`) neither there nor in town → fail/restart) — the still loading screen passes the frame-hash load check and loads are sometimes slow, so a
fixed delay zoomed on the loading screen (user) — then: **Ctrl held + mouse wheel down**
`ZoomOutNotches` (15) at (50 %, 40 %) — the game only zooms with Ctrl (user), then **face north at once** (`NorthOnArrival`), then
`WaitForGate`. The camera keeps its heading through the stage teleports, so the north check after
stage 4 only needs a tap or two and the camera already looks at the gate. Taps are `RotateStepMs`
180 ms (user; settings v11 migrates 40/120 → 180), half taps 90 ms near the peak. The searchlight
is matched over the full scale range on every tap (`FindIconAnyScale`): a learned scale from one
bad frame (×0.5) stuck once and every later score was wrong (best 0.52, run 13 arrival). Not yet
seen live.

### 4.2 Dungeon loop (stages in order)

`Sanctified Unicorn` → `Spectral` → `Sundersword` → `Ragesoul Heathen`

| Phase | Behaviour |
| --- | --- |
| `WaitForGate` | matches `dungeongateopen.png` in `Regions.GateBanner` (note: the dungeon path uses `GateBanner`, not `Gate`; `Gate` belongs to the old single-shot sequence), OCR fallback `DUNGEON GATE OPENED` every 4th poll; gives up after 180 s (40 s banner timeout just logs a warning) |
| gate→map | **3 s delay** (`GateToMapDelayMs`) so the kill animation / walk to the gate finishes; "no walkable path" is logged if it shows up |
| `ClickMap` | clicks `mapicon.png` in `Regions.MapIcon`; if the map window is not visible after `MapOpenDelayMs` + one confirmation poll it re-clicks (up to `MapReopenRetries` 3). **Map open = `MapWindowVisibleAsync`**: the "Spawns" header reads in `Regions.MapScroll` (similarity ≥ 0.90; the paging anchor) **and** ≥ 1 `rightchevron` ≥ 0.55 in `Regions.MapBossList`. Was 2 chevrons at 0.50 (user, 2026-10-26: a mouse move / click of theirs while the map opened left it shut and the list clicks went into the game world): the 'x' of "Drop rate x 2 Event", the ')' of the item list's "(time(s))" lines and the boss's body pass for chevrons — even 3+ in one column gave 38 false columns on the saved frames, and the real chevrons only score ~0.61 in `FindIcons` (median), so no score floor separates them. "Spawns": 219/219 open-map frames (0.93–1.00, OCR 12 ms median, p90 23), 0 of 210 others. Probe: `--mapopen <dir>` prints chevron hits / column, the Spawns read and the verdict per PNG next to the old test |
| `ReadStage` | OCRs `Regions.MapBossList` for `Stages[stageIndex]`; only a whole name counts (`IsFullName`: similarity ≥ 0.90 or ≥ 60 % of the letters — the `[Rage` of a [Rage Elixir] text passed at 0.84 and was clicked as stage 4, run 23). Missing → page **down** (list always opens at the top), max `MapScrollMaxSteps` 8 → reopen map → fail. **Every tick first checks `MapWindowVisibleAsync`** (no map → wait 700 ms → reopen ≤ 3 → fail); the old "chevrons not detected but the name is readable — carrying on" escape is gone (2026-10-26): a name read with the map shut is the name tag in the world, and the click walks the character |
| `AdvanceStage` | clicks the **boss name** first (highlights the row), waits `BossNameClickDelayMs`, then clicks the highlighted row's `>` found with `rightchevronfoc.png`; retries 3×, then fallbacks: bright-blob glyph → template column → name click. The name is only trusted with the map open (`IsFullName` + `MapWindowVisibleAsync`). **Name gone before the `>` click** (`_chevronClicked` false; the name click only highlights, the `>` closes the map — the fallback's glyph / column click counts as a `>` click): map still open → re-read ≤ 3 × 500 ms (`_rowNameMisses`) → `ReadStage` again; map shut → something else closed it (user, 2026-10-26: their own mouse) → `ClickMap` again (≤ `MapReopenRetries`, shared counter) → then `WaitBoss` anyway. Before this the pilot went to `WaitBoss` and waited 60–90 s for a boss that was never picked |
| `WaitBoss` | waits `BossSpawnWaitMs` (60 s), taps `J` (autohunt; **only without `HoldBeforeAutohunt`** — with it, the default, `J` follows the held press in `SelectBoss`), taps `Tab` (again every 1 s until something shows — one Tab on arrival at stage 5 showed nothing for 25 s, run 23), then looks for `monstericon.png` or the stage name; needs the boss visible `BossSelectDelayMs` (2 s). The 90 s no-boss timeout still turns the autohunt on and watches anyway |
| stages 1–4 hold | **(user, 2026-10-27: "on other stages, before going autohunt, try to skill hold first"; `Dungeon.HoldBeforeAutohunt` on, check box "Hold skill before autohunt (stages 1-4)")** `TickSelectBossAsync`, `!IsFinalBoss && !_autohuntPressed`: `StartHeldSkill` (`KeyDown`, `_heldSkill` / `_holdUntil`, `HoldSkillMs` 3000 shared with stage 5) → `TickHeldSkill` lets go + Ctrl + skill (`CtrlSkill`) → `_heldBeforeHunt` → `EngageAutohunt` (`J`) → `AutohuntSettleMs` (1 s) → **quiet log**: the combat log read every 250 ms until unchanged for `SelectQuietMs` (1.5 s; cap `SelectQuietMaxMs` 6 s → warning "still changing", click anyway), that state = the click baseline → the centre clicks as before (click give-up → `EngageAutohunt` is a no-op → `WatchBoss`). Off, no skill key or `HoldSkillMs` 0 → the old flow (`J` in `WaitBoss` right after the spawn wait). `_heldBeforeHunt` / `_holdUntil` reset per boss (`ResetBossState`); the key is down between ticks, `Advance` / Stop release it. History: on 2026-10-22 delaying `J` on stages 1–4 until after the selection "broke the run" (unknown how) — the check box is the way back. Not yet seen live; the input order (Tab → hold → Ctrl+1 → `J` → centre click → hit → `WatchBoss`, and `J` → Tab → click with the switch off) is pinned by `tests/WuxAuto.Core.Tests/StageBossSelectTests` (user, 2026-10-27, after v0.14.0: "after enabling autohunt in Stage 1-4 do still click near center ... it seemed to be removed" — it was not; the clicks follow `J` after the 1 s settle, as in this morning's v0.13.0 log where every stage hit on click 1–3). **Quiet-log wait (user, 2026-10-29: "click the center ... was removed ... doesn't try to select the boss at stage 2 above"):** the clicks were there, but stopped too early. Before the hold, 12 full runs: stage 2 always hit on click 2+ (the boss stands above the centre), stage 3 on click 3+. v0.21.0 with the hold (07:24): stage 2 "hit" on click 1, stage 3 on click 2 - the Ctrl + skill tap right before `J` prints hits 1–2 s later, past the fixed 1 s settle, and one after click 1 read as the selection. Pinned by `StageBossSelectTests` (late hits for 2 s after `J`, click 1 misses → 2 clicks, first click ≥ 2 s after `J`; a never-quiet log still clicks after the cap). Not yet seen live |
| `SelectBoss` | **Stage 5 only (user, 2026-10-22 — on stages 1–4 it broke the run, those keep the old flow: `J` after the spawn wait — since 2026-10-27 after the held press above — then centre clicks until an attack line → `WatchBoss`):** autohunt only after a hit by hand. **Since 2026-10-27 (user: "in the log, it says it didn't see the pink ring, but it is there, and stops attacking the boss. also it didn't get near or inside the boss. Instead do this: Waypoint 1, make sure autohunt is off; Attack Hold skill, then do 10x attack (total 11); check if has attack succeeded, then auto hunt then click on the boss"):** the boss's name in view (`WaitBoss`, `BossSelectDelayMs` 2 s settle) → `StartManualAttack` at once — no selection ring, no target-bar read, no 15 s skill spam first (all gone; `FindSelectionRing` only lives on in the `--ring` probe). `TickManualAttackAsync`: press 1 **holds** `SkillKey` down for `HoldSkillMs` (**3000**, UI "Hold skill (ms)", 0 = taps only; `_heldSkill` / `_holdUntil` — the key is down *between* ticks, so Stop / F10 / `Advance` let go of it through `ReleaseHeldSkill` like the LOD hold), released → Ctrl + skill tap (`CtrlSkill`), then `ManualAttacks` (**10**; settings v21 turns the v20 15 into 10) `SkillTap` taps ≥ `ManualAttackIntervalMs` (300) apart — 11 presses in all. **Only a new attack line after the last press counts** (`_manualHit`, user 2026-10-23; checked `ManualAttackWaitMs` 500 after it); none → more taps, up to 3× the total; still none → `Tab` + another round (hold + taps, `_manualRounds` ≤ `MaxManualRounds` 3) → `nohit_s5` snapshot (≤ 3 per Start, `_stuckShots`, shared with `stall_s5`), `ArriveAtFinalBoss` (Tab wait, **autohunt still off** — nothing is clicked while it is off, user 2026-10-27). A hit → `EngageAutohunt` (`J` unless already on) → `TickAutohuntConfirmAsync`: `AutohuntSettleMs` (1000, const) of silence first (the manual attacks' last skill animations still print attack lines after `J`), the log re-read as the baseline (`_huntBaselineAt`), then **the stage 1–4 centre clicks at once** (`_huntClicks`, `SelectClickPoint`, `finalboss.png` first, one per `AttackConfirmMs`, ≤ `BossSelectMaxTries`; user 2026-10-27: "after enabling J, it unselected the boss, add the click the middle to try to select the boss") — a new line after any click → `WatchBoss`; all clicks without one → `J` (off), `StartManualAttack` again (counts as a round, same ≤ 3 limit) → 3 rounds → `nohit_s5`, Tab wait, autohunt off. The 2 s `AutohuntConfirmMs` wait before the clicks is gone (setting removed): in the v0.12.0 log (2026-10-27 09:02–09:08, 1440×900) every cycle read "ring + line → J → one late line 1.3 s after J → 15 s stall → J off" six times over, with the character standing ~250 px from the boss's feet the whole time (`stall_s5` / `ring_s5` frames) — the taps alone hit from a distance and the autohunt never attacked; the hold is the user's answer to "didn't get near or inside the boss". Before this (2026-10-26/27): the ring (`FindSelectionRing`, HSV H 138–156, S ≥ 89, V ≥ 190 in (0.05, 0.12, 0.70, 0.78)) + a new line + the boss in the target bar (`IsFinalBossTargetedAsync`, removed) gated `J`; round 1 in that log read "No selection ring" with the ring under the boss. **`WatchBoss` watchdog at stage 5 (2026-10-27):** with the autohunt on and the boss up, the combat log is read every boss poll; no new attack line for `AutohuntStallMs` (15000) → `J` off, snapshot `stall_s5` (≤ 3, `_stuckShots`), `ArriveAtFinalBoss` (autohunt-off check → Tab → hold + taps → `J` → clicks). Catches every "autohunt on but not attacking" state whatever let it through; unbounded, the stage closes itself within ~2 min anyway. At stage 5 nothing else turns the autohunt on: the click give-up before `J` goes back to the Tab wait (`attack_s5` snapshot; only reachable with an empty `SkillKey`), and the 90 s no-boss timeout only applies to stages 1–4 (stage 5 keeps tabbing until the zone guard sees the stage closed). Stages 1–4: click give-up and the 90 s timeout still turn autohunt on. **Autohunt off at the waypoint (user, 2026-10-26: "when in Waypoint 1, disable auto hunt"):** the first `WaitBoss` tick at stage 5 (`_finalHuntOffChecked`, every `ArriveAtFinalBoss`) runs `EnsureAutohuntOffAsync` (the town check's logic, shared) with `trustVisibleLine`: the newest autohunt line already in the combat log decides — "Auto hunt ended" = off, nothing pressed (the stage 4 `J` is a blind toggle, but its line is normally still on screen); "Auto hunt started" = on → `J`, expect "ended"; no line → tap and read as in town (≤ 3 taps, `autohunt_on` / `autohunt_unread`). `_phaseStart` restarts after it. Not yet seen live. Before that: **left-clicks around the middle of the client** until a new `Attack succeeded!... damage(+N)` line appears in the combat log (`Regions.AttackMessage`, left side): centre, ring of 8 at `BossSelectStep` (0.06 × height), ring of 8 at 2 steps, max `BossSelectMaxTries` 17, `AttackConfirmMs` 1200 per click. The log holds ~8 lines and is usually full, so line counts don't change: it compares the visible lines (normalised `attacksucceeded...damage(N)`, top to bottom) with a baseline taken before the first click; any change = hit. Gives up → warning + `attack_s<N>` snapshot → `WatchBoss` anyway. Autohunt will not attack an unselected boss |
| `WatchBoss` | gate banner checked every `GatePollMs` (template every poll, OCR every `GateOcrEveryNPolls`, OCR only counts *new* lines vs a baseline) = dead → next stage skips `WaitForGate` (last stage → `FaceNorth`). Boss name/icon checked every `BossPollMs` (2 s); it only counts as dead after `BossGoneMs` (10 s) of continuous absence — two quick misses once ended a live fight (the name flickers when the boss is off camera). The 15 s `GateFallbackMs` first re-checks the previous boss and goes back to `WatchBoss` if it is still up. The banner check was added because after stage 4 the pilot sat in `WatchBoss` while the gate banner was already up. **No time limit** while the boss is visible (2026-10-24, user: the 15 min `BossDeadTimeoutMs` moved a friend's run on with the stage 1 boss at 1 %; the dungeon stays open for a slow kill) |
| after stage 4 | `FaceNorth` |

### 4.3 Face north

Short taps with checks + **peak search** (2026-10-20):

1. Read the minimap `x , y` line once (§5.6); it anchors the searchlight box
   (`CompassRegion`: from the line down ~20 text heights, ±12 wide; text height capped at
   client height / 64 so a bad read cannot blow the box up).
2. Template `FlashlightTemplate` = **`flashlight3`** (user, 2026-10-21, settings v13 migrates
   flashlight/flashlight2; flashlight2 lined up NNW and the 40 ms fine-tune could not see it). The PNG
   is 395×341 and mostly transparent; `TemplateLibrary.LoadGray` crops any PNG with transparency to
   its largest opaque patch (here the ~98×67 beam; a stray yellow mark is dropped) — matching ignores
   alpha. On saved north-facing frames it scores 0.93–0.98 (flashlight2 0.96–1.00). Not yet seen
   live. Earlier: `flashlight2` (90×60 straight-up beam cropped by the user at 1024×768; falls back
   to `flashlight`). It is stricter than the old crop: the heading the old
   logic called north (user: actually NNE) scores 0.864 with flashlight2 vs 0.922 with flashlight.
3. Tap `A` (`RotateStepMs` 120 ms, half taps once within 0.05 of the gate), score after each.
4. `FlashlightMatchScore` (0.85, settings v9 migrates 0.90) is only the gate: once passed, keep
   tapping while the score rises; when it drops by 0.02 or stops rising for 4 taps, undo the taps
   since the best heading with `D`, one by one with the same lengths (`TapBack`). Stopping at the
   first tap above the threshold left the beam NNE.
4b. **Fine-tune (run 25, user: "a little bit NNW"):** a 180 ms tap turns ~15°, so the coarse peak
   can be several degrees off. `TickRefineNorth` then taps `NorthRefineTapMs` (40 ms) with `D` while
   the score rises by > 0.004, undoes the tap that did not; if `D` never helped it tries `A` the same
   way (≤ 8 taps per direction). Log: `Searchlight fine-tuned: ...`. Not yet seen live.
5. After 60 taps without reaching the gate: tap back to the best heading if it scored ≥ 0.60.

### 4.3b Auto-reconnect (`Sequence.AutoReconnect`, on)

Every 5 s while running (even while a phase waits) the whole screen is OCR'd (×1) for a logged-out
screen — `SERVERS`, `ACCOUNT NAME` / `Remember me` (login form; the game was once found there and a
SERVERS-only check missed it), `START GAME` (not `CHARACTER SELECT` — the in-game Settings window has that button and was taken for a disconnect, run 32; nothing counts while a `SETTINGS` line shows); found = disconnected → `Reconnect` (snapshot `disconnected`). `TickReconnectAsync` follows the user's order
**Online → CONNECT → LOG IN → OK → START GAME**, 800 ms after each click (`ReconnectStepDelayMs`):
the expected step is clicked when its button reads; a later step's button jumps ahead; `SERVERS`
again starts over. Whole-screen OCR runs at ×2 (×1.5 returned garbage on these screens, ×1 is the
fallback). The gold `LOG IN` button reads as `OG N` or not at all → OCR variants, then
`ScreenAnalyzer.FindGoldButtons` (HSV H 12–25, S ≥ 110, V ≥ 95, 3 px close, filled wide rectangles;
measured on LOG IN: H 15–18 S 128–143; the sunset behind the panel has S ≈ 85 — exact hit
`369,483 286×45`, no hits on in-game frames), then the gap between "Remember me" and "BACK". OK:
a whole line reading "OK"/"0K" *below* the "Login OK"/"Login 0K" message (a single word was
accepted once and the pilot clicked the "OK" inside the message), else the gold button below the
message that is not the LOG IN button (the dialog sits over the form). On the server list CONNECT is never
clicked before Online (it was clicked twice while the list was still loading). Enter was tried for OK and does not work (user). After START GAME it waits for the town name **or the Labyrinth** in
the minimap band (not the character screen) and restarts the run from `Focus`. **START GAME (2026-10-22, user: "doesn't find START GAME"):** logs show
the character screen with "Login OK" still up and START GAME unread for 15–60 s → `IsStartGame` (exact or
Levenshtein ≤ 2, 7–11 letters), a read "OK" below "Login OK" is clicked again (every 3 s; never a guessed
gold button there — DELETE CHARACTER is on that screen), START GAME still showing 4 s after its click is
clicked again, and 5 s on the character screen without it saves `startgame_unread`. No frame of the
character screen exists yet — not verified (→ `CheckZone` resumes the stage). Only clicks — the
client remembers the account ("Remember me"). Limits: 30 clicks / 3 min → fail. Verified live (18:41):
SERVERS → Online → CONNECT → LOG IN (gap fallback) → START GAME (read) → Piercing City → run
restarted, ~15 s; a second real disconnect at 18:50 (during stage 3's boss wait) recovered the
same way. The OK dialog's button was not read either time; the gold fallback for it is not verified yet.

### 4.4 Run to target

**Default since 2026-10-21: `SimpleSteer` (user idea).** We face north, so screen-up is world-north
(y down) and screen-right is world-east: each step clicks from the character straight towards the
target (`RunClickPx` 200 reach, shorter within 300, scaled into the click area), every
`RunDashWaitMs` (900) so the character keeps walking; the coordinates are read only to correct
direction and detect arrival. No probes, no fit. **Heading fix (run 24):** the searchlight "north"
can be a few degrees off — run 24 drifted ~16 units west per ~170-unit step and hit the west wall at
3406,7228 after 13 steps (run 23 drifted ~4). `LearnHeadingError` measures the angle between the
(corrected) click direction and the actual movement each step (≥ 60 units, |angle| ≤ 1.2 rad,
smoothed ×0.5, ≤ 35°) and turns every click by it; the `Run:` line shows `heading fix ±N deg`.
Simulated from the real start (3593,9306): heading errors −20…+15° all arrive in 47 steps, ≤ 24 units
off the line at −5.4°. Not yet seen live. **Tab + skill at the last waypoint (user, 2026-10-21, `TabAtWaypoint` on, v16 turns
the west turn off: "it doesn't work well"):** on reaching 3603,1973 the walk stops; `ArriveAtFinalBoss`
→ `WaitBoss` Tabs every **1 s** (user) **without** autohunt; the boss spawns with "DUNGEON GATE OPENED" (new
banner line, template or OCR every 2nd poll). Name seen → `SelectBoss`: since 2026-10-27 the hold + 10 taps of
`SkillKey` (`1`; a single digit maps to the top-row key D1; each press followed by Ctrl + skill key,
`Dungeon.CtrlSkill`) — the character runs at the targeted boss and attacks — then a new "Attack succeeded"
line → `J` (autohunt on, only now — user) → the select clicks from the centre → `WatchBoss` (§4.2
`SelectBoss`; the 15 s skill spam before the manual attacks is gone). No
name 15 s after the banner (60 s without one) → the coordinate walk to 3325,1932 — **only with
`Dungeon.WaypointWalkFallback` (off since 2026-10-22, user: "it tries to go left instead of Tab until the
boss shows")**; otherwise it keeps pressing Tab. Skipping the last waypoint (closer to the boss than the
waypoint) also starts the Tab step now — it used to walk on west. Not yet seen live. **West at the last waypoint (user, 2026-10-21, `FaceWestAtWaypoint` on):** the
boss is west of 3603,1973, so on reaching it the pilot turns to face west — the FaceNorth tap/peak
search against `<FlashlightTemplate>west` (generated in `TemplateLibrary`, the beam turned a quarter
left; in the searchlight box a north-facing frame scores 0.98–0.99 north vs 0.59–0.61 west) — and
steers on with `_headingBase` = −π/2 (screen-up = world-west); `LearnHeadingError` works relative to
that base. Simulated from the waypoint with ±20° error: 3–11 steps to the boss. Not yet seen live.
The steps below are the fallback (`SimpleSteer`
off). Stall/wall rules, waypoint skipping, jump hold-back and arrival rules apply to both.

Reads the `x , y` label under the minimap (§5.6), then:

1. probes one click straight up and one up-right (seed samples),
2. **least-squares fit** of click offset (right, up in `RunClickPx` steps) → world movement over
   *every* step (`AddRunSample`/`SolveRunOffset`, steering steps fade ×0.9, the two probes are kept at
   full weight — with only straight-up steps afterwards the left/right information faded and the
   solve clicked bottom-left for a target straight north, run 19). Sanity check: facing north, a
   target clearly north/south (|Δy| > 100) solved the other way resets the fit and re-probes. The probes alone are skewed
   (measured while the character is still walking); the fit corrects itself as it goes,
3. the wanted offset is **scaled as a whole** to fit the click area (25–74 % x, 25–78 % y — with 75 %/15 %
   the top-right corner clicked the stat panel's pet (bee) icon; ≤ 2 steps,
   ≥ 0.35 step so it never clicks the character). Clamping the axes separately bent "mostly up,
   a little left" into a 45° diagonal and pinned every click to the top-left corner (runs 5/6),
4. walks each `Route` point (within `WaypointTolerance` 60; skipped once the character is closer to
   the boss than the waypoint is — it took a shortcut to 3366,1996 once and then fought back towards
   the waypoint into a wall), then `TargetX,TargetY` (3325,1932,
   within 30); after 2 near-stalled steps (< 20 units) a forward target clicks straight up (wall), and stalled clicks get shorter (×0.5 from 2 stalls, ×0.3 from 4: with a steeper camera a full-length click lands in the gate doorway, run 34);
   6 near-stalls = fail, but a stall within 120 of the boss counts as arrived (`ArriveAtFinalBoss`), and so does
   a stall (≥ 2) with `FinalBoss`'s name on screen — the boss came to the character and autohunt engaged
   it ~270 short of its spot, its body blocked the walk and the run failed as "not moving" (run 29 loop 2); a reading > 500 units from the last one is held back (OCR read 3432 as
   432 and 8014 as 9014 — the latter got through a 1000 limit, the run then clicked backwards and
   refused every real reading); two held-back readings that agree (≤ 200) re-anchor the position — **five** if x or y has a
   different number of digits than the last good reading (OCR read 3306 as `306` twice in a row,
   re-anchored, and the steering drove the character east into a wall, run 16),
5. on arrival: stage 5 (`FinalBoss`), see §4.2 / settings. The first time its name shows a
   `stage5_boss` snapshot is saved (user: a reference picture of the boss); a `finalboss.png`
   cropped from it (templates folder or Assets) is then matched (any scale, ≥ 0.60) and clicked first
   in `SelectBoss` (≤ 3 tries), before the ring around the centre. `finalboss.png` (in Assets) is the boss's breastplate cropped from the run-33 `stage5_boss` snapshot (380,218 180×110): 0.92 on that frame, not found on any other saved frame — not even another stage-5 frame with the boss in a different pose — so it rarely helps; the ring is what usually selects.

**AimGate (user idea, 2026-10-20, not yet seen live):** after facing north the pilot right-drags
upwards by `CameraTiltPx` (**60 px**, user 2026-10-21; was 150 — 40 px only showed floor on the first live try; positional → reset per run) to tilt the camera; the next stage's
background then shows as a red/orange glow ahead. `FindRedOrangeGlow` (HSV H 0–22 or 168–180,
S ≥ 120, V ≥ 120, 7 px close, biggest patch ≥ w·h/4000) first in the **upper band (0.25, 0.07, 0.31, 0.15)** —
with the 60 px tilt the gate is high up (run 29: doorway at 370–470 × 55–130, glow 388,89 44×20, the
old band missed it and the run steered by coordinates; the band stays left of the stat panel at
x ≈ 0.57, whose icons are orange) — then in (0.25, 0.22, 0.50, 0.20) (ends above the character: with a band down to 50 % a torch beside a
character already standing in the doorway matched) — wider bands hit the
target HP bar, stat-panel icons and the minimap — and only within ±15 % of the centre line (run 29's gate was ~100 px left of centre at 1024). Click
**below** it on the brown floor (`Bottom + 3 % h`, user: the red/orange is the background), then
looks for the gate **before** tilting and tilts only when it is not in view (run 34: without the drag back the tilt stayed from run 33, a second 60 px put the gate at y ≈ 190 and the steering clicked into the unwalkable doorway); **no longer** drags back down (user, 2026-10-21: not needed; `Dungeon.UntiltAfterGate` turns `UntiltCamera` back on if the tilt ever adds up), then `RunToTarget`
only watches the coordinates until the walk stops — still (< 20 units) for **3 readings (~3 s)** and
≥ 5 s after the gate click; after one still reading the first steering click landed on the wall while
the character had only paused (user, run 34) — then steers. Snapshot `gate_view`
every time. On 25 saved frames: no false central hit in the dungeon (one in Bohai Outpost town, an
orange armour). Was off from settings v12 (user, 2026-10-21 morning); **back on with a 60 px tilt since settings v14**
(user: "right click up 60px after turning to north ... see the stage 5 gate ... run towards it").
Not found → straight to the coordinate run, so the coordinates stay the final check.

**Stage 5 time limit (user, 2026-10-20):** stage 5 stays open only ~1–2 min after stage 4 dies; then
the game moves the character to town (the 19:12 and 20:20 "kicks"). A clean run takes ~60 s
(north ~8 s, ~29 steps × 1.6 s to 3603,1973, 3 steps to the boss), so the run must not stall. During
stage 5 the zone guard below sees the town title → snapshot `stage5_closed`, start the next run.

**Zone guard (user, 2026-10-21).** Every Labyrinth phase (`IsLabyrinthPhase`: WaitForGate, ClickMap,
ReadStage, AdvanceStage, WaitBoss, SelectBoss, WatchBoss, FaceNorth, AimGate, RunToTarget) reads the
minimap zone title every 2 s (`ReadMinimapTitleAsync`: the line containing `DestinationText`, else the
first `[`-line without digits). No "Labyrinth" for > 6 s → the phase is not ticked at all (no input),
warning once; > 2 min **with the game in front** → fail (behind another window the capture shows that
window, not the minimap — this chat once covered the game for 2 min and stopped an LOD fight; now it
just waits for the game to come back). **Only with the bound window in front** (2026-10-27, 1440×900: a second game client — another character in Piercing City / Dark Soul Cave — covered the game and its minimap read as "sent to town" 7× during stages 1–2; the run restarted each time and then teleported again, a key gone; behind another window the guard now just holds): title = `ReturnDestination` (town) → next run; any other zone read 3× →
snapshot `wrong_zone`, next run. Before this, rotating/Tab/map clicks were sent in town, and once
(19:29, 2026-10-20) the run carried on in **Bohai Outpost (Adept)**. Checked on all 188 saved frames
(`--titles <dir>` probe): every stage frame reads `[The Labyrinth (Adep` at 1024, 1920 and 2048 wide,
map open or not; town/Bohai/Eight Symbols Island read as other zones. Failure snapshots with the
coordinate overlay drawn over the title do not read — the overlay, not the game.

**Bohai Outpost dialog (user, 2026-10-21).** `Sequence.CancelDialogText` (`Bohai`, empty = off): the
5 s whole-screen OCR (shared with the disconnect check) looks for a line with that text outside the
minimap and clicks a `Cancel` word within a third of the client height of it; snapshot
`canceldialog`. Without a readable Cancel it only warns (`canceldialog_unread`, once a minute) —
the `[Bohai Outpost (Adept)] [10] minute(s) to opening of the entrance` notice line has no buttons.
Skipped while the teleport list is open. **No frame of the dialog itself yet** — not verified.

Replay of run 6's real steps through the solver + a closed-loop simulation with that movement
model reaches the route point in ~32 steps and the boss 3 steps later.

### 4.5 Auto LOD (user, 2026-10-21, `Sequence.Mode` = `AutoLod`, the Mode drop-down above Start)

`AutomationPilot.Lod.cs`, settings `AppSettings.Lod`. Teleport as usual but to **`Lod.Destination`
"Ultimate Den of Rebirth"** (right under the Labyrinth in the scroll list; a hit must contain the start of
the name — `ZoneMatches` — so a plain "[Den of Rebirth]" cannot pass) → zoom out → **face south**
(the user's `flashlightLOD.png` **as is — it is the south-facing beam** on this map's non-black
minimap background. It was first turned 180° by mistake (I misread its orientation from pixel
brightness) and the pilot "lined up" facing north at ~0.82 — user caught it. flashlight3 only reached
0.78; the arrival heading is about WSW) →
`LodRun`: **walks forward to 1199, 1782** and waits there, tapping Tab every `Lod.TabEveryMs` (1.5 s) all along
→ name **`Lod.BossName` "Bai Gu Great"** on screen → `LodAttack`: spam `Dungeon.SkillKey` every 0.3 s — or hold it down (`Lod.HoldSkill`, "Hold skill (LOD)"; released on every phase change, Stop, panic and failure) — Tab when the name is missing.
* **Walk forward to 1199, 1782, then wait** (user, 2026-10-28: "sometimes the boss doesn't go near the
  char. move the character forward to 1199, 1782" ... "that is after facing south"; `Lod.WalkToBossSpot`
  **on** + `Lod.TargetX/Y` **1199, 1782**, tolerance 60; settings v22 turns the walk on and moves the old
  1191, 2413 default - a spot set by hand stays): after the south turn (arrival, respawn, a kill / lost
  target → `LodRun`) SimpleSteer with `_headingBase` = π (screen-up = world-south) walks there while
  tabbing, then `At x, y, the boss spot - tabbing ...` and only Tab; drifted off → walks back. The
  teleport lands at **1195, 1578** (5 of the 6 LOD walks in the logs of 2026-10-22/24; one 1201, 1530), so it is ~204 units
  straight south, 2-3 clicks (those walks made 80-140 units a click); the boss was fought at ~1212, 1775
  on 2026-10-24. The boss name seen at any point → attack, the walk stops. **Death check:** a death puts
  the character back at ~1195, 1578, only ~200 from a fight at the spot - less than `Lod.TeleportJump`
  (400), so the coordinate jump may not see it (no `killed by the boss?` line in any log so far); the lost
  target then most likely reads as "gone 2 s" → loot → `LodRun`, and the walk brings it back, without the
  respawn buff. Lowering `TeleportJump` risks a running chase reading as a death (~100 units/s).
  History: until v0.15 the walk went to 1191, 2413 (the middle of the map); v0.16/0.17 stood still where the teleport landed (user,
  2026-10-27: "stay still on the same place upon teleport ... retain the rotate to south so you will be
  able to target on the ring") - check box "Walk to the boss spot (LOD)" off brings that back (no clicks,
  no coordinate reads). Pinned offline by `tests/WuxAuto.Core.Tests/LodStandStillTests` (defaults: from
  1195, 1578 a walk click, then at 1201, 1760 only Tab; off: only Tab until the name, then `LodAttack`)
  and `SettingsMigrationTests` (v22). The walk to 1199, 1782 itself is not yet seen live.
* **Ctrl + skill key** (user, 2026-10-26: "when manually attacking couple it with ctrl+ skill key";
  `Dungeon.CtrlSkill` on, check box "Ctrl + skill key too"): every press of the spam is `SkillTap` (IGameControls) =
  the plain key, 60 ms (`CtrlSkillGapMs`), then `WindowInput.CtrlKeyTap` (Ctrl down, 40 ms, key tap,
  40 ms, Ctrl up) — the same helper as the Laby stage 5 presses (§4.2). With `HoldSkill` the held key
  is let go every `SkillEveryMs`, Ctrl + key tapped, then held again. What Ctrl + skill does in the
  game is the user's word. Verified offline with a low-level keyboard hook (key order and gaps, Ctrl
  released after an F10 unbind mid-tap); not yet seen live.
* **Walk up to the boss** (user, 2026-10-24, `Lod.ApproachBoss` on, "Walk up to the boss (LOD)"): the first
  *new* "Attack succeeded" line of a fight (combat-log lines from the same whole-screen OCR, keyed like
  the Labyrinth's `ReadAttackLogAsync`; baseline = the lines at the fight's first targeted tick) ->
  `LodApproach`: `StopLodAttack` (Shura, else `StopAttackKey` x `ApproachStopPresses` 3) -> fresh frame -> **Ctrl+left click** the centre
  of the pink ring round the boss's feet (`ScreenAnalyzer.FindBossRing`: H 135-162, S >= 35, V >= 170 - under
  the boss's blue skill light the ring read S 50-80, too pale for `FindSelectionRing`; the biggest flat
  patch plus every pink piece within 5 % h, since legs and the character cut it into arcs) -> wait
  `ApproachWaitMs` (1.5 s) -> back to `LodAttack` (fresh "seen" timer, coordinate baseline re-read; a jump
  > `TeleportJump` since before the walk = died -> `LodRespawn`). The first click always goes out - the
  skill already stops the character on the ring at ~0.5 of its radius; a retry (<= `ApproachTries` 2) only
  when the character (0.5 w, 0.54 h) is still outside 0.6 of the ring's radius. Click clamped to the
  walk-click area (chat box!). Ctrl+click = walk (a plain click attacks). No ring -> `FindBossBelt` (3+ cyan
  patches stacked in one column, >= 8 % h tall, or one tall thin cyan patch) and the click goes 6 % h
  **below** it: a Ctrl+click on the belt itself is a floor point behind the boss (live: walked ~190 units
  past it; from behind, the "belt" found was a cyan blade on its back). Verified live 2026-10-24 at
  1920x1080: probe walk-away -> Ctrl+click ring -> 1183,1611 -> 1212,1775 at the feet, attack resumed by
  itself; two pilot fights: first hit -> click -> 0.56 / 0.12 of the radius -> attacking. When only the
  ring's front arc shows (boss over the character) its centre is a little in front of the feet. Snapshots
  `lod_approach` / `lod_approach_miss` (<= 3 per Start). Not seen: the belt fallback inside the pilot.
* **Keep to the ring** (user, 2026-10-27: "in LOD, try to go to the ring as much as possible, do a periodical
  check or maybe shura then ctrl+attack old or ctrl+click on the foot of the boss or ring"): after the first
  walk up of a fight, `LodAttack` looks for the ring every `Lod.RingCheckEveryMs` (**3000**; 0 = off) while the
  boss is targeted; the character further out than `Lod.RingStayMax` (**1.0** = on the ring) → the same walk
  up (`BeginLodApproach(..., ringOnly: true)`: Shura → Ctrl+click the ring → ≤ `ApproachTries`), log
  `Ring check N: off the pink ring (x of its radius out)`. A walk that did not get there → next check 3×
  later (`ResumeLodAttackAsync(reached: false)`). Distance = `ScreenAnalyzer.DistanceFromRing` (also printed
  by `--lodparts` as `away`): character at (0.5 w, 0.54 h), ring radii; the ring is ~half as tall as wide,
  so a flatter box (< 0.4) is a partial arc and the distance is also taken from where the whole ring's centre
  would be (front or back arc), nearest counts — with the character under the boss only the front arc shows
  (a 160×50 arc read 2.0 from its own centre, 0.89 corrected). Saved first-walk frames: 0.07–0.89 on / under
  the boss, 1.82–2.0 beside it. **Ring only, no belt** in the check and its walk: live 2026-10-27 23:20
  (2048×1152, v0.16.0) the first walk's 1st Ctrl+click (ring above the character, 2.33 out) put the
  character under the boss — camera inside the boss's body, no ring — and a cyan patch at the boss's side
  read as the "belt". So the belt is only used before a walk's first click (first walk of a fight); after a
  click, no ring in view ends the walk as reached ("no ring in view after N Ctrl+click(s) - under the boss"). "Ctrl + attack hold" from the request was not used: what Ctrl + skill does is unknown, the
  Ctrl+click walk is verified live. Not yet seen live.
* **The boss respawns after dying** (user, after run 2: one "fight" lasted 18 min) → the map is unlimited
  until its timer runs out; then the game sends the character to town → the zone guard teleports in
  again (if that is refused, "Still in … after the teleport" stops the run). A target gone for
  `BossGoneMs` and then re-targeted no longer counts as a kill.
* **Kill = "see you in hell"** (`Lod.DeathText`, what the boss says when it dies, user): a *new* line of
  it in the attack OCR (same whole-screen read as the name; an old line in the chat cannot re-trigger)
  → `LodLoot`: spam `Lod.LootKey` **`** (Oem3, vk 192; "`"/"~" are mapped) every 150 ms for
  `Lod.LootMs` **10 s** (minimum, user 2026-10-23; settings v19) — there is no autohunt here to pick
  drops up (user) — **no Tab while looting** → back to Tab for the respawn. The name / target-bar
  treasure icon gone `Lod.LootAfterMissingMs` **2 s** (was 5 s, v19) mid-fight = dead, loots the same
  way without counting a kill; no Tab in that 2 s gap either (Tab during an attack only when the loot
  key is empty).
* **Boss name in view while turning south → stop the turn and attack** (user); the heading is then
  unknown, so the next time the boss is lost the pilot turns south first (no buff) before tabbing again.
* **Killed by the boss → put back at the map's starting point** (coordinate jump > `Lod.TeleportJump`
  400 while attacking) → `LodRespawn` (two equal coordinate readings) → face south → `LodRun` again.
* Name gone `Lod.BossGoneMs` (10 s) while attacking → back to `LodRun` (lost target).
* **Boss name or treasure icon = targeted** (user). The name is matched per whole OCR **line** — the best
  hit was sometimes a single word ("[LOD]") too short to pass, and the pilot walked or turned with the
  boss right there, also right after a restart inside (user). No readable name → the target bar's chest
  (`monstericon.png`, top centre band; 0.90 at (391,45) on three targeted Labyrinth frames, not found on
  three without a target) counts too. No extra Tab while targeted (it could switch targets).
* (user: "boss name shown but not attacking"): the whole-screen OCR also reads the
  boss's name tag over its head, and the skill does nothing without a target. Targeted = the name in the
  **target bar**, the top-centre band (y < 13 %, |x − 50 %| < 22 %; measured on Labyrinth frames: y 5–7 %,
  x 52–55 %). Name tag only → Tab every `Lod.TargetTabEveryMs` (700 ms) alongside the skill until the bar
  shows it. Any sighting still resets the "gone" timers.
* **Buff** (user; autohunt is disabled on this map): `Lod.BuffKey` **O** on arrival and after being put
  back at the start (before the south turn), then every `Lod.BuffEveryMs` **290 s** (most buffs last
  5 min) while walking or attacking; a held skill is let go for it. Mid-fight the buff is ignored until
  the attack stops (user): `StopLodAttack` (also before the walk up to the boss), 0.4 s, then O, then
  the skill resumes. Empty key = off.
* **Stop the attack = Shura** (user, 2026-10-25, instead of spamming Space): `StopLodAttack(frame,
  presses, out how)` in `AutomationPilot.Lod.cs`. The user's `Assets/shura.png` (51×51, the blue burst)
  is matched in `Regions.Hotbar` (0.20, 0.85, 0.60, 0.15 — the skill bar; 1920×1080: x 600–1320,
  y 995–1050) with `FindIcons` ≥ `Lod.ShuraMatchScore` **0.80**; best score wins, a tie (the same icon in
  two slots, 1035 and 1240 at 1920×1080, both 1.00) → the leftmost; clicked `Lod.ShuraPresses` **3**
  times `Lod.ShuraEveryMs` **200** ms apart (user: "3 spam is enough, 0.2 s is okay"). Not on the bar →
  `Lod.ShuraKey` (UI "Shura key (LOD)", the slot the player put it in; default **E**, user) tapped the
  same way → empty key → `StopAttackKey` Space × 15 (buff) / `ApproachStopPresses` 3 (walk) as before. No frame (the buff before the south turn) = no icon lookup. Probe:
  `--shura <dir>` prints score/box per PNG: 0.95–1.00 on every saved bar (1024, 1152, 1920 wide),
  ≤ 0.70 on frames without one (login screen, stage5_closed, a window over the game). Not yet seen
  live — whether a click on the icon actually stops the attack is the user's word.
* **Out of entries → Auto Laby** (user, 2026-10-22): the LOD entry is on page `Lod.ListPage` **2** of the
  teleport list (user) — that page seen without it = out of keys → `Sequence.Mode` = `AutoLaby`
  (saved, buttons refreshed) and "Labyrinth" is clicked on the same page. Fallback for other layouts: With no "[Den of Rebirth]" entries left the map
  is not in the teleport list (run 8: page 3/3 without it, the item line was gone). The last page has no
  `>` in the list (a click there did nothing — "paged" 12 times) and a `<` (`leftchevron`, the right
  chevron mirrored, 0.985 on that frame; the list arrows ≥ 0.80, the hotbar's are outside the list
  region). Destination missing on the last page → in LOD: snapshot `lod_no_entries`, `Sequence.Mode`
  = `AutoLaby` (saved; `ModeChanged` refreshes the buttons), page **back** with `<` for "Labyrinth";
  first page reached without it → fail. The list's "3 / 3" counter is not read by OCR, so it is not used.
* `CheckZone` on Start in the LOD zone (user): boss name in view → **rebuff** (`StopLodAttack` + O, a fight may be
  running) → attack; otherwise Tab up to 3× (1.5 s apart) and look again; still nothing → buff, face south
  and `LodRun`. Tab is also spammed while turning south (`TurnTabEveryMs` 400 ms, user; the turn ticks every
  ~0.5 s, so in practice once per tap).
* The minimap zone title is cut off at the panel edge ("[The Labyrinth (Adep"): `ZoneMatches` accepts
  10+ letters of the destination's start ("[Ultimate Den of Re"); all zone checks (guard, CheckZone,
  arrival wait, re-login) use it.
* **Not seen live at all** — no LOD frame exists yet: minimap readability (coordinates, title), the south
  template's score and the boss name are all unverified.

---

## 5. Detection

### 5.1 Templates + learned scale

`ScreenAnalyzer.FindWithLearnedScale`:

* The PNGs may have been cropped at any resolution, so `clientHeight / 1080` is only a guess.
* First search runs a **scale range ×0.70 … ×1.55, step 0.05**; the winning scale is cached per
  `(template, clientHeight)`; later frames use the fast single-scale path.
* Threshold is deliberately loose: `Detection.TemplateThreshold` = **0.70**.
* Template lookup order: `%LOCALAPPDATA%\WuxAuto\templates` first, then shipped `Assets`.
* A PNG with transparent areas is cropped on load to its largest opaque patch (`LoadGray`).
  The templates folder is currently **empty**, everything lives in `Assets`.
* `upchevron` / `downchevron` are **generated at runtime** by rotating `rightchevron.png`
  (so the map list arrows need no user crop).

### 5.2 OCR

`OcrService` wraps Windows.Media.Ocr (English). Crops are upscaled 1.5× (`Detection.OcrScale`).
`TextMatcher.FindBest` is fuzzy (containment aware, `OcrMinSimilarity` 0.72, requires ≥4 chars), so
`Labyrinth` matches `[The Labyrinth (Adept)]` and OCR noise like `SanctifiedUnicor` still matches
`Sanctified Unicorn`.

### 5.3 Bright-blob fallback

`ScreenAnalyzer.FindBrightBlobs` = Otsu threshold + contours over a region, ignoring blobs that
touch the right edge of the strip. Used for:

* the map list's per-row `>` when the template fails,
* **the map list's page arrows**, see §5.4.

### 5.4 Map list paging (the most recently reworked part)

The map's monster list only shows ~12 rows; `Spectral`/`Sundersword` need paging.

`ScreenAnalyzer.FindMapScrollArrowsAsync(frame, roi)`:

1. OCR the region for the word **`Spawns`**.
2. Take the band **above** that word: `y = spawns.Y - 3h … spawns.Y - h/4`, `x = spawns.X ± 5h`.
3. Collect bright blobs in it, filter by size relative to the text height `h`, order by `CenterX`.
4. `down` = rightmost blob, `up` = leftmost **only if there are ≥2 blobs** (the up arrow is not
   drawn when the list is at the top).

`MapScroll` must therefore merely **contain the "Spawns" header**; it is a deliberately tall band
(0.68, 0.09, 0.32, 0.28; it started at x 0.84 until 2026-10-21, which missed "Spawns" at 1920×1080 where the panel ends at x≈1600 — down arrow there ≈1570,253; the `downchevron` fallback now needs 0.75, a 0.53 look-alike far right was clicked 30×) because the map window is **not laid out proportionally** across
resolutions. Order of preference in the pilot: header blobs → `downchevron` template → wheel (last
resort). Scrolling is **down-only**.

### 5.6 Minimap coordinates (anchor)

The panel keeps roughly the same pixel size at every resolution, so the old fixed
`Regions.Coordinates` / `Regions.Compass` (measured at 2302×1353) miss it at 1024×768. Run 30: Otsu dropped the 6 of `3604` at every scale (`304. 2012`) and the run gave
up 270 short of the boss; plain cubic upscales (×3, ×4, no threshold) are now tried after the Otsu
ones, then ×6 — that frame reads `3604.2012`. Plain OCR of
the label drops the first number and reads the comma as a dot (`. 16247`). The reader therefore:
OCRs `Regions.Minimap` (0.60, 0, 0.40, 0.42) to find lines with digits, re-reads each widened to the
left with a cubic upscale (×4, then ×3, then ×5 — which one reads cleanly depends on what is behind
the label; the north-pointing beam sits right behind it) + Otsu threshold (`OcrEnhancedAsync`), and accepts a line matching
`\d{3,6} , \d{3,6}` (comma with a space on one side) or `\d{4,6} . \d{4,6}` (Otsu often turns the
comma into a dot: `3489. 6867`); stat bars `200.000` / `13.300` / `100/100` never match. Adaptive
thresholding reads the comma but changed a digit (6867 → 6857) — do not use it. Light minimap
backgrounds (towns, e.g. Bohai Outpost) are not readable; the Labyrinth minimap is dark. The line
box is cached per client size (cleared with the scale cache every run) and is the compass anchor.
**The plain line is tried first (run 25):** the ×2 band OCR read `3601 . 2149` cleanly while all
three enhanced re-reads dropped digits (`, 2149`, `3-01 . 2149`), and the run gave up 300 short of the
waypoint after 40 unreadable frames. A plain line that fully matches the pair pattern (dot form only
with 4+ digits on both sides) is accepted as is; the probe's `coord read:` lines trace every attempt.
Re-checked on all saved frames: same values everywhere, four previously unread frames now read.
Verified on real frames: `3653 , 16247`, `3616 , 28733`, `3592 , 9309`, `3548 . 9670`, `3489. 6867`
(beam behind the label), live `3592, 7297`. `RunToTarget` logs `Run: at x, y (d from target)` every
step and tolerates 40 unreadable frames.

### 5.5 Compass

`CompassDetector` rotates `flashlight.png` through 360° (15° step) and template-matches inside
`Regions.Compass`; there is also a brightness-moment fallback (`CompassMode.Brightness`). Only
`FaceNorth` uses it (after the mandatory left sweep).

---

### 4.5b Keys (user, 2026-10-22)

**The teleport decides** (user: the item-list check below said "no Temple of Fiends Key" with keys
left, after a successful Labyrinth run). Labyrinth: teleport clicked but still in town (`ZoomOut`, 20 s /
45 s lagging) → keep waiting (no second teleport) until `Sequence.LabyKeyWaitSeconds` (120 s, UI "Laby key
wait (s)", user) after the teleport → still in town = out of Temple of Fiends Keys. LOD: not on teleport-list page 2 (or not on the last page) = out of Den of Rebirth
entries. `OutOfKeys`: one used up → the other mode (saved, buttons refreshed); both used up this Start →
**"Regular War on"** (`AutomationPilot.RegularWar.cs`, `PilotPhase.RegularWarOn`, `BeginRegularWar` / `TickRegularWarAsync` / `FinishRegularWar`; renamed from `AutoWar` / `AutomationPilot.War.cs` in Auto War's Task 1 - **not** `RunMode.AutoWar`; user 2026-10-22: players using the pilot get "Regular War" switched
off, so the character stayed in town during a war): `Sequence.AutohuntSettingsKey` **S** opens the autohunt
settings (≤ 3 presses, 2.5 s apart) → the AUTO BUFF dialog covers the list on lower resolutions: its title
(whole line "autobuff", not the "[Auto buff scroll]" item) → click the X right of it
(`FindCloseButtonRightOf`: first bright blob; live frame: X at 905,116, found exactly) → the "Regular War"
line → its box (`FindCheckboxLeftOf`: gold frame left of the word "Regular"; the frames of neighbouring rows
touch, so the box = a square of the frame's width centred on the label row) → ticked? (`IsCheckboxTicked`:
inner max > 140; measured 200 ticked vs 68 empty; 4/4 rows right on a live frame) → click if empty, ≤ 3 →
`J` → `Stop`. Not found in 60 s → `J` anyway + snapshot `autowar_unticked`. The Regular War row itself has
not been seen yet (the dialog covered it) — not verified live.

**Item-list check — off** (`Sequence.CheckKeys`, settings v18 turns it off):

In town, before a teleport (`CheckZone`, every run): the item status list at the bottom right
(0.62, 0.50, 0.38, 0.50) — **"[Temple of Fiends Key] N (time(s))"** = Labyrinth keys, **"[Den of
Rebirth] N"** = LOD entries; a missing line = run out. The chosen mode out → the other mode (saved,
buttons refreshed); both out → `J` in town (the game's built-in auto war, user) and `Stop`. OCR is
noisy ("[TemPléof FiendS Key] 62", "[Denpf Rebirth] 3") and sometimes skips a whole line, so: accents
stripped, keywords ("fiend"+"key", "rebirth"), three scales (×1.5, ×2, ×1), present if any read sees
it. Checked on six saved town/dungeon frames: all right (one needed the union — ×1.5 alone skipped the
Temple line). A hidden list (fewer than 3 "time(s)/min(s)" lines) decides nothing. The teleport-list
page-2 rule (§4.5) stays as a backup.

### 4.6 Restart on error (user, 2026-10-22, "Restart on error" checkbox, `Sequence.RestartOnError` on)

`Fail()` no longer stops the pilot: it saves the snapshot as before, waits `RestartDelayMs` (5 s) and
starts over from `Focus` (zone check → teleport or resume). At most `MaxRestarts` (5) within 30
minutes, then it stops as before. Stop / F10 / a closed game window still stop for good.

### 4.7 Auto confirm (user, 2026-10-25, "to make it faster to sell")

`src/WuxAuto/Automation/AutoConfirm.cs`, ticked by `WuxEngine.VisionLoopAsync` on every frame **while the pilot is
not running** (the pilot handles its own dialogs; its Ctrl+clicks would arm it). `Sequence.AutoConfirmDialogs`
(on, check box "Auto confirm (Ctrl + right click)"), `AutoConfirmText` "Confirm", `AutoConfirmButton` "OK".
The player's gesture is polled per frame with `GetAsyncKeyState` (Ctrl down + right button edge, or the
"pressed since last call" bit for a click shorter than a frame) while the game is in front and the cursor
is over its client → watch for 3 s (extended 1.5 s after every confirmed dialog so a spree with Ctrl held
keeps going). While watching: `ScreenAnalyzer.FindConfirmDialogAsync` OCRs the whole frame at ×1 (37–575 ms,
avg 157 on the saved frames) → the widest line containing "confirm" (one letter off per word allowed) →
the button: a line reading exactly "OK"/"0K" under it, else `FindGoldButtons` sized in text heights
(≥ 3 h wide, 1.2–5 h tall; the dialog keeps its pixel size at every resolution) under the message
(8 h down, ±12 h sideways), lowest row, right-most (OK bottom right; USE ALL above it; CANCEL is dark).
One click per 400 ms; a frame without the dialog resets the per-dialog count (a selling spree's dialogs
all pop up at the same spot); 3 clicks on one dialog that stays → warning + `autoconfirm_stuck`. Snapshots `autoconfirm` (≤ 3 per app start). Dry run only logs. Checked on the 342
saved debug frames: 5 hits, all the real `[Bohai Outpost (Adept)] Confirm to move?` WAR dialog (OK never
read as text, always the gold button, click dead centre at 1024, 1273, 1920 and 2048 wide), nothing else.
`tests/frames/confirm_use_dialog.png` is the user's crop of the USE dialog ("Fist Scroll Box Confirm to
use?", USE ALL (x100) / CANCEL / OK). **The sell dialog itself and the live click are not yet seen.**
Note: while the pilot is idle the Bohai dialog is not cancelled; a Ctrl + right click within 3 s of it
would confirm the move instead.

### 4.8 Auto War (user, 2026-10-27, `Sequence.Mode` = `AutoWar`, `AutomationPilot.War.cs` / `.WarTimer.cs` / `.WarFight.cs`, settings `AppSettings.War`)

Spec: `docs/superpowers/specs/2026-10-27-auto-war-design.md` (§4 flow, §9 not verified, §11 assumptions). The alt
stands in a **cave on page 3 of the teleport scroll** with the autohunt on; every war (Bohai Outpost, Adept) it
leaves, fights, and comes back to the **same cave and monster**. No town steps (autohunt off, Run skill, font size
do not run); the 5 s Bohai-dialog Cancel is skipped in this mode (the OK is what we want). Phases (`PilotPhase`):
`WarRemember, WarWait, WarJoin, WarEnter, WarBuff, WarFight, WarDead, WarBack, WarArrive, WarHuntOn`.

* **Start (`WarRemember`).** Zone must be a cave (not a home town, Labyrinth, LOD or Bohai) - else warning "Auto War
  must be started inside a cave on page 3 of the teleport scroll, in a home town, or inside Bohai Outpost" (`StartPlaceMessage`) and stop. Keys (`WarKeys(settings)` / `WarKeyProblem`): Skill, Target, Autohunt,
  Inventory required; Shura, Buff and the autohunt settings key optional; any key that is set must be pressable (`IsKnownKey`) -
  else warning + stop. **Buffs and Shura are optional (user, 2026-10-28: "make the skills optional, not required for pilot to
  start")**: `CheckWarSkillBar`, once per Start (`_warBarChecked`), looks for `WarBarIcons` - the buffs ticked on (`BuffIcons`,
  `WeaponBuffIcons`, `ItemBuffIcons`, minus `DisabledBuffs`) + Shura's icon only when `Lod.ShuraKey` is set - on `Regions.Hotbar`
  (`IconMatchScore` **0.90**, own score: warbuff2, a small grey streak, hit 0.871 on floor texture; Shura 0.951-1.000 on
  real bars, <= 0.781 elsewhere); the missing ones are **one warning** "Not on the skill bar (score 0.90 needed): warbuff3 (best
  0.84), shura (not seen), warbuff7 (no warbuff7.png in templates/Assets) - carrying on without them ..."
  (`DescribeMissingIcons(missing, hasTemplate)`: a template `ScreenAnalyzer.Library` never loaded is a PNG to add, not an icon
  to put on the bar) + `war_bar_missing`, and the run carries on. Shura in the missing list adds "- clear 'Shura key (LOD)' if
  this character has no Shura, or the fight still holds that key".
  Opens the teleport list on page 3 (`ListPage`), picks the entry that fits the minimap title (`WarText.BestListEntryFolded`:
  case-blind, I/l/1/| and O/0 folded; **two caves that fit = ambiguous = stop, never a guess**; 3 reads then stop), closes
  the list with its X, `Tab`s (<= 3) for the monster, **confirms the name against the map window's monster list** (a
  player's name can never be saved; the row whose **whole name column** is the name - `WarText.MonsterRows`, see Back - and
  the list's spelling is saved), saves `War.HomeMap` / `War.HomeMonster` (settings.json; the read-only "Back to:" line in the
  UI refreshes through `ModeChanged`, "(not set yet - press Start in the cave)" while empty).
  Start inside Bohai (user, 2026-10-28: supported) -> straight to the enter step, no skill bar check, nothing on that path needs
  the buff icons or Shura; with no cave saved one Info "No cave saved - after this war the pilot waits in town for the next one.
  ..." (`_warNoCaveTold`, once per Start) and after the war it waits in town (below).
  **Start in a home town (user, 2026-10-29: "allow auto war in city most are afk in town anyway")** -> `WarWaitInTown`: this
  Start's home is that town (`_warTownHome`, `WarStaysInTown`; "Auto War waits for the wars in <town> this Start (no cave trip,
  no autohunt)."), the skill bar check, `WarWait`. A saved cave is ignored this Start (kept in settings). Only the Start's first
  zone read counts (`_warZoneDispatched`): a restart that lands in town of a cave / Bohai Start goes on as after a war. Town home
  changes: the join skips the autohunt off (J in town starts a Smart Farm journey) and, with no dialog, the autohunt back on;
  after the war (or sent to town while waiting) -> `WarWait` again, no teleport; another zone where the trip back would start
  (`BeginWarReturn`) -> stop. Pinned by `WarTickTests.Start_InTown_*` / `Join_InTown_*`. Not seen live.
* **Wait (`WarWait`, `.WarTimer.cs`).** The menu is the ☰ button at the top centre (`menubutton.png`; **no key**) ->
  "Tribe Status" tile (`MenuTileText`) -> row `TribeRowText` "Bohai Outpost (Adept)" -> `hh:mm:ss` or `In progress`;
  then closes TRIBE STATUS (`PickWarClose`: only the X read in the title row, tolerance 16 px - **never a blind click**) and
  the MENU. Windows OCR drops `hh:mm:ss` in whole-frame reads, so `ScreenAnalyzer.ReadWarCountdownAsync` reads the
  digits with a colon-gap band read (user's screenshot: `00:04:25` = 265 s). Missing row = unknown, not zero. Re-reads ~2 min
  before zero and every ~15 min; `In progress` every ~5 min; 3 unreadable -> `tribe_unread`. Only reads the screen with the game in front.
* **Join (`WarJoin`).** `LeadMs` (10 s) before zero: autohunt off, confirmed in the combat log (`EnsureAutohuntOffAsync` logic);
  the WAR dialog (Auto confirm's `FindConfirmDialogAsync`, gold OK) is polled <= 1 s; none within `DialogWaitMs` (2 min) -> autohunt
  on again, back to the wait. Then the load and the minimap title "Bohai Outpost" (`Destination`, 60 s). The 5 s whole-screen
  check also watches for the dialog in `WarWait` / `WarRemember` (not once the remember step has its monster confirmed: that tick
  saves the spot first). Only with the game in front: the dialog wait runs on the war clock (`FrontClock`) and stands still behind
  another window.
* **Game in front.** Every Auto War step that reads the screen waits while the game window is behind another window
  (`WarInFront`: the timer read's steps, the join, the autohunt at the monster, the Settings visit and everything in Bohai; the map
  pick through the zone guard; the 5 s whole-screen check is skipped) - a second game client's dialog, map or log is never read as
  ours. The shared teleport steps (inventory, scroll, list pages, load) and the re-login still read, but send nothing then
  (input is refused while the game is behind).
* **Enter / buff (`WarEnter`, `WarBuff`).** Settings visit sets TAB target to `TabTargetOption` ("Enemy worth CP"; the shared
  FontSize phase takes the wanted option and steps the chevrons by `TabTargetOptions.StepsTo`; Laby/LOD still ask for Default; left
  as is after the war). Autobuff? "[Auto buff scroll]" in the item list, else `S` + the AUTO BUFF title (closed with its X, then the
  settings window; the "Regular War" label is the sign the settings are open). One round (`TickWarBuffUpAsync` step 3, the icon list
  fixed at the round's start by the pure `WarBuffClicks(war, byBuffKey)`): autobuff -> `O`, then the **item buffs** (user,
  2026-10-28: "with autobuff the item buffs on the skill bar are still clicked"; every rebuff); no autobuff -> `BuffIcons`
  (warbuff1, 3, 4, 5, 6) -> `WeaponBuffIcons` (warbuff2 = sword, warweapon1 = white-gold lightning around armour, warweapon2 =
  blue swirl; skill buffs, so not with autobuff) -> `ItemBuffIcons` (waritem1 = light scroll with a glyph, waritem2 = blue scroll); only the ones ticked on, each once; one icon per
  tick, found on that tick's frame, `BuffClickGapMs` (1 s) apart; a missing one is skipped with **one warning per Start**
  (`_warMissingIcons`, cleared in `Start`; "cannot be looked for (no X.png in templates/Assets)" when its PNG is missing). Rebuff every `BuffEveryMs` (**345000** = 5 min 45 s), only between fight cycles.
* **Buff switches (user, 2026-10-28).** `War.DisabledBuffs` (default empty = every buff on) holds the template names unticked in
  Settings > Auto War > **Buffs** (`ViewModels/WarBuffSwitch`: one check box per icon of `BuffIcons`, `WeaponBuffIcons`,
  `ItemBuffIcons`, labelled by list and place: "Buff 1".."Buff 5" (warbuff1, 3, 4, 5, 6), "Weapon 1 (sword)" (warbuff2), "Weapon 2"
  (warweapon1), "Weapon 3" (warweapon2), "Item 1/2"; the PNG from `templates\` then `Assets` loaded with `BitmapCacheOption.OnLoad`
  (no file lock; any exception -> next folder, else label only - it runs in the view model's constructor); one-line tooltip
  "Clicked when buffing" (+ "(uses an item; also clicked with autobuff)"; the X buff warbuff4 = "Buff 3" says it is also the fight
  cycle's X buff) ending in the template file name, since the logs name templates; WrapPanel `ItemWidth` 168, two per row - "Weapon
  1 (sword)" with its icon is ~159 DIP). `WarSettings.IsBuffOn` / `SetBuffOn` (a new list replaces the old one: the loop thread may
  be reading it); saved like every other setting (close / Save settings). A character uses one weapon, so usually two of the three
  weapon buffs are unticked. SelfTest check "war buff switches" (labels, tooltips, DisabledBuffs written, damaged PNGs).
* **Fight (`WarFight`, `.WarFight.cs`).** `Tab` ~1/s until a **target** (in the target box only: a name over its numbers line
  (`WarText.FindTargetName`; not the version/ping line, a system notice or a lone number), the numbers line alone or the chest - see
  "Target rule" below) or the "Gate is Open" line; cycle = `Tab` -> X buff (`XBuffIcon` warbuff4;
  skipped when unticked; ticked but not found -> one Warning per Start with its best score or "(no warbuff4.png in templates/Assets)" + `war_xbuff_missing` (`WarnXBuffMissing`, `_warMissingIcons`; with autobuff nothing else looks for it), 0.90 gate kept) -> **hold the Shura key `ShuraHoldMs` 1200** (`_heldSkill`, released by `Advance` / Stop / F10; **no Shura key
  = no hold**, one Info per Start "No Shura key is set ..." (`_warShuraSkipTold`)) -> skill spam (`SkillTap`, Ctrl + skill)
  until no target for `TargetGoneMs` (2 s) -> next cycle. After the war has started a death/rebuff goes back to the cycle, not the start wait.
  **Target rule (2026-10-28, user: "it seems not to try to attack" + a sample: player "zMADARAz" in red, "49293/49293" in white on a red
  bar, buff icons under it, no chest):** the fight reads `ScreenAnalyzer.ReadWarTargetAsync` (the remember step keeps `ReadTargetAsync`,
  unchanged): name by plain x1.5, then white x2, then **red x2** (`RedTextMask`: R - max(G,B) > 80 -> 255; `OcrRedTextAsync`;
  `FindTargetName` on the red read alone) - and the target bar's **"N / M" line alone is a target** (`WarText.TargetHpLine`: the whole
  line is the pair, spaces optional, O -> 0, <= 3 stray characters each end that are neither letters, digits nor ':' ("HP 100/100"
  never passes), up to 9 digits (boss bars "16284125/65136500"), M >= 100; dates and times never pass; **the line's centre in the
  middle half of the target box** - without that, 55 of the 592 saved frames, none with a target, read the pet / mount panel's gold
  "100/100" bar under the top bar at 0.69-0.71 w (1024, 1273, 1538 wide) as a target; the target bar sits at ~0.52 w), name "?" then.
  "Gone" (step 4) uses the same rule with `seenOnly`: it stops at the first sign (the chest, then the first read with a name or the
  numbers line). Before this a red name never read (the white read blacks it out, the plain read drops the bar's slash), so a player
  was never a target and the skill never went out - the likely cause of the report; once a target is seen nothing else stands between
  it and the skill (X buff and Shura are optional, `SkillKey` is required at Start). The Trace log says which read decided
  (`Target 'X' (red x2)`, `A target (HP numbers, name unread: '...')`). `--target` 2026-10-28 (592 saved frames, none from Bohai): 128
  named (58 x1.5, 70 white x2, 0 red - no red name in them), 4 by the numbers alone (all four with the chest: a boss bar under a
  dialog), 11 by the chest alone, 136 with the chest, 449 none; 0 frames without a target with a numbers line in the band; 127 of the
  128 named targets would also be seen by their numbers alone.
  **Walk out of the respawn point (user, same day: "try to move out of respawn" -> toward players on screen):** step 5 of the fight,
  `AutomationPilot.WarFight.cs`. Due (`WarWalkDue`) when **the war has started** (`_warStarted`: "Gate is Open" or a first target -
  before that everyone stands at the closed gate), `WarWalkAfterTabs` 5 Tabs in a row found no target, fewer than `WarWalkMaxSteps` 6
  steps in this spell and fewer than **`WarWalkMaxTotalSteps` 18 in this war** (reset by a target and by `BeginWarEnter`; at 18 one Info
  line, then Tab only), not resting; and only while the last minimap title read (`WarZoneCheckAsync`, `_warTitleIsWarMap`) was the war map
  (else "the minimap does not read" once per spell). On the next tick's frame: whole-screen OCR x1 -> `WarWalkGoal`: no goal at all
  while a window covers the world (`WarText.IsWindowTitleLine`: "Spawns" / "Monsters on this map" - the map's rows read like tags, 147
  saved frames chose "Serpent Knight" -, INVENTORY, TELEPORT SCROLL, SETTINGS, SERVERS); else lines passing `WarText.IsPlayerTagText`
  (3+ letters, no ':', no sentence ending '.', '!', '?' or ',', no opening quote - boss speech -, <= 4 words - notices -, not one
  bracketed name alone - zone banners, "[Premium]" -, no combat-log word: attack, damage, auto hunt, arrived, succeeded), not a text
  found to be UI this war, centre outside **`WarWalkBands`** and more than `WarOwnTagRadius` **0.22 h** from the character's spot
  (0.5 w, 0.54 h; the brief said 0.12 h, but our own "[Premium]" / name tags sit up to 0.176 h away) -> the nearest.
  `WarWalkBands`, measured with `--warui` on the saved frames (the UI scales with the client): top strip 0-0.13 h; combat log
  0-0.31 w x 0.13-0.56 h (log text 0.00-0.30 w, 0.20-0.55 h); chat + bottom bar 0-0.40 w below 0.56 h (chat 0.01-0.38 w, 0.66-0.96 h);
  pet panel + minimap 0.56-1 w above 0.23 h and minimap panel 0.74-1 w above 0.37 h (pet panel 0.57-0.74 w, 0.06-0.22 h; minimap panel
  0.76-0.98 w, 0.06-0.36 h); event notices 0.80-1 w x 0.37-0.60 h (0.83-1.00 w, 0.37-0.58 h); item list 0.70-1 w below 0.55 h
  (0.72-1.00 w, 0.56-1.00 h); skill bar 0.20-0.80 w below 0.85 h. Searchable: 26 % of the frame and 0.31-0.80 w at the character's
  height (the reading regions `AttackMessage` / `Minimap` / `ItemList` used first left 16 % and 0.40-0.62 w).
  The goal -> **Ctrl+click** (`WarWalkClick`: 4 % h below the tag, pulled in to <= 0.25 h from the spot, clamped to the LOD walk area
  0.25-0.74 w, 0.25-0.78 h), Shura released first, dry run logs only (and remembers no goal); Info "No target after N Tabs - walking
  toward 'X' (step k/6)."; `war_walk` snapshot on a spell's first step (<= 3 per Start); `WarWalkWaitMs` 1.5 s -> Tab. **Static-goal
  memory** (`WarWalkStatic`): on the next look, the last goal's text within 2 % of the screen of where it was = it did not move with
  the camera = UI ("'X' stayed put after the step"), ignored for the rest of the war (`_warStaticTags`, cleared by `BeginWarEnter`).
  After 6 steps Tab only for `WarWalkRestMs` 20 s (war clock), then a new spell; no tag -> no click, "no players in view" once per
  spell, and `WarWalkEmptyLooks` 3 empty looks in a row -> the same 20 s rest (no whole-screen OCR on every Tab). A target ends the
  spell; `BeginWarFight` (buffs, revive) starts the spell fresh. `--warwalk` (592 saved frames): a goal on 36 frames - 13 other
  characters ("(Tiger) [ZhuFan]", "[PvP] (Dragon) Delisya", "Roronoazoroo", "Indomie"...), 8 WuxAuto's own overlay labels in debug
  snapshots ("selected", "pink rin"; not on a live frame), 15 other words ("search" 8, desktop captures); the first version (wide
  reading regions, no window rule) had a goal on 68: 5 players, 21 "SERVERS", 12 map-list rows, the rest UI words. **Not seen live**: the red read on a real red name, the numbers rule on a real player bar, name
  tags over players in Bohai, the walk itself.
* **Death watch (`WarDead`).** The top-left HP "cur / max" read about once a second. Death = `Cur == 0` and Max in 0.5-1.05x of the
  **alive max** (the "0/3000" bar and the MP line never count; the alive max is learned from agreeing reads, a raise is capped at 1.5x
  and warned once). Unreadable = alive (`war_hp_unread` after 10 s). Dead: release keys, wait for HP > 0, rebuff, fight; 30 s dead -> `war_dead`.
* **Back (`WarBack`, `WarArrive`, `WarHuntOn`).** Home town (or the cave itself, or any other zone read 3x -> `wrong_zone`) = war over:
  scroll -> page 3 -> the **exact** saved entry (`SameEntry`; "[Dark Soul Cave IX]" must not pass for "[...X]"), twice in town -> stop; map icon -> the saved
  monster's row -> `>` (the Laby pick, but the row is `WarText.MonsterRows`: the OCR line whose **whole name column** - the words before the Lv / Spawns
  numbers, a one-letter marker dropped - is the name, same `FoldedKey` or >= 0.90 and not longer: "Goblin" never clicks the "Goblin Elite" row, which the best
  single word did, and "Cave Bat" is found; two such rows = stop, never a guess); wait for the combat-log line "Arrived: <monster>" (`ArrivedText`; **new lines
  only** - an old one from an earlier trip is ignored; 90 s then carry on); `J` confirmed by "Auto hunt started" (<= 3 taps) -> back to `WarWait`. At most
  **3 trips back in 30 min without a war in between** (`WarReturnAllowed`; `BeginWarEnter` clears the count), then stop - a cave that takes the teleport but
  sends the alt back to town would loop, a teleport a round.
* Restart / reconnect land by zone: the saved cave -> `WarHuntOn` (the autohunt on, read-confirmed) -> wait; Bohai -> enter; town -> wait there
  (town home, or no cave saved), else back. After a re-login
  the reconnect counts the war map or any zone title on the minimap as back in (`WarReloginZone`; the cave is `Destination`, but a re-login during the war
  lands in Bohai), not while a login screen still shows.
  Dry run never clicks the dialog OK, so `WarEnter` times out (60 s) and restarts - expected.
* **Upgrade note (settings across versions).** Once settings.json holds `"Mode": "AutoWar"`, a build from before Auto War throws on the unknown
  enum name in `Load`, falls back to the defaults and saves them on close - the Laby/LOD settings are gone. Release and update every installed copy
  before choosing Auto War on a PC where an older copy shares settings.json (`%LOCALAPPDATA%\WuxAuto`). The loader is not changed: old builds cannot be fixed.
  Settings v23 (2026-10-28) is **removal only**: it takes `warbuff2` (the sword buff, now in the new `WeaponBuffIcons`) out of a saved
  `BuffIcons`, keeping the user's order and other entries; nothing is ever added to `BuffIcons`, because v0.19.0 requires every
  `BuffIcons` icon on the bar at Start (with v23 it simply checks five). `WeaponBuffIcons`, `ItemBuffIcons` and `DisabledBuffs` are
  new properties v0.19.0 ignores - and drops when it saves (the check boxes come back all ticked). The SelfTest probes load with
  `SettingsStore.Load(persistMigration: false)` (the migration stays in memory) - a probe of a newer build never rewrites the
  settings.json of a running older copy. The app itself and UiShot (which restores the file) still save the migration.

**Probe numbers (Task 4, 561 saved frames, none from Bohai):** `--hp` 511 read (508 white-text + 3 plain fallback), none reads 0/N; `--target` 114 named,
119 with the treasure chest (historical: the Task 4 reader on 561 frames - today's `--target` numbers are under "Target rule" above); menu button score lowest real game frame 0.836 (1440x900), highest non-game 0.451, `MenuButtonScore` **0.70**;
Shura on the bar 0.951-1.000; `warbuff1-6` crops: **no saved frame shows them** (0.90 unproven on a real alt bar). `--tribe` on the user's Tribe Status
screenshot: `00:04:25` (265 s).
`--warbar` 2026-10-28 (592 saved frames, `debug\`): the first alt bar frame (`war_stop`, 2048x1152, a v0.19.0 stop that morning) has
all six: warbuff1 0.932, warbuff2 0.989, warbuff3 0.920, **warbuff4 0.912** (closest to the 0.90 gate), warbuff5 0.982, warbuff6
0.970; `warweapon1` (the user's crop, Assets since 2026-10-28) 0.968 on 44 frames of the main character's 1273x1024 bar, always
the same slot (424,970), checked by eye - the real icon; `warweapon2` best 0.716 (on no saved bar); `waritem1` (light scroll with a glyph, 31x24) best 0.653 and `waritem2` (blue
scroll, 28x30) best 0.591 - no saved frame shows them on a bar, and nothing else reaches 0.90 (look-alikes: tiny 15-20 px boxes); Shura
on 554 frames. Run over `war-inputs\` itself the probe says nothing: each crop is its own frame and `Regions.Hotbar` is its bottom 15 %.

**Not seen live (spec §9 + the task reports) - everything inside Bohai:** the Bohai minimap title after the dialog; "Gate is Open"; an enemy player in the target box
(and whether it has the chest); HP 0 and the revive (does the game send a dead alt to town?); the buff, weapon and item buff icons (warweapon2, waritem1 and waritem2 are on no saved bar) on a real alt bar at 0.90; the ☰ menu opened and
the **MENU close geometry** (from faint glyphs on one screenshot; a window whose X is not read is left open with `war_close_stuck`) and whether ☰ toggles the menu shut;
TRIBE STATUS X at other resolutions (one 795x829 crop); the AUTO BUFF X as a bright blob; the "Regular War" label in the autohunt settings; page 3 of the teleport list at other
resolutions; the X of the TELEPORT SCROLL (closing it costs no scroll, and whether the inventory stays open); the wording "Arrived: <monster>"; "Auto hunt started" in a cave;
"1F" vs "IF" in cave names (folded, but unseen). Known limits: buffs more than doubling max HP hide a death; the Settings visit (shared FontSize phase) has no War zone check;
a war dialog during `WarRemember` before the monster is confirmed joins before the spot is saved (the return then stops instead of guessing) - once it is
confirmed, the dialog watch waits for the save; a war dialog while a timer read has a window open can leave it open.
Each part logs what it saw and saves `war_*` snapshots (<= 3 per tag per Start) - the first live war gives the frames to tune from.

### 4.9 Auto Blossom (user, 2026-10-29, `Sequence.Mode` = `AutoBlossom`, `AutomationPilot.Blossom.cs`, settings `AppSettings.Blossom`)

Spec: `docs/superpowers/specs/2026-10-29-auto-blossom-design.md`; plan `docs/superpowers/plans/2026-10-29-auto-blossom-mode.md`.
Blossom Garden needs no key (user). Phases: `BlossomTurn, BlossomWalk, BlossomAttack, BlossomLoot, BlossomRespawn` (+ the
shared `WarBuff`). The zone guard covers them (and `WarBuff` in this mode); a home town = the AFK checker's kick (user) ->
teleport in again (LOD's rule); still in town after the teleport = "destination closed?" fail, never out-of-keys.

* **Town -> map:** the shared steps (autohunt off, Run skill, font size), TAB target **`Blossom.TabTargetOption` "Nearest monster"** (user, 2026-10-30: "make sure that the tab target is nearest monster" - Tab then reaches monsters further off; `AutomationPilot.TownTabTarget`, whatever the Laby/LOD "TAB target: Default" box says; once per Start, in town only - a Start inside Blossom Garden leaves the game's value alone), no Regular War untick, scroll page 1
  "Blossom Garden", zoom out after the minimap title reads -> `BeginBlossomArrival` = buffs (`BeginBlossomBuff(thenTurn: true)`)
  -> `BlossomTurn`.
* **Turn:** `ScreenAnalyzer.FindPillarPair` (`Assets/blossompillar.png` = the left pillar's grey base, 96x42, cropped from the
  user's zoomed-out 2000x1168 frame at 788,270; hits in `PillarBand` (0.15, 0.05, 0.62, 0.45), duplicates per place dropped, a
  pair = one row, 0.10-0.40 w apart, the most central). User's frame: left 0.979, right (green-tinted) 0.807, offset +0.020.
  No pair -> `D` (`TurnDirection.Right` = `Input.RotateKey`) `TurnTapMs` 100, 350 ms settle; after `TurnTaps` 7 one Info, after
  `TurnMaxTaps` **15** or `TurnMaxMs` **10000** (whichever first; user, 2026-10-30: "reduce the turn to pillars to 15 turns or
  10s"; was 50 taps, ~1 min; v24 moves a saved 50) a warning + `blossom_no_pillars` and the walk. Pair off centre (> `PillarCenterTolerance` 0.06 w) -> 40 ms fine
  taps, direction flipped when the offset grew (D's direction is not assumed), <= 12. No Tab while turning.
* **Walk:** `BlossomRoute`, starting at the **nearest** waypoint (arrival 19148,19062 -> waypoint 1). **Default route since
  settings v25 (user, 2026-10-30: "make it default"):** the 83 waypoints the user recorded on the first island at 09:13
  (Record route, no camera taps; waypoint 1 = the arrival spot), `LoopFrom` 1, `BackAndForth` **on**, `WaypointTurns` empty.
  The route lived only in the recording PC's settings.json / `blossom-route.txt`, so a second PC's fresh v0.23.0 install
  walked the first default instead. v25 moves a file whose waypoints are exactly the first default
  (`BlossomSettings.First17Waypoints()`: 17 user waypoints, 1-5 then 6-17 looped, `LoopFrom` 6, turns `1:A5, 2:D5, 3:A5,
  4:D5`) to the whole new route block; a recorded route stays (`SettingsMigrationTests.V25_*`). `BlossomPilotTests.Harness`
  still runs the walk tests on the first route (their coordinates are written for it). The first step after a turn clicks **straight up**; the movement sets `_headingBase` (the view's
  world direction is unknown), then `LearnHeadingError`; 3 steps in a row > 0.6 rad off the heading (>= 60 units each) =
  the first sample was a wall push -> heading re-learnt from the latest step (final review: one bad first sample locked
  in a wrong heading for the run). **Continuous clicks** (user, live 2026-10-29: "continuously click to run instead of
  staggered and not every click is checked"): the same walk click goes out every `WalkClickEveryMs` 350, `WalkClickPx`
  260 from the centre; the coordinates are read every `WalkReadEveryMs` 900 and decide the next click (a "step" = one
  read); the target bar is read every 700 ms; Tab never holds the tick. **Camera turns** (user: "In waypoint 1, do a
  left turn / 4x A, then on waypoint 2 do right turn / D 5x"): `WaypointTurns` "1:A5", "2:D5", "3:A5", "4:D5" (user, with the new waypoints 1-6 after the 4th live run; "N:KC" or "N:CK", edited in the app as "Camera turns" = `WaypointTurnsText`) - when leaving that waypoint
  the walk taps the key (`TurnTapMs`), then learns the heading again from the next step. Game behind another window ->
  the walk clicks and counts nothing. Tab every `TabEveryMs` 1000. Reached (<= `WaypointTolerance` **40**, or blocked within 150 = "close enough"; within 300 one shorter click per read every 450 ms so it homes in - at 100 the walk stopped 61 north of waypoint 1 and every next leg started off the line, live run 5 (user); before that, live 2026-10-29
  waypoints 2, 4, 5 lie just past a railing / pond edge and the walk stopped 86, 67, 71 short at the old 60) -> stand + Tab
  <= `WaypointWaitMs` 3000 (clamped <= 5000, AFK checker; the pause reads no coordinates, so an unreadable minimap cannot
  stretch it), then the next. **Blocked** (live: wedged against the side of the bridge at ~13900, 15140 between waypoints 5
  and 6, where the camera had zoomed in): from the 2nd still step a full-length click each read, the ways of
  `BlossomStepWays` in turn relative to the way (right, left, back, back-right, back-left, ahead-right, ahead-left), the
  camera zoomed out again at the start of each round of 7 - **until it moves** (user, 2026-10-30: "If the character didn't
  move for 5s, try to zoom out and move in every direction until it moves"; it used to skip the waypoint on the 5th still
  step); the 5th still step only warns and saves `blossom_stuck`. The no-progress rule (15 reads without getting 30
  closer) still skips a waypoint (`blossom_too_long`). 40 unread coordinates -> fail. Snapshots: <= 3 per tag per Start.
* **Run, don't stagger** (user, 2026-10-30: "dont walk, run far away instead of staggered"): `Blossom.RunAheadDistance` **600**
  (0 = off): each walk read passes every waypoint closer than that (a recorded route has one every 120 units, so the walk was
  always homing in and pausing) and aims at the first one at least 600 along the route; a waypoint with a camera turn is still
  reached. **Random skip** (same day: "randomly try to skip multiple waypoints so that it doesn't look like a bot"):
  `RandomSkipMax` **4** (0 = off) - after a kill 0-4 waypoints ahead at random (`BlossomRoute.SkipWithin`), only onto ones within
  1000 of where the fight ended (a typed route's far-apart waypoints never become one long straight line).
  **Off the route after a fight** (live 11:20: a chase ended 111 from waypoint 20 and the walk tried waypoint 7, 2750 south,
  straight through the terrain for minutes): the first walk read after a fight (`_blossomRecheckRoute`) - further than
  600 + `RunAheadDistance` from the waypoint it was heading for and within 300 of another -> on from that one. **Unblock ways**
  go on round the list (`_blossomUnblockWay`) until the walk gets 30 closer to its waypoint - live, each sidestep moved the
  character 20+ units, reset the still-step count, and it stepped right, left, right ... for minutes.
* **AFK Checker** (user, 2026-10-30: the chat whispers "(Whisper) [AFK Checker] : Start 1/5" ... "Reset 0/5" - "if 1+/5, try
  to move a little bit to other directions"): `Blossom.AfkCheckerText` "AFK Checker" (empty = off). The shared 5 s
  whole-screen OCR (`BlossomAfkWatch`: Blossom phases or the buff round, game in front) -> `BlossomText.AfkCheckerCount`:
  the bottom-most chat line (left 45 % w, below 55 % h) naming it with a "k/5" -> k >= 1 -> `TryBlossomAfkStep`: the held
  skill let go, a Ctrl+click half `WalkClickPx` from the feet, a different way each time; `blossom_afk`. Not yet seen live
  (the whisper lines are from the 10:20 snapshot).
* **Walk back** (user, 2026-10-30, with a recorded 83-waypoint first-island route: "1 - 83 then 83, 82, 81 to 1 then loop"): `Blossom.BackAndForth` (check box "Walk back the same way"; on by default since v25, for the recorded default route) -> `BlossomRoute.Next(..., backAndForth, ref forward)`: forward to the last waypoint, back to `LoopFrom`, forward again (`_blossomForward`, reset per run; a log line at each turnaround). On the way back a waypoint's camera turn is undone when leaving it (the key flipped, A <-> D, same taps), so each stretch has the camera it had on the way out; a turnaround taps nothing (the last waypoint's own turn is never made). Record route ticks it when the recording ends > 400 from its loop start (`RouteRecording.WalkBack`, `ClosingGap`): a loop would cross the map in one straight line. Pinned by `BlossomRouteTests` / `BlossomPilotTests.BackAndForth_WalksTheRouteBack_AndUndoesItsCameraTurns` / `RouteRecordingTests`. Not yet seen live.
* **Following the road** (user, live run 7: "not aligned by the dirt road ... goes straight to the water beside the bridge"): along a leg the walk aims 250 units ahead on the line from the waypoint just left to the next one (`BlossomRoute.LookAhead`, pure pursuit), so drift is pulled back onto the user's line; within 300 of the waypoint it clicks the waypoint's own spot from the character's feet (0.55 h, >= 80 px - a short click just under the feet lands on the character's body and does nothing; 1.8 px per unit). A death mid-fight needs two coordinate reads that agree (run 7: "13521" read as "1352" sent it back to the pillars).
* **Record route** (user, 2026-10-29: "I walk the road once by hand and WuxAuto saves the waypoints"): Settings > Auto Blossom > **Record route** / **Loop starts here** / **Stop recording**. `Automation/RouteRecorder` (desktop, ticked by `WuxEngine` beside `AutoConfirm` while the pilot is not running) reads the coordinates every 500 ms and polls the turn keys (`GetAsyncKeyState`, game in front) into `RouteRecording` (Core, pinned by `RouteRecordingTests`): a waypoint every 120 units, a jump > 400 only when the next read agrees, A / D presses = the latest waypoint's camera turns (net, "N:5A"), the loop at the latest waypoint (or the spot, >= 60 past it). Stop (>= 2 waypoints) replaces `Waypoints` / `LoopFrom` / `WaypointTurns`, saves, and writes `blossom-route.txt` (a copy - a pre-Blossom WuxAuto drops the block when it saves). Start while recording saves the recording first. Not yet used live.
* **Minimap title:** `ScreenAnalyzer.ReadMinimapLinesAsync` (all modes): the band read, and without a title-like line the
  `MinimapTitleStrip` (0.76, 0.03, 0.23, 0.08) x2 then x3 - live 2026-10-29 the whole-band OCR over Blossom Garden's bright
  yellow map read **no words at all** and the arrival failed "never showed on the minimap" twice.
* **Start inside:** the autohunt is made off first (`EnsureAutohuntOffAsync`, `trustVisibleLine: true`; skipped in a dry run),
  then the buffs (or the fight, a Jaso already targeted) and the walk from the nearest waypoint - **no pillar turn** (user,
  2026-10-30: "it is possible to start it already in the map and no need to look for the pillar"; away from the arrival spot
  they are not in view, and the turn took 50 taps for nothing). The turn stays for the teleport's arrival and after a death.
* **Target:** whole-screen OCR line in the target-bar band (y < 13 % h, |x - 50 %| < 22 % w) containing the name after its "[S]"
  ("jasopractitioner") or >= 0.85 like `TargetName`, else `monstericon` >= 0.80 at the top centre (user: the Jaso shows the chest).
  Anything else is not attacked. **Full health only** (user, 2026-10-30: "Only Attack monster if full health"): the same read's
  target-bar numbers (`BlossomText.TargetHp` on `WarText.TargetHpLine`, box (0.25, 0, 0.50, 0.16)) below max -> not fought
  (Info at most every 5 s), the walk and Tab go on; unread numbers do not stop a fight. Live: a Jaso at 615378/750000 was
  another player's.
* **Fight / loot:** the skill key **held** through the fight (`Blossom.HoldSkill`, on; check box "Hold skill (Blossom)"; user,
  2026-10-30: "spam or hold attack skill, currently it skills then normal attacks" - one tap casts once, then normal attacks;
  with Ctrl + skill the key is let go for a Ctrl tap every `Lod.SkillEveryMs`, as LOD's hold), off = tapped every
  `Lod.SkillEveryMs`; the loot key every `LootEveryMs` 200 **through the fight** (`LootWhileFighting`, on; check box "Loot
  while fighting"; user, 2026-10-30: "When attacking a monster, try looting also, so that when killed after 1s, the pilot can
  proceed" / "the 1s delay after killing is enough for the loot, so remove the loot after kill") - gone `TargetGoneMs`
  **1000** = kill -> straight on with the walk (rebuff if due). Off: kill ->
  `LootDelayMs` 1000 -> loot key every `LootEveryMs` 200 for `LootMs` **2000**, no Tab -> the walk (a fight met during a waypoint
  pause moves on to the next waypoint, in the walk's direction - with walk-back too). User, 2026-10-30: "the check if monster
  is killed, set it to 1s, loot for 2s" (were 2000 / 5000; settings **v24** moves those old defaults, a value set by hand
  stays). **Stuck in a fight** (user, same day: "if a monster is tab, but there is 5s that it didn't move and no attack
  succeeded log, try to click on other direction to reposition"): every `FightStuckMs` **5000** (0 = off; `BlossomFightStuckAsync`)
  the fight's latest coordinates and the combat log's `AttackConfirmText` lines (`ReadAttackLogAsync`) are compared with the
  last check; moved < 40, the log unchanged and the target bar's HP not lower (live 11:19: looting while fighting fills the log
  with "No items or [Silver] to obtain", so the attack lines scroll away - the HP is the hit signal that survives) -> the skill let go, a **Ctrl+click** (walk, the target stays - LOD's approach)
  `WalkClickPx` from the feet, the 7 `BlossomStepWays` in turn (a zoom-out at the start of each round), 0.8 s, then the hold
  again, and the next look 1.5 s later (until it moves or hits; then the ways start over); `blossom_fight_stuck`. An unread
  spot never counts as still. Live 10:14: fired on a flying Jaso up a slope at 750000/750000 with no hits; killed 9 s after. `FightMaxMs` 120000 -> give up (user: a Jaso dies within 2 min), Tab, and targets are
  ignored for 10 s so the same one does not start the same fight again. Coordinate jump >
  `Lod.TeleportJump`, confirmed by the next read, with **no target seen for 1 s** (`BlossomChaseTargetMs`) -> `BlossomRespawn`
  (settle) -> arrival again. With the target still shown it is a chase and the new spot becomes the baseline (live
  2026-10-30: TAB target "Nearest monster" + the Run skill ran ~500 units between two reads, alive at full HP, and was taken
  for a death 6 times in 15 min, each costing a 50-tap pillar search away from the pillars; a misread that read the same
  twice, "19274, 1901", passed the two-read check too). A death whose target bar stays up is not caught (then
  `FightMaxMs`); live deaths in Blossom are not seen yet. After the settle, the pillar turn only runs within 500 of the
  arrival spot 19148, 19062 (`BlossomArrivalX/Y`, where every live teleport landed and a respawn is assumed to); elsewhere it
  was not a death (live 10:20: alive at full HP, the target lost after a chase while the minimap did not read, 778 units on)
  -> buffs and the walk from the nearest waypoint.
* **Rebuff:** the Auto War buff steps (`TickWarBuffAsync`, no Bohai zone check in this mode; `FinishWarBuffAsync` ->
  `AfterBlossomBuff`), timer `War.BuffEveryMs`; `Start` resets `_warHasAutoBuff` / `_warBuffedAt`. Due -> after a loot or at a
  waypoint pause; 30 s overdue -> on the walk. Never mid-fight. In `WarBuff` an unread minimap title counts as Blossom Garden
  (the autohunt settings / AUTO BUFF dialog may cover it for longer than the guard's 6 s); a town or another zone still counts.
* Pinned by `BlossomRouteTests`, `PillarPairTests`, `BlossomPilotTests` (first click up, nearest start, pause <= wait, Jaso ->
  attack -> loot -> walk with no click / Tab in between, another target ignored, turn gives up, rebuff after the loot, arrival
  buffs before the first `D`; final review: a given-up fight walks on, a wrong first heading is re-learnt, Start inside turns
  the autohunt off, the buff finishes with the title unread, the status names the phase, 5 still steps skip with <= 3
  snapshots, the pause ends on time with the coordinates unread). **Not seen live:** which way `D` turns, the live pillar scores at other resolutions, walls between
  waypoints, the Jaso name in the target bar, the AFK kick.

## 6. Settings and persistence — read this before changing defaults

* Path: `%LOCALAPPDATA%\WuxAuto\settings.json` (settings, log, debug and kills paths all come from `AppPaths` in WuxAuto.Core and are unchanged by the M1 split; also written on close; **Save settings** writes it
  immediately).
* **Screen positions are not persisted** (since 2026-10-20, `CurrentVersion = 3`):
  `AppSettings.Regions` and `DungeonSettings.MapWheelX/Y`, `MapRowChevronX`, `BossSelectX/Y`, `BossSelectStep` are
  `[JsonIgnore]`. `AutomationPilot.ResetRunState` (Start + every looped run) calls
  `AppSettings.ResetPositions()` and, on the next tick (loop thread), `ScreenAnalyzer.ResetScaleCache()`.
  So **region defaults in code are always what runs** — no migration needed when you change one.
  The region editors were removed from the UI (they were wiped every run; the Compass row was
  bound to non-existent `CompassCenterX/Y/Size`). Any *new* positional setting should get
  `[JsonIgnore]` and a line in `ResetPositions()`.
* **Every other setting: the saved file wins over code defaults.** Changing a non-position default
  in code will *not* reach an existing install. History: a stale saved `MapScroll`
  (`0.85,0.11,0.15,0.12`) once cut off the map arrows — that is why positions are no longer saved.
  `SettingsStore.Migrate` + `CurrentVersion` exist for this; bump the version and add a step when a
  non-position default must be forced onto existing installs.
* `Normalize()` clamps everything and stamps `Version`; `Load()` also persists a migration.

Key `DungeonSettings` defaults. The settings panel's **Dungeon** section (2026-10-21) edits Skill key,
Autohunt key, Target key, Font size % and the switches Tab + skill at the last waypoint, Autohunt off
for the run, Ctrl + skill key too, Click the stage 5 gate, Auto reconnect, Hold skill (LOD), Walk up to the boss (LOD), Walk to the boss spot (LOD), TAB target: Default, Autohunt off in town, Run skill on in town (and the Run key field, default W), Regular War off before Laby; everything else only via `settings.json`.
`tests/WuxAuto.UiShot -- <png> --settings --bottom` renders that panel (`--mini` renders the Mini window instead, `--kill-alert` fills the last-kill line, `--kill-sound` shows a picked kill sound + the Default button); it restores `settings.json`
afterwards (it ticks check boxes for the picture and the window saves on close — that once switched
Dry run on in the real settings).

| Setting | Default | Meaning |
| --- | --- | --- |
| `Stages` | 4 boss names above | order of the run |
| `GateTemplate` / `GateText` | `dungeongateopen` / `DUNGEON GATE OPENED` | banner detection |
| `GateTimeoutMs` / `GateGiveUpMs` | 40000 / 180000 | banner timeout / hard stop |
| `GateOcrEveryNPolls` / `GateFallbackMs` | 2 / 15000 | banner OCR cadence; between stages open the map anyway after 15 s without a banner (`nogate_s<N>` snapshot) |
| `GateToMapDelayMs` | 3000 | wait after the banner before clicking the map |
| `MapOpenDelayMs` / `MapReopenRetries` | 1000 / 3 | map button retries |
| `MapScrollMaxSteps` / `MapScrollSettleMs` | 8 / 600 | paging |
| `RowChevronTemplate` / `RowChevronThreshold` | `rightchevronfoc` / 0.55 | selected-row `>` |
| `BossNameClickDelayMs` / `StageDelayMs` | 800 / 2000 | row selection timing |
| `BossSpawnWaitMs` / `BossPollMs` | 60000 / 2000 | boss wait / poll (was 5000: death noticed too late, banner missed; settings v5 migrates) |
| `AutohuntKey` / `TabKey` | `J` / `Tab` | |
| `BossSelectDelayMs` / `BossSelectX,Y` | 2000 / 0.5,0.5 | click to select the boss |
| `AttackConfirmText` / `AttackConfirmMs` | `Attack succeeded` / 1200 | select confirmed when the combat log changes; empty = single click (settings v6 migrates the old `Attack Successful`) |
| `BossSelectMaxTries` / `BossSelectStep` | 17 / 0.06 | select click pattern around the centre |
| `FlashlightTemplate` / `FlashlightMatchScore` | `flashlight3` / 0.85 | "facing north" (v13 → flashlight3) |
| `RotateLeftSweepMs` / `RotateLeftKey` / `RotateStepMs` | 0 / `A` / 180 | optional blind turn (off) / face-north tap key / tap length (v11 → 180) |
| `ZoomOutNotches` / `NorthOnArrival` | 15 / true | after the labyrinth loads: zoom out, face north at once (§4.1b) |
| `BossGoneMs` | 10000 | name/marker absent this long (Tab re-pressed every ~2 s, at least one Tab must reach the game) = dead |
| `Route` / `WaypointTolerance` | `[3603,1973]` / 60 | points walked first, in order (the diagonal from stage 4 hits a wall; settings v8 adds it) |
| `TargetX,Y` / `TargetTolerance` | 3325,1932 / 30 | final run (last boss, left of the route point) |
| `FinalBoss` / `FinalBossPressAutohunt` | `Sacred Sky Lord` / **true** (v15) | stage 5 at the target: Tab → select → watch (no spawn wait). With the flag on, `J` turns autohunt **off** right after stage 4 dies (user: its auto-buff stopped the gate walk, run 33), `WaitBoss` at stage 5 first makes sure it is off (combat log, §4.2 `SelectBoss`) and `J` goes on again only after the manual attacks (hold + taps) hit; off = autohunt keeps running from stage 4 |
| `ManualAttacks` / `HoldSkillMs` / `ManualAttackIntervalMs` / `ManualAttackWaitMs` | 10 (v21) / 3000 / 300 / 500 | stage 5: the skill taps on the boss after the held press (user, 2026-10-27: "Attack Hold skill, then do 10x attack (total 11)"; v20 had 15 taps and no hold), how long the first press holds the key (UI "Hold skill (ms)"; also the stages 1–4 hold before `J`; 0 = taps only / `J` after the spawn wait, clamp 0–30000; the length is my guess, not the user's), the minimum gap between two presses, the wait after the last press before the attack-line check. `AutohuntConfirmMs` (the 2 s wait after `J` before the clicks) was removed the same day — a stale key in settings.json is ignored |
| `AutohuntStallMs` | 15000 | stage 5 `WatchBoss`: autohunt on, boss up, no new attack line this long → `J` off, select and attack by hand again (`stall_s5`; user, 2026-10-27); clamp 3000–120000 |
| `HoldBeforeAutohunt` | true | stages 1–4: hold the skill key once on the boss in view before `J` (user, 2026-10-27: "on other stages, before going autohunt, try to skill hold first"; UI "Hold skill before autohunt (stages 1-4)"); off = `J` in `WaitBoss` right after the spawn wait. New bool, a saved file without it loads `true` — no migration |
| `CtrlSkill` | true | every manual skill press (stage 5: the held press and the `ManualAttacks` taps; stages 1–4: the held press before `J`; LOD skill spam) = the skill key, then Ctrl + skill key (user, 2026-10-26); with `Lod.HoldSkill` the held key is let go for a Ctrl tap every `Lod.SkillEveryMs`. New property, so a saved file without it loads `true` — no migration (checked on this PC's settings.json) |
| `War.*` (`WarSettings`; UI block "Auto War": Shura hold, Rebuff every, Lead time, TAB target, Buffs (one check box per buff icon), read-only "Back to") | see right | `Destination` "Bohai Outpost" (teleport list entry + minimap zone), `TribeRowText` "Bohai Outpost (Adept)", `MenuTileText` "Tribe Status", `LeadMs` 10000 (user: 10 s before zero), `DialogWaitMs` 120000, `TabTargetOption` "Enemy worth CP" (user), `BuffIcons` warbuff1, warbuff3, warbuff4, warbuff5, warbuff6 (the plain buffs; v23 removes warbuff2 from a saved list, removal only; an empty list is refilled with the defaults), `WeaponBuffIcons` warbuff2 (sword), warweapon1, warweapon2 (user, 2026-10-28; skill buffs clicked after `BuffIcons`, not with autobuff; new property), `ItemBuffIcons` waritem1 (light scroll with a glyph), waritem2 (blue scroll) (user, 2026-10-28; clicked after the weapon buffs, with every rebuff and with autobuff too; new property), `DisabledBuffs` [] (the unticked buffs of all three lists; empty = every buff on; a file without the new lists loads their defaults; `NormalizeWar` trims the three new lists, drops blanks, keeps each name once, never refills an empty one), `XBuffIcon` warbuff4, `BuffClickGapMs` 1000, `BuffEveryMs` 345000 (my call: 5 min 45 s; the user's buffs last at least 6 min), `ShuraHoldMs` 1200, `TargetGoneMs` 2000, `GateText` "Gate is Open", `ListPage` 3, `ArrivedText` "Arrived", `ArriveWaitMs` 90000, `IconMatchScore` 0.90, `HomeMap` / `HomeMonster` "" (written by Start's remember step). New block (v0.19.0, no version bump; v23 removes warbuff2 from `BuffIcons`); keys are reused (`Dungeon.SkillKey` / `CtrlSkill` / `AutohuntKey` / `TabKey`, `Lod.ShuraKey` / `BuffKey`, `Sequence.AutohuntSettingsKey`) |
| `HomeTowns` | Piercing City, Molten City, Wandering City | every clan's home town (user, 2026-10-21): any of them on the minimap = "in town" (zone guard, LOD kill, stage 5 closed, still-in-town, re-login); the one seen (`TownIn`, remembered per Start) is where the return teleport goes |
| `ReturnDestination` | `Piercing City` | fallback until a home town has been seen; after stage 5: `J` (autohunt off) → teleport scroll there → `Done`; only then may `Loop` start the next run |
| `RunClickPx` / `RunStepWaitMs` / `RunMaxSteps` | 200 / 1500 / 200 | final run steering |
| `NoPathText` | `no walkable path` | logged only, diagnostic |

Keys and mouse buttons are read only by `KeyboardMouseControls` (Core, M3): the pilot asks for a
`GameAction` (or a skill / turn / walk) and never names a key. `Input.RotateKeyReverse` and
`Sequence.RotationMode` are no longer read by anything (their only user, `ApplyTurn`, had no callers and
was deleted in M3); they stay in settings.json and the UI until removed with a settings migration.

Input: `InputMode.ForegroundSendInput` (default) requires the bound HWND to be foreground;
otherwise the action is suppressed + logged and retried. `BackgroundMessages` posts to the HWND —
works for some clicks, but **long key holds (the 5 s `A` sweep) only work in foreground mode**, and
the sweep is skipped in background mode. Ctrl holds (`CtrlClick`, `CtrlKeyTap`, `CtrlWheel` for the
zoom) all go through `WindowInput.WithCtrl`, which sends the foreground Ctrl key-up even when F10
unbinds the window mid-hold (UI thread) — `KeyUp` refuses an unbound window, and an injected Ctrl
left down sticks for every program.

---

## 7. Debugging workflow

1. Ask for / take an **F11 snapshot** (or use the automatic ones the pilot saves).
2. `%LOCALAPPDATA%\WuxAuto\debug\` contains:
   * `*_fail_*.png` — saved on failure, with overlays,
   * `*_rowclick_s<N>_*.png` and `*_chevron_s<N>_*.png` — automatically saved around the map row
     selection (max ~6 per run; tag includes the stage),
   * `*_snapshot_*.png` / `*_scan.png` — F11 / Test scan.
3. Run the frame probe on it:

   ```powershell
   dotnet run --project .\tests\WuxAuto.SelfTest -- --frame "<png>"
   # plus a zoomed crop to see exactly where a click landed:
   dotnet run --project .\tests\WuxAuto.SelfTest -- --frame "<png>" --crop 860 130 164 140 5
   ```

4. Overlays use `DebugArtifacts.SaveFrame(frame, overlays, tag, click)`; the click point is drawn
   as a crosshair. Overlays are drawn opaque (alpha 255) since M2; older snapshots show white or see-through boxes where the alpha was 0.
5. Logs: the log pane, and since 2026-10-20 also `%LOCALAPPDATA%\WuxAuto\logs\wuxauto_<start time>.log`
   (one file per app start). `WuxAuto.exe --autostart` binds the detected game window and presses
   Start, so a run can be launched and followed from a terminal. The most useful lines:
   * `'X' is not visible - clicked the down arrow (above 'Spawns', n/8).`
   * `Selected the 'X' row ...` / `Clicked the highlighted '>' of 'X' at x,y (0.99).`
   * `Boss for stage N is up: ...` / `Boss for stage N is gone.`
   * `Turning left ('A') for 5s ...` / `Searchlight lined up (score 0.9x)`
   * `At x,y: within 30 of 3325,1932 after n step(s).`

---

## 8. Measured UI facts (1024×768 client — current test resolution)

Captured frames are **client only**; screenshots pasted from chat may include the ~31 px title bar,
so y values there are ~31 px larger.

```
map title "MAP"                     y ≈ 122
"Current map" button                770–975, y 150–188
"Monsters on this map"              y ≈ 214
up arrow                            ≈ (900, 180)
down arrow                          ≈ (946, 180)   <- verified on 4 real frames
"Spawns" header OCR box             (885, 190, 32×9)
"Lv" header                         x ≈ 850
first list row                      y ≈ 222, ~33 px per row (12 rows visible)
row names                           start x ≈ 639
row '>' chevrons                    x ≈ 950
map panel right edge                x ≈ 990   <- right of this is game world:
                                                 clicking there moves the character!
```

Other resolutions measured earlier: 1400×866 (down arrow ≈1355,165; Spawns ≈1310,187; row `>` x≈1367),
1432×1000 (up 1303,160 and down 1373,160 visible). The map window does **not** scale proportionally,
which is why regions are tall bands and positions are anchored on OCR text, not ratios.

Template scores seen on real 1024×768 frames:

| Template | Score | Note |
| --- | --- | --- |
| `rightchevronfoc` | 0.994 | matches **every** row chevron; row-band + column guards are what make it correct |
| `rightchevron` | 0.955 | |
| `downchevron` (rotated) | 0.867 | box centre sits near the glyph's bottom edge; prefer the blob centre |
| `upchevron` (rotated) | 0.869 | |
| `flashlight` | 0.985 | on a dungeon frame in `Regions.Compass` |
| `mapicon` | 0.983–0.993 | learned scale ≈0.78 |
| `teleportscroll` | 0.999 | |

---

## 9. Current state

### Works / verified

* Capture (DXGI Desktop Duplication), binding, DPI-aware clicking, foreground guard.
* Teleport flow (live, end to end).
* Learned-scale template detection at the test resolution.
* Coordinate reader on a real frame; compass/flashlight on a real dungeon frame.
* Map list scrolling: **re-fixed 2026-10-20** — arrows are now located from the "Spawns" header and
  return (946,180) on all four real frames; the stale saved region that caused it can no
  longer happen: positions are not persisted any more (§6).
* Map row selection + highlighted `>` click at 1024×768 (snapshots prove the flow reaches
  `AdvanceStage`).
* Full state machine compiles and is wired; F11/snapshots/probe tooling is solid.

* **First complete run, live (run 12, 2026-10-20 20:30–20:51, 21 min):** teleport → 4 stages →
  north (flashlight2 0.96) → waypoint 3603,1973 in 25 steps → boss 3325,1932 in 3 more (55 s from
  north) → `Sacred Sky Lord` selected on click 1, gone after 3.5 min (4 Tab checks) → `J` off →
  teleport to Piercing City → `Loop` started run 2 (user stopped it). Auto-reconnect verified
  live several times (all 5 steps incl. OK).

### Open / unverified

1. **AimGate:** with a 40 px tilt the camera still showed only floor (`gate_view` 20:46) → raised
   to 150 px, not yet seen live. The coordinate run works without it.
2. **`Regions.GateBanner` (0.05, 0.05, 0.90, 0.55) is still a guess** — no F11 frame of the
   `DUNGEON GATE OPENED!` banner has been supplied.
3. **`monstericon.png` is unverified.** Boss presence is currently detected by OCRing the full
   screen (`Regions.BossName` = whole client) for the stage name, which costs ~100 ms+ per poll.
   An F11 frame taken right after `Tab` with the boss on screen would let us (a) verify the icon
   template, (b) narrow `BossName`/`MonsterIcon`, (c) make `WatchBoss` much cheaper and safer.
4. Map list paging / row chevrons are only verified at 1024×768. The header-anchored method should
   scale, but check at least 1920×1080 before claiming it.
5. `RunToTarget` works live (run 12). A coordinate misread (8014 → 9014) once stalled it until
   stage 5 closed; the hold-back + re-anchor rule for that is not yet seen live.
6. **Select retry: the real message is `Attack succeeded!Normal/Critical damage(+N) Attribu...`**
   (seen on a live `attack_s1` snapshot, 2026-10-20). The first version looked for
   `Attack Successful` and counted lines, which failed because the log is always full. The
   change-based check is not yet verified live. `Regions.AttackMessage` (0, 0.10, 0.40, 0.85) does
   cover the log at 1024×768 (lines at x≈20–285, y≈200–410). A miss-click near the centre walks the character, and a click
   on a normal mob would also print the message.

---

## 10. Gotchas / hard-won lessons

* **Settings JSON overrides defaults** — see §6. This is the single biggest time sink so far.
* **The app stays x64** (`PlatformTarget x64`, `publish.ps1 -r win-x64`). Emgu CV's runtime package
  also carries win-x86 and win-arm64 `cvextern.dll`, but only the win-x64 build is shipped and tested
  (OpenCvSharp, used until the M2 port, had only win-x64 natives).
* **Never write into a Mat's memory from managed code** on this machine (Windows on ARM, x64
  emulation): `Marshal.Copy` into OpenCvSharp's `mat.Data` crashed the CLR. With Emgu CV a frame is a
  Mat header over the pinned capture buffer (`new Mat(rows, cols, DepthType.Cv8U, 4, ptr, stride)`,
  `WuxEngine`), pixels are read out through OpenCV (`CvHelpers.Bytes` = `Mat.CopyTo(byte[])`) or
  copied *from* `DataPointer` (preview, OCR), and copies are `.Clone()`.
* **Emgu CV 5 is OpenCV 5.0**: `putText` draws its own fonts (not 4.x's Hershey strokes) and, with
  anti-aliasing on a BGRA image, blends the alpha channel - the text comes out nearly transparent and
  Windows OCR reads garbage from it. Synthetic test text goes on a BGR canvas, then `Bgr2Bgra`
  (WarProbe `--tribe`, 2026-10-28). Overlay labels on the preview / debug snapshots use the new font.
* **Desktop Duplication cannot capture exclusive fullscreen**; the game must be windowed/borderless.
* **Hybrid-GPU laptops:** the default adapter (dGPU) cannot duplicate a monitor wired to the iGPU
  (`DXGI_ERROR_UNSUPPORTED`), so the device must be created on the output's own adapter — and with an
  explicit adapter D3D11 requires `DriverType.Unknown`; `Hardware` + adapter is `E_INVALIDARG` by design
  (0x80070057; `--dxgi` here: unknown/all-levels → duplicate OK, every hw + adapter variant fails). That
  bug kept capture from starting on a friend's PC (2026-10-21). Order now: default adapter (hardware) →
  output adapter (Unknown) → output adapter (Hardware) → WARP. Even then the friend's RTX 3060 (Optimus)
  laptop got `DXGI_ERROR_UNSUPPORTED` from every `DuplicateOutput` (the app runs on the dGPU) →
  **`WindowCaptureSession`** (Windows Graphics Capture of the game window, works across adapters) takes
  over automatically when duplication cannot start; `WUXAUTO_CAPTURE=window` forces it. It crops the
  window image (DWM extended frame bounds) to the client area: here 2052×1199 window → 2048×1152 at the
  same screen position as duplication, and it captured the game while Explorer covered it (duplication
  shows whatever is on screen). Windows 11 draws a yellow frame round the window (no `IsBorderRequired`
  in the 19041 projection). Workaround without it: Windows Graphics settings → WuxAuto.exe → Power saving.
* **Desktop Duplication is refused (`E_ACCESSDENIED`) while a secure desktop is up** — lock screen,
  UAC prompt, Ctrl+Alt+Del. After `AccessLost` the capture thread retries every 2 s
  (`RecreateSession`) instead of faulting; that fault once ended a run at stage 3 (run 17, 22:21).
* **The map panel does not fill the client at 1024×768** (right edge ≈990). Clicking right of it
  sends the character walking. Any click fallback near the panel edge must be clamped.
* `rightchevronfoc.png` scores ~0.99 on **any** row chevron, and row chevrons sit in the same column
  as the scroll arrows — always constrain by the OCR'd row band, and keep the arrow band above the
  `Spawns` text.
* OCR word boxes are tight; `h` (box height) is ~9 px at 1024×768. Offsets expressed in `h` are
  more stable than pixels, but the blob approach beats both.
* The scroll arrows are tiny (`~30×12`); a click one glyph-height off misses. Click the **blob
  centre**.
* Window-input safety: every send is gated on the bound HWND being foreground; a suppressed action
  is not a bug, it logs `Input blocked ...`.
* Debug snapshots include overlays; a filled box or label in a snapshot is the renderer, not game UI (snapshots before M2 show them white or see-through: alpha 0).
* `Assets\*.png` are copied to the output on build; user overrides go to
  `%LOCALAPPDATA%\WuxAuto\templates` and win over `Assets`. That folder is currently empty.
* Stray PNGs in the repo root are the user's original screenshots — do not delete them.

---

## 11. Next steps (in order)

1. **Live 4-stage run at 1024×768.** Watch for `clicked the down arrow (above 'Spawns', n/8)` and
   `Clicked the highlighted '>'`. If the list fails to move, inspect the two automatic snapshots
   for that stage.
2. **Get an F11 frame of the gate banner** → tighten `Regions.GateBanner`, verify
   `dungeongateopen.png` score; if it is poor, re-crop the template.
3. **Get an F11 frame right after `Tab` with a boss visible** → verify `monstericon.png`, narrow
   `Regions.BossName` (and/or `MonsterIcon`), which speeds up `WaitBoss`/`WatchBoss`.
4. **Validate paging + row selection at one larger resolution** (1920×1080), then spot-check 2560×1440.
5. **Run `FaceNorth` + `RunToTarget` for real** and tune `RunClickPx` / `RunStepWaitMs` /
   `TargetTolerance` from the coordinate deltas in the log.

If a non-position default changes, remember §6 (bump `CurrentVersion`). Region defaults apply
on the next run automatically.

---

## 12. Conventions

* C# 13 / nullable enabled / file-scoped namespaces, `sealed` classes, one type per file where it
  makes sense; settings are plain settable properties (serialized by `System.Text.Json`, enums as
  strings).
* Every region is a `NormRect` (fractions of the client) and converted with `.ToPixels(w, h)`.
* Detection methods return `TemplateMatch` / `PixelRect` / `(bool Found, string Text)`; the pilot
  adds `OverlayItem`s so the preview explains its decisions.
* Log every decision at the level that matches: `Success` for a completed action, `Info` for a
  step, `Warning` for something surprising but recoverable, `Error`/`Fail` only when the run stops.
* Keep `README.md` in sync — the user reads it, and so should you.

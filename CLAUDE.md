# WuxAuto — project handoff

Single-file briefing for picking this project up in a new session. Written 2026-10-20 from the
state of `C:\Projects\personal\auto-wux`. Git since 2026-10-24 (public: github.com/atherosxd/WuxAuto);
earlier history exists only in this file.

---

## 1. What this is

`WuxAuto` is a **.NET 10 / WPF** pilot for the game window `Wuxen2.exe` (TwelveSky2, Unity port).
It captures the game with the **Desktop Duplication API**, finds UI elements with **OpenCV template
matching + Windows.Media.Ocr**, and sends **keyboard/mouse input only to the bound window**.

The automation goal:

1. Teleport into **[The Labyrinth (Adept)]** via the in-game teleport scroll.
2. Clear a **4-stage dungeon**, stage by stage, using the map window to pick the next boss.
3. After the last stage: **face north**, then **run to the world coordinate `3325,1932`** (the 5th/last boss; it is not in the map list, it is reached on foot)
   (tolerance 30).

Everything it does is visible: detections are drawn on the live preview and every step is logged.

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

Another PC: `.\publish.ps1` → `dist\WuxAuto-win-x64.zip` (self-contained, no .NET install; the
unused `opencv_videoio_ffmpeg` DLL is dropped). `App.OnStartup` runs `Core/StartupCheck` before
the window: Media Foundation DLLs (imported by `OpenCvSharpExtern.dll` — missing on Windows N and
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
clean, pushed) → `publish.ps1 -Version` → `gh release create --target <HEAD>` (GitHub makes the tag). `Core/UpdateService` reads
`releases/latest` on start (Yes/No with the notes) and on **Check for updates**; install = download to
`%TEMP%\WuxAuto-update`, hidden `install_update.cmd` waits for the PID, `robocopy /E` over the app folder,
restarts; log `logs\update_*.log`. Refused while running, from a build folder (no `coreclr.dll`) or a
read-only folder. Probes: `--update-check [owner/repo]`, `--update-install <appFolder> <zip>`.

Package for someone else (self-contained, no .NET needed; `src/WuxAuto/Assets/QuickStart.md` becomes
the zip's top-level `README.md`):

```powershell
.\publish.ps1     # -> dist\build\WuxAuto\ (the build), dist\WuxAuto-win-x64.zip (~93 MB) and dist\WuxAuto\
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

# stage 5 selection ring (FindSelectionRing) for every PNG in a folder; crops -> %TEMP%\wuxauto-ring
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

# auto-confirm dialog (message line + OK button) for every PNG (absolute path; tests\frames holds the user's crop)
dotnet run --project .\tests\WuxAuto.SelfTest -- --confirm "$env:LOCALAPPDATA\WuxAuto\debug"
```

`--do` frames land in `%TEMP%\wuxauto-step<N>-<verb>.png`. Check the frame before every click: an
Escape with a target selected only clears the target (no SETTINGS window), and the clicks meant for
the window then walk the character (2026-10-24). `WuxAuto.slnx` does not build `tests\WuxAuto.SelfTest`
— build it on its own before `--no-build` runs, or the probe is stale.

The frame probe prints, per template, the best score, box, learned scale and timing, plus OCR
words and the map scroll-arrow result. `--crop` writes `<frame>.crop.png` next to the frame.

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
| **Kill screenshots** (user, 2026-10-24: "to check whether it looted or not") | opens `%LOCALAPPDATA%\WuxAuto\kills`. `AutomationPilot.KillShots.cs` + `Vision/KillScreenshots`: plain JPEG (q 90, no overlays, newest 500 kept) when the pilot sees a boss die — Laby `WatchBoss` (banner / name gone) → `laby_s<N>_<boss>`, LOD death text → `lod_kill<N>`, target gone 2 s → `lod_gone` — and a second one when it moves on: LOD at the end of `LodLoot` (`_looted`); Laby 3 s later (`_after`, only in a zone-guarded phase) or earlier, right before `ClickMap` / `OpenInventory` act (the final boss's return teleport opens the inventory ~1.6 s after). Restarts drop a pending shot (`ResetRunState`). Not yet seen live |
| **Kill chime / Kill notification / Test alert** (user, 2026-10-26: friends run the pilot on a second PC next to their main one and LOD does not always loot everything - "a reminder to recheck"; "do the same for Laby") | `AutomationPilot.BossKilled` (string, loop thread) → `WuxEngine.BossKilled` → `MainViewModel.OnBossKilled` (UI thread) → `Core/KillAlert`. Raised in `OnLabyBossDead` (both `WatchBoss` kill paths, every stage incl. 5: "Stage 2: Spectral down") and at the **end** of `TickLodLoot` ("Bai Gu Great down" - after the loot, the reminder is to check what it left; both LOD kill paths go through it). Chime: `templates\chime.wav` if present (a bad file → built-in + warning), else `KillAlert.GenerateChime` (A5-C#6-E6, 1.6 s, 16-bit mono) written to `%LOCALAPPDATA%\WuxAuto\chime-builtin.wav` - **played from a file on purpose**: `SoundPlayer` from a stream hands a managed buffer to async `PlaySound` (pinned only during the call). Notification: WinRT toast, silent, 2 h expiry, AUMID `atherosxd.WuxAuto` registered under `HKCU\Software\Classes\AppUserModelId` (DisplayName + `notify-icon.png` from the window icon) - an unpackaged app needs that; `ToastNotifier.Setting` throws "Element not found" here, so it is only read best-effort. `Sequence.KillChime` / `KillNotification` (on; check boxes under Loop). Status box + Mini window bottom row show `LastKillText`. A failure logs once per app start; **Test alert** always reports. Verified 2026-10-26 on this PC: chime plays, notification centre count for the AUMID 0 → 1, the button in the real app logs "chime OK" / "notification OK". Not yet seen during a live kill |
| checkboxes | `Loop`, `Focus on start`, `Only while game is focused`, `Restore cursor` |

---

## 3. Repository map

```
src/WuxAuto
  Assets/              shipped templates: teleportscroll, rightchevron, rightchevronfoc,
                       mapicon, flashlight, dungeongateopen, monstericon (.png)
  Automation/          AutomationPilot.cs - the whole state machine (~1650 lines)
  Capture/             DesktopCaptureService - DXGI Desktop Duplication + frame pool
  Core/                AppSettings (all tunables; positions reset per run), SettingsStore (load/save/migrate),
                       Geometry (NormRect/PixelRect), LogEntry, paths
  Engine/              WuxEngine - capture thread + vision loop + pilot tick
  Input/               WindowInput - foreground-guarded SendInput, PostMessage mode, wheel
  Interop/             NativeMethods, window enumeration, global hotkeys, process info
  Vision/              TemplateMatcher, TemplateLibrary, ScreenAnalyzer, OcrService,
                       CompassDetector, OverlayRenderer
  ViewModels/, MainWindow.*   WPF UI
tests/WuxAuto.SelfTest      offline probes (see above)
tests/WuxAuto.UiShot        renders the window to a PNG
branding/                   wuxen2-source.png + make_icon.py -> WuxAuto-logo.png (README) and
                            src/WuxAuto/WuxAuto.ico (exe + window icon; "AUTO" drawn over "ONLINE")
README.md                   user-facing documentation (kept up to date - read it too)
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
| `WaitBoss` | waits `BossSpawnWaitMs` (60 s), taps `J` (autohunt), taps `Tab` (again every 1 s until something shows — one Tab on arrival at stage 5 showed nothing for 25 s, run 23), then looks for `monstericon.png` or the stage name; needs the boss visible `BossSelectDelayMs` (2 s) |
| `SelectBoss` | **Stage 5 only (user, 2026-10-22 — on stages 1–4 it broke the run, those keep the old flow: `J` after the spawn wait, centre clicks until an attack line → `WatchBoss`):** autohunt only after selection; `WaitBoss` does not press `J` at stage 5 (it engaged before the character was at the stage 5 boss, and autohunt did not attack a selected boss). Selected = the **magenta selection ring** on the floor under the target (`ScreenAnalyzer.FindSelectionRing`: HSV H 138–156, S ≥ 89, V ≥ 190 in (0.05, 0.12, 0.70, 0.78), ≥ 1.5/10 000 of the client; on 267 saved frames every hit was a real ring, the HUD's magenta specks stay below) **plus** the boss name (`FindBossAsync`), or a new attack line after a click / the stage 5 skill. Then `TickManualAttackAsync` presses `SkillKey` `Dungeon.ManualAttacks` (**15**, UI "Manual attacks"; was 10, settings v20 raises 1–14 to 15) times at least `ManualAttackIntervalMs` (**300** ms, UI "Attack gap (ms)"; measured from the press itself, `_lastManualPressAt`) apart — **each press, here and in the skill spam before it, is `TapSkill`: the plain key, then Ctrl + skill key** (user, 2026-10-26: "when manually attacking couple it with ctrl+ skill key"; `Dungeon.CtrlSkill` on, check box "Ctrl + skill key too"; the plain key's key-up, 60 ms, then `WindowInput.CtrlKeyTap`; a blocked Ctrl tap still counts as the press; the logs read `'1' + Ctrl+1`; not yet seen live); **only a new attack line after the last press counts** (user 2026-10-23; the log is read every tick, earlier hits are forgotten at the last press), checked `ManualAttackWaitMs` (500, user) after it, none → press again, up to 3× — then **(user, 2026-10-26: "stage 5 boss still manages to get stuck on autohunt but not attacking it") the selection ring must show** (`FindSelectionRing` on the current frame) **and a new attack line after the last press** (`_manualHit`; user, same day, after v0.9.0 still stood at the boss with autohunt on: "couple the selection ring detection with attack succeeded log") **and the boss still targeted in the target bar** (`IsFinalBossTargetedAsync`: the treasure icon ≥ 0.80 or the full name read in the top-centre band (0.25, 0, 0.50, 0.16) — not `FindBossAsync`, which also reads the name tag over the boss's head; user 2026-10-27: "goes directly below the boss with autohunt on but doesn't target or attack it"): the passing frame is saved as `ring_s5` (≤ 3 per Start, ring drawn) — user after v0.9.2: "the selection ring is not properly read", but no 0.9.2 stage 5 run is in the logs yet; `--ring <dir>` runs `FindSelectionRing` over a folder (crops in `%TEMP%\wuxauto-ring`). On the saved frames: every `stage5_boss` snapshot at 1920/1273 has **no** ring — at that moment the boss is targeted (target bar) but far away, not on screen; the one fight frame (2026-10-21, 1024) has the 203×88 ring where the character stands between the boss's legs, i.e. at the boss's feet, so the detector itself looks right: either missing → `Tab` + another round of `ManualAttacks` presses (`_manualRounds` ≤ 3 rounds; the log names what was missing; **no centre clicks while the autohunt is off** — user 2026-10-27: "in other stages Autohunt is on then clicking the middle, don't click the middle while the autohunt is off"; the 15 s skill spam without a hit also goes back to the Tab wait instead of clicking), still missing → warning + `noring_s5` snapshot (≤ 3 per Start), `ArriveAtFinalBoss` (back to the Tab wait, **autohunt still off**); both → `EngageAutohunt` (`J` unless already on) → **`TickAutohuntConfirmAsync`** (`_huntOn`, user 2026-10-26: "if autohunt is on and no attack succeeded follows after 2s, turn off auto hunt then try to manually attack then autohunt"): first `AutohuntSettleMs` (1000, const) of silence — the manual attacks' last skill animations still print attack lines after `J`, and in v0.9.3 such a line passed the check with the autohunt not attacking (user, 2026-10-27) — then the log is re-read as the baseline (`_huntBaselineAt`) and a new line within `AutohuntConfirmMs` (2000) of *that* → `WatchBoss`; none → **the stage 1–4 centre clicks** (`_huntClicks`, `SelectClickPoint`, `finalboss.png` first, one per `AttackConfirmMs`, ≤ `BossSelectMaxTries`; user 2026-10-27: "after enabling J, it unselected the boss, add the click the middle to try to select the boss"), a new line after any click → `WatchBoss`; all clicks without one → `J` (off), `StartManualAttack` again (counts as a round, same ≤ 3 limit) → presses → ring + line + target bar → `J` → confirm again; 3 rounds → `noring_s5`, Tab wait, autohunt off. **`WatchBoss` watchdog at stage 5 (same day):** with the autohunt on and the boss up, the combat log is read every boss poll; no new attack line for `AutohuntStallMs` (15000) → `J` off, snapshot `stall_s5` (≤ 3, shares the `noring_s5` counter), `ArriveAtFinalBoss` (autohunt-off check → Tab → skill → presses → gate → `J`). Catches every "autohunt on but not attacking" state whatever let it through; unbounded, the stage closes itself within ~2 min anyway. The centre clicks run only *after* `J` (autohunt on), as on stages 1–4; the pre-`J` click selection of PR 13 (`_clickSelect`) is gone again (user, 2026-10-27). At stage 5 nothing else turns the autohunt on any more: the click give-up before `J` goes back to the Tab wait (`attack_s5` snapshot), and the 90 s no-boss timeout only applies to stages 1–4 (stage 5 keeps tabbing until the zone guard sees the stage closed). Stages 1–4: click give-up and the 90 s timeout still turn autohunt on. **Autohunt off at the waypoint (user, 2026-10-26: "when in Waypoint 1, disable auto hunt"):** the first `WaitBoss` tick at stage 5 (`_finalHuntOffChecked`, every `ArriveAtFinalBoss`) runs `EnsureAutohuntOffAsync` (the town check's logic, shared) with `trustVisibleLine`: the newest autohunt line already in the combat log decides — "Auto hunt ended" = off, nothing pressed (the stage 4 `J` is a blind toggle, but its line is normally still on screen); "Auto hunt started" = on → `J`, expect "ended"; no line → tap and read as in town (≤ 3 taps, `autohunt_on` / `autohunt_unread`). `_phaseStart` restarts after it. Not yet seen live. Before that: **left-clicks around the middle of the client** until a new `Attack succeeded!... damage(+N)` line appears in the combat log (`Regions.AttackMessage`, left side): centre, ring of 8 at `BossSelectStep` (0.06 × height), ring of 8 at 2 steps, max `BossSelectMaxTries` 17, `AttackConfirmMs` 1200 per click. The log holds ~8 lines and is usually full, so line counts don't change: it compares the visible lines (normalised `attacksucceeded...damage(N)`, top to bottom) with a baseline taken before the first click; any change = hit. Gives up → warning + `attack_s<N>` snapshot → `WatchBoss` anyway. Autohunt will not attack an unselected boss |
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
banner line, template or OCR every 2nd poll). Name seen → `SelectBoss` **spams** `SkillKey` (`1`; a single
digit maps to the top-row key D1; each press followed by Ctrl + skill key, `Dungeon.CtrlSkill`) every ~0.3 s — the character walks to the targeted boss and attacks —
for up to 15 s or until a new "Attack succeeded" line shows (user). Hit → `J` (autohunt on, only now —
user) → the usual select clicks from the centre → `WatchBoss` (arrived by walking, autohunt already on →
`WatchBoss` straight away). 15 s without a hit → the clicks with autohunt still off; since 2026-10-26 `J` needs the selection ring (§4.2 `SelectBoss`). No
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
`LodRun`: SimpleSteer with `_headingBase` = π (screen-up = world-south) towards **1191, 2413**
(`Lod.TargetX/Y`, tolerance 60), Tab every `Lod.TabEveryMs` (1.5 s) → name **`Lod.BossName` "Bai Gu Great"**
on screen → `LodAttack`: spam `Dungeon.SkillKey` every 0.3 s — or hold it down (`Lod.HoldSkill`, "Hold skill (LOD)"; released on every phase change, Stop, panic and failure) — Tab when the name is missing.
* **Ctrl + skill key** (user, 2026-10-26: "when manually attacking couple it with ctrl+ skill key";
  `Dungeon.CtrlSkill` on, check box "Ctrl + skill key too"): every press of the spam is `TapSkill` =
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
  unknown, so the next time the boss is lost the pilot turns south first (no buff) before walking.
* **Killed by the boss → put back at the map's starting point** (coordinate jump > `Lod.TeleportJump`
  400 while attacking) → `LodRespawn` (two equal coordinate readings) → face south → walk again.
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
  and walk. Tab is also spammed while turning south (`TurnTabEveryMs` 400 ms, user; the turn ticks every
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
**`AutoWar`** (`AutomationPilot.War.cs`, user 2026-10-22: players using the pilot get "Regular War" switched
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

`Automation/AutoConfirm.cs`, ticked by `WuxEngine.VisionLoopAsync` on every frame **while the pilot is
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

## 6. Settings and persistence — read this before changing defaults

* Path: `%LOCALAPPDATA%\WuxAuto\settings.json` (also written on close; **Save settings** writes it
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
for the run, Ctrl + skill key too, Click the stage 5 gate, Auto reconnect, Hold skill (LOD), Walk up to the boss (LOD), TAB target: Default, Autohunt off in town, Run skill on in town (and the Run key field, default W); everything else only via `settings.json`.
`tests/WuxAuto.UiShot -- <png> --settings --bottom` renders that panel (`--mini` renders the Mini window instead, `--kill-alert` fills the last-kill line); it restores `settings.json`
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
| `FinalBoss` / `FinalBossPressAutohunt` | `Sacred Sky Lord` / **true** (v15) | stage 5 at the target: Tab → select → watch (no spawn wait). With the flag on, `J` turns autohunt **off** right after stage 4 dies (user: its auto-buff stopped the gate walk, run 33), `WaitBoss` at stage 5 first makes sure it is off (combat log, §4.2 `SelectBoss`) and `J` goes on again only after the manual attacks with the selection ring under the boss; off = autohunt keeps running from stage 4 |
| `ManualAttacks` / `ManualAttackIntervalMs` / `ManualAttackWaitMs` | 15 (v20) / 300 / 500 | stage 5: skill presses on the selected boss before `J`, the minimum gap between two presses, the wait after the last press before the attack-line check (user, 2026-10-26: "at least 15 times ... at least 300 ms in between") |
| `AutohuntConfirmMs` | 2000 | stage 5: a new attack line must follow the `J` within this long (counted from `AutohuntSettleMs` = 1 s after `J`, when the manual attacks' late hits are over), else `J` off and the manual attacks repeat (user, 2026-10-26); clamp 500–30000 |
| `AutohuntStallMs` | 15000 | stage 5 `WatchBoss`: autohunt on, boss up, no new attack line this long → `J` off, select and attack by hand again (`stall_s5`; user, 2026-10-27); clamp 3000–120000 |
| `CtrlSkill` | true | every manual skill press (stage 5 skill spam + `ManualAttacks`, LOD skill spam) = the skill key, then Ctrl + skill key (user, 2026-10-26); with `Lod.HoldSkill` the held key is let go for a Ctrl tap every `Lod.SkillEveryMs`. New property, so a saved file without it loads `true` — no migration (checked on this PC's settings.json) |
| `HomeTowns` | Piercing City, Molten City, Wandering City | every clan's home town (user, 2026-10-21): any of them on the minimap = "in town" (zone guard, LOD kill, stage 5 closed, still-in-town, re-login); the one seen (`TownIn`, remembered per Start) is where the return teleport goes |
| `ReturnDestination` | `Piercing City` | fallback until a home town has been seen; after stage 5: `J` (autohunt off) → teleport scroll there → `Done`; only then may `Loop` start the next run |
| `RunClickPx` / `RunStepWaitMs` / `RunMaxSteps` | 200 / 1500 / 200 | final run steering |
| `NoPathText` | `no walkable path` | logged only, diagnostic |

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
   as a crosshair. Note the overlay renderer fills some boxes white — that is expected.
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
* **OpenCvSharp ships only `win-x64` natives** — the app must stay `PlatformTarget x64` (it is).
* **Do not `Marshal.Copy` into `mat.Data`** on this machine (Windows on ARM, x64 emulation) — it
  crashed the CLR. Use `Mat.FromPixelData(...)` over a pinned buffer, and `.Clone()` for copies.
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
* Debug snapshots include overlays; a white filled box in a snapshot is the renderer, not game UI.
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

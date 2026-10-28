# WuxAuto for Android — Plan 2: the phone app (M4 LOD, M5 Laby, M6 extras + release)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A .NET MAUI app that runs Auto LOD, then Auto Laby, then the extras, on the phone next to the game, driving the shared `WuxAuto.Core` pilot through touches.

**Architecture:** Plan 1 left the pilot in `src/WuxAuto.Core` on Emgu CV, talking to the game only through `IGameControls` (actions) and `IOcrEngine`. Plan 2 adds, in Core, a platform profile (where the phone's *flow* differs), a touch seam `ITouchInput`, and `TouchControls : IGameControls` that turns actions into taps / swipes / joystick holds — all OS-free and unit-tested on the desktop. The Android app (`src/WuxAuto.Mobile`) only supplies capture (MediaProjection), touch delivery (accessibility `dispatchGesture`, serialised), ML Kit OCR, the floating control and pages.

**Tech Stack:** .NET 10, .NET MAUI (net10.0-android), Emgu CV 5.0.0.6584 (`Emgu.CV.runtime.maui.mini.android`), `Xamarin.Google.MLKit.TextRecognition` 116.0.1.9, xUnit.

**Spec:** `docs/superpowers/specs/2026-10-26-android-pilot-design.md` (§n below). Also read `docs/superpowers/notes/2026-10-26-m0-gate-results.md` ("What Plan 2 must carry" overrides the spec where they differ) and the root `CLAUDE.md`. The M0 spike `spikes/WuxAuto.MobileSpike/` + `spikes/mumu.ps1` hold working code for capture, gestures, ML Kit and Emgu on the device — reuse it, do not ship it.

## Global Constraints

- Phone: `net10.0-android`, minSdk 31, `android-arm64` only; app id `io.github.atherosxd.wuxauto`. Core stays `net10.0` and OS-free (CLAUDE.md §3 RULE).
- Packages, exact: `Emgu.CV` / `Emgu.CV.runtime.maui.mini.android` 5.0.0.6584; `Xamarin.Google.MLKit.TextRecognition` 116.0.1.9. SDK 10.0.402 with the `maui-android` workload (pin with a `global.json` inside `src/WuxAuto.Mobile/` only, as `spikes/global.json` does).
- `WuxAuto.Mobile` is **not** in `WuxAuto.slnx` (desktop builds must not need the Android workload); it gets `WuxAuto.Mobile.slnx` at the repo root.
- **Desktop behaviour identical.** Every Core change is behind `PlatformProfile.Phone`; with `PlatformProfile.Desktop` (the default) the pilot sends exactly today's input: `dotnet test tests\WuxAuto.Core.Tests` (existing assertions unchanged) and `.\tests\probe-baseline.ps1` identical to main after every Core task.
- Capture = MediaProjection (foreground service type `mediaProjection`, permissions `FOREGROUND_SERVICE` + `FOREGROUND_SERVICE_MEDIA_PROJECTION`), consent once per session, then bring the game back (`com.wuxen.wuxen2/com.wuxen.gxd.GXDPlayerActivity`). Teardown: VirtualDisplay → ImageReader → projection. A frame counts only when the zone title reads (a launcher frame is not black).
- Touches are **serialised**: never a tap inside a held joystick gesture (M0 MULTI failed); end the step, tap, resume.
- OCR on crops (the minimap crop reads the desktop's coordinate pattern; the full frame drops the spaces). Any OCR-rule relaxation is phone-profile-only.
- Positions are never hardcoded per device: measured from the frame (templates, OCR anchors) or as fractions; MuMu runs 1920×1080, the Xiaomi 17 Pro Max 2608×1200. Positional settings get `[JsonIgnore]` + a line in `ResetPositions()` (CLAUDE.md §6).
- MuMu: adb only (`"C:\Program Files\Netease\MuMuPlayerARM\shell\adb.exe"`, `10.0.2.95:5555`), never its window; stay in town; no Item Mall, shops, trades, chat SEND; **no Labyrinth / LOD entry without the user's OK** (uses their keys). Never type credentials or log in.
- No game frames in git (only template crops). Phone frames live in `%LOCALAPPDATA%\WuxAuto\phone-frames\` (local).
- The release signing key lives outside the repo (`%LOCALAPPDATA%\WuxAuto\signing\`, password from env `WUXAUTO_KEYSTORE_PASS`).
- Each milestone in its own worktree/branch from `origin/main`, ending in a PR with its URL printed and the user's live check before merge: M4 `phone-lod`, M5 `phone-laby`, M6 `phone-extras`. Merge `origin/main` in before pushing (main moves daily).
- Never stop a running WuxAuto; never launch a second desktop WuxAuto.

## Review Focus

1. **The screen size changes** (MuMu 1920×1080 ↔ Xiaomi 2608×1200, or MuMu restarted at another size): shortcut positions, joystick centre and regions must be re-found, not reused. → Task 4 `ShortcutMapTests.SizeChange_ForgetsPositionsAndSearchesAgain`; Task 3 `TouchControlsTests.Walk_UsesJoystickFoundForThisFrameSize`.
2. **The game leaves the foreground** (notification shade, a call, the player opens another app): no touch may land in another app. → Task 3 `TouchControlsTests.GameNotInFront_SendsNothingAndReturnsFalse`.
3. **Capture stops mid-run** (consent revoked, Android 14+ single-use token, projection killed): the pilot stops ticking, the floating control turns red, nothing is tapped blind. → Task 5 `FrameGateTests.NoFreshFrame_PausesThePilot` (pure logic in Core `Phone/FrameGate`).
4. **A needed shortcut is missing and no (+) slot is free**: stop with a message naming the shortcut; never overwrite a filled slot (spec §6.1). → Task 7 `ShortcutCheckTests.MissingAndNoFreeSlot_StopsAndNamesIt` and `…_NeverTapsAFilledSlot`.
5. **Stop / long-press / notification Stop while a skill or the joystick is held**: every held gesture is cancelled and nothing stays pressed. → Task 3 `TouchControlsTests.ReleaseAll_EndsHeldSkillAndJoystick`; Task 5 checks the Android side cancels the in-flight gesture.

---

## Part E — M4: the phone app, LOD first (branch `phone-lod`)

### Task 1: MuMu checks and a phone frame set

**Files:** Create `docs/superpowers/notes/<date>-phone-checks.md` (measurements, no screenshots). Extend `spikes/` only if a check needs a new broadcast action.

- [ ] **Step 1: The pending M0 re-run** — WALK, TAP (WORLD tab), WALK, TAP (GENERAL) back to back with no adb sleeps; gaps from logcat. Record whether each tap is delivered and the smallest joystick-release → tap gap that works (try 0, 50, 150, 300 ms). This number is `PhoneSettings.TapSettleMs`.
- [ ] **Step 2: Town-safe spec §10 items** — #1 (Teleport Scroll shortcut opens the list? paging? — open and close it, never pick a destination), #3 pinch amount, #4 swipe px → degrees (two swipe lengths), #5 map list scrolling (town map), #6 Durations window layout/wording, #7 Settings rows, #8 how to reach autohunt settings, #9 skill long-press = hold (in town on nothing), #10 Shura on the hotbar / how to stop an attack, #11 Run icon + ring, #14 ring unlock → (+) → picker → lock (add **Pick up** only if a (+) is free; never clear a slot), #15 floating-control spot that covers no HUD at 1920×1080. Items #2, #12, #13 need a dungeon: list them for Task 8.
- [ ] **Step 3: Frame set** — `adb exec-out screencap -p` frames of every screen used above (town, ring unlocked/locked, picker, teleport list, map, Durations, Settings, autohunt settings, hotbar) into `%LOCALAPPDATA%\WuxAuto\phone-frames\` named `<screen>_<w>x<h>.png`.
- [ ] **Step 4: Commit the note** — `git commit -m "Phone checks in MuMu: gaps, gestures, windows"`. STOP if a check shows a flow the spec did not foresee (e.g. teleport needs the inventory) — rule on it in the ledger and carry it into Tasks 2–7.

### Task 2: Platform profile, touch seam, phone settings (Core)

**Files:** Create `src/WuxAuto.Core/Platform/PlatformProfile.cs`, `src/WuxAuto.Core/Platform/ITouchInput.cs`, `src/WuxAuto.Core/Core/PhoneSettings.cs`; Modify `AppSettings.cs` (`public PhoneSettings Phone { get; set; } = new();`), `IGameControls.cs` (`GameAction.TeleportScroll`), `KeyboardMouseControls.cs` (`Has(TeleportScroll)` = false, `Press` → false), `AutomationPilot.cs` ctor; Test `tests/WuxAuto.Core.Tests/PlatformProfileTests.cs`.

**Interfaces — Produces:**

```csharp
public sealed record PlatformProfile(
    bool TeleportViaShortcut,      // phone: tap the Teleport Scroll shortcut; desktop: I + scroll right-click
    bool TargetShowsRing,          // phone: TargetNearest selects with the ring
    bool KeyCountsInDurations,     // phone: ☰ → Durations (M6)
    bool ShortcutCheck,            // phone: check/add ring shortcuts on Start
    bool SupportsCtrlSkill,        // desktop only
    bool SupportsAutoConfirm)      // desktop only
{
    public static PlatformProfile Desktop { get; } = new(false, false, false, false, true, true);
    public static PlatformProfile Phone   { get; } = new(true, true, true, true, false, false);
}

public interface ITouchInput
{
    bool IsDryRun { get; }
    bool IsGameInFront { get; }
    bool BringGameToFront();
    bool Tap(int x, int y, int holdMs);
    bool Swipe(int x1, int y1, int x2, int y2, int durationMs);
    bool Pinch(int cx, int cy, int fromGap, int toGap, int durationMs);
    bool Joystick(int cx, int cy, int dx, int dy, int durationMs);   // one step, blocks until released
    bool PressDown(int x, int y);   // long press until Release
    void Release();                 // ends any held press; no-op when none
}
```

`PhoneSettings` (non-positional, saved): `TapHoldMs` 60, `TapSettleMs` (Task 1's number), `JoystickStepMs` 900, `SwipePxPerTurnMs` (Task 1: px per ms of desktop turn), `PinchFromGap` 360, `PinchToGap` 96, `PinchMs` 400, `ListSwipePx` (Task 1), `FloatingX/Y` fractions. New property → no migration (missing loads defaults; CLAUDE.md §6).
`AutomationPilot(AppSettings, ScreenAnalyzer, IGameControls, PlatformProfile? profile = null)` — null = Desktop.

- [ ] **Step 1: Tests** — `Desktop_IsTheDefault` (pilot built without a profile reports `Profile == PlatformProfile.Desktop`); `KeyboardMouseControls_HasNoTeleportScroll`; `PhoneSettings_LoadsDefaultsFromAnOldFile` (settings.json without `Phone`).
- [ ] **Step 2–4:** RED → implement → GREEN; `dotnet test tests\WuxAuto.Core.Tests` all pass; probe baseline identical to main.
- [ ] **Step 5: Commit** — `"Core: platform profile, touch seam, phone settings"`.

### Task 3: `TouchControls` — actions as touches (Core)

**Files:** Create `src/WuxAuto.Core/Phone/TouchControls.cs`, `src/WuxAuto.Core/Phone/JoystickMath.cs`, `src/WuxAuto.Core/Phone/IShortcutSource.cs`; Test `tests/WuxAuto.Core.Tests/TouchControlsTests.cs` (a `RecordingTouch : ITouchInput` appending `"Tap:x,y,60"`, `"Swipe:…"`, `"Joy:cx,cy,dx,dy,ms"`, `"Down:x,y"`, `"Release"`, `"Pinch:…"`).

**Interfaces:**
- Consumes: Task 2.
- Produces:

```csharp
public interface IShortcutSource
{
    PixelRect? Find(GameAction action);        // last known position for the current frame size
    PixelRect? Skill(string skill);            // skill wheel button "1"/"2"/"3"
    (int Cx, int Cy, int Radius)? Joystick { get; }
    PixelRect? Menu { get; }                   // ☰
    void Invalidate();                         // after a miss or a size change
}
public sealed class TouchControls(AppSettings settings, ITouchInput touch, IShortcutSource shortcuts) : IGameControls
{
    public void ReleaseAll();
}
public static class JoystickMath
{
    // screen vector from the character (0.5 w, 0.54 h) to the click point → joystick offset of length radius
    public static (int Dx, int Dy) ToStick(int frameW, int frameH, int x, int y, int radius);
}
```

Mapping (one test per row): `Press(a)` → `Tap` at `Find(a)` centre (TeleportScroll, ToggleAutohunt, Buff, TargetNearest, Loot); `Press(OpenInventory|ToggleSettings|OpenAutohuntSettings)` → `Tap(Menu)`, then the ☰ entry is the pilot's job (Task 7) — `Press` returns true after the menu tap; `Has(a)` = `Find(a) != null`, except `RunSkillKey`/`ShuraKey`/`StopAttack`/`OpenInventory`/`ToggleSettings`/`OpenAutohuntSettings` per Task 1's findings (write the decided values into the test); `ActionDown/Up(ShuraKey)` → `PressDown` / `Release` on the Shura icon; `Tap`/`UseItem` → `Tap(x,y,TapHoldMs)`; `SkillTap(s)` → `Tap(Skill(s))`; `SkillCtrlTap` → same as `SkillTap` (no Ctrl on the phone); `SkillDown/Up` → `PressDown`/`Release` on the skill button; `WalkTo(x,y,_)` → `Joystick(c, JoystickMath.ToStick(...), JoystickStepMs)` then sleep `TapSettleMs`; `Turn(dir, ms)` → horizontal `Swipe` on empty ground (centre 0.5 w, 0.35 h) of `ms * SwipePxPerTurnMs` px, left = finger moves right (Task 1 decides the sign; pin it); `BeginTurn/EndTurn` → a swipe of the sweep length, `EndTurn` no-op; `TiltCamera(x,y,px,ms)` → vertical `Swipe`; `ZoomOut(x,y,n)` → `Pinch(x,y,PinchFromGap,PinchToGap,PinchMs)` once per 5 notches (min 1); `ScrollList(x,y,n)` → vertical `Swipe` of `n * ListSwipePx / 3` (negative n = list moves down = finger up; pin it); `IsKnownKey` → true; `IsGameInFront`/`TryBringGameToFront`/`IsDryRun` → `ITouchInput`.

- [ ] **Step 1: Tests** — one per mapping row above, plus `GameNotInFront_SendsNothingAndReturnsFalse` (every member), `Walk_UsesJoystickFoundForThisFrameSize`, `ReleaseAll_EndsHeldSkillAndJoystick` (`["Down:…","Release"]`), `MissingShortcut_PressReturnsFalseAndInvalidates`, `JoystickMath_StraightUp_IsMinusRadiusY`, `JoystickMath_ZeroVector_IsZero`.
- [ ] **Step 2–4:** RED → implement → GREEN. Desktop tests untouched.
- [ ] **Step 5: Commit** — `"Core: TouchControls maps game actions to touches"`.

### Task 4: Phone templates, `ShortcutMap`, phone regions, offline probe

**Files:** Create `src/WuxAuto.Mobile/Assets/phone/*.png` (crops from Task 1 frames: teleportscroll, autohunt, autobuff, sword, pickup, shura, runskill, skill1-3, menu, ringplus, ringlock, joystick, and the LOD south beam on the phone minimap — LOD faces south on arrival), `src/WuxAuto.Core/Phone/ShortcutMap.cs`, `src/WuxAuto.Core/Phone/PhoneRegions.cs`, `tests/WuxAuto.SelfTest/PhoneProbe.cs` (`--phone <dir>`); Modify `AppSettings.ResetPositions` (phone profile → `PhoneRegions.Apply(settings, w, h)`), Core.Tests csproj (link `Assets\phone\*.png`); Test `tests/WuxAuto.Core.Tests/ShortcutMapTests.cs`.

**Interfaces — Produces:** `ShortcutMap(ScreenAnalyzer analyzer) : IShortcutSource` with `void Refresh(Mat frame)` (full search on a new frame size or after `Invalidate`, else no-op); `PhoneRegions.Apply(AppSettings s, int w, int h)` sets the `Regions.*` the LOD flow reads (Minimap, AttackMessage (top-left on the phone, M0 finding), Hotbar, TargetBox, BossName, Ocr/TeleportList) from Task 1's measurements as fractions; template names prefixed `phone_` so desktop templates never collide.

- [ ] **Step 1: Tests** (synthetic frames with pasted crops at two sizes) — `FindsEachShortcutOnRingFrame`, `SizeChange_ForgetsPositionsAndSearchesAgain`, `Invalidate_SearchesAgainOnNextRefresh`, `DesktopProfile_RegionsUnchanged` (ResetPositions with Desktop leaves today's defaults byte-for-byte).
- [ ] **Step 2–4:** RED → implement → GREEN.
- [ ] **Step 5: Probe** — `dotnet run --project tests\WuxAuto.SelfTest -- --phone "$env:LOCALAPPDATA\WuxAuto\phone-frames"` prints per frame: each shortcut (score, box), zone title, coordinates, combat-log lines (Windows OCR stands in for ML Kit here). Expected: every shortcut found on the frames that show it, scores ≥ 0.85, nothing on frames without it; template robustness (M0 carry): also run on each frame scaled ×0.9 and ×1.1 and report.
- [ ] **Step 6: Commit** — `"Phone templates, shortcut map, phone regions, --phone probe"`.

### Task 5: The Android app — capture, touches, OCR

**Files:** Create `src/WuxAuto.Mobile/` (MAUI, net10.0-android; `Platforms/Android/{PilotAccessibilityService.cs, CaptureService.cs, AndroidTouchInput.cs, MlKitOcrEngine.cs, AssetInstaller.cs}`, `Resources/xml/accessibility_service.xml`, `AndroidManifest.xml`), `WuxAuto.Mobile.slnx`, `src/WuxAuto.Core/Phone/FrameGate.cs`; Test `tests/WuxAuto.Core.Tests/FrameGateTests.cs`.

**Interfaces — Produces:** `CaptureService.LatestFrame` → `(Mat Bgra, long Ticks)?` (a clone per call; content-checked); `AndroidTouchInput : ITouchInput` (one gesture at a time on a single worker; `Joystick` = one stroke centre → centre+offset held `durationMs`; `PressDown` = continued strokes (`willContinue`) every 400 ms until `Release`; a new gesture first ends the held one; `IsGameInFront` = the accessibility service's foreground package is `com.wuxen.wuxen2`); `MlKitOcrEngine : IOcrEngine` (crop → upscale → ML Kit blocks/lines/elements → `OcrWord` with line bounds, like `OcrService`); `AssetInstaller.Install()` copies `Assets/phone/*` and the shared desktop templates to the app's **external** files dir (`Android/data/io.github.atherosxd.wuxauto/files`, reachable over adb/USB, spec §5.4) and calls `AppPaths.Configure(externalFilesDir, assetsDir)`. Frame size comes from the real display size (`WindowManager.CurrentWindowMetrics`, landscape), not `Resources.DisplayMetrics` (may report portrait or exclude system bars — M0 carry). The DUMP-protected debug receiver is compiled into Debug builds only. `FrameGate(int staleMs)`: `bool Fresh(long nowTicks, long frameTicks)`; the engine (Task 6) skips the tick when not fresh. Debug receiver from the spike (DUMP-protected) kept for adb probes only in Debug builds.

- [ ] **Step 1: FrameGate tests** — `NoFreshFrame_PausesThePilot`, `FreshFrame_Ticks`.
- [ ] **Step 2: Build** — `dotnet build WuxAuto.Mobile.slnx -c Debug` clean; `dotnet build .\WuxAuto.slnx` still clean.
- [ ] **Step 3: MuMu** (install via adb, enable the service with `spikes/mumu.ps1`'s pattern): capture → zone title read by ML Kit through `IOcrEngine` on the minimap crop; `Tap` on the WORLD tab and back; `Joystick` step moves the coordinates; `Swipe` turns the beam; `Pinch` zooms; `PressDown`/`Release` on a skill in town. Log lines as evidence (no frames in git).
- [ ] **Step 4: Commit** — `"Mobile app: capture, serialised touches, ML Kit OCR"`.

### Task 6: Engine loop, floating control, notification, pages

**Files:** Create `src/WuxAuto.Mobile/Engine/MobileEngine.cs`, `Platforms/Android/FloatingControl.cs`, `Platforms/Android/PilotNotification.cs`, `Pages/{SetupPage, MainPage, SettingsPage, LogPage}.xaml`, `Platforms/Android/DebugBundle.cs`.

**Interfaces — Consumes:** Tasks 2–5. **Produces:** `MobileEngine(AppSettings, CaptureService, ITouchInput, IOcrEngine)` — owns `ScreenAnalyzer`, `ShortcutMap`, `TouchControls`, `AutomationPilot(..., PlatformProfile.Phone)`; loop: frame → `FrameGate` → `ShortcutMap.Refresh` (on size change / invalidate / every 30 s) → `pilot.TickAsync`; `Start(RunMode)`, `Stop(string)`, `Panic()` (= Stop + `TouchControls.ReleaseAll`), events `Log`, `StateChanged`, `BossKilled`; logs to `AppPaths.LogFolder` like the desktop.

- Floating control (spec §5.4): accessibility overlay, round button (grey idle / green running / red error), drag + edge snap, position saved in `PhoneSettings.FloatingX/Y`; tap → panel (Laby/LOD, Start/Stop, fixed 3-line status, last kill, Open app); **long-press = Panic**. It hides for the frame the pilot captures, or sits outside every region the pilot reads/taps (pick one in Task 1 #15 and say which).
- Notification while running: status + Stop (→ `Stop`). Keep screen on while running.
- Setup page checklist: accessibility on (Android 13+ "Allow restricted settings" note), notifications, battery exemption, HyperOS autostart hint, screen-capture consent on Start.
- Debug bundle: zip of today's log, last 20 debug pictures, settings.json → Android share sheet.

- [ ] **Step 1:** Build clean (both solutions).
- [ ] **Step 2: MuMu, dry run** (`Sequence.DryRun` on): Start LOD in town → the log shows the phases and decisions with no touches; floating control colours follow state; long-press stops; notification Stop stops.
- [ ] **Step 3: Commit** — `"Mobile: engine loop, floating control, notification, pages"`.

### Task 7: The pilot's phone flow for LOD (Core)

**Files:** Modify `src/WuxAuto.Core/Automation/AutomationPilot*.cs` (profile branches only), Create `src/WuxAuto.Core/Phone/ShortcutCheck.cs`; Tests `tests/WuxAuto.Core.Tests/ShortcutCheckTests.cs`, `PhoneLodFlowTests.cs` (a fake `ITouchInput` + `FakeOcrEngine` + synthetic frames, like `WarTickTests`).

**Interfaces — Produces:** new `PilotPhase.ShortcutCheck` (phone only, every Start, before CheckZone): required = TeleportScroll, Loot (LOD), Buff, ToggleAutohunt; missing + `ringplus` visible → tap lock → tap (+) → OCR the picker for the entry name → tap it → tap lock; never tap a filled slot; missing and no (+) → `Fail` with "Add the '<name>' shortcut to the ring" (no restart loop). Phone branches: teleport = `Press(TeleportScroll)` then the existing list search (Task 1 #1 decides any extra step); LOD targeting = `Press(TargetNearest)` (ring shows at once); ☰ entries opened by `Press(...)` + OCR tap on the entry name; no Ctrl+skill (`SupportsCtrlSkill` false); LOD walk-up = `WalkTo(ring centre)`. Town checks: AutohuntOff runs as on PC through `Press(ToggleAutohunt)` + the combat-log read (phone region); RunSkill uses the phone `runskill` icon + ring (Task 1 #11) or is skipped with an Info line when the icon is not on the hotbar (no key fallback on the phone); the Settings visit (font size / TAB target) runs only if Task 1 #7 found those rows, else the phone profile skips it.

- [ ] **Step 1: Tests** — `MissingAndNoFreeSlot_StopsAndNamesIt`, `MissingWithFreeSlot_AddsItAndLocksTheRing`, `…_NeverTapsAFilledSlot`, `AllPresent_GoesToCheckZone`, `PhoneTeleport_TapsShortcutNotInventory`, `PhoneLod_TargetsWithSwordAndSpamsSkillTap`, `PhoneRunSkill_NoIconOnBar_SkipsWithoutAnyTap`, `PhoneArrival_PinchesToZoomOut`, `DesktopProfile_ShortcutCheckNeverRuns`.
- [ ] **Step 2–4:** RED → implement → GREEN; all existing Core tests pass unchanged; probe baseline identical to main.
- [ ] **Step 5: Commit** — `"Pilot: phone flow for LOD (shortcut check, shortcut teleport, sword targeting)"`.

### Task 8: LOD live in MuMu — STOP for the user first

- [ ] **Step 1: Ask the user** for OK to use an LOD entry on the MuMu character (and to switch character if needed). Do nothing below without it.
- [ ] **Step 2:** Run Auto LOD from town in MuMu (not dry): teleport, south turn, walk, target, attack, loot, buff, respawn. Capture spec §10 #2, #12, #13 frames into phone-frames; fix what fails (Core changes keep the desktop gate: tests + probe baseline).
- [ ] **Step 3: M4 PR** — merge `origin/main`, push `phone-lod`, open the PR (URL printed); body lists what ran in MuMu. STOP: the user installs the APK on MuMu and the Xiaomi (Debug signing is fine for M4) and runs LOD. Re-check accessibility capture on the Xiaomi (M0 carry) and the display-size handling (portrait/system bars). Merge after their OK.

## Part F — M5: Laby on the phone (branch `phone-laby`)

### Task 9: Laby phone specifics

**Files:** `src/WuxAuto.Mobile/Assets/phone/` (mapicon, beam north/south from the phone minimap, map row `>` if the desktop crop scores < 0.85, gate banner, monster icon), `PhoneRegions.cs` (MapIcon, MapScroll, MapBossList, Compass band, GateBanner), `AutomationPilot*.cs` (phone branches: zoom = pinch, map list paging = `ScrollList` swipe when no arrows, stage 5 select = `Press(TargetNearest)` + ring check before the desktop fallback clicks), tests `PhoneLabyFlowTests.cs`, probe `--phone` extended to map/beam/gate.

- [ ] **Step 1: Tests** — `PhoneMapList_SwipesWhenNoArrows`, `PhoneArrival_PinchesInsteadOfCtrlWheel`, `PhoneFaceNorth_UsesSwipeTurns`, `PhoneStage5_SwordThenRingThenAttack`, `DesktopProfile_LabyUnchanged` (existing Laby tests untouched).
- [ ] **Step 2–4:** RED → implement → GREEN; desktop gate (tests + probe baseline).
- [ ] **Step 5: Commit.**

### Task 10: Laby live in MuMu — STOP for the user first

- [ ] Ask for OK (Temple of Fiends Key). Run a full Laby loop in MuMu; fix; PR (URL printed); STOP for the user's MuMu + Xiaomi Laby run; merge after OK.

## Part G — M6: extras and release (branch `phone-extras`)

### Task 11: Key counts via Durations; Auto War on the phone

**Files:** `AutomationPilot*.cs` (`KeyCountsInDurations` branch: in town before a teleport, ☰ → Durations → read "Temple of Fiends Key" / "Den of Rebirth" counts (Task 1 #6 wording) → close; 0 → the desktop's `OutOfKeys` rules; unreadable → the teleport-decides fallback), Auto War phone path per Task 1 #8 (if the autohunt settings / Regular War box cannot be reached on the phone, Auto War is marked unsupported: the mode is hidden and both-out stops with a message); tests `PhoneDurationsTests.cs`, `PhoneWarTests.cs`.

- [ ] Tests (`ZeroLabyKeys_SwitchesToLod`, `BothZero_StartsWarOrStopsWhenUnsupported`, `Unreadable_FallsBackToTeleportRule`) → implement → GREEN; desktop gate; commit.

### Task 12: Kill alert, kill screenshots, reconnect, restart on error

**Files:** `src/WuxAuto.Mobile/Platforms/Android/PhoneKillAlert.cs` (notification + the built-in chime or `Sequence.KillSound`), wiring of `BossKilled`; phone templates/regions for the login / server screens if the desktop OCR rules miss them on phone frames (capture them in MuMu without logging in — the client remembers the account; never type credentials).

- [ ] MuMu: kill alert on a test trigger; kill screenshots saved to `AppPaths.KillsFolder`; a forced disconnect (only if the user agrees to it) reconnects; restart on error after a forced fail. Commit.

### Task 13: Self-update, signing, release, README

**Files:** `src/WuxAuto.Mobile/Platforms/Android/ApkUpdater.cs` (reads `releases/latest`, downloads `WuxAuto-android.apk`, hands it to the package installer; needs `REQUEST_INSTALL_PACKAGES`), `release.ps1` (builds the signed APK with the key from `%LOCALAPPDATA%\WuxAuto\signing\wuxauto.keystore`, password `$env:WUXAUTO_KEYSTORE_PASS`; refuses without them; attaches the APK to the release), `README.md` (phone setup: install, accessibility + restricted settings, capture consent, battery/autostart, floating control), CLAUDE.md (§2 mobile build, §3 map).

- [ ] **Step 1:** Ask the user to create (or let you create, stored only under `%LOCALAPPDATA%\WuxAuto\signing\`) the release key; never commit it or its password.
- [ ] **Step 2:** Signed Release APK builds; installs over the M4/M5 Debug build only after an uninstall (signature change — tell the user).
- [ ] **Step 3:** Update check finds a newer test release (use a pre-release on a fork or a dry-run flag; never publish a public release without the user's OK).
- [ ] **Step 4: M6 PR** (URL printed); STOP for the user's check on the Xiaomi; merge after OK. The first public release with the APK is the user's call.

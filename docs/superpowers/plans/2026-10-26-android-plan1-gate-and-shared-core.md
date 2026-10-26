# WuxAuto for Android — Plan 1: go/no-go gate (M0) and shared core (M1–M3)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Prove in MuMu that an on-device app can see and drive the Android game (M0), then move the
desktop's run logic, vision and settings into a shared `WuxAuto.Core` library, port its picture
finding to Emgu CV 5 behind a parity gate, and put the pilot on game *actions* instead of keys — with
the desktop behaving exactly as today after every step.

**Architecture:** M0 is a throwaway MAUI Android app driven by adb broadcasts. M1 creates
`src/WuxAuto.Core` (net10.0, no Windows types) and moves files into it unchanged, behind three seams
(`IOcrEngine`, `IGameInput`, configurable `AppPaths`). M2 swaps OpenCvSharp for Emgu CV inside Core
and proves "almost the same" with a parity probe that runs a frozen copy of the old vision code next
to the new one. M3 adds `IGameControls` (actions) with a keyboard/mouse implementation that reproduces
today's exact calls, pinned by unit tests.

**Tech Stack:** .NET 10, WPF (desktop), .NET MAUI for Android, Emgu CV 5.0.0.6584, OpenCvSharp4
4.13.0.20260627 (until M2), Google ML Kit text recognition, Windows.Media.Ocr (desktop), xUnit.

**Spec:** `docs/superpowers/specs/2026-10-26-android-pilot-design.md` (read it first; §n below refers
to it). The repo handoff notes are the root `CLAUDE.md`.

**Not in this plan:** the phone app itself (M4 LOD, M5 Laby, M6 extras) and the platform profile.
They become Plan 2, written after M0's results and the MuMu checks in spec §10 — those decide the
capture method, the OCR engine and half the phone flows.

## Global Constraints

- Core: `net10.0`. Desktop: `net10.0-windows10.0.19041.0`, **x64 only** (CLAUDE.md §10). Phone: `net10.0-android`, minSdk 31, `android-arm64` only.
- Packages, exact: `Emgu.CV` 5.0.0.6584; `Emgu.CV.runtime.maui.mini.android` 5.0.0.6584; `Emgu.CV.runtime.mini.windows` 5.0.0.6584 (fall back to `Emgu.CV.runtime.windows` 5.0.0.6584 only if a used function is missing from mini); `Xamarin.Google.MLKit.TextRecognition` 116.0.1.9; `OpenCvSharp4` / `OpenCvSharp4.runtime.win` 4.13.0.20260627 until M2 removes them from the apps.
- **Desktop behaviour identical** after M1, M2 and M3. The parity rule for M2 ("almost the same", spec §5.2): same found/not-found verdict for every detector on every frame; boxes within 2 px; scores within ±0.02; identical OCR-derived results. Any verdict difference goes to the user; they decide.
- Never stop a running `WuxAuto` process for a build (memory: the installed copy may be mid-run). Only on a file-lock build failure, and after asking.
- Each milestone in its own worktree under `.claude/worktrees/<name>` (memory: parallel sessions share the checkout), on its own branch from `origin/main`, ending in its own PR with the URL printed: M0 `android-pilot` (this branch: spec + this plan + spike + results), M1 `core-split`, M2 `core-emgu`, M3 `core-actions`. Each merges before the next starts; rebase on `origin/main` before pushing.
- No game frames in git: only template crops. The frozen frame set lives in `%LOCALAPPDATA%\WuxAuto\parity-frames\` (local only).
- MuMu (spec §4): adb only (`"C:\Program Files\Netease\MuMuPlayerARM\shell\adb.exe"`, `10.0.2.95:5555`), never bring its window to the front; stay in town; no Item Mall, shops, trades, chat SEND; no Labyrinth/LOD entry without the user's OK.
- No new settings in M1–M3. (If one becomes unavoidable: CLAUDE.md §6 — bump `SettingsStore.CurrentVersion` and migrate.)
- Self-review loop from the user's global instructions after every task; verify by running, state the evidence.

## Review Focus

1. **Window size changes between frames** (the learned-scale cache is keyed by client height): after the Emgu port, a frame of a new size must still be searched over the full scale range first. → Task 11's probe processes frames in name order without resetting the cache and prints how many size changes it crossed (must be > 0).
2. **Templates with transparency** (`flashlight3.png`, 395×341, mostly transparent; `LoadGray` crops to the largest opaque patch): the crop must come out the same size after the port. → Task 12, `TemplateLibraryTests.LoadGray_CropsTransparentPngToSameBoxAsLegacy`.
3. **Non-ASCII Windows profile paths** (a user name like "Pétur" in `%LOCALAPPDATA%`): templates must load and debug/kill pictures must save. → Task 12, `ImageFileTests` (two tests).
4. **Paths read before `AppPaths.Configure`** (the phone sets its root at start-up; anything that captured the old root keeps writing to it): → Task 6, `AppPathsTests.Configure_MovesEveryFolderAndSettingsStoreFollows`.
5. **A held key or turn left down when the pilot stops** (stop/panic mid-hold): the action layer must release exactly what today's code releases. → Task 14, `KeyboardMouseControlsTests.EndTurn_ReleasesTheKeyBeginTurnPressed` and `SkillUp_ReleasesTheHeldSkill`.

---

## Part A — M0: go/no-go spike in MuMu (branch `android-pilot`)

Throwaway code under `spikes/WuxAuto.MobileSpike/`, not in `WuxAuto.slnx`, no tests (spike). It is
driven from the PC with `adb shell am broadcast`, so every check is scriptable and leaves evidence
(logcat lines, PNGs pulled to the scratchpad).

### Task 1: Toolchain and a spike app with an Accessibility service

**Files:**
- Create: `spikes/global.json` (pins SDK `10.0.402`, `rollForward: latestPatch`)
- Create: `spikes/WuxAuto.MobileSpike/WuxAuto.MobileSpike.csproj`, `MauiProgram.cs`, `App.cs`, `Platforms/Android/MainActivity.cs`, `Platforms/Android/MainApplication.cs`, `Platforms/Android/AndroidManifest.xml`
- Create: `spikes/WuxAuto.MobileSpike/Platforms/Android/SpikeService.cs`, `Platforms/Android/Resources/xml/spike_accessibility.xml`
- Create: `spikes/mumu.ps1` (helpers: connect, install, enable service, broadcast, pull files)

**Interfaces:**
- Produces: package `io.github.atherosxd.wuxauto.spike`; service Java name `io.github.atherosxd.wuxauto.spike.SpikeService`; log tag `WuxSpike`; output folder = `GetExternalFilesDir("shots")`.

- [ ] **Step 1: Install the MAUI Android workload for SDK 10.0.402**

Run (from `spikes/`, so `global.json` selects 10.0.402): `dotnet workload install maui-android`
Expected: "Successfully installed workload(s) maui-android". If it fails for lack of rights, stop and ask the user to run the same command in an elevated terminal.

- [ ] **Step 2: Create the project**

MAUI single project restricted to Android: `<TargetFrameworks>net10.0-android</TargetFrameworks>`,
`<SupportedOSPlatformVersion>31</SupportedOSPlatformVersion>`, `<RuntimeIdentifiers>android-arm64</RuntimeIdentifiers>`,
`<ApplicationId>io.github.atherosxd.wuxauto.spike</ApplicationId>`, packages `Emgu.CV`,
`Emgu.CV.runtime.maui.mini.android`, `Xamarin.Google.MLKit.TextRecognition` at the Global Constraints
versions. One page with a label is enough.

- [ ] **Step 3: Declare the Accessibility service**

`SpikeService : AccessibilityService` with `[Service(Name = "io.github.atherosxd.wuxauto.spike.SpikeService", Permission = "android.permission.BIND_ACCESSIBILITY_SERVICE", Exported = true)]`,
the `android.accessibilityservice.AccessibilityService` intent filter, and meta-data pointing at
`@xml/spike_accessibility`, which sets `android:canPerformGestures="true"`,
`android:canTakeScreenshot="true"`, `android:accessibilityEventTypes="typeWindowStateChanged"`,
`android:accessibilityFeedbackType="feedbackGeneric"`. `OnServiceConnected` logs `WuxSpike: connected`
and registers a broadcast receiver (exported, Android 13+ flag) for the actions of Tasks 2–3.

- [ ] **Step 4: Build, install, enable over adb**

`spikes/mumu.ps1` functions: `Connect-MuMu` (`adb connect 10.0.2.95:5555`), `Install-Spike`
(`dotnet build -c Debug -f net10.0-android` then `adb install -r <apk>`), `Enable-Spike`
(`adb shell settings put secure enabled_accessibility_services io.github.atherosxd.wuxauto.spike/io.github.atherosxd.wuxauto.spike.SpikeService`
and `adb shell settings put secure accessibility_enabled 1`), `Send-Spike <action> [extras]`
(`adb shell am broadcast -a io.github.atherosxd.wuxauto.spike.<action> ...`), `Get-SpikeFiles` (pulls the shots folder to the scratchpad).

Run: `Connect-MuMu; Install-Spike; Enable-Spike; adb logcat -d -s WuxSpike`
Expected: a `WuxSpike: connected` line. The game must then be brought back to the front with
`adb shell monkey -p com.wuxen.wuxen2 1` (the install opens nothing, but check with a screenshot).

- [ ] **Step 5: Commit**

```bash
git add spikes/
git commit -m "Spike: MAUI Android app with an Accessibility service for the M0 gate"
```

### Task 2: Capture and touch checks

**Files:**
- Modify: `spikes/WuxAuto.MobileSpike/Platforms/Android/SpikeService.cs`

**Interfaces:**
- Consumes: Task 1's receiver and log tag.
- Produces: broadcast actions `SHOT` (`--ei count N`), `WALK` (`--ei dx --ei dy --ei ms`, direction in screen units, joystick centre 220,682 and radius 88 px at 1600×900 hardcoded), `SWIPE` (`--ei x1 --ei y1 --ei x2 --ei y2 --ei ms`), `PINCH` (`--ei cx --ei cy --ei from --ei to --ei ms`, two strokes moving towards each other), `MULTI` (joystick held up for `ms` with a tap at `--ei tx --ei ty` half-way, one `GestureDescription` with a continued joystick stroke).

- [ ] **Step 1: `SHOT`** — `TakeScreenshot(Display.DefaultDisplay, executor, callback)` `count` times back to back; on each success wrap the hardware buffer, copy to a software ARGB_8888 bitmap, log `WuxSpike: shot i WxH t=<ms since previous>`; save the last one as `shot_last.png`; log `WuxSpike: shots ok=<n> fail=<n> fps=<n.n>`. Failures log their error code.
- [ ] **Step 2: `WALK`, `SWIPE`, `PINCH`, `MULTI`** — each dispatches with a completion callback that logs `WuxSpike: <action> completed|cancelled`.
- [ ] **Step 3: Run the checks in town** (MuMu account stopped and in town first, spec §4)

Run, each followed by an adb `screencap` pulled to the scratchpad:
`Send-Spike SHOT -Extras '--ei','count','20'`; `Send-Spike WALK -Extras '--ei','dx','0','--ei','dy','-100','--ei','ms','2500'`;
`Send-Spike SWIPE -Extras '--ei','x1','1100','--ei','y1','320','--ei','x2','900','--ei','y2','320','--ei','ms','600'`;
`Send-Spike PINCH -Extras '--ei','cx','800','--ei','cy','400','--ei','from','300','--ei','to','80','--ei','ms','600'`;
`Send-Spike MULTI -Extras '--ei','ms','2500','--ei','tx','1530','--ei','ty','553'` (the right-hand AUTO BUFF button).
Expected: `fps` ≥ 2.0 with every shot showing the game (not black); minimap coordinates move ≥ 50
units after `WALK`; the minimap beam turns after `SWIPE`; the view zooms out after `PINCH` (compare the
character's size); after `MULTI` the coordinates moved **and** a buff icon / "+1" appeared.

- [ ] **Step 4: Commit** — `git commit -am "Spike: screenshot rate and gesture checks"`

### Task 3: ML Kit and Emgu on the device

**Files:**
- Modify: `spikes/WuxAuto.MobileSpike/Platforms/Android/SpikeService.cs`

**Interfaces:**
- Produces: `OCR` (ML Kit Latin recognizer over the last shot; logs every line with its box as `WuxSpike: ocr "<text>" x,y,w,h`, then `ocr ms=<n>`), `MATCH` (`--ei x --ei y --ei w --ei h`: crops that box out of the last shot as the template, converts both to gray Emgu `Mat`s, `CvInvoke.MatchTemplate` with `TemplateMatchingType.CcoeffNormed`, logs `WuxSpike: match score=<s> at x,y ms=<n>`).

- [ ] **Step 1: Implement `OCR`** — call `CvInvokeAndroid.Init()` once before any Emgu call (Emgu's Android rule); feed ML Kit an `InputImage.FromBitmap(bitmap, 0)`.
- [ ] **Step 2: Implement `MATCH`**
- [ ] **Step 3: Run**

`Send-Spike SHOT -Extras '--ei','count','1'`; `Send-Spike OCR`; `Send-Spike MATCH -Extras '--ei','x','1490','--ei','y','50','--ei','w','60','--ei','h','50'` (the minimap map icon).
Expected: an OCR line containing `Piercing City` and a line matching the desktop's coordinate pattern
`(\d{3,6})(?:\s+,\s*|\s*,\s+)(\d{3,6})|(\d{4,6})\s*\.\s*(\d{4,6})`; `match score` ≥ 0.99 at 1490,50.

- [ ] **Step 4: Commit** — `git commit -am "Spike: ML Kit and Emgu checks on the device"`

### Task 4: Gate report — STOP for the user

**Files:**
- Create: `docs/superpowers/notes/2026-10-26-m0-gate-results.md` (use the actual date)

- [ ] **Step 1: Write the results table** — one row per check of Tasks 2–3: what was run, measured value, pass/fail, evidence file names (screenshots stay in the scratchpad, not in git). Then the three decisions the spec defers to M0: capture method (accessibility at the measured fps, or MediaProjection), OCR engine (ML Kit, or the Tesseract fallback), gestures accepted (yes/no).
- [ ] **Step 2: Commit, push `android-pilot`, open the PR** (spec + this plan + spike + results), print its URL.
- [ ] **Step 3: STOP.** Report the table to the user. Pass = screenshots, `WALK`, `SWIPE`, `MULTI` and `MATCH` pass, and OCR reads the title and coordinates. Any of screenshot / walk / multi failing → no-go: present the options (spec §7 M0) and wait. `PINCH` or OCR failing alone is not a no-go — note it for Plan 2. **Part B starts only after the user's go.**

---

## Part B — M1: shared core, move only (branch `core-split`)

### Task 5: Frozen frames and a probe baseline

**Files:**
- Create: `tests/probe-baseline.ps1`

**Interfaces:**
- Produces: `.\tests\probe-baseline.ps1 -Out <dir> [-Frames <dir>]` — writes one normalised text file per probe into `<dir>`. Default `-Frames` = `$env:LOCALAPPDATA\WuxAuto\parity-frames`. Later tasks compare two outputs with `git diff --no-index --stat <a> <b>`.

- [ ] **Step 1: Freeze the frame set** — copy every `*.png` from `%LOCALAPPDATA%\WuxAuto\debug` to `%LOCALAPPDATA%\WuxAuto\parity-frames` (517 files, ~940 MB on 2026-10-26). Never modify that folder afterwards.
- [ ] **Step 2: Write the script** — builds `tests\WuxAuto.SelfTest` (Release), then runs into separate files: the synthetic self test (no args, `--verbose`); `--ring`, `--mapopen`, `--lodparts`, `--shura`, `--runskill`, `--titles`, `--confirm` over `-Frames`; `--frame <png>` for every 10th PNG in name order. Normalisation: replace `\d+(\.\d+)?\s*ms` with `<ms>`; drop lines that only report timing or temp paths (`%TEMP%` crop paths).
- [ ] **Step 3: Prove it is deterministic** — run it twice on unchanged `main` into `baseline-a` and `baseline-b` (both in the scratchpad).
Run: `git diff --no-index --stat baseline-a baseline-b`
Expected: no output. Tighten the normalisation until this holds.
- [ ] **Step 4: Keep `baseline-a` as the M1 baseline** (scratchpad, not git). Commit the script: `git add tests/probe-baseline.ps1; git commit -m "Probe baseline script for refactor checks"`

### Task 6: Core and test projects; move the pure types

**Files:**
- Create: `src/WuxAuto.Core/WuxAuto.Core.csproj` (net10.0, Nullable, ImplicitUsings, `RootNamespace` WuxAuto, `InternalsVisibleTo` WuxAuto, WuxAuto.SelfTest, WuxAuto.Core.Tests)
- Create: `tests/WuxAuto.Core.Tests/WuxAuto.Core.Tests.csproj` (net10.0, x64, xUnit; references Core)
- Move (`git mv`, namespaces unchanged): `src/WuxAuto/Core/{AppSettings,Geometry,SettingsStore,LogEntry}.cs` → `src/WuxAuto.Core/Core/`; `src/WuxAuto/Vision/VisionModels.cs` → `src/WuxAuto.Core/Vision/`
- Split: `TextMatcher` (bottom of `src/WuxAuto/Vision/OcrService.cs`) → `src/WuxAuto.Core/Vision/TextMatcher.cs`
- Modify: `src/WuxAuto.Core/Core/LogEntry.cs` (drop `Brush` and the WPF using); Create `src/WuxAuto/Converters/LogLevelBrushConverter.cs`; Modify `src/WuxAuto/MainWindow.xaml:476` to `Foreground="{Binding Level, Converter={StaticResource LogLevelBrush}}"` (same five colours as today)
- Modify: `WuxAuto.slnx`, `src/WuxAuto/WuxAuto.csproj` (ProjectReference to Core)
- Test: `tests/WuxAuto.Core.Tests/AppPathsTests.cs`, `tests/WuxAuto.Core.Tests/TextMatcherTests.cs`

**Interfaces:**
- Produces in `AppPaths` (static, `WuxAuto.Core`): `string Root { get; private set; }` (default unchanged), `string AssetsFolder { get; private set; }` (default `Path.Combine(AppContext.BaseDirectory, "Assets")`), `static void Configure(string root, string? assetsFolder = null)`. `SettingsFile`, `DebugFolder`, `TemplateFolder`, `LogFolder`, `KillsFolder` stay computed from `Root` on every read.

- [ ] **Step 1: Write the failing tests**

```csharp
[Collection("AppPaths")] // static state: never in parallel
public class AppPathsTests
{
    [Fact]
    public void Configure_MovesEveryFolderAndSettingsStoreFollows()
    {
        string root = Path.Combine(Path.GetTempPath(), "wux-core-" + Guid.NewGuid());
        AppPaths.Configure(root, Path.Combine(root, "assets"));
        Assert.Equal(Path.Combine(root, "settings.json"), AppPaths.SettingsFile);
        Assert.Equal(Path.Combine(root, "debug"), AppPaths.DebugFolder);
        Assert.Equal(Path.Combine(root, "kills"), AppPaths.KillsFolder);
        Assert.Equal(Path.Combine(root, "assets"), AppPaths.AssetsFolder);
        var settings = new AppSettings();
        settings.Sequence.Mode = RunMode.AutoLod;
        SettingsStore.Save(settings);
        Assert.True(File.Exists(Path.Combine(root, "settings.json")));
        Assert.Equal(RunMode.AutoLod, SettingsStore.Load().Sequence.Mode);
    }
}

public class TextMatcherTests
{
    static IReadOnlyList<OcrWord> Words(string text) =>
        [new OcrWord(text, new PixelRect(0, 0, 10, 10), text, new PixelRect(0, 0, 10, 10))];

    [Fact] public void Levenshtein_Classic() => Assert.Equal(3, TextMatcher.Levenshtein("kitten", "sitting"));
    [Fact] public void FindBest_MatchesDestinationInsideBrackets() => Assert.NotNull(TextMatcher.FindBest(Words("[The Labyrinth (Adept)]"), "Labyrinth", 0.72));
    [Fact] public void FindBest_ToleratesDroppedLetter() => Assert.NotNull(TextMatcher.FindBest(Words("SanctifiedUnicor"), "Sanctified Unicorn", 0.72));
    [Fact] public void Similarity_ShortPieceGetsNoContainmentBonus() => Assert.True(TextMatcher.Similarity("Rag", "Ragesoul Heathen") < 0.72);
}
```

- [ ] **Step 2: Run** `dotnet test tests\WuxAuto.Core.Tests` — Expected: FAIL to compile (`AppPaths.Configure` missing).
- [ ] **Step 3: Do the moves and add `Configure`/`AssetsFolder`**; `TemplateLibrary` reads `AppPaths.AssetsFolder` instead of building the path itself.
- [ ] **Step 4: Run** `dotnet test tests\WuxAuto.Core.Tests` and `dotnet build .\WuxAuto.slnx` — Expected: PASS, build clean.
- [ ] **Step 5: Commit** — `git commit -m "Core library: settings, geometry, log entry, text matching"`

### Task 7: The three seams, still inside the desktop project

**Files:**
- Create: `src/WuxAuto.Core/Platform/IOcrEngine.cs`, `src/WuxAuto.Core/Platform/IGameInput.cs`
- Move: `MouseButton` enum (top of `src/WuxAuto/Input/WindowInput.cs`) → `src/WuxAuto.Core/Platform/MouseButton.cs` (namespace `WuxAuto.Input` kept)
- Modify: `src/WuxAuto/Vision/OcrService.cs` (implements `IOcrEngine`), `src/WuxAuto/Input/WindowInput.cs` (implements `IGameInput`), `src/WuxAuto/Vision/ScreenAnalyzer.cs` (ctor), `src/WuxAuto/Automation/AutomationPilot.cs:232,446` (ctor, `TickAsync`), `src/WuxAuto/Engine/WuxEngine.cs`, every `new ScreenAnalyzer(` in `tests/WuxAuto.SelfTest` (9 sites)

**Interfaces:**
- Produces:

```csharp
public interface IOcrEngine
{
    string Status { get; }
    bool IsAvailable { get; }
    double LastDurationMs { get; }
    bool EnsureInitialized();
    Task<IReadOnlyList<OcrWord>> RecognizeAsync(Mat bgra, PixelRect area, double upscale);
}

public interface IGameInput
{
    bool IsDryRun { get; }
    bool IsTargetForeground { get; }
    bool TryFocus(int timeoutMs = 1200);
    bool KeyTap(string keyName);
    bool KeyDown(string keyName);
    bool KeyUp(string keyName);
    bool Click(int clientX, int clientY, MouseButton button = MouseButton.Left);
    bool CtrlClick(int clientX, int clientY);
    bool CtrlKeyTap(string keyName);
    bool CtrlWheel(int clientX, int clientY, int notches);
    bool Drag(int fromX, int fromY, int toX, int toY, int durationMs = 260);
    bool Wheel(int clientX, int clientY, int notches);
}
```

  `ScreenAnalyzer(AppSettings settings, IOcrEngine ocr)` with `IOcrEngine Ocr { get; }`;
  `AutomationPilot(AppSettings settings, ScreenAnalyzer analyzer, IGameInput input)`;
  `Task TickAsync(Mat frame, CancellationToken cancellationToken)` (the `CaptureFrame` parameter is unused today — drop it). `Mat` is OpenCvSharp's until Task 12.

- [ ] **Step 1: Add the interfaces and implement them on the existing classes** (signatures already match; only `: IOcrEngine` / `: IGameInput` and the constructor parameters change).
- [ ] **Step 2: Build** `dotnet build .\WuxAuto.slnx` and `dotnet build tests\WuxAuto.SelfTest` — Expected: clean.
- [ ] **Step 3: Baseline check** — `.\tests\probe-baseline.ps1 -Out <scratch>\m1-t7`; `git diff --no-index --stat <baseline-a> <scratch>\m1-t7` — Expected: no output.
- [ ] **Step 4: Commit** — `git commit -m "Seams: IOcrEngine, IGameInput; pilot tick takes only the frame"`

### Task 8: Move vision and the pilot into Core

**Files:**
- Move (`git mv`, namespaces unchanged): `src/WuxAuto/Vision/{ScreenAnalyzer,TemplateMatcher,TemplateLibrary,CompassDetector,OverlayRenderer,KillScreenshots}.cs` → `src/WuxAuto.Core/Vision/`; `src/WuxAuto/Automation/{AutomationPilot,AutomationPilot.Lod,AutomationPilot.War,AutomationPilot.KillShots,AutohuntLog}.cs` → `src/WuxAuto.Core/Automation/`
- Stay in the desktop: `Vision/OcrService.cs`, `Automation/AutoConfirm.cs`, `Engine/`, `Capture/`, `Input/WindowInput.cs`, `Interop/`, `Core/{KillAlert,StartupCheck,UpdateService}.cs`, `Assets/`
- Modify: `src/WuxAuto.Core/WuxAuto.Core.csproj` (`OpenCvSharp4` 4.13.0.20260627 — managed only; the `.runtime.win` stays in the desktop), `tests/WuxAuto.SelfTest/WuxAuto.SelfTest.csproj` and `tests/WuxAuto.UiShot/WuxAuto.UiShot.csproj` if they need the Core reference explicitly

- [ ] **Step 1: Move the files.** Anything internal that the desktop or SelfTest uses is covered by Task 6's `InternalsVisibleTo`.
- [ ] **Step 2: Core stays OS-free** — Run: `git grep -nE "using (System\.Windows|Windows\.|Microsoft\.Win32)|DllImport|LibraryImport" -- src/WuxAuto.Core` — Expected: no output.
- [ ] **Step 3: Build and test** — `dotnet build .\WuxAuto.slnx`; `dotnet build tests\WuxAuto.SelfTest`; `dotnet test tests\WuxAuto.Core.Tests` — Expected: clean / PASS.
- [ ] **Step 4: Baseline check** as in Task 7 Step 3 (`m1-t8`) — Expected: no output.
- [ ] **Step 5: Commit** — `git commit -m "Move the pilot and the vision code into WuxAuto.Core"`

### Task 9: M1 docs, PR, live check

**Files:**
- Modify: `CLAUDE.md` §3 repository map (Core project, the seams, where each folder lives now), `README.md` if it describes the layout

- [ ] **Step 1: Update the docs; commit.**
- [ ] **Step 2: Self-review the whole branch diff** (`git diff origin/main...`): every change outside the moves must be one of Tasks 6–7's listed edits.
- [ ] **Step 3: Rebase on `origin/main`, push `core-split`, open the PR, print the URL.**
- [ ] **Step 4: STOP for the user's live check:** a build of this branch (`.\publish.ps1` into a separate folder, never over their running install) runs one Laby loop and one LOD loop. Merge only after their OK.

---

## Part C — M2: Emgu CV and the parity gate (branch `core-emgu`)

### Task 10: Freeze the old vision code for comparison

**Files:**
- Create: `tests/WuxAuto.SelfTest/Legacy/` — copies of `src/WuxAuto.Core/Vision/{ScreenAnalyzer,TemplateMatcher,TemplateLibrary,CompassDetector}.cs` and `src/WuxAuto/Vision/OcrService.cs`, namespace `WuxAuto.Legacy`
- Modify: `tests/WuxAuto.SelfTest/WuxAuto.SelfTest.csproj` (explicit `OpenCvSharp4` + `OpenCvSharp4.runtime.win` 4.13.0.20260627)

**Interfaces:**
- Produces: `WuxAuto.Legacy.ScreenAnalyzer(AppSettings settings)` — exactly today's class, constructing its own `WuxAuto.Legacy.OcrService` (the legacy copy must not depend on `IOcrEngine`, whose `Mat` becomes Emgu's in Task 12).

- [ ] **Step 1: Copy and re-namespace; build SelfTest** — Expected: clean.
- [ ] **Step 2: Record the legacy crop of `flashlight3.png`** — print `Legacy.TemplateLibrary`'s `flashlight3` template width × height (one-off SelfTest line or debugger) and note it for Task 12's test.
- [ ] **Step 3: Commit** — `git commit -m "SelfTest: frozen OpenCvSharp vision for the parity check"`

### Task 11: The parity probe

**Files:**
- Create: `tests/WuxAuto.SelfTest/ParityProbe.cs`; Modify `tests/WuxAuto.SelfTest/Program.cs` (`--parity <dir>`)

**Interfaces:**
- Consumes: `WuxAuto.Legacy.ScreenAnalyzer` (Task 10), `WuxAuto.Vision.ScreenAnalyzer` (Core).
- Produces: `dotnet run --project tests\WuxAuto.SelfTest -- --parity <dir>`; record `DetectorResult(bool Found, PixelRect? Box, double? Score, string? Text)`; last line `parity: frames=<n> checks=<n> size-changes=<n> differences=<n> (verdict=<n> box=<n> score=<n> text=<n>)`.

- [ ] **Step 1: The checks** — per frame, run on both analyzers and compare:
  - every template the pilot searches, in the region the pilot passes for it (find each `FindIcon`/`FindIcons`/`FindIconAnyScale` call in `src/WuxAuto.Core/Automation` and reuse its region and threshold);
  - `ReadCoordinatesAsync`, `ReadCompass`, `FindSelectionRing`, `FindBossRing`, `FindBossBelt`, `RingFraction` (only where `runskill` is found), `FindGoldButtons` (count + first box), `FindRedOrangeGlow` in the pilot's two bands, `FindMapScrollArrowsAsync` on `Regions.MapScroll`, the `--mapopen` verdict, the `--titles` zone title, `FindConfirmDialogAsync("Confirm","OK")`, and `FindCheckboxLeftOf`/`IsCheckboxTicked`/`FindCloseButtonRightOf` only on frames where the autobuff title is found.
- [ ] **Step 2: Compare** — verdict equal; each box edge within 2 px; score within 0.02; text equal after `TextMatcher.Normalize`. Print every difference as `<frame> <check> legacy=<…> new=<…>`. Frames in name order, one analyzer pair for the whole run (no cache resets); count size changes between consecutive frames.
- [ ] **Step 3: Prove the probe** — today both sides are OpenCvSharp, so:
Run: `dotnet run --project tests\WuxAuto.SelfTest -- --parity "$env:LOCALAPPDATA\WuxAuto\parity-frames"`
Expected: `differences=0` and `size-changes` > 0.
- [ ] **Step 4: Commit** — `git commit -m "SelfTest: --parity compares legacy and Core vision per detector"`

### Task 12: Port Core's vision to Emgu CV 5

**Files:**
- Modify: `src/WuxAuto.Core/WuxAuto.Core.csproj` (`Emgu.CV` 5.0.0.6584 replaces OpenCvSharp4), every file in `src/WuxAuto.Core/Vision/` and the `Mat` usages in `src/WuxAuto.Core/Automation/`, `src/WuxAuto.Core/Platform/IOcrEngine.cs` (`Emgu.CV.Mat`)
- Modify desktop: `src/WuxAuto/WuxAuto.csproj` (`Emgu.CV.runtime.mini.windows` replaces both OpenCvSharp packages), `src/WuxAuto/Engine/WuxEngine.cs` (frame `Mat` over the pinned capture buffer), `src/WuxAuto/Vision/OcrService.cs` (Emgu `Mat` → `SoftwareBitmap`), `src/WuxAuto/Automation/AutoConfirm.cs`, `src/WuxAuto/Core/StartupCheck.cs` (load probe)
- Modify: `tests/WuxAuto.Core.Tests/WuxAuto.Core.Tests.csproj` (Emgu Windows runtime, the Assets link below)
- Modify: the SelfTest probes outside `Legacy/` (`FrameProbe.cs`, `Program.cs`, … — every OpenCvSharp use) to Emgu; `ParityProbe.cs` now decodes each PNG twice: OpenCvSharp `Mat` for the legacy side, Emgu `Mat` for Core
- Test: `tests/WuxAuto.Core.Tests/TemplateLibraryTests.cs`, `tests/WuxAuto.Core.Tests/ImageFileTests.cs`

**Interfaces:**
- Produces: `IOcrEngine.RecognizeAsync(Emgu.CV.Mat bgra, PixelRect area, double upscale)`; `AutomationPilot.TickAsync(Emgu.CV.Mat frame, CancellationToken)`; all other public signatures unchanged except the `Mat` type.

- [ ] **Step 1: Write the failing tests**

The test project copies the shipped templates to its output:
`<None Include="..\..\src\WuxAuto\Assets\*.png" Link="Assets\%(Filename)%(Extension)" CopyToOutputDirectory="PreserveNewest" />`.
`TempRoot()` = a new folder under `Path.GetTempPath()`.

```csharp
[Collection("AppPaths")]
public class TemplateLibraryTests
{
    const int LegacyWidth = 0, LegacyHeight = 0; // paste Task 10 Step 2's numbers

    [Fact]
    public void LoadGray_CropsTransparentPngToSameBoxAsLegacy()
    {
        AppPaths.Configure(TempRoot(), Path.Combine(AppContext.BaseDirectory, "Assets"));
        using var library = new TemplateLibrary();
        Assert.True(library.TryGet("flashlight3", out var t));
        Assert.Equal((LegacyWidth, LegacyHeight), (t.Gray.Width, t.Gray.Height));
    }
}

[Collection("AppPaths")]
public class ImageFileTests
{
    [Fact]
    public void TemplateLibrary_LoadsFromNonAsciiFolder()
    {
        string root = Path.Combine(TempRoot(), "Pétur");
        Directory.CreateDirectory(Path.Combine(root, "templates"));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Assets", "mapicon.png"), Path.Combine(root, "templates", "mapicon.png"));
        AppPaths.Configure(root, Path.Combine(root, "no-assets"));
        using var library = new TemplateLibrary();
        Assert.True(library.TryGet("mapicon", out _));
    }

    [Fact]
    public void DebugArtifacts_SaveFrame_WritesToNonAsciiFolder()
    {
        AppPaths.Configure(Path.Combine(TempRoot(), "Pétur"));
        using var frame = new Mat(64, 64, DepthType.Cv8U, 4);
        frame.SetTo(new MCvScalar(10, 20, 30, 255));
        DebugArtifacts.SaveFrame(frame, [], "test");
        string file = Assert.Single(Directory.GetFiles(AppPaths.DebugFolder, "*.png"));
        using var back = new Mat();
        CvInvoke.Imdecode(File.ReadAllBytes(file), ImreadModes.Unchanged, back);
        Assert.Equal((64, 64), (back.Width, back.Height));
    }
}
```

- [ ] **Step 2: Run** `dotnet test tests\WuxAuto.Core.Tests` — Expected: FAIL (Emgu types missing).
- [ ] **Step 3: Port.** Rules: OpenCvSharp `Cv2.X` → `CvInvoke.X` with the same flags (Otsu, `CcoeffNormed`, `Inter.Cubic`, contour retrieval/approx modes, morphology kernels); `ImRead` → read bytes + `CvInvoke.Imdecode(bytes, ImreadModes.Unchanged, …)`; `ImWrite` → `CvInvoke.Imencode` + `File.WriteAllBytes` (as `KillScreenshots` already does — fixes non-ASCII paths, Review Focus 3); pixel loops read from `Mat.GetData()`/spans, never `Marshal.Copy` into `mat.Data` (CLAUDE.md §10: crashed the CLR on this Windows-on-ARM machine). The desktop frame `Mat` is `new Mat(height, width, DepthType.Cv8U, 4, pinnedPtr, stride)` over the capture buffer, pinned for the `Mat`'s lifetime. Generated templates (`upchevron`, `downchevron`, `leftchevron`, `<flashlight>west`) keep their rotations. `StartupCheck`: the OpenCV load probe creates an Emgu `Mat` instead; re-derive the Media Foundation DLL list from `cvextern.dll`'s imports (`dumpbin /dependents` from a VS developer prompt) and drop the check if it imports none.
- [ ] **Step 4: Run** `dotnet test tests\WuxAuto.Core.Tests`, `dotnet build .\WuxAuto.slnx`, `dotnet run --project tests\WuxAuto.SelfTest` (synthetic self test) — Expected: PASS / clean / every self-test line passes.
- [ ] **Step 5: Commit** — `git commit -m "Core vision on Emgu CV 5"`

### Task 13: Run the gate; packaging; licence — STOP for the user

**Files:**
- Modify: `publish.ps1:26-27` (the `opencv_videoio_ffmpeg` removal → remove only Emgu's video-I/O natives, if the mini runtime ships any), `README.md` (licence + Emgu)
- Create: `LICENSE` (GPL-3.0, the canonical text from gnu.org)

- [ ] **Step 1: Parity** — Run: `dotnet run --project tests\WuxAuto.SelfTest -- --parity "$env:LOCALAPPDATA\WuxAuto\parity-frames" > <scratch>\parity.txt`. Expected: `verdict=0`. Box/score/text differences are listed for the user either way.
- [ ] **Step 2: Probes** — `.\tests\probe-baseline.ps1 -Out <scratch>\m2`; diff against the M1 baseline. Score digits may move within 0.02; any other line difference goes into the report.
- [ ] **Step 3: Package** — `.\publish.ps1` into `dist\`; note the zip size before (≈93 MB) and after; check the package holds Emgu's **win-x64** native `cvextern.dll` (the app must stay x64, CLAUDE.md §10); the published app starts (`StartupCheck` report clean in `logs\startup_check_*.txt`).
- [ ] **Step 4: Licence and README; commit.**
- [ ] **Step 5: STOP.** Show the user the parity summary line, every verdict difference (if any) and the size change. Their decision rules (spec §5.2): any verdict difference they reject → the desktop stays on OpenCvSharp with two vision implementations — come back with a revised Part C before continuing. Otherwise: rebase, push `core-emgu`, PR with the URL, and their live Laby + LOD loop on a separately published build before merging.

---

## Part D — M3: actions instead of keys (branch `core-actions`)

### Task 14: `IGameControls` and its keyboard/mouse implementation

**Files:**
- Create: `src/WuxAuto.Core/Platform/IGameControls.cs`, `src/WuxAuto.Core/Platform/KeyboardMouseControls.cs`
- Test: `tests/WuxAuto.Core.Tests/KeyboardMouseControlsTests.cs` (with a `RecordingInput : IGameInput` that appends `"KeyTap:J"`, `"KeyDown:A"`, `"KeyUp:A"`, `"Click:10,20,Left"`, `"CtrlClick:5,6"`, `"CtrlKeyTap:1"`, `"Drag:1,2,3,4,300"`, `"Wheel:7,8,-3"`, `"CtrlWheel:7,8,-15"` and returns a configurable result)

**Interfaces:**
- Produces:

```csharp
public enum GameAction { ToggleAutohunt, Buff, TargetNearest, Loot, OpenInventory, ToggleSettings,
                         OpenAutohuntSettings, RunSkillKey, ShuraKey, StopAttack }
public enum TurnDirection { Left, Right }

public interface IGameControls
{
    bool IsDryRun { get; }
    bool IsGameInFront { get; }
    bool TryBringGameToFront();
    bool Has(GameAction action);            // desktop: its key is not blank
    bool Press(GameAction action);
    bool Tap(int x, int y);
    bool UseItem(int x, int y);             // desktop: right click
    bool SkillTap(string skill);            // desktop: today's TapSkill
    bool SkillDown(string skill);
    bool SkillUp(string skill);
    bool WalkTo(int x, int y, bool forceWalk); // desktop: Click, or CtrlClick when forceWalk
    bool Turn(TurnDirection direction, int ms);
    bool BeginTurn(TurnDirection direction);
    void EndTurn(TurnDirection direction);
    bool TurnByDrag(int x, int y, int pixels, int durationMs);
    bool TiltCamera(int x, int y, int pixels, int durationMs);
    bool ZoomOut(int x, int y, int notches);
    bool ScrollList(int x, int y, int notches);
}
```

  `KeyboardMouseControls(AppSettings settings, IGameInput input) : IGameControls`. Key per action:
  ToggleAutohunt `Dungeon.AutohuntKey`, Buff `Lod.BuffKey`, TargetNearest `Dungeon.TabKey`, Loot
  `Lod.LootKey`, OpenInventory `Input.InventoryKey`, ToggleSettings `"Escape"`, OpenAutohuntSettings
  `Sequence.AutohuntSettingsKey`, RunSkillKey `Sequence.RunSkillKey`, ShuraKey `Lod.ShuraKey`,
  StopAttack `Lod.StopAttackKey`. Turn Left = `Dungeon.RotateLeftKey`, Right = `Input.RotateKey`.
  `SkillTap`: `KeyTap(skill)`; if it returned true and `Dungeon.CtrlSkill`, wait 60 ms then
  `CtrlKeyTap(skill)`; returns the plain tap's result. `Turn`: `KeyDown`, sleep `ms`, `KeyUp`.
  `TiltCamera` = `Drag(x, y, x, y + pixels, durationMs)`; `TurnByDrag` = `Drag(x, y, x + pixels, y, durationMs)`;
  `ZoomOut` = `CtrlWheel(x, y, -notches)`; `ScrollList` = `Wheel(x, y, notches)`.

- [ ] **Step 1: Write the failing tests** — one per mapping, asserting the exact recorded list, e.g.:

```csharp
[Fact] public void Press_ToggleAutohunt_TapsAutohuntKey()      => Assert.Equal(["KeyTap:J"], Run(c => c.Press(GameAction.ToggleAutohunt)));
[Fact] public void SkillTap_WithCtrlSkill_TapsPlainThenCtrl()   => Assert.Equal(["KeyTap:1", "CtrlKeyTap:1"], Run(c => c.SkillTap("1"), ctrlSkill: true));
[Fact] public void SkillTap_PlainBlocked_SkipsCtrlAndFails()    { /* KeyTap returns false → ["KeyTap:1"], result false */ }
[Fact] public void WalkTo_ForceWalk_UsesCtrlClick()             => Assert.Equal(["CtrlClick:5,6"], Run(c => c.WalkTo(5, 6, forceWalk: true)));
[Fact] public void Turn_Left_HoldsRotateLeftKey()               => Assert.Equal(["KeyDown:A", "KeyUp:A"], Run(c => c.Turn(TurnDirection.Left, 40)));
[Fact] public void EndTurn_ReleasesTheKeyBeginTurnPressed()     => Assert.Equal(["KeyDown:A", "KeyUp:A"], Run(c => { c.BeginTurn(TurnDirection.Left); c.EndTurn(TurnDirection.Left); }));
[Fact] public void SkillUp_ReleasesTheHeldSkill()               => Assert.Equal(["KeyDown:1", "KeyUp:1"], Run(c => { c.SkillDown("1"); c.SkillUp("1"); }));
[Fact] public void ZoomOut_SendsNegativeCtrlWheel()             => Assert.Equal(["CtrlWheel:7,8,-15"], Run(c => c.ZoomOut(7, 8, 15)));
[Fact] public void Has_FalseForBlankKey()                       { /* Lod.LootKey = "" → Has(Loot) false, nothing recorded */ }
```

  plus the remaining `Press` keys, `Tap`, `UseItem` (`Click:x,y,Right`), `Turn` Right (`D`),
  `TurnByDrag`, `TiltCamera`, `ScrollList`, `IsDryRun` mirrors `Sequence.DryRun`.
- [ ] **Step 2: Run** `dotnet test tests\WuxAuto.Core.Tests` — Expected: FAIL to compile.
- [ ] **Step 3: Implement** the interface and `KeyboardMouseControls`.
- [ ] **Step 4: Run** — Expected: PASS.
- [ ] **Step 5: Commit** — `git commit -m "IGameControls: game actions with a keyboard/mouse mapping"`

### Task 15: The pilot uses actions

**Files:**
- Modify: `src/WuxAuto.Core/Automation/AutomationPilot*.cs` (every `_input.` call), `src/WuxAuto/Engine/WuxEngine.cs` (`new KeyboardMouseControls(settings, _input)` into the pilot; `AutoConfirm` keeps `WindowInput`)

**Interfaces:**
- Consumes: Task 14.
- Produces: `AutomationPilot(AppSettings settings, ScreenAnalyzer analyzer, IGameControls controls)`.

- [ ] **Step 1: Replace each call site** (`git grep -n "_input\." -- src/WuxAuto.Core/Automation` lists ~60): a `KeyTap(<settings key>)` becomes `Press(<action>)` per Task 14's key table, keeping each site's blank-key check as `Has(<action>)`; `TapSkill` → `SkillTap`; `HoldKey(RotateLeftKey|RotateKey, ms)` → `Turn(Left|Right, ms)`; the sweep's `KeyDown/KeyUp(RotateLeftKey)` → `BeginTurn/EndTurn(Left)`; held LOD skill `KeyDown/KeyUp` → `SkillDown/SkillUp`; `Click(x, y)` → `Tap` except the Laby run-step clicks (→ `WalkTo(x, y, false)`) and the LOD ring walk `CtrlClick` (→ `WalkTo(x, y, true)`); the teleport scroll's right click → `UseItem`; the tilt drags → `TiltCamera`; `CtrlWheel` zoom → `ZoomOut`; list `Wheel`s → `ScrollList`; `IsTargetForeground` → `IsGameInFront`; `TryFocus` → `TryBringGameToFront`. `ApplyTurn` has no callers — delete it instead of porting it.
- [ ] **Step 2: No raw input left** — Run: `git grep -n "_input\." -- src/WuxAuto.Core/Automation` — Expected: no output.
- [ ] **Step 3: Build, test, baseline** — `dotnet build .\WuxAuto.slnx`; `dotnet test tests\WuxAuto.Core.Tests`; `.\tests\probe-baseline.ps1 -Out <scratch>\m3` diffed against the M2 output — Expected: clean / PASS / no output.
- [ ] **Step 4: Commit** — `git commit -m "Pilot drives the game through IGameControls"`

### Task 16: M3 docs, PR, live check

- [ ] **Step 1: Update `CLAUDE.md`** (§3 map: `Platform/` interfaces; §6 note that keys are read only by `KeyboardMouseControls`); commit.
- [ ] **Step 2: Self-review the branch diff; rebase; push `core-actions`; open the PR; print the URL.**
- [ ] **Step 3: STOP for the user's live check** (one Laby loop, one LOD loop on a separately published build). Merge after their OK. Then write Plan 2 (phone app, M4–M6) from the spec, the M0 results note and the MuMu checks of spec §10.

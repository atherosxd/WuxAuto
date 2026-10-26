# WuxAuto for Android — design

Date: 2026-10-26. Branch: `android-pilot`. Status: draft for the user's approval.

Marking used below: **(user)** = the user decided or stated it; **(my call)** = decided by me during the
design conversation, open to the user's veto; **(verify)** = unknown, to be checked in MuMu or on a
phone before the part that depends on it is built.

---

## 1. Intent

WuxAuto (`src/WuxAuto`, WPF, see the root `CLAUDE.md`) pilots the PC client of Wuxen2: Auto Laby
(teleport → 4 stages → stage 5 boss → home, looped) and Auto LOD (walk to the Bai Gu Great, attack,
loot, respawn, buff). The game also has an Android client with almost the same interface; only the
controls differ (joystick instead of WASD/clicks, a shortcut ring and a ☰ menu instead of keyboard
shortcuts) **(user)**.

The new app runs **on the phone itself, next to the game**: while the pilot runs, the phone is
**unlocked, screen on, game in front** **(user)**, and nobody uses it; the app reads the screen and taps
for the player. (Without root nothing can read or tap a locked or dark screen, so this is a hard
requirement, not a preference.)

Why: LOD has no autohunt, so on a phone the player presses skills and loots by hand the whole time —
"a hassle playing in mobile" **(user)**. Both Laby and LOD must be there **(user)**.

Expectations: an experiment. "Not expecting it to work on most phones, but I need to try" **(user)**.
Success = it runs reliably on MuMu and on the user's own phone, a **Xiaomi 17 Pro Max** **(user)**;
other phones are a bonus.

## 2. Goals, non-goals, anti-goals

Goals
- An Android app (.NET MAUI) that runs Auto Laby and Auto LOD on the phone, Android 12+ first **(user)**.
- **Most of the code shared with the desktop app** — one copy of the run logic, picture finding and
  settings, so a fix lands in both **(user: "shared most of it to the desktop app")**.
- Feature parity with the desktop except what only makes sense on a PC (§6.10) **(user: "nothing much
  I guess that is unique in the PC")**.
- A floating control over the game, like Android's screen-recording controls, in place of the
  desktop's Mini window **(user)**.

Non-goals
- Working on most phones. Different screen shapes are handled as well as the desktop handles
  different window sizes (templates + OCR anchors), no more.
- Root-only features, iOS, tablets in portrait.
- Remote control of the phone from the PC.
- Auto confirm (the desktop's Ctrl + right-click selling helper) — no such gesture on a phone **(user
  agreed to the PC-only list)**.

Anti-goals (failure even if the phone app works)
- **Breaking the desktop app.** The user and friends run it live every day. Every desktop-side change
  must keep its behaviour identical, and the switch to Emgu CV only happens if results are "almost
  the same" **(user)** — see the parity gate, §5.2.
- Wasting the user's dungeon keys / LOD entries or touching their account during exploration:
  no Item Mall, shops, trades, chat SEND; ask the user before any test that teleports into the
  Labyrinth or LOD **(my call)**.
- Committing game screenshots that show other players' names to the public repo **(my call)**:
  only template crops go into git; full test frames stay local.

## 3. Constraints

- Licence: Emgu CV's free edition is GPL; the project will be open source **(user: "I know emgucv is
  dual license and this will be open source")**. Add a GPL-3.0 `LICENSE` to the repo root **(my call)**;
  the desktop's other dependencies (CommunityToolkit.Mvvm MIT, Vortice MIT) are compatible.
- Picture finding: **Emgu CV 5** (`Emgu.CV` 5.0.0.6584) **(user)**. The free Android runtime on
  NuGet is `Emgu.CV.runtime.maui.mini.android` 5.0.0.6584 (net10.0-android36.0). The "mini" build has
  no Tesseract (Emgu's own build notes: *mini — minimal subset*, *core — no Tesseract*), but keeps
  imgproc (template matching, colour masks, contours) — everything the desktop vision code uses.
- Text reading: **Google ML Kit text recognition** on the phone **(user)**. Compiling the full Emgu
  build with Tesseract for Android is allowed **(user: "don't mind if we compile it if needed")** but
  only as a fallback if ML Kit misreads game text **(my call)**.
- Desktop OCR stays **Windows.Media.Ocr** **(my call — never discussed; it works and is verified on
  hundreds of frames)**.
- Toolchain on this PC (checked 2026-10-26, Windows 11 ARM64): .NET SDKs 10.0.402 and
  11.0.100-rc.2 (the default, no `global.json`); workloads for 10.0.402: `android` 36.1, `maui-windows`
  — **`maui-android` is not installed yet**; JDK 21 at `C:\Program Files\Android\openjdk\jdk-21.0.8`;
  Android SDK at `C:\Program Files (x86)\Android\android-sdk` (platforms android-36, android-37.0).
- Target: `net10.0-android`, minSdk 31 (Android 12), ABI **arm64-v8a only** (MuMu ARM and every
  target phone are arm64; keeps the APK smaller) **(my call)**.
- The desktop app must stay `x64` (CLAUDE.md §10) — check that Emgu's Windows runtime package ships
  win-x64 natives (verify).
- **Test devices.** MuMu (Android 12, 1600×900, §4) and the user's **Xiaomi 17 Pro Max**: per spec
  listings Android 16 with **HyperOS 3**, 6.9" 1200×2608 → **2608×1200 landscape (~19.5:9)** — confirm
  on the device. Consequences: the Android 13+ "Allow restricted settings" step before the
  accessibility toggle; HyperOS's own background limits (Autostart on, battery saver "No
  restrictions", possibly "Display pop-up windows while running in the background") on the setup page;
  a much wider screen than MuMu, so every region and the floating control must hold at both aspect
  ratios. adb to the phone over USB or wireless debugging is to be arranged with the user when M4
  reaches it; the "share debug bundle" button covers the gap until then.
  *After M0:* MuMu now runs 1920×1080, not 1600×900 (see `docs/superpowers/notes/2026-10-26-m0-gate-results.md`).

## 4. What exploration found (MuMu, 2026-10-26)

Test device: **MuMu Player ARM** (Hyper-V VM) on this PC, logged into **a different game account than
the PC pilot** **(user)**, so both can run at once. That account is normally grinding; the builder may
stop it, go to town and switch its autohunt off for tests **(user)**; for dungeon tests the user may
switch it to a character that can enter the Labyrinth / LOD **(user)**. Drive MuMu **only through adb,
never by bringing its window to the front** — on this PC its window could cover the live PC game,
which the desktop pilot captures from the screen **(my call)**. adb:
`"C:\Program Files\Netease\MuMuPlayerARM\shell\adb.exe" connect 10.0.2.95:5555` (host/port from
`...\MuMuPlayerARM\vms\vm0.gmadoa\setting.json`, `AdbHost`/`AdbPort`). Android 12 (SDK 32),
arm64-v8a, 900×1600 physical = **1600×900 landscape**, density 240. Game package
`com.wuxen.wuxen2`, activity `com.wuxen.gxd.GXDPlayerActivity`.
*After M0:* MuMu now runs 1920×1080 (see `docs/superpowers/notes/2026-10-26-m0-gate-results.md`).

Verified by hand (adb `screencap` / `input`), in Piercing City:
- **Screenshots are not blocked** (no secure flag): full game frame captured.
- **Faked joystick touch walks the character**: `input swipe 220 680 220 590 2500` → 10362,13793 →
  10289,13862. (adb injects through InputManager; the app will inject through the Accessibility
  service — same event path in principle, still to be proven, §7 M0.)
- **Swiping on empty ground turns the camera**, and the **minimap beam turns with it** (the beam is
  the camera, the small arrow is the character) — the desktop's face-north idea carries over.
- Minimap: zone title `[Piercing City]` and coordinates `10362, 13793` in the top-right panel, like PC.
- **Map window** (minimap icon) is the same as PC: "Monsters on this map", Lv / Spawns columns, a `>`
  per row. No scroll arrows were visible above "Spawns" (list probably scrolls by swipe — verify).
- **☰ menu** (top bar, ~990,25) opens a panel over the right side (it covers the minimap): Settings
  (ESC), Stat (C), Inventory (I), SKILLS, Mount, Costume, Auto-Hunt (J), Auto-Potion, Personal Shop,
  Consignment Shop, Friend (F), Guild (G), Tribe Status, Level Bonus, Item Mall, Leader Vote, Daily
  Reward, Rank Buff, Battle Pass, Ranking, Farming Guide, **Durations**. Close X top right.

From the user:
- **Durations** (☰ → Durations) holds the Temple of Fiends Key / Den of Rebirth counts — on PC they
  are on screen, on the phone they are hidden there.
- **Big single sword** (right side, ~1380,680 at 1600×900) = Tab: selects the nearest mob/boss **and
  shows the selection ring at once** (on PC, Tab shows only the name, no ring).
- **Tapping the boss** selects it. There is no tap-to-walk; walking is joystick only.
- **White crossed swords** = auto-buff trigger. (A tap on the right-hand one, ~1530,553, made a "+1"
  buff icon appear.)
- **Left shortcut ring** around the joystick: player-filled slots. Locked by default; unlocked, a tap
  on a slot or a (+) opens a picker: CLEAR, HUNT (J: start…), AUTO BUFF, **Pick up** (loot), Gold
  Scroll, Camera lock, Rage Burst, **Return Scroll**, **Teleport Scroll**, Settings (ESC), Stat,
  Inventory, SKILLS, Mount, Costume, Auto-Hunt (J), Auto-Potion, … (every ☰ entry). The pilot may
  **add missing shortcuts to free (+) slots itself** **(user)**.
- Skill wheel bottom right: 1 / 2 / 3.

The user's MuMu key mapping (`%LOCALAPPDATA%\Netease\MuMuARM\keymaps\keyboard\com.wuxen.wuxen2\
customer\Customized Mapping Scheme.json`, relative to 1600×900) shows which ring slot holds what on
their account: WASD joystick centre 220,682 (radius 0.055 w); J → 128,823; O (buff) → 86,790;
I → 59,625; C → 83,565; Alt+S → 120,535. Nothing is mapped for Tab, the skills or loot. These are one
player's slots — the app must **find shortcuts by icon**, not by position.

Frames from the exploration stay in a local scratchpad, not in git; recapture as needed.

## 5. Architecture

### 5.1 Projects (my call)

```
src/WuxAuto.Core        net10.0 class library, no Windows/Android types
  Automation/           AutomationPilot (all partials), AutohuntLog — the state machine
  Vision/               ScreenAnalyzer, TemplateMatcher, TemplateLibrary, CompassDetector,
                        VisionModels, OverlayRenderer, KillScreenshots — on Emgu.CV (from M2)
  Core/                 AppSettings, SettingsStore, Geometry, LogEntry (no WPF brush), TextMatcher
  Platform/             the interfaces below
  Engine/               the vision loop (capture → pilot tick → preview), minus WPF types
src/WuxAuto             WPF desktop: UI, DesktopCaptureService, WindowInput (→ IGameInput),
                        WindowsOcr (→ IOcrEngine), KillAlert (toast), UpdateService, StartupCheck,
                        AutoConfirm, Mini window
src/WuxAuto.Mobile      .NET MAUI, net10.0-android only: Accessibility service (capture + gestures
                        + floating control), ML Kit OCR, foreground notification, settings/log pages,
                        APK self-update
tests/WuxAuto.SelfTest  desktop probes, plus the parity probe (§5.2)
```

Names, namespaces and the exact split are the builder's; the rule is: **anything that decides what to
do in the game lives in Core; anything that touches the OS lives in the app.**

Platform interfaces in Core (shape is the builder's; these are the responsibilities):
- **Frame source** — delivers the latest frame as an Emgu `Mat` (BGRA) plus size and timestamp.
- **`IOcrEngine`** — `RecognizeAsync(Mat, PixelRect area, double upscale) → IReadOnlyList<OcrWord>`
  (today's `OcrService` signature; words with line grouping). Desktop: Windows OCR; phone: ML Kit
  (blocks → lines → elements map onto words + line bounds).
- **`IGameInput`** — game *actions*, not keys (§5.3). Plus `IsReady` (desktop: bound + foreground;
  phone: service connected and the game package in front) and `IsDryRun`.
- **Platform profile** — capability switches where the *flow* differs between PC and phone (§6), e.g.
  "teleport via inventory scroll" vs "via Teleport Scroll shortcut", "Tab shows the ring", "key counts
  in Durations". The pilot branches on these; it does not test "am I on Android".
- **App paths / alerts / clock** — where settings, logs, debug pictures and templates live; the
  kill-alert sink (the existing `BossKilled` event stays).

### 5.2 Desktop switch to Emgu CV — the parity gate (user: "switch if it will almost be the same result")

The desktop's vision code (~2,400 lines in `Vision/`, plus `Mat` as the frame type in ~70 places in
the pilot; the pilot itself never calls OpenCV directly) moves from OpenCvSharp 4.13 to Emgu CV 5.

Gate, before the desktop ships on Emgu:
1. Keep the OpenCvSharp `ScreenAnalyzer` stack alive side by side (in the SelfTest project only) while
   the Emgu port is built.
2. A SelfTest probe (`--parity <dir>`) runs both over every PNG in `%LOCALAPPDATA%\WuxAuto\debug`
   (~340 saved frames, 1024–2304 wide) and the synthetic self-test frames, for every detector the
   pilot uses: each template (`FindIcon`/`FindIcons`/`FindIconAnyScale`, learned scale), coordinates
   read, compass/flashlight scores, `FindSelectionRing`, `FindBossRing`, `FindBossBelt`, `RingFraction`
   (Run skill), Shura, `FindGoldButtons`, `FindRedOrangeGlow`, `FindBrightBlobs` + map arrows,
   checkbox/close-button finds, map-open test, zone title, confirm dialog.
3. **"Almost the same"** (my definition): the same found / not-found verdict on every frame for every
   detector; boxes within 2 px; scores within ±0.02; identical OCR-derived results (coordinates,
   titles). The probe prints a summary and every difference.
4. Any verdict difference → show the user the list; they decide. If they say no, the desktop stays on
   OpenCvSharp and the vision code sits behind an interface with two implementations (Emgu on phone,
   OpenCvSharp on PC) — the run logic is still shared.
5. Also re-run the existing offline probes (`dotnet run --project tests\WuxAuto.SelfTest`, `--ring`,
   `--mapopen`, `--lodparts`, `--shura`, `--runskill`, `--titles`, `--confirm`) and compare outputs.

Side effects to handle: `publish.ps1` (drops `opencv_videoio_ffmpeg` today — Emgu's native file names
differ), `StartupCheck` (checks that OpenCV loads and that the Media Foundation DLLs it imports exist —
re-check what Emgu's `cvextern.dll` imports), package size, the Windows-on-ARM rule "no
`Marshal.Copy` into `mat.Data`" (CLAUDE.md §10) — keep the pinned-buffer approach.

### 5.3 Actions instead of keys (my call)

The pilot today calls `WindowInput` ~60 times: `KeyTap/KeyDown/KeyUp` with keys from settings,
`Click`, `CtrlClick`, `CtrlKeyTap`, `Drag`, `Wheel`, `CtrlWheel`. Each becomes an action; the desktop
adapter turns it into exactly the call it makes today (so desktop behaviour is unchanged), the phone
adapter turns it into touches.

| Action | Desktop (unchanged) | Phone |
|---|---|---|
| Tap point | left click | tap |
| Autohunt toggle | `AutohuntKey` J | Auto-Hunt shortcut on the ring (fallback ☰ → Auto-Hunt) |
| Buff | `Lod.BuffKey` O | AUTO BUFF (white crossed swords) |
| Target nearest | `TabKey` Tab | big single sword |
| Skill press / hold | `SkillKey` (+ Ctrl+skill when `CtrlSkill`) | skill-wheel button tap / long press; no Ctrl variant |
| Loot | `Lod.LootKey` \` | Pick up shortcut |
| Inventory / Settings | I / Escape | ☰ → Inventory / ☰ → Settings |
| Autohunt settings (Auto War) | `AutohuntSettingsKey` S | (verify — ☰ → Auto-Hunt?) |
| Run skill | `RunSkillKey` W / click icon | tap the icon on the hotbar (verify it is there) |
| Stop attack | Shura icon / `ShuraKey` / Space | Shura icon on the hotbar; else (verify) |
| Walk towards (direction, length) | click / Ctrl+click at the point the pilot computes today | joystick held in that direction for the step time |
| Turn camera (amount) | `RotateLeftKey`/`RotateKey` hold (A/D) | horizontal swipe on empty ground |
| Tilt camera | right-drag up | vertical swipe (verify) |
| Zoom out | Ctrl + wheel | two-finger pinch (verify) |
| Scroll a list | wheel / arrow click | swipe inside the list (verify) |

Walking: the pilot keeps computing the screen-space click offset it computes today (SimpleSteer,
heading fix, `_headingBase`); the desktop clicks there, the phone converts the same vector to a
joystick angle (screen-up = camera forward) and holds it for `RunDashWaitMs`-ish step times. LOD's
"walk up to the boss" (Ctrl+click on the ring centre) becomes a joystick hold toward the ring centre,
length from the distance (calibrate in MuMu).

Settings: the key fields stay for the desktop; the phone gets its own section (shortcut identities,
swipe lengths per camera "tap", joystick step times, pinch size) **(my call)**. The `[JsonIgnore]` +
`ResetPositions()` rule for anything positional (CLAUDE.md §6) applies to the new fields too.

### 5.4 The Android app

**Accessibility service** (the only permission-like step: the player turns it on once in Android
Settings) **(my call, agreed in conversation)**:
- **Screen capture:** `AccessibilityService.takeScreenshot` (API 30+): no "start recording?" prompt.
  The system rate-limits it (about one shot per ~333 ms) → ~3 frames/s. The pilot's waits are
  time-based (0.3–2 s), so that should do; **MediaProjection** (up to 30 fps, one consent prompt per
  start) is the fallback if 3 fps proves too slow — decide in M0 **(my call)**. Hardware bitmap →
  software ARGB copy → Emgu `Mat` (BGRA).
  *After M0:* capture is MediaProjection, with a consent prompt per session; accessibility capture was black on MuMu (to re-check on the Xiaomi). See `docs/superpowers/notes/2026-10-26-m0-gate-results.md`.
- **Touches:** `dispatchGesture`. **A new gesture cancels the one in flight**, so all touches go
  through one scheduler that merges simultaneous needs (joystick held + a tap) into one multi-stroke
  `GestureDescription`, and uses continued strokes (`willContinue`) to keep the joystick finger down
  across direction changes. Stop / panic cancels everything.
  *After M0:* touches are serialised. A tap inside a held-joystick gesture is not delivered, so the scheduler ends the joystick step, taps, then resumes (see `docs/superpowers/notes/2026-10-26-m0-gate-results.md`).
- **Floating control** (user: "like the screen recording"): drawn as an accessibility overlay
  (`TYPE_ACCESSIBILITY_OVERLAY`, no extra "draw over other apps" permission) **(my call)**. Collapsed:
  a small round button, draggable, snaps to the screen edge, colour = state (idle / running / error),
  remembers its spot. Tap → a small panel: Laby / LOD, Start / Stop, 3-line status (fixed height, like
  the desktop Mini window), last kill, "Open app". Long-press → stop at once (F10 equivalent) **(my
  guess)**. The overlay must never cover what the pilot taps or reads — it stays out of the regions
  it uses, or hides for the frame **(my call)**.
- **Foreground notification** with a Stop button while the pilot runs (keeps the process alive and
  gives a second stop) **(my call)**. Keep the screen on while running **(my call)**. Ask for the
  battery-optimisation exemption on the setup page.
- **Android 13+ sideload note:** a sideloaded app's accessibility toggle is greyed out until
  App info → ⋮ → "Allow restricted settings". Android 12 does not need it. The setup page explains it.

**App pages (MAUI):** setup checklist (Accessibility on, notifications allowed, battery exemption,
install-unknown-apps for updates), mode + Start, the settings the desktop shows in its panel that
still apply, log view, debug pictures folder, kill alerts, Check for updates. Plain and small — the
floating control is where the player spends their time.

**OCR:** ML Kit Latin text recognition, bundled model (no download, works offline). Binding package
for .NET Android and its net10 compatibility: verify in M0. Same entry points as the desktop OCR
(region + upscale), so `TextMatcher`, `IsFullName`, `ZoneMatches` etc. work unchanged.

**Templates:** the phone UI differs in look (ring icons, sword buttons, ☰, map icon, beam on a
different minimap background), so the app ships its own set (`Assets/mobile/*.png`, crops from MuMu
frames), loaded by the same `TemplateLibrary` with the same learned-scale search **(my call)**. Shared
templates (e.g. the map row `>`) are reused where they score well.

**Storage:** settings JSON, logs and debug pictures in the app's external files dir
(`Android/data/<package>/files/...`, reachable over adb/USB) **(my call)**. Kill screenshots likewise.

**Updates:** the GitHub release for `vX.Y.Z` carries `WuxAuto-android.apk` next to the desktop zip;
`release.ps1` builds both; the app reads `releases/latest`, downloads the APK and hands it to the
package installer (Android always asks the player to confirm) **(my call)**. The APK must be signed
with **one stable release key kept outside the public repo** (e.g. `%LOCALAPPDATA%\WuxAuto\signing\`,
password from an environment variable); losing it means every phone must uninstall to update.
App id `io.github.atherosxd.wuxauto` **(my call)**.

## 6. How a run works on the phone

The phase machine is the desktop's (CLAUDE.md §4). Only the steps below change; everything not listed
(zone guard, restart on error, loop, gate banner, `WatchBoss`, LOD kill/respawn rules, Bohai dialog,
reconnect screens) runs as on PC with taps for clicks.

1. **Shortcut check (new, every Start):** find each needed shortcut on the ring by icon: Teleport
   Scroll, Pick up (LOD), AUTO BUFF, Auto-Hunt / HUNT, (Return Scroll if used). Missing + a free (+)
   slot → unlock the ring (lock icon), tap the (+), pick the entry, lock again **(user)**. **Never
   replace a filled slot (my call)**. Missing and no free slot → tell the player which one to add,
   stop.
2. **Key check:** in town, ☰ → Durations → read Temple of Fiends Key / Den of Rebirth counts → close.
   One at 0 → switch mode; both at 0 → Auto War — the desktop's rules **(user: Durations; my guess:
   read before each teleport)**. The desktop's "the teleport decides" rule stays as the fallback when
   the read fails.
3. **Teleport:** tap the Teleport Scroll shortcut → destination list (my guess: opens directly —
   verify) → same OCR search / paging / row tap as PC.
4. **Arrival:** wait for the zone title (as PC) → **pinch** to zoom out → face north.
5. **Face north:** same peak search on the minimap beam (phone template), with short horizontal
   swipes as the "taps" and shorter swipes for the fine-tune. Camera lock may change this — test it.
6. **Map / stage pick:** minimap map icon → same window → OCR the stage name → tap the row, then its
   `>`. Paging by swipe in the list if there are no arrows (verify).
7. **Boss select:** the big sword (Tab) selects the nearest target **with the ring**; check the ring
   (`FindSelectionRing`) + boss name; fallback: taps around the boss as the desktop's centre clicks.
   Stage 5's long desktop dance (skill spam, ring + attack-line checks, autohunt confirm) keeps its
   logic but should succeed on the first try far more often.
8. **Walk to stage 5 / LOD boss:** joystick steering (§5.3) with the desktop's coordinate checks,
   stall rules and waypoints.
9. **LOD:** target with the sword, skill wheel spam (or long press), Pick up spam for `LootMs`,
   AUTO BUFF every `BuffEveryMs`, Shura / stop-attack before buffing (verify how to stop an attack).
10. **Return home:** Teleport Scroll to the home town as on PC (Return Scroll as an alternative to
    verify) **(my call)**.

6.10 **PC-only, not ported** **(user agreed)**: Auto confirm; Mini window and F9/F10/F11 hotkeys
(replaced by the floating control and notification); Desktop Duplication / window-capture fallbacks;
`StartupCheck`, `Diagnose.cmd`; Ctrl + skill key presses.

Ported extras: auto reconnect, restart on error, zone guard, Bohai dialog cancel, autohunt off in
town, Run skill on in town, Auto War, kill alert (Android notification + the chime), kill screenshots,
debug snapshots + log, self-update. Font size / TAB target settings visit: only if the phone's
Settings window has those rows (verify; TAB target may not matter with the sword button).

## 7. Order of work (my call)

Each milestone ends in its own PR (the user's rule: every piece of work ends in a PR). Desktop
features keep landing on `main` meanwhile (several PRs a day from parallel sessions), so the desktop
milestones are **short-lived branches, rebased often, each merged before the next starts**; the big
move is mechanical and goes first so later conflicts stay small **(my call)**.

- **M0 — spike (throwaway, in MuMu), a go/no-go gate:** minimal MAUI Android app with the
  Accessibility service: takeScreenshot → saves a PNG (measure the real frame rate); joystick hold
  walks; a swipe turns the camera; a pinch zooms; a multi-stroke gesture (joystick + tap) works; ML Kit
  reads the minimap title and coordinates; Emgu loads and `MatchTemplate` runs on the device. Answers:
  3 fps enough or MediaProjection; ML Kit good enough or Tesseract; Unity accepts accessibility
  gestures. **Nothing on the desktop side starts before M0 passes.** If the game ignores accessibility
  gestures or capture, stop and go back to the user with the options (e.g. Shizuku / wireless-debugging
  `input`, which must be re-paired after every reboot) — no fallback is built without their OK
  **(my call)**.
  *After M0:* gate results and go/no-go recommendation are in `docs/superpowers/notes/2026-10-26-m0-gate-results.md`; the choices (MediaProjection, serialised touches, ML Kit) override the wording above.
- **M1 — shared core, move only:** create `WuxAuto.Core` and move the pilot, vision, settings and
  engine loop into it with the smallest seams needed to compile without Windows types (OCR, input,
  capture, paths, alerts behind interfaces; desktop implementations are today's classes). Still on
  OpenCvSharp; no behaviour change. Offline probes give identical output; the user does a live check.
- **M2 — Emgu CV + parity gate (§5.2)** inside Core. The user runs one live Laby and one LOD loop on
  the installed build before it merges.
- **M3 — actions + platform profile (§5.3):** the pilot calls actions; the desktop adapter maps each to
  exactly today's call. Desktop behaviour identical; user live check.
- **M4 — phone app, LOD first** (the user's main pain): service, capture, gesture scheduler, floating
  control, notification, settings/log pages, ML Kit adapter, phone templates, shortcut check, LOD flow,
  and an in-app "share debug bundle" (log + snapshots + settings) for phones without adb. Verified in
  MuMu.
- **M5 — Laby on the phone.**
- **M6 — extras + release:** key check via Durations, Auto War, reconnect, kill alert, restart on
  error, self-update, `release.ps1` building the APK, README section for phone setup.

The pilot does not detect the player's own touches in v1: the player stops it with the floating
control or the notification before using the phone **(my call)**.

## 8. Verification

- Desktop: the parity probe and existing probes (§5.2); `dotnet build .\WuxAuto.slnx`; the user's live
  runs (no automated stand-in exists for a live run). Never stop a running WuxAuto for a build
  (memory: the installed copy may be mid-run).
- Phone: MuMu over adb — screenshots before/after each step; then the user's Xiaomi. Debug snapshots on
  the phone with the same tags as the desktop (`fail`, `attack_s<N>`, …).
- Offline phone probes: SelfTest modes that run the Core detectors over phone frames (MuMu captures)
  so phone templates and regions can be tuned without the game, like `--frame` today.

## 9. Left to the builder

Project and namespace names; exact interface shapes; how `WuxEngine` splits between Core and the
desktop; gesture timings and swipe lengths (calibrate in MuMu); the phone settings section's fields;
the ML Kit binding package; whether to pin the SDK with `global.json` (MAUI on .NET 10 needs the
10.0.402 SDK with `maui-android`; the default SDK here is 11 rc2); page layouts of the MAUI app; how
phone test frames are stored locally (not in git).

## 10. To verify in MuMu before the dependent part is built

1. Teleport Scroll shortcut opens the destination list directly; list paging on the phone.
2. Where the combat log shows "Attack succeeded" and "Auto hunt started/ended" on the phone (many
   checks depend on those lines).
3. Pinch zoom: works through accessibility gestures; how much per pinch.
4. Swipe length → camera degrees; what Camera lock does.
5. Map list scrolling (swipe vs arrows).
6. Durations window: layout and wording of the key lines.
7. Phone Settings window: font size / TAB target rows.
8. How to reach the autohunt settings (Regular War) for Auto War.
9. Skill button long press = held skill?
10. How to stop an attack (no Space on the phone); Shura on the hotbar.
11. Run skill icon and its ring on the phone hotbar.
12. Gate banner, target bar and boss name positions on the phone layout.
13. Pick up: one tap picks everything nearby, or spam needed.
14. Ring shortcuts: tappable while locked; the unlock → (+) → picker → lock flow.
15. The floating control and the game's own HUD do not overlap at 1600×900 and at the Xiaomi's
    2608×1200.

Items that need a dungeon entry (2, 12, 13, and the Laby / LOD flows themselves) need the user's OK
first — they use the MuMu character's keys / entries, and the user may have to switch MuMu to a
character that can enter.

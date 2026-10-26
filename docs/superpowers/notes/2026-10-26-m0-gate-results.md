# M0 gate results (2026-10-26)

M0 asked one question: can an app on the phone capture the game, send touches, read text and match
pictures? This note holds the measurements and three decisions. The decisions are the controller's
**recommendations**. The user accepts or changes them.

## Setup

- Device: MuMu Player (ARM), Android 12.
- Resolution: 1600x900 at first. The user restarted MuMu (performance mode high), after which it ran
  at 1920x1080. All results below are from 1920x1080, except the first SHOT runs.
- Character: Piercing City, autohunt off, at 10362,13793 before the walk.
- Spike app: `spikes/WuxAuto.MobileSpike` (accessibility service plus a MediaProjection service).
  Actions are sent over adb as broadcasts.
- Security: the spike's broadcast receiver needs the `DUMP` permission (adb has it, ordinary apps do
  not) and clamps its inputs. The "ordinary app is refused" side was not tested.

## Results

| Check | What was run | Measured | Result | Evidence |
|---|---|---|---|---|
| SHOT | `AccessibilityService.takeScreenshot`, 20 shots (also with a CPU-side `HardwareBuffer` read) | About 2.3 fps. The platform allows one shot per ~333 ms (error 3 if faster). Most frames are pure black: 4 of 20 lit. The CPU read of the buffer is black too. `adb screencap` showed the game fine at the same time. Performance mode did not help. | FAIL | `b0.png`, `b1.png`, `shot_last.png` |
| SHOTMP | MediaProjection into an ImageReader (RGBA_8888), 20 shots | About 22.7 fps. Shots 1-4 were the launcher (the consent dialog backgrounded the game). Shots 5-20 were game frames, 8-190 ms apart. | PASS | `d2.png`, log `mp shots ok=20 fail=0 fps=22.7` |
| WALK | Joystick stroke up 2.5 s (centre 264,818, r 106) | 10362,13793 to 10157,13987 (about 282 units) | PASS | `c0.png`, `w_after.png` |
| SWIPE | 1320,384 to 1080,384 in 600 ms | Camera turned. Minimap beam about -130 deg to -81 deg. | PASS | `s_before.png`, `s_after.png` |
| PINCH | Two fingers from 360 to 96 px apart, centre 960,480 | Character height about 160 px to about 110 px (zoomed out) | PASS | `p_before.png`, `p_after.png` |
| TAP (control) | Tap-only gesture on the WORLD chat tab, then back to GENERAL | WORLD highlighted, then restored | PASS | `k_tabs.png` |
| MULTI | One gesture: joystick held 2.5 s plus a tap on WORLD at the half-way point | Joystick moved 285 units. Tap not delivered (WORLD not highlighted). | FAIL | `n_tabs.png`, `n_coords.png` |
| MULTIC | Continued stroke (`willContinue`) for the joystick plus a tap in the second gesture | Joystick moved 354 units. Tap not delivered. (The continued stroke is a 1 px move, close to a hold.) | FAIL | `q_tabs.png`, `q_coords.png` |
| OCR title | ML Kit Latin on the full 1920x1080 frame | `[Piercing Cityl` at 1671,65. 353 ms (first call includes model load). | PASS | `t3_ocr_match_run2.txt` |
| OCR coordinates | Same, full frame, then a x2 crop of the minimap (1560,55,340x70) | Full frame: `10158,13589`. The digits are right but ML Kit drops the spaces around the comma, so the desktop's pattern fails. Crop x2: `10158, 13589` in 78 ms. | PASS on the crop | `t3_ocr_match_run2.txt` |
| MATCH | Emgu CV `MatchTemplate` on the device, template cut from the same frame (58x50 at 1790,62) | Score 1.0000 at 1790,62 in about 90 ms. Logged as `selfmatch=path-check`, template std 74.4. | PASS as a path check | log line `match selfmatch=path-check ...` |

Notes on the results:

- MULTI and MULTIC: two simultaneous strokes do work (PINCH zooms the camera), and a tap alone works.
  So the game appears to ignore a UI-button tap while the joystick touch is held. It is not a
  `dispatchGesture` limit.
- The original MULTI target, Auto Buff, could not be used. It needs Force 90% or more ("Force 90%
  and over required") and the character is at 83%. A harmless target, the WORLD chat tab, was used
  instead.
- MATCH matches the frame against itself. It proves Emgu loads and runs on the phone. It does not
  show that a template survives a shift or a rescale. Plan 2 measures that on real templates.
- SHOTMP needs a one-time consent dialog per capture session. That dialog sends the game to the
  background, and it needed about 10 s plus an `am start` to come back to the front. Reinstalling
  the app loses the session, so consent is needed again.
- The two flat-template refusals in MATCH compile but were not triggered live.
- Screenshots named above stay in the session scratchpad. They are not in git.

## Decision 1: capture method

**Recommendation: MediaProjection.**

- Accessibility capture is black on MuMu in most frames, and the CPU-side read is black too, so it is
  not a copy problem in our code. It is also capped near 3 fps by the platform.
- MediaProjection gives real game frames at about 23 fps.
- Cost: the consent dialog appears once per capture session and backgrounds the game, so the app
  must bring the game back to the front (`am start` in the spike) and wait.
- Before discarding the accessibility route for good, check it again on the user's Xiaomi 17 Pro Max
  (HyperOS 3, Android 16) in Plan 2. The black frames look like an emulator GPU-readback problem and
  may not happen on a real phone. It is still capped near 3 fps.

## Decision 2: OCR engine

**Recommendation: ML Kit. Tesseract is not needed.**

- It read the zone title on the full frame and the coordinates on the minimap crop, both quickly.
- Plan 2 note: ML Kit drops the spaces around the comma in the coordinates. Use the minimap crop
  (the desktop also reads a crop) and relax the pattern to `\d{3,6}\s*,\s*\d{3,6}`.

## Decision 3: gestures accepted

**Recommendation: yes for the joystick, swipe, pinch and single taps.**

- A tap while the joystick is held is not delivered, in either form tried.
- The gesture scheduler therefore serialises touches: end the joystick step, tap, resume. The pilot
  already walks in discrete steps, so this fits. In LOD, the Tab press during a walk becomes a tap
  between two steps. The cost is a little walking latency per tap.
- Spec section 5.4 assumed "joystick held plus a tap" in one gesture. That assumption is dropped.

## Go / no-go

The plan treats screenshot, walk or multi failing as a no-go. Screenshot passes through the
MediaProjection fallback and walk passes. MULTI fails as specified but has a workaround. So the
call is the user's.

**Recommendation: GO, with the two changes above (MediaProjection capture, serialised touches).**

## What Plan 2 must carry

- Capture through MediaProjection: the consent flow, a foreground service of type `mediaProjection`
  (permissions `FOREGROUND_SERVICE` and `FOREGROUND_SERVICE_MEDIA_PROJECTION`), and bringing the game
  back to the front after the dialog.
- A content check on frames. A launcher frame is not black (lit 0.188) and passes a "not black" test.
  The zone title read is the guard.
- Serialise touches in the gesture scheduler (no joystick plus tap in one gesture).
- OCR on crops, with the relaxed comma pattern.
- Positions measured per resolution, never hardcoded. MuMu changed from 1600x900 to 1920x1080
  mid-spike, and the user's phone is 2608x1200.
- Re-check accessibility capture on the Xiaomi 17 Pro Max before dropping it there.
- Measure template robustness (shift, scale) with real mobile templates. MATCH here was a self-match.
- Test the `DUMP` permission lock from an ordinary app (only the adb side was verified).

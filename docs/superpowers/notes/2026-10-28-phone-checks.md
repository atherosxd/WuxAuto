# Phone checks in MuMu (2026-10-28, Plan 2 Task 1)

MuMu Player ARM, Android 12, **1920x1080 landscape**, game `com.wuxen.wuxen2`, character in Piercing City
the whole time (no teleport picked, no dungeon entered, nothing consumed). All input over adb: `input tap`
for window exploration, the spike's accessibility gestures (`SEQ`, `DRAG`, `SWIPE`, `PINCH`, `WALK`) for
everything measured. Frames: `%LOCALAPPDATA%\WuxAuto\phone-frames\<screen>_1920x1080.png` (local only).
Positions are `x,y` px at 1920x1080 and `(fx, fy)` = fractions of width / height. Crop boxes are
`frame: x,y,w,h` (±3 px; Task 4 checks them by eye).

**Read first — findings that change the plan:**
1. **Turn sign is the opposite of the provisional one:** the finger moving LEFT turns the camera LEFT.
2. **The map's monster list does not scroll by swipe.** It has PC-style arrows above "Spawns"
   (up arrow left, down arrow right, each only drawn when there is something to scroll to). The desktop's
   header-anchored arrow logic (§5.4) carries over; `ScrollList` is not the map's paging.
3. **The skill wheel (1/2/3) is empty on this character**, so skill long-press (#9) and the Shura stop (#10)
   could not be checked. The pilot needs a skill on the wheel; the shortcut check should report an empty
   skill button by name.
4. **Autohunt settings (#8) not reached.** Both ☰ and the ring picker have "Auto-Hunt (J)", the picker also
   "HUNT (J: sta…)". Neither was tapped: in town a J toggle starts the Smart Farm journey (desktop §4.1
   AutohuntOff), which moves the character. Needs the user to say which entry opens the settings.
5. The ring was found **unlocked with all 14 slots empty** (not the M0 state). Empty slots show **(+) only
   while unlocked**; locked, they are blank circles.
6. Ring scaling differs: the Run skill's "on" ring sits at 1.12–1.22 × the icon radius (desktop
   `RingFraction` measures 0.68–0.85 of a 33 px box) — the phone needs its own ring band.

## 1. Joystick release → tap gap (`TapSettleMs`)

New spike action `SEQ` (separate gestures back to back on the device, each awaited on its completion
callback, gaps from logcat). Each run: `WALK 1.5 s; wait g; TAP chat tab; wait g; WALK 1.5 s` (both walks
the same direction), the tab highlight read from a frame afterwards; WORLD 803,876 / GENERAL 698,876
alternated so every run had to change the tab.

| release → tap gap (measured) | tap delivered |
|---|---|
| 0 ms (2 runs) | no, no |
| 21 ms (2 runs that could show it) | no, no |
| 38 ms | no |
| 41 ms | yes |
| 51–53 ms (3 runs) | yes ×3 |
| 101 ms (2) | yes ×2 |
| 153 ms | yes |
| 302 ms | yes |

- Tap → next joystick step with a **0 ms** gap: delivered every time (each double walk moved 111–128 units,
  a single 1.5 s step 55–60 units).
- Gesture timing on the device: a 100 ms tap completes in 101–103 ms, a 1500 ms joystick step in
  1502–1504 ms; dispatch overhead ≈ 1–2 ms.
- The threshold (~40 ms) is about one game frame: MuMu shows the game at **FPS 15** (~67 ms/frame).
- **`TapSettleMs` = 100** (2× the threshold; a real phone at 60 fps should need less, keep 100).
- Walk speed in town on foot: ~37–40 units/s (M0's 282 units in 2.5 s was faster — mounted then?).
- Joystick centre **262,816 (0.136, 0.756)**, radius ≈106 (0.055 w) — matches M0.

## 2. Camera: turn, tilt, zoom

### Turn (spec #4) — method

Joystick "up" walks along the camera's forward direction, so the heading is the minimap coordinate change of
a 2 s step up (and the reverse of a 2 s step down; the two agreed within 1–3°). Heading before and after each
drag → degrees turned. All drags at y = 356 on the right band unless stated.

| gesture | length | turned | per try |
|---|---|---|---|
| quick swipe, 180 ms, no hold | 240 px | **35.0°** | 34.7, 35.4, 34.7, 35.1, 35.2 (very repeatable) |
| swipe 600 ms, no hold | 240 px | 45° | 42.6, 47.0 |
| swipe 1000 ms, no hold | 240 px | 41.3° | 1 try |
| drag 800 ms, no hold | 240 px | 34° | 35.8, 31.6 |
| **drag 800 ms + 100 ms hold** | 240 px | **46.6°** | 48.0, 42.7, 49.6, 43.4, 48.2 (both directions) |
| drag 400 ms + 100 ms hold | 120 px | 24.8° | 1 try |
| swipe 300 ms, no hold | 120 px | 18° | 21.2, 15.4 |
| swipe 300 ms, no hold | 60 px | 7.9° | 1 try |
| drag 300 ms + 100 ms hold | 26 px | ~1–2° | 1.1, 2.3 (dead zone) |

- **Direction: finger moves LEFT → camera turns LEFT** (facing south, the heading moved towards east);
  finger right → turns right. Checked in every run above.
- **Drag + end hold:** ≈ 0.21 °/px after a ~20 px dead zone: `px ≈ 20 + 4.8 × degrees`. Lifting without
  the hold loses part of the move (the game reads the finger at 15 fps); the hold makes it count.
- **No overshoot / no spinning:** the camera never moved after the finger lifted; fast swipes turn *less*,
  not more. Spread for the drag+hold: ±3° on ~47°.
- **Centre-ground drag** (840→1080 at y 378 = (0.44–0.56, 0.35)): turned 44.4° right, no walk (coordinates
  unchanged), nothing targeted. The right band and the centre behave the same; the right band keeps the
  finger off characters and mobs (a drag does not select, a short drag that ends up a tap might).

### Turn swipe area (user: "adjust the POV on the right side")

Clear band at 1920x1080: **x 1150–1690 (0.60–0.88), y 205–510 (0.19–0.47)**. Excluded around it: the
pet/target panel 1282–1550 × 60–175, the minimap 1568–1897 × 60–390, the event text 1730–1900 × 400–525,
the potion orbs from y ≈ 535, the sword / skill wheel below. Measured centre: **1420,356 (0.74, 0.33)**.
Drags that *started* on the pet panel (y 150) were ignored.

### Tilt (vertical drag)

- Finger **up** = camera drops towards the horizon (looks further ahead; 300 px up from the default view went
  all the way to a ground-level camera looking at the sky). Finger **down** = camera rises / looks down.
  Same sign as the desktop's right-drag up (`TiltCamera` negative pixels = finger up). Pitch and camera
  distance move together.
- Three finger-down drags at x = 1420 (0.74) did nothing while the camera was at its lowest pitch; the same
  drag at x = 1250 (0.65) worked at once. Cause unknown — **do vertical drags at x ≈ 0.65 w**, y 0.19–0.47.
- 100 px finger up from the highest pitch gave a moderate tilt (`camera_highpitch` → town view). The
  desktop's 60 px `CameraTiltPx` value needs a dungeon to tune (Task 8).
- The camera was put back to a normal town view afterwards.

### Pinch (spec #3)

Two fingers 360 → 96 px apart (zoom out) or 96 → 360 (zoom in), 400 ms, centre 960,300. Character height
≈100 px → ≈170 → ≈400 px (clamped) over two zoom-in pinches, and back ≈170 → ≈110 over two zoom-out
pinches: **about ×0.5 per pinch, the full range is 2–3 pinches.** 3 pinches (15 notches / 5) reach full
zoom-out. Frames `zoom_in`, `zoom_out`.

## 3. Screens and positions (town, 1920x1080)

### HUD (frame `town_clean`, `hotbar`)

| element | position (px) | fraction | crop box (frame: x,y,w,h) |
|---|---|---|---|
| ☰ menu | 1188,28 | (0.619, 0.026) | town_clean: 1158,2,62,54 |
| zone title / coordinates (minimap) | 1730,77 / 1730,103 | (0.901, 0.071) / (0.901, 0.095) | OCR crop 1560,55,340,70 ×2 (M0) |
| minimap map icon | 1818,87 | (0.947, 0.081) | town_clean: 1795,66,46,44 |
| minimap close X — **never tap** (hides the minimap) | 1872,85 | (0.975, 0.079) | |
| ping / FPS text ("98ms — FPS: 15", "--ms" seen) | 1610–1740, 29 | (0.84–0.91, 0.027) | |
| target / pet panel | 1282–1550 × 60–175 | | |
| big sword (Tab / target nearest) | 1655,815, r≈118 | (0.862, 0.755) | town_clean: 1560,720,190,190 |
| AUTO BUFF crossed swords | 1836,662 | (0.956, 0.613) | ring_locked: 1790,616,92,92 |
| skill 1 / 2 / 3 (empty here) | 1836,866 / 1788,950 / 1705,997 | (0.956,0.802) / (0.931,0.880) / (0.888,0.923) | needs a character with skills |
| Run skill orb (flame) | 1430,760, icon r≈59 | (0.745, 0.704) | runskill_off: 1371,701,118,118 |
| potion orbs "999" | 1493,648; 1606,588; 1735,596; 1433,888; 1505,996 (r≈55) | | |
| hotbar 8 slots, y 1022 | x 680, 760, 840, 919, 998, 1077, 1157, 1236 | band (0.333, 0.907, 0.365, 0.079) | |
| hotbar slot 1 (blue burst on a sword — Shura candidate, not verified) | 680,1022 | (0.354, 0.946) | hotbar: 646,988,72,70 |
| hotbar lock (leave alone) | 1314,1022 | (0.684, 0.946) | |
| chat tabs, y 876 | GENERAL 698, WORLD 803, CLAN 906, GUILD 1012, PARTY 1114, WHISPER 1220 | | |
| chat box | 640–1280 × 680–960 | | |
| system lines (PvP lucky tickets, "… Succeeded") — M0 saw the combat log here too | 20–540 × 225–470 | (0.01–0.28, 0.21–0.44) | |
| event text (EXP x 2 …) | 1730–1900 × 400–525 | | |

### Shortcut ring (spec #14; frames `town`, `ring_picker`, `ring_unlocked`, `ring_locked`)

- 14 slots around the joystick, centres: 216,612; 286,608; 352,628; 410,668; 450,726; 470,792; 466,862
  (right arc, top to bottom) and 152,640; 100,688; 66,748; 55,818; 66,885; 100,946; 152,995 (left arc).
  Slot radius ≈28. Lock icon **438,928 (0.228, 0.859)**.
- **Unlocked** (open padlock): empty slots show (+); a tap on (+) opens the picker at once. **Locked**
  (closed padlock): empty slots are plain blank circles, no (+).
- Picker: a panel titled "MENU" at 717–1200 × 10–1065, X at 1164,38; 5 columns x = 768, 861, 953, 1046, 1139;
  icon rows y = 97, 210, 322, 435, 547, 660, 773 (label ≈ +50). Entries in order: CLEAR, HUNT (J: sta…),
  AUTO BUFF, **Pick up**, Gold Scroll / Camera lock, Rage Burst, Return Scroll, **Teleport Scroll**,
  Settings (ESC) / Stat (C), Inventory (I), SKILLS, Mount, Costume / Auto-Hunt (J), Auto-Potion, Personal
  Shop, Consignment Shop, Friend (F) / Guild (G), Tribe Status, Level Bonus, Item Mall, Leader Vote /
  Daily Reward, Rank Buff, Battle Pass, Ranking, Farming Guide / Durations. A tap on an entry fills the slot
  and closes the picker. Not seen: a "stop attack" entry.
- Done here: **Pick up** → slot 100,688 and **Teleport Scroll** (shows its stack count "999") → slot
  66,748, then the ring was **locked**. Shortcuts work while locked (the Teleport Scroll was tapped locked).
- Crop boxes: (+) slot `town: 124,612,56,56`; open lock `town: 410,900,56,56`; closed lock
  `ring_locked: 414,904,48,48`; Pick up `ring_locked: 76,664,48,48`; Teleport Scroll (without the count)
  `ring_locked: 44,724,44,38`; empty locked slot `ring_locked: 124,612,56,56`.

### Teleport Scroll (spec #1; frames `teleport_list`, `_p2`, `_p3`)

- The ring shortcut **opens the destination list directly**; nothing consumed (still 999). No inventory.
- Window 760–1160 × 155–960, title "TELEPORT SCROLL:" 960,177, close X **1121,182**. Pager "1 / 3" at 960,257,
  `<` 856,257, `>` **1064,257** (dimmed on the last page). 9 rows per page centred x 960, y = 300 + 36.6·k.
- Page 1: Piercing City … Heavenly Garden. Page 2: Bohai Outpost (Adept), Highlands (Adept), Coliseum, ATTR
  Dungeon 5 (B.Dragon), Maya Cliff, **[The Labyrinth (Adept)] (row 6, y ≈ 482)**. Page 3: the (WD) zones.
  This character has 0 Den of Rebirth entries and no LOD row on page 2 — fits the desktop's "page 2
  without it = out of entries" rule.
- Paging is the `>` tap (the desktop's `rightchevron` path), not a swipe.

### Map window (spec #5; frames `map`, `map_long_top`, `map_long_down1`, `map_long_drag300`)

- Window 272–1645 × 25–1062; "MAP" 960,48; close X **1608,52**. Zone name with `<` 328,102 / `>` 1061,102
  browses other maps (info only; a "Current map" button 1477,104 then appears and returns).
- "Monsters on this map" 1228,175; headers Lv 1457,205, **Spawns 1522,205** (text ≈1493–1552 × 197–213,
  h ≈ 16). Rows y = 253 + 63.4·k (11 visible), names from x 1157, row `>` x 1592.
- **Paging: arrows only.** Down `v` ≈ **1583,185** (drawn while rows are hidden below), up `^` ≈ **1520,186**
  (directly above "Spawns", drawn once scrolled). One `v` tap moved 2 rows (to the end of a 13-row list).
  A 300 px drag + hold and a 400 px swipe in the list moved nothing. Crops: `map_long_top: 1566,176,36,20`
  (down), `map_long_down1: 1503,176,36,20` (up).

### Durations (spec #6; frame `durations`)

☰ → Durations (menu grid 1571,565). Window 722–1197 × 155–895, title "Durations" 960,177, X **1158,182**,
pager "1 / 1" 960,257 (`<` 856, `>` 1064). Lines from x 808, y = 299 + 36.5·k, item icon at x ≈ 785:
`[Temple of Fiends Key] 15 (time(s))`, `[Sky Hunt Scroll] 99.99.9999`, `[Unattended Sales Edict] 27.09.2027`.
No Den of Rebirth line = 0 entries (same "missing line = run out" rule as the desktop's item list).
Opening a ☰ entry closes the menu.

### ☰ menu (frame `menu`)

Panel 1428–1910 × 10–1065, X 1873,38. 5 columns x = 1478, 1571, 1664, 1757, 1849; icon rows y = 97, 210, 322,
435, 547 (label ≈ +50). Settings (ESC), Stat (C), Inventory (I), SKILLS, Mount / Costume, Auto-Hunt (J),
Auto-Potion, Personal Shop, Consignment Shop / Friend (F), Guild (G), Tribe Status, Level Bonus, Item Mall /
Leader Vote, Daily Reward, Rank Buff, Battle Pass, Ranking / Farming Guide, Durations. It covers the minimap.

### Settings (spec #7; frames `settings`, `settings_visual`, `settings_visual2`, `settings_visual_scrolled1`)

- Window 672–1247 × 135–945, X **1209,162**. Tabs at y 224: SOUND 755, **VISUAL 890**, IGNORED 1026,
  ACCEPTANCE 1162. **Opens on SOUND every time.** Bottom: SAVE 795,889, CHARACTER SELECT 958,889, EXIT
  1123,889 (never tap the last two).
- Rows: label x 724, value centre x 1092, `<` x 1000, `>` x 1183; first row y 287, pitch 42, 14 visible.
  VISUAL: Language, Interface scale, **Font size (120 %, `>` dimmed = at max)**, Particle effects, Field of
  view, Shadow, FPS (60), Quality preset, Name tags, HP/stat bars, Skill effects, Battle mode visual,
  Brightness, Color filter, Crowd limit, **TAB target (Default)**, Violence mode. TAB target needs the list
  scrolled (3 rows hidden). Both desktop rows exist, so the FontSize / TAB target visit can be ported.
- List scrolling by drag works here: finger **up** shows lower rows. 84 px drag + hold → 44 px (one row);
  300 px → the list end (clamped). Roughly content ≈ finger − 40 px.

### Run skill (spec #11; frames `runskill_off`, `runskill_tap1`, `runskill_on`)

- The flame orb 1430,760 (the same icon is also hotbar slot 7, 1157,1022). Off: plain orb. Tap → **on: four
  gold arcs** at r 66–72 px from the centre (icon r ≈ 59), colour RGB 185,161,107 (OpenCV HSV ≈ 20,107,185 —
  inside the desktop's gold H 15–30, S 60–150, V ≥ 150). The hotbar copy shows no ring. Tapped again → off
  (left as found).

### Skill long-press, Shura, stop attack (spec #9, #10)

- Wheel slots 1/2/3 carry no skill on this character; a 3 s press on slot 1 showed nothing. **Unverified.**
- Shura: hotbar slot 1 shows a blue burst on a sword — possibly Shura (the desktop `shura.png` is a blue
  burst); not tapped (it would cast). How to stop an attack on the phone is **unverified**.

### Bohai WAR dialog (frame `war_dialog`)

Popped up by itself in town: box 652–1268 × 447–632, "WAR", `[Bohai Outpost (Adept)] Confirm to move?`,
**CANCEL 853,589**, OK 1065,589; the screen behind is dimmed. Cancelled. The desktop's Bohai cancel rule
(`CancelDialogText` "Bohai", the Cancel word near the line) applies as is.

## Decisions for TouchControls

| provisional item | decision |
|---|---|
| Turn(Left) = finger swipes RIGHT | **Changed: Turn(Left) = finger moves LEFT** (camera turns with the finger). Flip `sign` in `TurnSwipe` and `Turn_Left_FingerSwipesRight`. |
| Turn swipe at (0.5 w, 0.35 h) | **Changed: drag on the right band**, centre **(0.74 w, 0.33 h)**, band x 0.60–0.88, y 0.19–0.47. The centre ground also turns (no walk, no target), but the right band is what the user asked for and stays off mobs. |
| Turn gesture | **Drag, not a quick swipe:** move at ≈ 0.3 px/ms (duration = px / 0.3, at least 150 ms), then **hold 100 ms** before lifting. `px ≈ 20 + 4.8 × degrees`; drags under ~40 px barely turn. Needs a hold in the seam (`ITouchInput.Swipe` has none: add `holdMs` or a `Drag` method — Task 2/3 change). A quick 180 ms swipe is more repeatable (35.0° ±0.4 per 240 px) but turns less and is not what the user asked for. |
| ScrollList(n < 0) = finger swipes UP | **Confirmed for scrollable lists** (Settings: finger up shows lower rows). **But the map list does not scroll by swipe**: page it with the arrows above "Spawns" (taps), as the desktop already prefers; never fall back to a swipe there. |
| Has(RunSkillKey) = icon found | **Confirmed**: the Run skill is a player-placed orb (here 1430,760) and/or a hotbar slot; match the flame icon. Ring = on (above). |
| Has(ShuraKey / StopAttack) = icon found | **Kept, unverified**: Shura is on the hotbar on this account (candidate slot 1); no stop-attack shortcut exists in the picker. Task 8 decides how an attack is stopped. |
| Has(OpenInventory / ToggleSettings / OpenAutohuntSettings) = ☰ found | **Confirmed for Inventory, Settings, Durations** (☰ grid above; Settings and Inventory can also be ring shortcuts). **OpenAutohuntSettings unverified** — which entry opens the autohunt settings is for the user (finding 4). |
| ZoomOut: one pinch per 5 notches, ≥ 1 | **Confirmed**: 360 → 96 px, 400 ms ≈ ×0.5 zoom; 15 notches → 3 pinches = full zoom-out. |
| TiltCamera keeps the desktop sign (negative = finger up) | **Confirmed sign** (finger up = camera towards the horizon). Do vertical drags at **x ≈ 0.65 w** (at 0.74 w they were ignored at the lowest pitch); same drag + 100 ms hold. Amount: Task 8. |

## PhoneSettings values

| setting | value | from |
|---|---|---|
| `TapSettleMs` | **100** | tap lost at ≤ 38 ms, delivered from 41 ms (§1) |
| `SwipePxPerTurnMs` | **0.5** px per ms of desktop turn | desktop 180 ms ≈ 15° → 20 + 4.8 × 15 ≈ 92 px; 92 / 180 ≈ 0.5 (= 4.8 px/° plus the 20 px dead zone folded in at the 180 ms tap). The 40 ms fine-tune gives 20 px, inside the dead zone → floor turn drags at 40 px (≈ 4°). |
| `ListSwipePx` | **170** | Settings list: content ≈ finger − 40 px, 3 rows = 126 px → ≈ 170 px per 3 notches (not for the map list) |
| `PinchFromGap` / `PinchToGap` / `PinchMs` | **360 / 96 / 400** (unchanged) | ×0.5 per pinch (§2) |
| `TapHoldMs` | 60 (unchanged; the tests used 100 ms taps, both fine) | |
| `JoystickStepMs` | 900 (unchanged; not measured here) | |
| `FloatingX` / `FloatingY` | **0.40 / 0.03** | §Floating control spot |

## Floating control spot (spec #15)

**Top strip, x 640–1110 (0.33–0.58), y 0–55 (0–0.05): suggested centre (0.40, 0.03) = 768,32, ≤ 56 px.**
Nothing the pilot reads or taps is there: the level/HP block ends at x ≈ 445, the red buff icon at
537–592, ☰ starts at 1158; system lines start at y 105 (PvP banner) / 225. Open windows: the map's empty
title bar spans that strip (fine, nothing is read there); Settings, Durations, the teleport list and the
WAR dialog start below y 135. MediaProjection captures the bubble too, so it must stay out of every crop.
Second choice: the left edge x 0–40, y 480–580 (between the system lines and the ring). The Xiaomi's
2608x1200 is not checked here.

## Needs a dungeon (Task 8)

- #2: where "Attack succeeded…" and "Auto hunt started/ended/stopped" show (M0: top-left), and their wording.
- #12: gate banner, target bar, boss name positions; the sword's selection ring.
- #13: Pick up — one tap or spam.
- #9 / #10: skill long-press = held skill, Shura / stopping an attack (needs skills on the wheel and a target).
- The autohunt settings entry (#8) and the J toggle's town behaviour on the phone (needs the user's OK first).
- Face-north / LOD-south beam template on the dungeon minimap; px per degree re-checked against the beam.
- `TiltCamera` amount for AimGate; walk speed and stall rules in the dungeon; a teleport row tap.

## Spike changes

`spikes/WuxAuto.MobileSpike`: actions `SEQ` (`--es steps 'W:dx:dy:ms[:jx:jy:jr];G:ms;T:x:y'`, gestures back to
back with timings in logcat) and `DRAG` (`x1 y1 x2 y2 ms hold`: a move, then a continued 1 px stroke for
`hold` ms). `spikes/mumu.ps1` points at MuMu's current address `10.0.2.80:5555`.

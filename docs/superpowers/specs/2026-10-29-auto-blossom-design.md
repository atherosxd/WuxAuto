# Auto Blossom - design

User request 2026-10-29. A fourth mode beside Auto Laby, Auto LOD and Auto War: `RunMode.AutoBlossom`.
The character teleports to **Blossom Garden**, lines the camera up on two red pillars, then walks a fixed
route of 17 waypoints for ever (1 -> 5 once, then 6 -> 17 -> 6 ...). On the way it presses Tab; a targeted
**[S]Jaso Practitioner** (or any target showing the boss chest) is attacked until it dies, looted, and the walk
carries on.

Marked **(user)** = the user's words; **(my call)** = a choice made here, open to correction.

## 1. Why

The user farms the Jaso Practitioners in Blossom Garden. The map has an **AFK checker (user)**, so the character
must keep moving: never stand still at a waypoint for more than ~5 s. The checker **kicks the character to town
(user)** - the zone guard's "in town -> teleport in again" covers it, no dialog to answer.

The minimap there is readable on the user's frame: title "[Blossom Garden", coordinates "19148 , 19062" (the
arrival, ~600 south of waypoint 1).

## 2. Goals / non-goals

Goals
- Teleport in from town (scroll, page 1, "Blossom Garden") and confirm the zone by the minimap title.
- Zoom out, turn to the pillars, walk the route, kill Jaso Practitioners met on the way, loot, rebuff on a timer.
- Loop until Stop; recover from a return to town (teleport in again) and from restarts / re-logins like LOD.

Non-goals
- No autohunt (off the whole time - the town check already turns it off).
- No kill chime / notification per kill (a Jaso dies every minute or so - it would be noise) **(my call)**.
- No HP-based death detection (Auto War's); a death is caught by the coordinate jump, as in LOD.
- No out-of-entries logic: Blossom Garden is assumed always reachable (not a key / entry map). If the teleport
  leaves the character in town, the LOD rule applies: "Still in <town> after the teleport" -> fail -> restart.

Anti-goals
- Standing still long enough to trip the AFK checker.
- Attacking other monsters or players (Tab cycles through everything near; only a Jaso or a chest target counts).

## 3. Flow

### 3.1 Town -> Blossom Garden (shared steps)
Same as LOD: Focus -> CheckZone -> AutohuntOff -> RunSkill -> FontSize (TAB target Default, like Laby/LOD)
-> OpenInventory -> FindScroll -> FindDestination with `Blossom.Destination` "Blossom Garden" on page
`Blossom.ListPage` **1 (user)** -> WaitForLoad -> ZoomOut (waits for "Blossom Garden" on the minimap
title, `ZoneMatches`, then Ctrl + wheel `Dungeon.ZoomOutNotches`). The Regular War untick before Laby does not
run (Laby only). Destination for this mode = `Blossom.Destination` (the `Destination` property).

### 3.2 Turn to the pillars (`BlossomTurn`)
**(user)** Tap **D** for **100 ms**, up to **7** taps, until the two red pillars are in view, the camera looking
at the middle between them (`war-inputs\blossom_pillars_view.webp`: one pillar top-left, one top-right, the
character centred under the gap).

- Template `blossompillar.png` in `Assets`: the left pillar's grey base (96x42) cropped at 788,270 from the user's
  zoomed-out lined-up frame below. The user's first crop (`redpillar.png`, 174x103) came from a closer camera,
  about 1.4x larger than the zoomed-out view.
- The user's lined-up frame (`war-inputs\blossom_lined_up.png`, 2000x1168 window incl. a ~44 px title bar,
  zoomed out, 2026-10-29): the left pillar is dark red, the right one green-tinted (a glow over it) - the grey
  stone bases look alike. Bases at x ~835 and ~1245, y ~290 (~0.22 h of the client); ~410 px = 0.205 w apart;
  midpoint 0.52 w. The bases there are ~90 px wide vs ~130 px in the crop, i.e. a scale of ~0.7, at the edge of
  the learned-scale range - so the pillars are matched at any scale (the `FindIconAnyScale` way the searchlight
  is), in the band (0.25, 0.05, 0.50, 0.45) (above the character, left of the minimap panel), up to 4 hits,
  score >= `Blossom.PillarMatchScore` 0.70 **(my call; the `--pillars` probe on that frame sets the real
  number before it ships)**.
- Lined up = two hits on about the same row (|dy| < 0.05 h), 0.10-0.40 w apart, midpoint x within
  `Blossom.PillarCenterTolerance` 0.06 w of the client centre **(my call, from the frame above)**.
- The arrival heading is the same every time **(user)**, so every teleport needs the D taps; the searchlight is
  hard to see on this map **(user)**, so the pillars are the only heading reference.
- After each tap: a fresh frame (~300 ms), check. Lined up -> walk.
- Seen but off centre (two pillars, midpoint not centred): fine-tune like the Laby searchlight's
  `TickRefineNorth` - 40 ms taps of D while the midpoint gets closer to the centre, undo the tap that did not
  help, then try A the same way; at most 6 taps per key **(my call)**. The direction D turns the view is
  not assumed.
- Not lined up after the 7 user taps: keep tapping D (100 ms) up to 50 taps in all (a full turn) and line up on
  the best view seen, like the Laby searchlight; nothing ever seen -> warning + snapshot `blossom_no_pillars`,
  walk anyway (the heading is learnt from movement, §3.3) **(my call)**.
- Tab is not pressed while turning.

### 3.3 Walk the route (`BlossomWalk`)
Waypoints **(user)**, world x, y:

| # | x | y | | # | x | y |
|---|---|---|---|---|---|---|
| 1 | 19136 | 18463 | | 10 | 12987 | 13184 |
| 2 | 15923 | 18292 | | 11 | 13557 | 12775 |
| 3 | 15916 | 16184 | | 12 | 14101 | 12622 |
| 4 | 14883 | 16026 | | 13 | 14546 | 12826 |
| 5 | 13953 | 15970 | | 14 | 14837 | 13243 |
| 6 | 13947 | 14697 | | 15 | 14923 | 13591 |
| 7 | 13370 | 14493 | | 16 | 14838 | 14013 |
| 8 | 13085 | 13906 | | 17 | 14359 | 14565 |
| 9 | 12944 | 13692 | | | | |

Order: 1, 2, 3, 4, 5, then 6 .. 17, then 6 .. 17 again for ever (`Blossom.LoopFrom` = 6, 1-based) **(user)**.
Settings: `Blossom.Waypoints` (list of "x,y"), `Blossom.LoopFrom`, `Blossom.WaypointTolerance` 60 (Laby's) **(my call)**.

Steering: the LOD walk (SimpleSteer: a click from the centre towards the waypoint every `Dungeon.RunDashWaitMs`,
`LearnHeadingError` corrects each step, shorter clicks when stalled).
- **Heading base**: which world direction screen-up points to after the pillar turn is not known. The first click
  goes straight up the screen; the coordinate change of that step sets `_headingBase` (atan2 of the movement),
  then the usual heading fix takes over **(my call)**. If the pillar heading turns out constant, it can be
  pinned later as a setting.
- Waypoint reached (within tolerance) -> the **waypoint pause**: stand and Tab for at most
  `Blossom.WaypointWaitMs` **3000** (user: "5 s or less", AFK checker) -> next waypoint.
- Stalled (< 20 units moved) 8 clicks in a row on one waypoint -> warning, skip to the next waypoint
  **(my call)**. 40 unreadable coordinate frames in a row -> fail (restart), as RunToTarget.
- **Tab while walking** every `Blossom.TabEveryMs` 1000 **(user: "keep tabbing")**.
- Start point: after the teleport / a restart / a death, the walk starts at the **nearest waypoint** to the
  character, then follows the order (the arrival is expected near waypoint 1; Start inside the map resumes
  where it is) **(my call)**. A nearest waypoint inside the loop continues the loop.

### 3.4 Target check (every walk / pause tick)
One whole-screen OCR, the LOD rule (`ReadLodScreenFullAsync` logic with the Blossom name): targeted =
a short line in the **target bar** (top-centre band) reading `Blossom.TargetName` "[S]Jaso Practitioner"
(fuzzy, the "[S]" may read as "[5]" or drop - match on "Jaso Practitioner", >= 0.85) **or** the target bar's
chest (`monstericon`, >= 0.80) **(user: "or until there is a chest on top, similar to a boss"; the Jaso
Practitioner, a normal monster, shows the chest beside its name)**. Another target without the chest (a different
monster, a player) does not count - Tab again next time. Targeted -> stop walking
(no more clicks) -> fight.

### 3.5 Fight (`BlossomAttack`)
**(my call, the user did not object)**: as LOD's attack without the walk-up / ring parts: the skill
(`Dungeon.SkillKey`) every `Lod.SkillEveryMs`, with Ctrl + skill when `Dungeon.CtrlSkill` (shared), held when
`Lod.HoldSkill`. The skill makes the character run to the target.
- Dead = the target (name and chest) gone for `Blossom.TargetGoneMs` 2000 (as LOD's `LootAfterMissingMs`).
- Coordinate jump > `Lod.TeleportJump` (400) during the fight = died -> wait to settle, pillar turn, nearest waypoint.
  Where a death puts the character is unknown **(user: never seen - AFK or a disconnect come first)**; a death that
  lands in town is the zone guard's town rule, a re-login is the reconnect.
- Fight with the target never gone for `Blossom.FightMaxMs` 120000 -> warning, Tab, back to the walk (a target out
  of reach would otherwise hold the character still) **(my call; user: a Jaso dies within 2 min even on an HP build)**.

### 3.6 Loot (`BlossomLoot`)
**(user)** 1 s after the kill (`Blossom.LootDelayMs` 1000), spam `Lod.LootKey` every 200 ms
(`Blossom.LootEveryMs`) for 5 s (`Blossom.LootMs` 5000). No Tab while looting.

### 3.7 Rebuff (`WarBuff` reused)
**(user: "same rebuff logic as auto war")**. Auto War's buff steps (autobuff known? item list -> AUTO BUFF ask ->
`O` + item buffs, or the buff / weapon / item icons on the skill bar, only the ones ticked in Settings > Auto War >
Buffs). Shared settings: `War.BuffIcons` / `WeaponBuffIcons` / `ItemBuffIcons` / `DisabledBuffs` /
`BuffEveryMs` (5 min 45 s) / `BuffClickGapMs` / `IconMatchScore`, `Lod.BuffKey`.
- When: on arrival (before the pillar turn) and after a death; then after a loot, **only when the timer is due**
  **(my call; the user can ask for a rebuff after every kill)**. Never mid-fight, never mid-walk-step: a due
  rebuff waits for the next loot or waypoint pause, at most 30 s overdue, then it runs at the next walk tick.
- After the buffs: back to the walk (same waypoint), not Auto War's fight. The Bohai zone check inside the War
  buff steps is replaced by the Blossom zone guard in this mode.

### 3.8 Zone guard, restarts, re-login
- The Blossom phases (`BlossomTurn`, `BlossomWalk`, `BlossomAttack`, `BlossomLoot`, `BlossomRespawn`) join the
  zone guard (`IsLabyrinthPhase`): no input unless the minimap said "Blossom Garden" within 6 s; in a home town
  -> teleport in again (LOD's rule); another zone 3x -> `wrong_zone`, restart.
- CheckZone with Blossom Garden on the minimap (Start inside, restart, re-login) -> rebuff -> pillar turn ->
  nearest waypoint (target already in view -> fight first, heading then unknown -> pillar turn after it).
- Dry run: full detection, no input (as other modes).

## 4. UI / settings

- Mode drop-down and Mini window: "Auto Blossom".
- Settings panel, new block "Auto Blossom": Target name, Waypoint pause (s), Loot time (s). Waypoints, pillar score,
  tolerances only via settings.json **(my call)**.
- `AppSettings.Blossom` (`BlossomSettings`): Destination "Blossom Garden", ListPage 1, PillarTemplate "redpillar",
  PillarMatchScore 0.70, PillarCenterTolerance 0.06, TurnTapMs 100, TurnTaps 7, TurnMaxTaps 50, Waypoints (the 17),
  LoopFrom 6, WaypointTolerance 60, WaypointWaitMs 3000, TabEveryMs 1000, TargetName "[S]Jaso Practitioner",
  TargetGoneMs 2000, FightMaxMs 120000, LootDelayMs 1000, LootMs 5000, LootEveryMs 200. New block, no version bump
  (a file without it loads the defaults). `Normalize` clamps them.
- Note (as Auto War): a build from before Auto Blossom fails to load a settings.json with `"Mode": "AutoBlossom"` and
  falls back to defaults - update every installed copy first.

## 5. Tests / verification

- Core tests (xUnit, PilotTestDoubles): waypoint order and loop (1..5, 6..17, 6...), nearest-start pick, heading base
  from the first step, pause <= WaypointWaitMs then next waypoint, target -> fight -> gone 2 s -> 1 s wait -> loot
  5 s every 200 ms -> walk to the same waypoint, rebuff only when due, settings defaults / clamps.
- SelfTest probe `--pillars <dir>`: pillar hits, midpoint offset, verdict per PNG (run on the user's screenshot).
- Not verifiable offline: everything live - the pillar score, the turn direction of D, the heading, the target bar
  text for Jaso, the chest, the walk between waypoints (walls), the AFK checker.

## 6. Left to the builder

File layout (`AutomationPilot.Blossom.cs` expected), how the War buff steps are parametrised for a different
"next" phase and zone check, the overlay labels and log wording (keep the project's style: every decision logged).

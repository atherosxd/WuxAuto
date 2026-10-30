# Auto Blossom Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A fourth pilot mode, `RunMode.AutoBlossom`: teleport to Blossom Garden, line up on the two pillars, walk a 17-waypoint route for ever, kill and loot every Jaso Practitioner met, rebuff on the Auto War timer.

**Architecture:** A new partial `AutomationPilot.Blossom.cs` holds five phases (`BlossomTurn`, `BlossomWalk`, `BlossomAttack`, `BlossomLoot`, `BlossomRespawn`). It reuses the LOD SimpleSteer walk and target read, Auto War's `WarBuff` steps (with a mode switch for the zone check and the phase after the buff), and the shared town -> teleport -> zoom flow. The route rules are a pure static class so they can be unit-tested without the pilot.

**Tech Stack:** .NET 10, C# 13, WPF (desktop), Emgu CV 5 template matching, Windows.Media.Ocr behind `IOcrEngine`, xUnit.

**Spec:** `docs/superpowers/specs/2026-10-29-auto-blossom-design.md`

## Global Constraints

- Core stays OS-free (no WPF / WinRT / Win32 in `src/WuxAuto.Core`).
- All game input goes through `IGameControls` (`_controls`), never raw keys in the pilot.
- Every decision is logged (`Log?.Invoke(level, ...)`): Success for a completed action, Info for a step, Warning for something recoverable.
- New settings block `AppSettings.Blossom` (`BlossomSettings`), **no `CurrentVersion` bump** (a file without it loads the defaults); `SettingsStore.Normalize` clamps every new number.
- Defaults (spec §4): Destination "Blossom Garden", ListPage 1, PillarTemplate "blossompillar", PillarMatchScore 0.70, PillarCenterTolerance 0.06, TurnTapMs 100, TurnTaps 7 (user), TurnMaxTaps 50 (my call: about a full turn), Waypoints = the 17 pairs of spec §3.3 in order, LoopFrom 6, WaypointTolerance 60, WaypointWaitMs 3000, TabEveryMs 1000, TargetName "[S]Jaso Practitioner", TargetGoneMs 2000, FightMaxMs 120000, LootDelayMs 1000, LootMs 5000, LootEveryMs 200.
- The D key is `TurnDirection.Right` (`Input.RotateKey`, default "D"); A is `TurnDirection.Left`.
- Game frames and the user's screenshots never go into git (only template crops in `Assets`).
- Frontend rule (user CLAUDE.md): `dotnet build` of the solution must pass before every commit that touches XAML.

## Review Focus

1. **Start inside Blossom Garden mid-route** (restart, re-login, AFK kick then teleport): the walk must begin at the nearest waypoint, not walk back to waypoint 1 across the map. -> Task 1 `Nearest` test + Task 3 `Start_InsideTheMap_WalksToTheNearestWaypoint`.
2. **A target that is not a Jaso** (another monster or a player) after Tab: never attacked; the walk goes on. -> Task 3 `OtherTarget_IsNotAttacked`.
3. **Standing still too long** (AFK checker): a reached waypoint must be left after `WaypointWaitMs` even with no target. -> Task 3 `Waypoint_PausesAtMostWaypointWaitMs_ThenNext`.
4. **Rebuff due mid-fight**: must not interrupt the fight or the loot; it runs after the loot. -> Task 4 `Rebuff_Due_RunsAfterLoot_NotMidFight`.
5. **Only one pillar matches** (the right base is green-tinted): the turn must not spin for ever - it falls back after the tap limit and walks anyway, heading learnt from movement. -> Task 2 `FindPillarPair_OneBase_IsNotAPair` + Task 3 `Turn_NoPair_GivesUpAndWalks`.

---

### Task 1: Settings, mode and route rules

**Files:**
- Modify: `src/WuxAuto.Core/Core/AppSettings.cs` (enum `RunMode` add `AutoBlossom = 3`; property `public BlossomSettings Blossom { get; set; } = new();` after `War`; new class `BlossomSettings` near `LodSettings`)
- Modify: `src/WuxAuto.Core/Core/SettingsStore.cs` (`Normalize`: clamp the new numbers, rebuild an empty / unparsable waypoint list from the defaults)
- Create: `src/WuxAuto.Core/Automation/BlossomRoute.cs`
- Test: `tests/WuxAuto.Core.Tests/BlossomRouteTests.cs`

**Interfaces:**
- Produces:
  - `public sealed class BlossomSettings` with the properties and defaults of Global Constraints; `Waypoints` is `List<string>` ("x,y" strings, e.g. "19136,18463", so settings.json stays hand-editable); `LoopFrom` is 1-based.
  - `public static class BlossomRoute` in namespace `WuxAuto.Automation`:
    - `IReadOnlyList<(int X, int Y)> Parse(IEnumerable<string> waypoints)` - skips blanks and unparsable entries.
    - `int Next(int index, int count, int loopFrom)` - 0-based index after `index`; after the last returns `loopFrom - 1` (clamped into range).
    - `int Nearest(IReadOnlyList<(int X, int Y)> route, int x, int y)` - index of the closest point (first wins a tie).
  - Clamps: PillarMatchScore 0.30-0.99, PillarCenterTolerance 0.01-0.30, TurnTapMs 20-1000, TurnTaps 1-60, TurnMaxTaps TurnTaps-120, LoopFrom 1..max(1, Waypoints.Count), WaypointTolerance 10-500, WaypointWaitMs 0-5000 (user: 5 s or less), TabEveryMs 300-10000, TargetGoneMs 500-30000, FightMaxMs 10000-600000, LootDelayMs 0-10000, LootMs 0-60000, LootEveryMs 50-2000, ListPage 1-20.

- [ ] **Step 1: Write the failing tests** (`BlossomRouteTests`)

```csharp
[Fact] public void Defaults_AreTheUsersRoute()
{
    var b = new AppSettings().Blossom;
    var route = BlossomRoute.Parse(b.Waypoints);
    Assert.Equal(17, route.Count);
    Assert.Equal((19136, 18463), route[0]);
    Assert.Equal((13947, 14697), route[5]);
    Assert.Equal((14359, 14565), route[16]);
    Assert.Equal(6, b.LoopFrom);
    Assert.Equal("Blossom Garden", b.Destination);
    Assert.Equal(1, b.ListPage);
    Assert.Equal(3000, b.WaypointWaitMs);
}

[Theory]
[InlineData(0, 1)] [InlineData(4, 5)] [InlineData(15, 16)] [InlineData(16, 5)]
public void Next_Loops17BackTo6(int from, int expected) => Assert.Equal(expected, BlossomRoute.Next(from, 17, 6));

[Fact] public void Nearest_PicksTheClosest()
{
    var route = BlossomRoute.Parse(new AppSettings().Blossom.Waypoints);
    Assert.Equal(0, BlossomRoute.Nearest(route, 19148, 19062));   // the arrival point (user's frame)
    Assert.Equal(9, BlossomRoute.Nearest(route, 12990, 13190));
}

[Fact] public void Parse_SkipsJunk() => Assert.Equal(new[] { (1, 2) }, BlossomRoute.Parse(new[] { "", "x,y", " 1 , 2 " }));

[Fact] public void Normalize_ClampsWaitAndRefillsAnEmptyRoute()
{
    var s = new AppSettings();
    s.Blossom.WaypointWaitMs = 9000;
    s.Blossom.Waypoints = [];
    SettingsStore.Normalize(s);
    Assert.Equal(5000, s.Blossom.WaypointWaitMs);
    Assert.Equal(17, BlossomRoute.Parse(s.Blossom.Waypoints).Count);
}
```

(Check `SettingsStore.Normalize`'s actual accessibility/name first; `SettingsMigrationTests` shows how existing tests call it.)

- [ ] **Step 2:** `dotnet test tests\WuxAuto.Core.Tests --filter BlossomRouteTests` - fails to compile.
- [ ] **Step 3:** Implement the settings class, enum value, clamps and `BlossomRoute`.
- [ ] **Step 4:** Same test command - PASS.
- [ ] **Step 5:** Commit `Auto Blossom: settings, mode and route rules`.

### Task 2: Pillar pair detection + template + probe

**Files:**
- Create: `src/WuxAuto/Assets/blossompillar.png` - copy of `%LOCALAPPDATA%\WuxAuto\war-inputs\pillarbase.png` (96x42, the left base cropped at 788,270 from the user's lined-up 2000x1168 frame). Check `src/WuxAuto/WuxAuto.csproj` copies `Assets\*.png` by wildcard; add an entry if not.
- Modify: `src/WuxAuto.Core/Vision/ScreenAnalyzer.cs` (new method) and `VisionModels.cs` (new record)
- Modify: `tests/WuxAuto.SelfTest/Program.cs` (new `--pillars <dir>` probe; follow how `--shura` is wired)
- Test: `tests/WuxAuto.Core.Tests/PillarPairTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record PillarPair(TemplateMatch Left, TemplateMatch Right, double OffsetFraction)` - `OffsetFraction` = (midpoint x - frame centre x) / frame width; negative = left of centre.
  - `public PillarPair? FindPillarPair(Mat frame, string templateName, double threshold)` on `ScreenAnalyzer`: `FindIcons` in `new NormRect(0.25, 0.05, 0.50, 0.45)`, maxResults 4; the pair = two hits with |dy| < 0.05 h and 0.10-0.40 w apart (the pair closest to the centre wins when there are more); null otherwise.

- [ ] **Step 1: Write the failing tests.** Load the template through `TemplateLibrary` the way `ScreenAnalyzerTests` does (AppPaths pointed at a temp root whose `templates\` holds `blossompillar.png` copied from the repo's `src/WuxAuto/Assets`), paste it twice onto a 1440x900 green-grey BGRA canvas:
  - `FindPillarPair_TwoBases_Centred`: bases at x 560 and 860 (centres), y 200 -> pair found, `Math.Abs(OffsetFraction) < 0.01`.
  - `FindPillarPair_OffCentre_SignedOffset`: bases at 760 and 1060 -> `OffsetFraction` ~ +0.14 (± 0.01).
  - `FindPillarPair_OneBase_IsNotAPair`: one base only -> null.
  - `FindPillarPair_TooClose_IsNotAPair`: bases 60 px apart -> null.
- [ ] **Step 2:** `dotnet test tests\WuxAuto.Core.Tests --filter PillarPairTests` - FAIL.
- [ ] **Step 3:** Implement `FindPillarPair`, the record, and the `--pillars` probe (per PNG: every hit `score box`, the pair or "no pair", the offset).
- [ ] **Step 4:** Tests PASS. Then run the probe on the user's frame:
  `dotnet run --project tests\WuxAuto.SelfTest -- --pillars "$env:LOCALAPPDATA\WuxAuto\war-inputs"` - expected: `blossom_lined_up.png` shows a pair with |offset| <= 0.06. Record both base scores. If the green right base scores under 0.70, set the `PillarMatchScore` default to (its score - 0.05), not below 0.55, and note it in the spec. If it is not found at all, stop and report - the template needs a second crop.
- [ ] **Step 5:** Commit `Auto Blossom: pillar pair detection and --pillars probe`.

### Task 3: The Blossom phases (turn, walk, target, fight, loot, respawn)

**Files:**
- Create: `src/WuxAuto.Core/Automation/AutomationPilot.Blossom.cs`
- Modify: `src/WuxAuto.Core/Automation/AutomationPilot.cs` - `PilotPhase` (add the five after `LodLoot`), the tick `switch`, `IsLabyrinthPhase` (add the five), zone guard condition (`_settings.Dungeon.Enabled || IsWar || IsBlossom`; town -> teleport in again for `IsLod || IsBlossom`), `TickCheckZoneAsync` (a Blossom branch beside the LOD one), `TickZoomOutAsync` (`IsBlossom` -> `BeginBlossomArrival()`; the still-in-town rules treat Blossom like LOD: `IsLod` -> `IsLod || IsBlossom` at the three `townLimit` / `OutOfKeys` spots), `_regularWarOffDue` (`&& !IsBlossom`), `Start()` (reset the Blossom counters), `ResetRunState()` (reset the per-run Blossom state), `Advance` (release a held skill already happens - check).
- Modify: `src/WuxAuto.Core/Automation/AutomationPilot.Lod.cs` - `Destination` gains `IsBlossom ? _settings.Blossom.Destination`; `CheckKeysAsync` returns false for Blossom.
- Test: `tests/WuxAuto.Core.Tests/BlossomPilotTests.cs` (PilotTestDoubles / `PilotRun.RunAsync`, as `LodStandStillTests`)

**Interfaces:**
- Consumes: Task 1 `BlossomRoute`, `BlossomSettings`; Task 2 `FindPillarPair`; existing `ReadCoordinatesAsync`, `LearnHeadingError`, `_headingBase` / `_headingError` / `_headingSamples`, `_simpleClick`, `_lastCoords`, `RunClickTop` / `RunClickRight`, `TapLodTab`-style Tab timing, `ReleaseHeldSkill`, `_controls.Turn(TurnDirection, ms)`, `_controls.SkillTap`, `GameAction.Loot`.
- Produces (used by Task 4):
  - `private bool IsBlossom` (`Mode == RunMode.AutoBlossom`)
  - `private void BeginBlossomArrival()` - rebuff first (Task 4 hooks in here), then `BeginBlossomTurn()`
  - `private void BeginBlossomTurn()`, `private void ResumeBlossomWalk()` (back to `BlossomWalk` on the current waypoint)
  - `private int _blossomWaypoint` (0-based current goal), `private int _blossomKills`

Behaviour per phase (spec §3; values from `BlossomSettings`):
- `BlossomTurn`: each tick `FindPillarPair`. Pair with |offset| <= tolerance -> log `Lined up on the pillars (offset +0.02, after N tap(s))`, start the walk at `Nearest` (coordinates read in the walk's first tick). Pair off centre -> 40 ms tap in `_blossomFineDir` (starts Right); if |offset| grew, flip to Left; at most 12 fine taps, then accept. No pair -> `Turn(Right, TurnTapMs)`; after `TurnTaps` say once "not lined up after N taps - turning on"; after `TurnMaxTaps` -> Warning "... no pillars ..." + snapshot `blossom_no_pillars`, walk anyway. ~350 ms between taps for a fresh frame. No Tab while turning.
- `BlossomWalk`: Tab every `TabEveryMs`; target check (below) every tick -> `BlossomAttack`. Coordinates unread -> wait 300 ms (40 in a row -> `Fail`). First tick with no waypoint chosen -> `_blossomWaypoint = Nearest(...)`, log `Walking the route from waypoint N`. **First step after a turn: click straight up** (screen offset (0, -RunClickPx)) and on the next reading set `_headingBase = _headingError = atan2(cross, dot)` of click vs movement (needs >= 60 units moved; else click up again, at most 3). Then steer as `TickLodRunAsync` does. Within `WaypointTolerance` -> pause: stop clicking, Tab, for at most `WaypointWaitMs`, then `_blossomWaypoint = Next(...)`, log `Waypoint N reached - next N+1`. 8 stalled steps (< 20 units) on one waypoint -> Warning, next waypoint.
- Target check: whole-screen OCR (`_settings.Regions.BossName`); targeted = a line in the target-bar band (y < 13 % h, |x - 50 % w| < 22 % w; LOD measured it) whose normalised text contains "jasopractitioner" or scores >= 0.85 against `TargetName`, **or** `monstericon` >= 0.80 in `NormRect(0.25, 0, 0.50, 0.16)`. Factor it as `private async Task<string?> FindBlossomTargetAsync(Mat frame)` returning what was seen (name or "chest 0.9x"), null for none.
- `BlossomAttack`: `SkillTap(Dungeon.SkillKey)` every `Lod.SkillEveryMs` (held with `Lod.HoldSkill`, as `TickLodAttackAsync`); target seen -> refresh `_blossomTargetSeenAt`; gone `TargetGoneMs` -> kill: `_blossomKills++`, Success log `Jaso Practitioner down (kill N) - looting in 1s`, `Advance(BlossomLoot, LootDelayMs)`. Coordinate jump > `Lod.TeleportJump` -> `BlossomRespawn`. `FightMaxMs` without the target ever going -> Warning, Tab, `ResumeBlossomWalk()`.
- `BlossomLoot`: `Press(GameAction.Loot)` every `LootEveryMs` for `LootMs`, no Tab; then `ResumeBlossomWalk()` (Task 4 puts the rebuff check here).
- `BlossomRespawn`: two equal coordinate readings (or 20 s) -> `BeginBlossomArrival()` (rebuff, turn, nearest waypoint).
- `TickCheckZoneAsync` inside Blossom Garden: target in view -> fight (`_blossomHeadingUnknown = true`, the turn follows the fight); else `BeginBlossomArrival()`.
- After a fight that started with the heading unknown, `ResumeBlossomWalk` goes through `BeginBlossomTurn()` first.

- [ ] **Step 1: Write the failing tests** (`BlossomPilotTests`; scene: minimap title "[Blossom Garden]" at 1000,10 and a coordinate line "x , y" at 1100,200 width 120, as in `LodStandStillTests`; `settings.Lod.BuffKey = ""` and every War buff icon unticked so Task 4's buff round is empty; frame 1440x900):
  - `Walk_FirstClickIsStraightUp`: `Start(PilotPhase.BlossomWalk)` (heading not learnt yet) with the coordinates at the arrival 19148,19062 -> the first `Click` is at x = 720 (centre) and y < 450.
  - `Start_InsideTheMap_WalksToTheNearestWaypoint`: coordinates 12990,13190 -> log contains `Walking the route from waypoint 10`.
  - `Waypoint_PausesAtMostWaypointWaitMs_ThenNext`: `WaypointWaitMs` 1000, coordinates on waypoint 6 (13947,14697) -> log `Waypoint 6 reached`, then within 2.5 s `next 7`; no click between the two lines; Tabs during the pause.
  - `JasoTargeted_StopsWalking_Attacks_ThenLoots_ThenWalksOn`: after 3 Tabs the scene adds `[S]Jaso Practitioner` at 720,50; after 4 skill taps it is removed -> phases go BlossomAttack -> BlossomLoot (>= `LootMs`/`LootEveryMs` - 1 loot presses, `LootMs` 1000, `LootEveryMs` 200, `LootDelayMs` 0, `TargetGoneMs` 500) -> BlossomWalk; no Click while attacking.
  - `OtherTarget_IsNotAttacked`: the scene shows "Forest Bandit" in the target bar -> phase stays BlossomWalk, no skill key in `input.Calls`.
  - `Turn_NoPair_GivesUpAndWalks`: blank frame (no pillars), `TurnTaps` 3, `TurnMaxTaps` 5, `Start(PilotPhase.BlossomTurn)` -> exactly 5 `KeyDown D` in `input.Calls`, the log line containing `not lined up after 3 taps`, the Warning containing `no pillars`, then phase BlossomWalk.
- [ ] **Step 2:** `dotnet test tests\WuxAuto.Core.Tests --filter BlossomPilotTests` - FAIL.
- [ ] **Step 3:** Implement the partial and the wiring above.
- [ ] **Step 4:** Tests PASS; then the full suite `dotnet test tests\WuxAuto.Core.Tests` - all pass (502 + new).
- [ ] **Step 5:** Commit `Auto Blossom: turn to the pillars, walk the route, fight and loot`.

### Task 4: Rebuff through Auto War's buff steps

**Files:**
- Modify: `src/WuxAuto.Core/Automation/AutomationPilot.WarFight.cs` - `TickWarBuffAsync`: in Blossom mode skip `WarZoneCheckAsync` (the general zone guard covers `WarBuff` - see below); `FinishWarBuffAsync`: in Blossom mode no HP read, then `_blossomAfterBuff()` instead of `BeginWarFight()`.
- Modify: `src/WuxAuto.Core/Automation/AutomationPilot.cs` - the zone guard also covers `PilotPhase.WarBuff` when `IsBlossom`.
- Modify: `src/WuxAuto.Core/Automation/AutomationPilot.Blossom.cs`
- Test: `tests/WuxAuto.Core.Tests/BlossomPilotTests.cs` (add)

**Interfaces:**
- Consumes: `BeginWarRebuff(string why, int delayMs)`, `_warBuffedAt`, `_warHasAutoBuff`, `WarSettings.BuffEveryMs`, Task 3's `BeginBlossomArrival`, `ResumeBlossomWalk`, `BeginBlossomTurn`.
- Produces: `private bool BlossomRebuffDue(long now)` = `now - _warBuffedAt >= _settings.War.BuffEveryMs`; `private PilotPhase? _blossomAfterBuffTurn` style flag so `FinishWarBuffAsync` knows whether to turn (arrival / respawn) or resume the walk (after a loot / at a pause).

Rules (spec §3.7): `BeginBlossomArrival` always buffs (`BeginWarRebuff("arrival", 300)`), then the turn. After every loot and at every waypoint pause: due -> rebuff, then resume the walk. Never during `BlossomAttack` or `BlossomLoot`. A rebuff more than 30 s overdue runs at the next walk tick even without a pause.

- [ ] **Step 1: Failing tests:**
  - `Rebuff_Due_RunsAfterLoot_NotMidFight`: `War.BuffEveryMs` 30000 (clamp minimum), `Lod.BuffKey` "O", item list scene shows "[Auto buff scroll]" in `Regions.ItemList`; start in BlossomWalk with `_warBuffedAt` long ago (start from `BlossomWalk` - `Start` leaves `_warBuffedAt` 0 so it is due at once); Jaso targeted then gone -> the "O" `KeyTap` appears in `input.Calls` after the last loot key press and after no skill key; log contains `Buffed with 'O'`.
  - `Arrival_BuffsThenTurns`: `Start(PilotPhase.ZoomOut)` with the Blossom title -> "O" before the first "D" KeyDown.
- [ ] **Step 2:** FAIL. **Step 3:** implement. **Step 4:** the Blossom tests and the full suite pass (the War tests must stay green).
- [ ] **Step 5:** Commit `Auto Blossom: rebuff with the Auto War buff steps`.

### Task 5: UI, docs, build

**Files:**
- Modify: `src/WuxAuto/ViewModels/Converters.cs` (`OptionLists.RunModes` add `RunMode.AutoBlossom`)
- Modify: `src/WuxAuto/MainWindow.xaml` - a fourth mode radio "Auto Blossom" (tooltip: "Blossom Garden: line up on the pillars, walk the 17-waypoint route, kill and loot every Jaso Practitioner, rebuff on the Auto War timer.") and, after the Auto War block, an "Auto Blossom" section: Target name (`Settings.Blossom.TargetName`), Waypoint pause (ms) (`WaypointWaitMs`, tooltip "At most 5000 - the map's AFK checker kicks idle characters to town"), Loot time (ms) (`LootMs`).
- Modify: `src/WuxAuto/MiniWindow.xaml` - radio "Blossom".
- Modify: `README.md`, `src/WuxAuto/Assets/QuickStart.md`, `CLAUDE.md` (new §4.9 Auto Blossom: phases, settings, what is not seen live - list of spec §5; repository map line for `AutomationPilot.Blossom.cs`; `--pillars` probe line in §2).
- Check: `tests/WuxAuto.UiShot -- <png> --settings --bottom` renders the new block (it restores settings.json afterwards).

- [ ] **Step 1:** Make the edits.
- [ ] **Step 2:** `dotnet build .\WuxAuto.slnx` - 0 errors (Release too: `dotnet build .\WuxAuto.slnx -c Release`). Do **not** run or kill the user's installed WuxAuto.
- [ ] **Step 3:** UiShot render of the main window and the settings panel; look at the PNGs: the four mode buttons fit, the new block lines up with Auto War's.
- [ ] **Step 4:** `dotnet test tests\WuxAuto.Core.Tests` - all pass.
- [ ] **Step 5:** Commit `Auto Blossom: mode buttons, settings block, docs`.

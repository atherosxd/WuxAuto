# Auto War mode — design

Date: 2026-10-27. Branch: `worktree-auto-war-mode`. Status: **approved by the user 2026-10-27**, with
the four open points in §11 taken as working assumptions (the user approved without answering them).

Marking used below: **(user)** = the user decided or stated it; **(my call)** = decided by me during the
design conversation, open to the user's veto; **(verify)** = unknown, no frame or live run shows it yet,
to be checked by a probe or the first live run before the part that depends on it is trusted.

---

## 1. Intent

The user's alt characters sit AFK in a cave or a premium cave — any map on **page 3 of the teleport
scroll** — with the game's autohunt on **(user)**. Every so often the tribe war at **[Bohai Outpost
(Adept)]** opens; the game then pops a `WAR — [Bohai Outpost (Adept)] Confirm to move?` dialog with
Cancel / OK. Today nobody answers it for an AFK alt, so the alt misses the war.

**Auto War** is a third run mode next to Auto Laby and Auto LOD **(user)**. With it selected and
started, the pilot is "afk and only waiting for the Bohai Outpost war" **(user)**: it joins every Bohai
war, buffs, fights in it, survives deaths, and when the war is over and the game sends the character to
town it takes the alt back to the same cave and the same monster and turns the autohunt back on, then
waits for the next war.

It is **not** the existing keys-out fallback (`PilotPhase.AutoWar`, `AutomationPilot.War.cs`), which only
ticks "Regular War" in the autohunt settings when Laby keys and LOD entries are both used up. That one
stays as it is **(user)**. Laby and LOD runs keep **cancelling** the Bohai dialog **(user)**.

## 2. Goals, non-goals, anti-goals

Goals
- Join each Bohai Outpost (Adept) war unattended, fight it, come back to the cave, hunt, repeat until
  Stop **(user; the repeat is my call, not objected)**.
- Know when the war starts from the in-game timer (Menu → Tribe Status → NEXT WARS) and turn the
  autohunt off ~10 s before it, so the alt is not fighting a mob when the dialog arrives **(user)**.
- Buffs up whenever it fights: on arrival, every ~6 min, after every death **(user)**.
- Back to the exact spot: the same map (page 3 of the teleport scroll) and the same monster (map window →
  monster → `>`) **(user)**.

Non-goals
- Other wars (Dragonfall Outpost, Legacy Holy Stone, …). Only Bohai Outpost (Adept).
- Joining a war that is already "In progress" when Start is pressed — it waits for the next one
  **(my call)**.
- Setting TAB target back to Default after the war **(user: keep Enemy worth CP)**. A later Laby/LOD run
  sets Default itself (`Sequence.TabTargetDefault`).
- Changing the Laby / LOD runs, the keys-out Regular War step, or the idle Auto confirm helper.

Anti-goals (failure even if the war part works)
- **Walking the alt away from its cave by a stray click.** Every click on a game window (☰ menu, Tribe
  Status, teleport list, map list) must be checked against a fresh frame first — a click meant for a
  window that is not open lands in the world and walks the character (CLAUDE.md §2, §10).
- **Blind `J` presses.** `J` is a toggle; every autohunt change is confirmed in the combat log
  (`AutohuntLog`), as the town check does.
- **Breaking Laby / LOD.** They are run live every day. Shared code that gets generalised (teleport
  destination/page, map monster pick, TAB target option) must behave exactly as before for them.
- **Committing screenshots with other players' names** to the public repo. Only template crops go into
  git; the Tribe Status screenshot (leader / guild names) stays local **(my call, as in the Android design)**.

## 3. Constraints

- `WuxAuto.Core` stays OS-free (CLAUDE.md §3 rule). Everything below is pilot / vision logic and belongs
  in Core; the desktop project only gets the mode drop-down, the Mini window button and the settings
  fields.
- All game-touching work happens on the vision-loop thread as ticked phases (`PilotPhase` + `Advance`),
  never a blocking script; Stop / F10 must take effect within one frame and release any held key
  (`ReleaseHeldSkill`).
- Screen positions are not persisted (`[JsonIgnore]` + `ResetPositions()`); any new region follows that.
- Settings: the saved file wins over code defaults. A new `WarSettings` class loads with code defaults on
  old files, so no migration is needed unless an existing default changes (CLAUDE.md §6).
- The game runs at many client sizes (1024×768 … 2304×1488 seen). Find things by template + OCR anchors,
  not fixed pixels.
- Input only while the bound window is in front (foreground guard); the zone guard holds while another
  window covers the game.

## 4. The run, step by step

### 4.1 Start (in the cave)

1. **Zone check.** Read the minimap title. It must be a cave: not a home town (`HomeTowns`), not the
   Labyrinth, not LOD, not Bohai Outpost **(my call)**. Then open the teleport scroll (inventory → right-click
   the scroll, as the Laby teleport does), page to **page 3** and check that the saved map's entry is there
   (`ZoneMatches`, the minimap title is cut off at the panel edge), then close the list with its **X**
   (top right of the TELEPORT SCROLL window) without clicking an entry **(my call)**. Not a cave or not on
   page 3 → warning "Auto War must be started inside a cave on page 3 of the teleport scroll" and **stop**
   **(user: warn; stop is my call, not objected)**. (verify: opening the list and closing it with X costs no
   scroll.)
   - Start in **Bohai Outpost** (war running) → straight to §4.5 with the saved spot from settings
     **(my call)**.
   - Start in a **home town** → if a saved spot exists, §4.8 (go back); else warn and stop **(my call)**.
2. **Skill bar check.** All 6 buff icons (§6) and the Shura icon (`shura.png`) must be on the skill bar
   (`Regions.Hotbar`, matched like Shura / Run). Any missing → warning naming which ones, and **stop**
   **(user: "all must be there"; stop rather than warn is my call)**. The check runs on every Start, even with
   autobuff (the X buff and Shura are used either way) **(my call)**.
3. **Remember the spot** **(user)**:
   - map = the **page-3 entry text** that matches the minimap title best (the title is cut off at the panel
     edge or read with noise: "[Dark Soul Cave X", "[Dark Soul Cave IF" in the logs, and `ZoneMatches`
     accepts any 10-letter prefix). The full entry text from the list is what gets saved and clicked later;
     two entries that match equally well → warning and stop **(my call)**;
   - monster = the name in the **target bar** after `Tab` (up to 3 taps, ~1 s apart; the alt is autohunting,
     so a target is normally already up), **checked against the map window's "Monsters on this map" list**
     (open the map, find the name, close it) so a player's name can never be saved — TAB target may still be
     "Enemy worth CP" from the last war **(my call)**.
   Saved in settings (`War.HomeMap`, `War.HomeMonster`) so an app restart mid-war still knows where to go
   back **(my call)**. Either unreadable / not confirmed → warning and stop **(my call, not objected)**.
4. Autohunt stays on. → §4.2.

### 4.2 Wait for the war (war timer)

1. **Open Tribe Status** **(user)**: there is no key, only the **☰ button** at the top centre of the client
   (1024 wide: ≈500,20; 1273: ≈580,28; 1920: ≈1183,28 — not proportional). Click it (template
   `menubutton.png`, cropped from a saved frame, matched in a top-centre band) → the MENU grid opens →
   OCR finds the **"Tribe Status"** tile → click it.
2. **Read the timer.** In TRIBE STATUS, under NEXT WARS, the row **"Bohai Outpost (Adept)"** has either a
   countdown `hh:mm:ss` (e.g. `00:04:25`) or `In progress`. Parse it; war start = now + countdown (PC
   clock).
3. **Close** TRIBE STATUS with its X (top right), and the MENU too if it is still open; check a fresh frame
   that both are gone.
4. **Wait** with autohunt on. Re-read the timer ~2 min before zero to catch a misread **(my call)**; if the
   countdown is long, re-read every ~15 min as well **(my call)**.
5. `In progress` → this war is missed; re-read every ~5 min until a countdown shows **(my call)**.
6. Timer unreadable 3× → snapshot `tribe_unread`, retry every 2 min. The dialog (§4.3) is still handled if
   it appears, so a broken timer read only loses the early autohunt-off **(my call)**.
7. The countdown runs out when the WAR dialog appears **(user: "10s before the timer runs out, turn off
   autohunt and wait for the Bohai Outpost dialog")**. The dialog window in §4.3 (T − 10 s … T + 2 min) covers
   a few seconds of difference either way.
8. Sent to a home town while waiting (not by a war: a premium cave's time limit, a disconnect, …) → §4.8 to
   go back **(my call)**.

### 4.3 Join (T − 10 s)

1. **10 s before zero** **(user)**: autohunt **off**, confirmed in the combat log (`EnsureAutohuntOffAsync`
   logic, "Auto hunt ended" / "stopped").
2. Wait for the **WAR dialog**: a line with "Bohai" and "Confirm" → click its gold **OK** (the Auto confirm
   finder already clicks this exact dialog dead centre at 1024 / 1273 / 1920 / 2048 wide on 5 saved
   frames). Poll fast (≤ 1 s) from T − 10 s on.
3. **In Auto War mode the pilot never cancels the Bohai dialog**: the 5 s whole-screen check that clicks
   Cancel (`Sequence.CancelDialogText`) is skipped in this mode **(user)**. If the dialog shows up at any
   other time (timer missed), autohunt off first, then OK **(my call)**.
4. No dialog within 2 min after zero → autohunt back on (confirmed), warning, back to §4.2 **(my call)**.
5. After OK: wait for the load (frame hash settles) and the minimap title **"Bohai Outpost"** **(user)**,
   timeout 60 s → snapshot, restart path (§5).

### 4.4 Inside Bohai: TAB target, then buffs

1. **TAB target = "Enemy worth CP"** **(user)**, on every entry. Reuse the SETTINGS visit (Escape → VISUAL →
   TAB target row, wheel to it, read the value, click the chevrons, SAVE, Escape). Today it only moves left
   to Default; it must go to a named option with left or right clicks (options in order: Default → Nearest
   enemy → Enemy worth CP → Nearest monster). Laby/LOD keep asking for Default. Left as is after the war
   **(user)**.
2. **Autobuff?** **(user)** Two signs, either is enough:
   - the lower-right item status list has an **"[Auto buff scroll]"** line (read-only, no input; the list
     reader already exists for the key check) — checked first **(my call)**;
   - else press **S** (autohunt settings): the **AUTO BUFF** dialog shows (title read as the whole line
     "autobuff", as the Regular War step does) → close it (X right of the title) and the settings window.
3. **Buff:**
   - autobuff → press **O** (`Lod.BuffKey`) **(user)**;
   - no autobuff → **click the 6 buff icons on the skill bar, one by one** **(user)**, ~1 s apart so a cast
     animation does not swallow the next click **(my call)**.
4. Buffs last at least 6 min **(user)** → rebuff every **5 min 45 s** **(my call)**, only between fight cycles
   (§4.5, at the `Tab` point, never during the Shura hold).

### 4.5 Fight

1. **Wait for the start** **(user)**: `Tab` about once a second until one of these shows:
   - a **target** — a name in the target bar (top centre) or the treasure-chest icon there
     (`monstericon.png`, as LOD finds it in `NormRect(0.25, 0, 0.50, 0.16)`). LOD looks for one known boss
     name anywhere on screen; here the name is unknown (enemy players), so "a name shows" needs a **new
     target-box reader**: text inside the target box only (on a 1273×1024 Laby frame the box is ≈485–795 ×
     58–118 with the name at y ≈ 72), never the version / ping line above it (y ≈ 28, same x range) or the
     player's own panel. Whether an enemy player's target bar has the chest icon at all is **(verify)**.
   - the text **"Gate is Open"** anywhere on screen (whole-screen OCR every ~2 s; exact wording **(verify)**).
2. **Cycle** **(user)**: `Tab` → click the **X buff** (icon 4, the bluish-white one with an X; it drops when
   another player hits) → **hold the Shura key 1.2 s** (`Lod.ShuraKey`, key down / key up; a click cannot
   hold) → **skill attack** (`Dungeon.SkillKey`, a press every 0.3 s, + Ctrl + skill when `Dungeon.CtrlSkill`
   is on, `TapSkill`) **until no name shows** in the target bar for ~2 s **(my call)** → next cycle from `Tab`.
3. `Tab` without a target → `Tab` again ~1 s later; Shura and the X buff only once a target shows **(my call)**.
4. After the war has started (target seen or "Gate is Open"), a death or rebuff goes straight back to the
   cycle, not to the start wait **(my call)**.

### 4.6 Death watch (all the time inside Bohai)

1. Read the **top-left HP numbers** "cur / max" (e.g. `42833 / 42833`, big red bar next to the level badge;
   readable on every saved frame) about once a second **(user: either this or the centre bar)**. `0 / …` =
   dead. Backup when the numbers do not read 3× in a row: the small HP bar under the character in the
   middle of the client, red → black **(user; numbers first is my call)**.
2. Dead → release any held key, stop pressing anything, wait until HP is above 0 again (the game puts the
   character back at the base by itself **(my guess, user did not object)**) → rebuff everything (§4.4.3)
   → fight (§4.5) **(user)**.
3. Still 0 after 30 s → snapshot `war_dead` (≤ 3 per Start), keep waiting **(my call)**. No frame of a death
   exists yet **(verify)**.

### 4.7 War over

The game sends the character to a home town **(user)**: minimap title in `HomeTowns` → §4.8. Any other zone
that is neither Bohai nor the saved cave (read 3×) → snapshot `wrong_zone`, warning, §4.8 anyway **(my call)**.
The saved cave itself → §4.8 step 3.

### 4.8 Go back and hunt

1. **Teleport** **(user)**: inventory → right-click the teleport scroll → page to **page 3** → click the saved
   map's entry (the Laby / LOD teleport phases, with the destination and page as parameters).
2. Wait for the load and the minimap title = the saved map (`ZoneMatches`), timeout 60 s → restart path.
   Still in town after that (the teleport was refused: a premium pass ran out, …) twice in a row → warning
   "could not go back to <map>" and **stop** **(my call)**.
3. **Walk to the mobs** **(user)**: click the map icon → find the saved monster in "Monsters on this map"
   (paging down as needed) → click its name → click its `>` (the Laby stage pick: `ClickMap` / `ReadStage` /
   `AdvanceStage`, given a monster name instead of `Stages[i]`; the "map really open" checks stay).
4. Wait for **"Arrived: <monster>"** in the **combat log area on the left**, the same place as "Attack
   succeeded" (`Regions.AttackMessage`) **(user)**. Nothing in 90 s → warning, carry on **(my call)**.
5. **`J`** → confirm "Auto hunt started" in the combat log (≤ 3 taps, the town check's logic aimed the other
   way) **(user: turn on autohunt; the check is my call)**.
6. → §4.2 (the next war) **(my call, not objected)**.

## 5. Restart, stop, reconnect

- `RestartOnError` works as for Laby / LOD: the run restarts from `Focus`, and the start dispatch (§4.1)
  picks up from the zone: cave → §4.2 (skipping the remember step if the saved spot's map matches),
  Bohai → §4.5, home town → §4.8 **(my call)**.
- Auto reconnect stays on in this mode; after it the same zone dispatch applies **(my call)**.
- The town-only steps of Laby/LOD (autohunt off in town, Run skill check, font size) do not run in Auto War
  **(my call)**.
- Zone guard: like the Labyrinth phases — no input while the expected zone is not on the minimap, and only
  judged with the bound window in front (a second game client once fooled it, CLAUDE.md §4.4).

## 6. Pictures and settings

Templates (into `src/WuxAuto/Assets`):
- `warbuff1.png` … `warbuff6.png` — the user's six crops (51×51, 33×36, 51×51, 51×51, 45×45, 51×51), in the
  order given: purple lightning, grey streak, dark ring, **bluish-white X (the one that drops)**, green ring,
  blue orb. Not the same picture as `shura.png` (checked by eye). Matched in `Regions.Hotbar` with the learned
  scale, threshold like Shura (0.80) until saved frames say otherwise **(verify: none of the saved frames is
  of an alt's skill bar)**.
- `menubutton.png` — the ☰ button, cropped from a saved frame **(my call)**.

`WarSettings` (new, `AppSettings.War`) **(my call; names and UI placement are the builder's)**:

| Setting | Default | Meaning |
| --- | --- | --- |
| `Destination` | `Bohai Outpost` | minimap title inside the war / dialog keyword |
| `TribeRowText` | `Bohai Outpost (Adept)` | the NEXT WARS row |
| `LeadMs` | 10000 | autohunt off this long before the timer runs out |
| `DialogWaitMs` | 120000 | after zero, how long to wait for the dialog |
| `TabTargetOption` | `Enemy worth CP` | TAB target inside the war |
| `BuffIcons` | warbuff1 … warbuff6 | clicked in order without autobuff |
| `XBuffIcon` | warbuff4 | clicked again in every fight cycle |
| `BuffClickGapMs` | 1000 | between two buff clicks |
| `BuffEveryMs` | 345000 | rebuff |
| `ShuraHoldMs` | 1200 | Shura key held per cycle |
| `TargetGoneMs` | 2000 | no name this long = next cycle |
| `GateText` | `Gate is Open` | war-start text |
| `ListPage` | 3 | teleport list page of the caves |
| `ArrivedText` | `Arrived` | "Arrived: <monster>" |
| `ArriveWaitMs` | 90000 | then `J` anyway |
| `HomeMap` / `HomeMonster` | empty | the remembered spot, written by Start |

Keys reused, not duplicated: `Dungeon.SkillKey`, `Dungeon.CtrlSkill`, `Dungeon.AutohuntKey`, `TabKey`,
`Lod.ShuraKey`, `Lod.BuffKey` (O), `Sequence.AutohuntSettingsKey` (S) **(my call)**.

UI **(my call)**: `RunMode.AutoWar` (= 2) with a third radio button "Auto War" next to "Auto Laby" / "Auto
LOD" (MainWindow "2. Run the teleport sequence") and a third "War" radio in the Mini window; a small "Auto
War" block in the settings panel (Shura hold, rebuff every, lead time) and the remembered map / monster shown
in the status line.

## 7. The old keys-out step

Renamed in code only, so the two cannot be mixed up **(my call)**: `PilotPhase.AutoWar` → `RegularWarOn`,
`BeginAutoWar` → `BeginRegularWar`, `AutomationPilot.War.cs` → `AutomationPilot.RegularWar.cs`. Behaviour
unchanged (keys out → tick Regular War → `J` → Stop).

## 8. Tests and probes

- Core unit tests: timer parse (`00:04:25` → 265 s, `In progress`, OCR noise like `00:O4:25`), HP line parse
  (`42833 / 42833`, `0 / 42833`, unreadable), TAB target option stepping (which chevron, how many clicks).
- SelfTest probes over a folder of PNGs, in the style of `--shura` / `--runskill`: `--warbar` (6 buffs +
  Shura, score + box), `--hp` (the parsed numbers), `--tribe <png>` (the Bohai row and its countdown),
  `--menu` (☰ found, "Tribe Status" tile found when the menu is open).
- The user's Tribe Status and Menu screenshots are test inputs kept **local** (names in them); only crops
  are committed.
- Laby / LOD parity: run `tests\probe-baseline.ps1` before and after the shared-code changes (teleport
  destination/page, map monster pick, TAB target option) and diff; empty = unchanged.

## 9. Not verified — needs the first live war

No saved frame shows: the inside of Bohai, "Gate is Open", the target bar on an enemy player, HP 0 and the
revive, "Arrived: <monster>", the opened ☰ menu or TRIBE STATUS in a full frame, page 3 of the teleport list,
an alt's skill bar. Each part logs what it saw and saves a snapshot the first time (`war_*` tags, a few per
Start), so the first live war gives the frames to tune from. Hints from the logs (Laby runs, which cancel
it): the WAR dialog appeared at xx:28:52–xx:29:00 on most hours and once at 16:47:53; "[Tribe] Victory!"
lines show up around xx:29 and xx:48 (other wars' results too). The timer, not these times, decides.

## 10. Where the pieces are today (2026-10-27, `main` at 23bb86b)

Short names: `AP` = `src/WuxAuto.Core/Automation/AutomationPilot.cs`, `LOD` = `AutomationPilot.Lod.cs`,
`AS` = `src/WuxAuto.Core/Core/AppSettings.cs`, `SA` = `src/WuxAuto.Core/Vision/ScreenAnalyzer.cs`.

| Need | Exists | Coupling to undo |
| --- | --- | --- |
| Mode | `RunMode { AutoLaby, AutoLod }` AS:24; `IsLod` LOD:39 is the only branch point; `Destination` LOD:42; `CurrentDestination` AP:2298 | many `IsLod` checks assume "not LOD = Laby" (AP:548, 755, 785, 1583, 2221–2277); the zone guard runs on `Dungeon.Enabled && IsLabyrinthPhase` (AP:510); `TickDone` loops on `Dungeon.Enabled` (AP:4979) — each needs an explicit War answer |
| Bohai cancel | shared 5 s whole-screen OCR AP:472–505 → `TryCancelDialog` AP:4623 | runs in every mode: skip the cancel (keep the reconnect check) in War |
| Confirm dialog | `SA.FindConfirmDialogAsync` SA:301 (Core) + `FindGoldButtons` SA:366 | none |
| Autohunt off / state | `EnsureAutohuntOffAsync` AP:934, `AutohuntLog.NewState` (pure) | an "ensure on" twin is new |
| TAB target | `TickFontSizeAsync` AP:1185, `ReadSettingRowAsync` SA:190 (generic) | `IsDefaultTabTarget` AP:1455 and the left-only chevron click AP:1397–1406: make the wanted option a parameter, pick the chevron by position in the option list |
| Teleport | `TickOpenInventory` AP:1457, `TickFindScroll` AP:1480, `TickFindChevron` AP:1519, `TickFindDestinationAsync` AP:1554, `TeleportListArrows` AP:4724 | destination via `CurrentDestination`; the page rule is LOD-only (AP:1583); arrival handling in `TickZoomOutAsync` AP:2202 is Laby/LOD-specific (zoom, face north/south) — War needs its own arrival step |
| Map monster pick | `TickClickMapAsync` AP:2483, `MapWindowVisibleAsync` AP:2472, `ClickStageRow` AP:2706, `FindFocusedChevron` AP:2740 (generic) | `TickReadStageAsync` AP:2543 and `TickAdvanceStageAsync` AP:2943 read `Stages[_stageIndex]`: make the name a parameter; `TickClickMapAsync` picks its next phase from `Dungeon.Enabled` (AP:2525) |
| Skill bar icons | `SA.FindIcons` SA:70 in `Regions.Hotbar` (AS:240), like `FindShura` LOD:181 / `FindRunSkill` AP:1133 | none |
| Skill / hold / release | `TapSkill` AP:3413 (+Ctrl), `KeyDown`/`ReleaseHeldSkill` LOD:837; `Advance` releases a held key on every phase change (AP:5035) | the 1.2 s Shura hold must live inside one phase or be exempted like `LodAttack` |
| Buff key | `TryLodBuff` LOD:78 | LOD-only state |
| AUTO BUFF dialog | `TickAutoWarAsync` (keys-out step) closes it with `FindCloseButtonRightOf` | — |
| Item list | `CheckKeysAsync` LOD:209 (inline region `(0.62, 0.50, 0.38, 0.50)`, 3 OCR scales) | "[Auto buff scroll]" is not read anywhere yet |
| Player HP | **none** | new |
| Target-box name | **none** (LOD searches one known name over the whole client, LOD:307) | new |
| Probes | `tests/WuxAuto.SelfTest/Program.cs` `Array.IndexOf(args, "--x")` blocks → `FrameProbe.X(dir)` | none |
| Templates | any `src/WuxAuto/Assets/*.png` is copied (csproj wildcard) and found by file stem | none |

## 11. Working assumptions (asked, not answered — the user approved the design as is)

1. **One game per WuxAuto.** The alt runs on its own PC (or is the only game WuxAuto drives); running Auto
   War side by side with a Laby / LOD client on one PC is out of scope (one pilot drives the window in front,
   the Shura hold needs the game in front, one `settings.json`).
2. **No walking in Bohai.** Enemies come within Tab range of where the alt arrives / respawns; the fight
   cycle never moves the character. If the first live war shows a whole war of Tab without targets, that is a
   follow-up (a walk step), not part of this build.
3. **Death = back at the base by itself.** No revive dialog is handled. If a death sends the alt to a home
   town instead, the pilot takes that as "war over" and goes back to the cave — acceptable for now; the
   `war_dead` snapshots and the log will show it.
4. **"Regular War" box: left alone.** Start does not tick or untick it. If the game joins the war by itself
   (the minimap shows Bohai Outpost without the pilot having clicked OK), the pilot simply goes on with §4.4.
   Likewise any other war zone the game moves the alt to is treated as §4.7 "any other zone".

## 12. Left to the builder

Phase names and how the new phases split into ticks; exact regions (measure on frames, express as
`NormRect` or text-height offsets); OCR scales; poll intervals not fixed above; how the teleport and map-pick
code is parametrised as long as Laby / LOD stay byte-for-byte in behaviour; which settings get UI fields;
README / CLAUDE.md / QuickStart updates (required by the project conventions).

using System.Text.Json.Serialization;

namespace WuxAuto.Core;

public enum InputMode
{
    /// <summary>Bring the bound window to the foreground, then use SendInput. Safest.</summary>
    ForegroundSendInput = 0,

    /// <summary>
    /// Post window messages straight to the bound HWND. Works without focus for some
    /// Unity builds, but many ignore it. Everything still goes only to the bound window.
    /// </summary>
    BackgroundMessages = 1,
}

public enum RotationMode
{
    Key = 0,
    MouseDrag = 1,
}

/// <summary>What Start runs: the Labyrinth (Adept) dungeon or the Ultimate Den of Rebirth boss.</summary>
public enum RunMode
{
    AutoLaby = 0,
    AutoLod = 1,
    AutoWar = 2,
    AutoBlossom = 3,
}

/// <summary>Where the mini mode window opens on the screen (user: default bottom right).</summary>
public enum MiniPosition
{
    TopLeft,
    TopCenter,
    TopRight,
    MiddleLeft,
    Middle,
    MiddleRight,
    BottomLeft,
    BottomCenter,
    BottomRight,
}

public enum GateDetectionMode
{
    Delay = 0,
    Template = 1,
    Text = 2,
}

public enum CompassMode
{
    Template = 0,
    Brightness = 1,
    Auto = 2,
}

public sealed class AppSettings
{
    /// <summary>Settings file version - see SettingsStore.Migrate. 0 means "written before versioning".</summary>
    public int Version { get; set; }

    public InputSettings Input { get; set; } = new();
    public DetectionSettings Detection { get; set; } = new();

    /// <summary>
    /// Screen positions. Never saved: the game can run at any resolution and the map window does
    /// not scale proportionally, so a stored copy goes stale. <see cref="ResetPositions"/> rebuilds
    /// them from the built-in defaults at the start of every run.
    /// </summary>
    [JsonIgnore]
    public RegionSettings Regions { get; set; } = new();

    public SequenceSettings Sequence { get; set; } = new();
    public DungeonSettings Dungeon { get; set; } = new();

    /// <summary>Auto LOD: the Ultimate Den of Rebirth boss (<see cref="RunMode.AutoLod"/>).</summary>
    public LodSettings Lod { get; set; } = new();

    /// <summary>Auto War: the tribe war in Bohai Outpost (<see cref="RunMode.AutoWar"/>).</summary>
    public WarSettings War { get; set; } = new();

    /// <summary>Auto Blossom: the Jaso Practitioner route in Blossom Garden (<see cref="RunMode.AutoBlossom"/>).</summary>
    public BlossomSettings Blossom { get; set; } = new();

    public UiSettings Ui { get; set; } = new();

    /// <summary>Puts every screen position (regions and click fractions) back to the built-in defaults.</summary>
    public void ResetPositions()
    {
        Regions = new RegionSettings();
        (Dungeon ??= new DungeonSettings()).ResetPositions();
    }
}

public sealed class InputSettings
{
    /// <summary>Key that opens the inventory. Uses WPF key names: I, B, F1, ...</summary>
    public string InventoryKey { get; set; } = "I";

    /// <summary>Key held to rotate the camera clockwise.</summary>
    public string RotateKey { get; set; } = "D";

    /// <summary>
    /// Optional opposite turn key. When it differs from RotateKey the pilot takes the short
    /// way round; when it is the same the pilot keeps holding RotateKey until it lines up.
    /// </summary>
    public string RotateKeyReverse { get; set; } = "A";

    public InputMode Mode { get; set; } = InputMode.ForegroundSendInput;

    /// <summary>Refuse to send any input while another window is in front.</summary>
    public bool RequireForeground { get; set; } = true;

    /// <summary>Bring the game forward once when Start is pressed.</summary>
    public bool FocusOnStart { get; set; } = true;

    /// <summary>Put the cursor back where it was after every synthetic click.</summary>
    public bool RestoreCursor { get; set; } = true;

    /// <summary>Pause after bringing the game to the front before the first key (the inventory 'I').</summary>
    public int FocusSettleMs { get; set; } = 800;

    public int KeyHoldMs { get; set; } = 45;
    public int MoveSettleMs { get; set; } = 25;
    public int ClickHoldMs { get; set; } = 35;
    public int AfterClickMs { get; set; } = 220;
}

public sealed class DetectionSettings
{
    /// <summary>
    /// Minimum template score. Kept deliberately loose (0.70) so icon detection survives UI scale
    /// changes and slight re-crops; the scale search does most of the work, the threshold only
    /// filters obvious non-matches.
    /// </summary>
    public double TemplateThreshold { get; set; } = 0.70;

    /// <summary>0..1 score needed to trust the compass template.</summary>
    public double CompassThreshold { get; set; } = 0.55;

    /// <summary>Degrees of error tolerated while lining up with north.</summary>
    public double CompassToleranceDeg { get; set; } = 8;

    /// <summary>Scale the PNG templates with the client height (templates were captured at 1080p).</summary>
    public bool AutoScaleTemplates { get; set; } = true;

    public double TemplateScale { get; set; } = 1.0;

    /// <summary>Upscale factor applied to the OCR crop.</summary>
    public double OcrScale { get; set; } = 1.5;

    /// <summary>Minimum similarity (0..1) for fuzzy text matching.</summary>
    public double OcrMinSimilarity { get; set; } = 0.72;

    public int CompassSearchStepDeg { get; set; } = 15;

    public CompassMode CompassMode { get; set; } = CompassMode.Template;
}

public sealed class RegionSettings
{
    /// <summary>Where to look for the teleport scroll. Full screen by default.</summary>
    public NormRect Scroll { get; set; } = new(0, 0, 1, 1);

    /// <summary>
    /// Area containing the teleport destination list and its page chevrons. Kept away from the
    /// skill bar (bottom right) and the inventory page arrows so the pilot cannot click those.
    /// </summary>
    public NormRect TeleportList { get; set; } = new(0.35, 0.08, 0.28, 0.86);

    /// <summary>Area scanned by OCR for the destination name.</summary>
    public NormRect Ocr { get; set; } = new(0.35, 0.08, 0.28, 0.86);

    /// <summary>Top-right map button.</summary>
    public NormRect MapIcon { get; set; } = new(0.68, 0.0, 0.32, 0.28);

    /// <summary>Area checked for the "gate open" visual, if template mode is used.</summary>
    public NormRect Gate { get; set; } = new(0.10, 0.12, 0.80, 0.45);

    /// <summary>
    /// Where the "DUNGEON GATE OPENED!" banner can appear (template + OCR).
    /// </summary>
    public NormRect GateBanner { get; set; } = new(0.05, 0.05, 0.90, 0.55);

    /// <summary>Where the boss/monster name is shown in the world (nameplate / Tab marker label).</summary>
    public NormRect BossName { get; set; } = new(0.0, 0.0, 1.0, 1.0);

    /// <summary>The scrollable "Monsters on this map" list in the map window (names + row chevrons).</summary>
    public NormRect MapBossList { get; set; } = new(0.60, 0.22, 0.40, 0.70);

    /// <summary>
    /// The up/down arrows above the "Spawns" column that page the monster list. The map window
    /// is not laid out proportionally, so this is deliberately a tall band: the arrows sit at
    /// ~0.19 of the height at 1400x866 and at ~0.29 at 1024x768.
    /// </summary>
    public NormRect MapScroll { get; set; } = new(0.68, 0.09, 0.32, 0.28);

    /// <summary>The '>' chevron that advances the map/stage list.</summary>
    public NormRect MapChevron { get; set; } = new(0.55, 0.08, 0.45, 0.84);

    /// <summary>Where the chest/monster icon appears after pressing Tab.</summary>
    public NormRect MonsterIcon { get; set; } = new(0.20, 0.10, 0.60, 0.70);

    /// <summary>
    /// Where the game prints short system notices ("no walkable path", ...). Only used to put
    /// those messages into the log; nothing is decided from it.
    /// </summary>
    public NormRect SystemMessage { get; set; } = new(0.0, 0.30, 0.32, 0.55);

    /// <summary>
    /// Left side of the client: the combat log where "Attack succeeded!... damage(+N)" lines show.
    /// Deliberately generous: no frame of the message has been measured yet.
    /// </summary>
    public NormRect AttackMessage { get; set; } = new(0.0, 0.10, 0.40, 0.85);

    /// <summary>
    /// Search band for the top-right minimap panel. The panel keeps roughly the same pixel size
    /// at every resolution instead of scaling with the client, so fixed fractions miss it: the
    /// "x , y" line is found by OCR inside this band and the compass box is placed relative to
    /// it (see ScreenAnalyzer.CompassRegion). Coordinates/Compass are only the fallback.
    /// </summary>
    public NormRect Minimap { get; set; } = new(0.60, 0.0, 0.40, 0.42);

    /// <summary>
    /// The "x , y" coordinate readout under the minimap title (top right). Defaults come from a
    /// 2302x1353 client; the panel is anchored to the top-right corner, so re-crop if you change
    /// aspect ratio drastically.
    /// </summary>
    public NormRect Coordinates { get; set; } = new(0.895, 0.075, 0.10, 0.045);

    /// <summary>
    /// The searchlight/beam on the minimap that shows which way the camera faces. Rotate until
    /// flashlight.png matches here. Defaults to the bottom of the top-right minimap panel at
    /// 2302x1353; it follows the panel, so it scales with the client size.
    /// </summary>
    public NormRect Compass { get; set; } = new(0.880, 0.230, 0.120, 0.150);

    public PixelRect CompassRect(int clientWidth, int clientHeight)
        => Compass.ToPixels(clientWidth, clientHeight);

    /// <summary>
    /// The skill bar along the bottom of the client (1920x1080: x 600-1320, y 995-1050). The LOD
    /// Shura skill (<see cref="LodSettings.ShuraTemplate"/>) is looked for here. A tall band because
    /// the bar keeps roughly the same pixel size at every resolution.
    /// </summary>
    public NormRect Hotbar { get; set; } = new(0.20, 0.85, 0.60, 0.15);

    /// <summary>
    /// Auto War: the player's HP / MP box at the top left. The HP line "42833 / 42833" is the topmost
    /// "N / M" in it (20261026_042225_228_lod_no_entries, 1920x1080: 184,36 116x15; the MP line (y 63) and
    /// "100.000%" (y 91) are under it). The "0/3000" bar below it read inside the box on none of the 561
    /// saved frames (1024x768 to 2304x1488, --hp probe).
    /// </summary>
    public NormRect PlayerHp { get; set; } = new(0.0, 0.0, 0.30, 0.10);

    /// <summary>
    /// Auto War: the target box at the top centre - the target's name with its "N / M" numbers line
    /// under it. Name / numbers line tops: 1024x768 489,49 / 489,72 (20261021_000527_994_fail_RunToTarget),
    /// 1273x1024 589,65 / 590,95 (20261025_124800_296_canceldialog_unread), 1920x1080 916,69 / 918,100
    /// (20261024_152017_954_lod_approach) - y 6-7 % and 9 % at every size.
    /// </summary>
    public NormRect TargetBox { get; set; } = new(0.25, 0.03, 0.50, 0.12);

    /// <summary>
    /// Auto War: the band of the top-centre MENU button (three bars in a gold frame), the way into Tribe
    /// Status. Not proportional - centre / size on the saved frames: 1024x768 499,21 43x36; 1273x1024
    /// 581,27 58x47; 1440x900 825,25 51x42; 1920x1080 1182,29 61x50; 2302x1353 1479,37 76x63 (x 0.49-0.64,
    /// bottom under 0.06). menubutton.png is the dark square inside the gold frame of
    /// 20261024_170342_166_lod_no_entries (frame x 1149-1215, y 1-56; crop 1152,4 61x50).
    /// </summary>
    public NormRect MenuButton { get; set; } = new(0.30, 0.0, 0.40, 0.08);

    /// <summary>
    /// Auto War: the item status list at the bottom right ("[Auto buff scroll] dd.mm.yyyy", the keys, the scrolls) - the
    /// band the Laby/LOD key check reads (inline in CheckKeysAsync).
    /// </summary>
    public NormRect ItemList { get; set; } = new(0.62, 0.50, 0.38, 0.50);
}

/// <summary>
/// Auto Blossom (user, 2026-10-29) - see AutomationPilot.Blossom.cs and docs/superpowers/specs/2026-10-29-auto-blossom-design.md.
/// Teleport to Blossom Garden, line up on the two pillars, walk the waypoints, kill and loot every Jaso Practitioner.
/// </summary>
public sealed class BlossomSettings
{
    /// <summary>Teleport list entry and minimap zone.</summary>
    public string Destination { get; set; } = "Blossom Garden";

    /// <summary>The teleport list page it is on (user: page 1).</summary>
    public int ListPage { get; set; } = 1;

    /// <summary>The pillar base template (Assets): two of them side by side = lined up.</summary>
    public string PillarTemplate { get; set; } = "blossompillar";

    public double PillarMatchScore { get; set; } = 0.70;

    /// <summary>Lined up = the midpoint of the two pillars within this fraction of the client width of its centre.</summary>
    public double PillarCenterTolerance { get; set; } = 0.06;

    /// <summary>One D tap (user: 100 ms).</summary>
    public int TurnTapMs { get; set; } = 100;

    /// <summary>The taps the user expects to need (7); past them the turn goes on to <see cref="TurnMaxTaps"/>.</summary>
    public int TurnTaps { get; set; } = 7;

    /// <summary>Give up turning after this many taps (about a full turn) and walk anyway.</summary>
    public int TurnMaxTaps { get; set; } = 50;

    /// <summary>The route, "x,y" world coordinates in order (user). From the last one it goes back to <see cref="LoopFrom"/>.</summary>
    public List<string> Waypoints { get; set; } = DefaultWaypoints();

    /// <summary>1-based waypoint the route loops back to after the last one (user: 17 -> 6).</summary>
    public int LoopFrom { get; set; } = 6;

    public int WaypointTolerance { get; set; } = 60;

    /// <summary>At most this long standing at a reached waypoint, tabbing (user: 5 s or less - the AFK checker).</summary>
    public int WaypointWaitMs { get; set; } = 3000;

    /// <summary>Tab while walking and pausing (user: "keep tabbing").</summary>
    public int TabEveryMs { get; set; } = 1000;

    /// <summary>The monster to kill (the target bar); its chest counts too (user).</summary>
    public string TargetName { get; set; } = "[S]Jaso Practitioner";

    /// <summary>The target gone this long = dead.</summary>
    public int TargetGoneMs { get; set; } = 2000;

    /// <summary>A fight whose target never goes is given up after this long (user: a Jaso dies within 2 min).</summary>
    public int FightMaxMs { get; set; } = 120000;

    /// <summary>Loot this long after the kill (user: 1 s), every <see cref="LootEveryMs"/> (0.2 s) for <see cref="LootMs"/> (5 s).</summary>
    public int LootDelayMs { get; set; } = 1000;

    public int LootMs { get; set; } = 5000;

    public int LootEveryMs { get; set; } = 200;

    public static List<string> DefaultWaypoints() =>
    [
        "19136,18463", "15923,18292", "15916,16184", "14883,16026", "13953,15970", "13947,14697",
        "13370,14493", "13085,13906", "12944,13692", "12987,13184", "13557,12775", "14101,12622",
        "14546,12826", "14837,13243", "14923,13591", "14838,14013", "14359,14565",
    ];
}

/// <summary>Auto LOD (user, 2026-10-21) - see AutomationPilot.Lod.cs.</summary>
public sealed class LodSettings
{
    /// <summary>Teleport list entry (right under the Labyrinth) and minimap zone.</summary>
    public string Destination { get; set; } = "Ultimate Den of Rebirth";

    /// <summary>
    /// The teleport list page it is on (user: page 2). Not on that page = out of keys/entries -> switch
    /// to Auto Laby.
    /// </summary>
    public int ListPage { get; set; } = 2;

    /// <summary>
    /// The searchlight facing SOUTH, cropped on this map by the user (its minimap background is not
    /// black); the south turn matches it as is. Missing -> Dungeon.FlashlightTemplate turned 180 degrees.
    /// </summary>
    public string FlashlightTemplate { get; set; } = "flashlightLOD";

    /// <summary>"Facing south" pass mark: the LOD minimap is busier, the best headings scored 0.78-0.83.</summary>
    public double FlashlightMatchScore { get; set; } = 0.80;

    /// <summary>The boss name on the target bar (matched fuzzily, whole words).</summary>
    public string BossName { get; set; } = "Bai Gu Great";

    /// <summary>
    /// Walk to <see cref="TargetX"/>/<see cref="TargetY"/> after the south turn, pressing Tab, then
    /// wait there tabbing (UI "Walk to the boss spot (LOD)"; on, settings v22). User, 2026-10-28:
    /// "sometimes the boss doesn't go near the char. move the character forward to 1199, 1782 ...
    /// after facing south" - the teleport lands at ~1195,1578. Off (user, 2026-10-27: "stay still on
    /// the same place upon teleport, just keep tabbing until the boss name shows"): the character stays
    /// where it is and Tab is pressed every <see cref="TabEveryMs"/>. The south turn happens either way
    /// (user: so the ring round the boss's feet can be targeted).
    /// </summary>
    public bool WalkToBossSpot { get; set; } = true;

    /// <summary>
    /// Where to walk to with <see cref="WalkToBossSpot"/> (user, 2026-10-28; the boss fought at ~1212,1775
    /// on 2026-10-24). Until v22 it was 1191,2413, the middle of the map.
    /// </summary>
    public int TargetX { get; set; } = 1199;
    public int TargetY { get; set; } = 1782;
    public int TargetTolerance { get; set; } = 60;

    /// <summary>Tab while waiting for the boss (or walking to it; user: every 1-2 s).</summary>
    public int TabEveryMs { get; set; } = 1500;

    /// <summary>Tab while turning south - spammed (user).</summary>
    public int TurnTabEveryMs { get; set; } = 400;

    /// <summary>Skill spam interval while the boss is targeted.</summary>
    public int SkillEveryMs { get; set; } = 300;

    /// <summary>Hold the skill key down while attacking instead of spamming it (user: either works).</summary>
    public bool HoldSkill { get; set; }

    /// <summary>
    /// Walk up to the boss (user, 2026-10-24; UI "Walk up to the boss (LOD)"): after the first
    /// "Attack succeeded" of a fight, stop attacking (<see cref="StopAttackKey"/> x
    /// <see cref="ApproachStopPresses"/>) and Ctrl+click the pink ring round the boss's feet (no ring:
    /// just below its cyan belt), then attack again. Ctrl+click walks there; a plain click attacks.
    /// </summary>
    public bool ApproachBoss { get; set; } = true;

    /// <summary>Stop-attack presses before each Ctrl+click (live: 2 were enough for a walk click).</summary>
    public int ApproachStopPresses { get; set; } = 3;

    /// <summary>Time for the walk after a Ctrl+click (live: ~165 units in under 1.8 s).</summary>
    public int ApproachWaitMs { get; set; } = 1500;

    /// <summary>Ctrl+clicks per walk up before attacking from where the character is.</summary>
    public int ApproachTries { get; set; } = 2;

    /// <summary>
    /// Keep to the ring (user, 2026-10-27: "try to go to the ring as much as possible, do a periodical
    /// check"): after the first walk up of a fight, the ring is looked for every this many ms while
    /// attacking; the character further out than <see cref="RingStayMax"/> -> the same walk up again
    /// (Shura, Ctrl+click). The ring only, never the belt: no ring in view is mostly the boss's body over
    /// the camera with the character under it. A walk that did not get there waits 3x as long before the
    /// next check. 0 = only the walk after the first hit, as before. Needs <see cref="ApproachBoss"/>.
    /// </summary>
    public int RingCheckEveryMs { get; set; } = 3000;

    /// <summary>
    /// The periodic check's "still on the ring": the character's distance from the ring's centre in ring
    /// radii (ScreenAnalyzer.DistanceFromRing) - 1 = on the ring itself. The walk up stops at 0.6, so a character it just put there is not sent again. Saved
    /// first-walk frames read 0.07-0.89 on or under the boss and 1.82-2.0 beside the ring.
    /// </summary>
    public double RingStayMax { get; set; } = 1.0;

    /// <summary>
    /// Auto-buff key (user: O; autohunt is disabled on this map). Pressed on arrival and again every
    /// <see cref="BuffEveryMs"/> (4 min 50 s) - most buffs last 5 min (user). Empty = no buffing.
    /// </summary>
    public string BuffKey { get; set; } = "O";

    public int BuffEveryMs { get; set; } = 290000;

    /// <summary>
    /// Shura (user, 2026-10-25): the skill that stops the attack sequence, instead of spamming Space
    /// - before a mid-fight buff and before the walk up to the boss. Its icon (shura.png, the blue
    /// burst) is looked for on the skill bar (<see cref="RegionSettings.Hotbar"/>) and clicked
    /// <see cref="ShuraPresses"/> times, <see cref="ShuraEveryMs"/> apart. Not found -> the
    /// <see cref="ShuraKey"/> the player set for its slot; no key either -> <see cref="StopAttackKey"/>
    /// as before. Empty = never look for the icon.
    /// </summary>
    public string ShuraTemplate { get; set; } = "shura";

    /// <summary>Minimum template score for the Shura icon (saved frames: 0.95-1.00 on the bar, at most 0.70 without one).</summary>
    public double ShuraMatchScore { get; set; } = 0.80;

    /// <summary>
    /// Fallback when the icon is not found: the hotbar key of the slot the player put Shura in
    /// (UI "Shura key (LOD)"; default E, user). Empty = straight to <see cref="StopAttackKey"/>.
    /// </summary>
    public string ShuraKey { get; set; } = "E";

    /// <summary>Clicks / key taps of Shura per stop (user: 3 are enough).</summary>
    public int ShuraPresses { get; set; } = 3;

    /// <summary>Pause between them (user: 0.2 s is fine).</summary>
    public int ShuraEveryMs { get; set; } = 200;

    /// <summary>
    /// A buff mid-fight only works once the character stops attacking (user): tap this key
    /// <see cref="StopAttackPresses"/> times first (user: Space x5). The last resort after
    /// <see cref="ShuraTemplate"/> and <see cref="ShuraKey"/>.
    /// </summary>
    public string StopAttackKey { get; set; } = "Space";

    /// <summary>15 x 100 ms (~1.5 s): 5 quick presses did not stop the attack, the buff was ignored (user).</summary>
    public int StopAttackPresses { get; set; } = 15;

    /// <summary>What the boss says when it dies (user) - the trigger to loot.</summary>
    public string DeathText { get; set; } = "see you in hell";

    /// <summary>
    /// Loot key, spammed for <see cref="LootMs"/> after the death text: there is no autohunt here to
    /// pick up the drops (user). "`" / "~" = the key left of 1.
    /// </summary>
    public string LootKey { get; set; } = "`";

    /// <summary>Loot time, at least 10 s (user); no Tab meanwhile.</summary>
    public int LootMs { get; set; } = 10000;

    /// <summary>
    /// The boss name / target-bar treasure icon gone this long mid-fight = dead, also without the
    /// death text: loot (user, 2026-10-23: 2 s, was 5 s).
    /// </summary>
    public int LootAfterMissingMs { get; set; } = 2000;

    /// <summary>Boss name missing this long while attacking = lost target, walk/Tab again.</summary>
    public int BossGoneMs { get; set; } = 10000;

    /// <summary>A coordinate jump this big while attacking = killed and put back at the start.</summary>
    public int TeleportJump { get; set; } = 400;

    /// <summary>Wait after being put back at the start before turning south.</summary>
    public int RespawnSettleMs { get; set; } = 3000;
}

/// <summary>Auto War (user, 2026-10-27): the tribe war in Bohai Outpost - see AutomationPilot.War.cs.</summary>
public sealed class WarSettings
{
    /// <summary>Teleport list entry and minimap zone of the war map (user).</summary>
    public string Destination { get; set; } = "Bohai Outpost";

    /// <summary>The row of the war in the Tribe Status window (user's screenshot).</summary>
    public string TribeRowText { get; set; } = "Bohai Outpost (Adept)";

    /// <summary>The Tribe Status tile in the Menu window (user's screenshot).</summary>
    public string MenuTileText { get; set; } = "Tribe Status";

    /// <summary>How long before the war opens the pilot starts moving (user: 10 s).</summary>
    public int LeadMs { get; set; } = 10000;

    /// <summary>How long it waits for the "Confirm to move?" dialog to appear (my call: 2 min).</summary>
    public int DialogWaitMs { get; set; } = 120000;

    /// <summary>The TAB target option the war needs (user: Enemy worth CP).</summary>
    public string TabTargetOption { get; set; } = "Enemy worth CP";

    /// <summary>
    /// The plain skill buffs clicked on the skill bar, in order, when there is no autobuff (user's crops): warbuff1, 3, 4, 5,
    /// 6. warbuff2 is the sword's buff, in <see cref="WeaponBuffIcons"/> since settings v23 (user, 2026-10-28).
    /// </summary>
    public List<string> BuffIcons { get; set; } = DefaultBuffIcons();

    /// <summary>
    /// The weapon buffs (user, 2026-10-28: "the buff of different weapons"): warbuff2 (sword), warweapon1 (white-gold
    /// lightning around dark armour), warweapon2 (blue swirl). Skill buffs, clicked after <see cref="BuffIcons"/> when
    /// there is no autobuff. A character uses one weapon, so usually only one of them is on its bar - untick the others in
    /// <see cref="DisabledBuffs"/>. Kept apart from BuffIcons, which a v0.19.0 copy sharing settings.json requires in full
    /// at Start. An empty list stays empty (no refill).
    /// </summary>
    public List<string> WeaponBuffIcons { get; set; } = DefaultWeaponBuffIcons();

    /// <summary>
    /// Item buffs placed on the skill bar (user, 2026-10-28: "optional but item buffs can be placed on the skill bar"):
    /// waritem1 (light scroll with a glyph), waritem2 (blue scroll); clicking one uses an item. Clicked after the skill and weapon buffs, with every rebuff, and with autobuff too (the
    /// buff key does not use them). An empty list stays empty (no refill: the user may have taken them out).
    /// </summary>
    public List<string> ItemBuffIcons { get; set; } = DefaultItemBuffIcons();

    /// <summary>
    /// Buff icons (of <see cref="BuffIcons"/>, <see cref="WeaponBuffIcons"/> and <see cref="ItemBuffIcons"/>) the user
    /// unticked in Settings > Auto War > Buffs (user, 2026-10-28): never clicked, not looked for at Start. Empty = every
    /// buff on.
    /// </summary>
    public List<string> DisabledBuffs { get; set; } = [];

    /// <summary>
    /// The "X buff" (user: icon 4, the bluish-white one with an X; it drops when another player hits): clicked again on the
    /// skill bar in every fight cycle, once a target shows, before Shura.
    /// </summary>
    public string XBuffIcon { get; set; } = "warbuff4";

    /// <summary>Gap between two buff clicks (user: 1 s); never under 800 ms, the buff animation (user, 2026-10-28).</summary>
    public int BuffClickGapMs { get; set; } = 1000;

    /// <summary>How often the buffs are renewed (my call: 5 min 45 s; the user's buffs last at least 6 min).</summary>
    public int BuffEveryMs { get; set; } = 345000;

    /// <summary>How long the Shura key is held at the start of each fight cycle, before the skill taps (my call).</summary>
    public int ShuraHoldMs { get; set; } = 1200;

    /// <summary>How long the target must be gone before the pilot acts (my call).</summary>
    public int TargetGoneMs { get; set; } = 2000;

    /// <summary>The text that says the war gate is open, read on the whole screen (user).</summary>
    public string GateText { get; set; } = "Gate is Open";

    /// <summary>The teleport list page of the home map (user: page 3).</summary>
    public int ListPage { get; set; } = 3;

    /// <summary>The combat log word that says the character arrived (user).</summary>
    public string ArrivedText { get; set; } = "Arrived";

    /// <summary>How long it waits for the arrival line (my call).</summary>
    public int ArriveWaitMs { get; set; } = 90000;

    /// <summary>
    /// Minimum template score of a buff icon (my call). 0.90, not 0.80: warbuff2 (the small grey streak)
    /// scored up to 0.871 on floor texture in the skill bar band of saved frames (0.80 or more on 32 of 561,
    /// e.g. 20261020_160055_722_nogate_s4 and the gate_view frames); the other crops stayed under 0.76.
    /// Shura, checked with the same score, reads 0.951-1.000 on every real bar and at most 0.781 elsewhere.
    /// </summary>
    public double IconMatchScore { get; set; } = 0.90;

    /// <summary>The map to go back to after the war, as the teleport list shows it (user; empty = not set).</summary>
    public string HomeMap { get; set; } = "";

    /// <summary>The monster to hunt on the home map (user; empty = not set).</summary>
    public string HomeMonster { get; set; } = "";

    /// <summary>The buff is ticked on: not in <see cref="DisabledBuffs"/> (any case).</summary>
    public bool IsBuffOn(string icon) => DisabledBuffs?.Contains(icon, StringComparer.OrdinalIgnoreCase) != true;

    /// <summary>
    /// Ticks a buff on or off. A new list replaces <see cref="DisabledBuffs"/> instead of changing it: the settings panel
    /// calls this on the UI thread while the pilot's loop thread may be going through the old one.
    /// </summary>
    public void SetBuffOn(string icon, bool on)
    {
        var disabled = DisabledBuffs ?? [];
        if (on)
            DisabledBuffs = [.. disabled.Where(d => !string.Equals(d, icon, StringComparison.OrdinalIgnoreCase))];
        else if (IsBuffOn(icon))
            DisabledBuffs = [.. disabled, icon];
    }

    internal static List<string> DefaultBuffIcons() => ["warbuff1", "warbuff3", "warbuff4", "warbuff5", "warbuff6"];

    internal static List<string> DefaultWeaponBuffIcons() => ["warbuff2", "warweapon1", "warweapon2"];

    internal static List<string> DefaultItemBuffIcons() => ["waritem1", "waritem2"];
}

public sealed class SequenceSettings
{
    /// <summary>What Start runs (UI: the mode buttons next to Start).</summary>
    public RunMode Mode { get; set; } = RunMode.AutoLaby;

    /// <summary>
    /// After an error, start over from the top (focus, zone check, teleport or resume) instead of
    /// stopping (user, 2026-10-22; UI: "Restart on error"). At most <see cref="MaxRestarts"/> within
    /// 30 minutes, then it stops as before.
    /// </summary>
    public bool RestartOnError { get; set; } = true;

    /// <summary>
    /// In town before a teleport: read the item list for the keys (Temple of Fiends Key = Labyrinth,
    /// Den of Rebirth = LOD); out -> the other mode, both out -> J (auto war) and stop.
    /// </summary>
    public bool CheckKeys { get; set; }

    public int RestartDelayMs { get; set; } = 5000;

    public int MaxRestarts { get; set; } = 5;

    /// <summary>
    /// Labyrinth teleport clicked but still in town: wait this long after it (server lag), then count it
    /// as out of Temple of Fiends Keys and switch to Auto LOD (user: 2 min is fast and reliable).
    /// </summary>
    public int LabyKeyWaitSeconds { get; set; } = 120;

    /// <summary>Opens the autohunt settings with the "Regular War" box (user: S).</summary>
    public string AutohuntSettingsKey { get; set; } = "S";

    /// <summary>
    /// While running, watch for the "SERVERS" screen (disconnected) and log back in: Online ->
    /// Connect -> Login -> OK ("Login OK") -> START GAME, then restart the run once the town
    /// (<see cref="DungeonSettings.ReturnDestination"/>) shows on the minimap. Only clicks
    /// buttons - the game client has to remember the account.
    /// </summary>
    public bool AutoReconnect { get; set; } = true;

    /// <summary>Pause after every reconnect click.</summary>
    public int ReconnectStepDelayMs { get; set; } = 800;

    /// <summary>
    /// A dialog with this text (outside the minimap title) is dismissed with its "Cancel" button
    /// (user: the "Bohai Outpost" dialog). Checked every 5 s while running; empty = off.
    /// </summary>
    public string CancelDialogText { get; set; } = "Bohai";

    /// <summary>
    /// Auto-confirm (user, 2026-10-25, for selling items): while the game is bound and the pilot is
    /// not running, a Ctrl + right click on the game starts watching every frame for the game's confirm
    /// dialog ("Fist Scroll Box Confirm to use?" with CANCEL / OK) and its <see cref="AutoConfirmButton"/>
    /// is clicked the moment it shows (one frame + OCR, ~0.2 s). The watch lasts 3 s per click
    /// (the dialog is up long before that). A plain right click leaves the dialog to the player. Check box "Auto confirm (Ctrl + right click)".
    /// </summary>
    public bool AutoConfirmDialogs { get; set; } = true;

    /// <summary>The word that marks the dialog's message line (one letter of OCR noise allowed).</summary>
    public string AutoConfirmText { get; set; } = "Confirm";

    /// <summary>
    /// The button to click: a line reading exactly this under the message, else the lowest gold
    /// button under it (OK and USE ALL are gold, CANCEL is dark).
    /// </summary>
    public string AutoConfirmButton { get; set; } = "OK";

    /// <summary>
    /// Kill alert (user, 2026-10-26: friends run the pilot on a second PC and LOD does not always
    /// pick up every drop - a reminder to check): a chime when a Labyrinth boss dies (every stage)
    /// and when an LOD kill's loot step ends. The sound is <see cref="KillSound"/>, else
    /// <c>KillAlert.CustomChimeName</c> (desktop project) in the templates folder, else the built-in
    /// chime. Check box "Kill chime".
    /// </summary>
    public bool KillChime { get; set; } = true;

    /// <summary>
    /// The kill chime's sound file, MP3 or WAV (user, 2026-10-27; a picked OGG is kept as a WAV). Empty = the built-in chime.
    /// Set with the "Choose..." button next to the check box, which copies the file into
    /// <c>%LOCALAPPDATA%\WuxAuto\sounds</c> first, so it keeps playing after the original is moved.
    /// </summary>
    public string KillSound { get; set; } = string.Empty;

    /// <summary>The same moments as <see cref="KillChime"/>, as a Windows notification. Check box "Kill notification".</summary>
    public bool KillNotification { get; set; } = true;

    /// <summary>
    /// On Start in town: Settings (Escape) -> VISUAL -> "Font size" is checked against
    /// <see cref="FontSizePercent"/> .. <see cref="FontSizeMaxPercent"/> (user: 110 - 120 % read well).
    /// Inside the range it is left alone; outside it is moved to the nearer end with the row's
    /// chevrons, then SAVE. 0 = leave it alone.
    /// </summary>
    public int FontSizePercent { get; set; } = 110;

    /// <summary>Upper end of the accepted font size range (see <see cref="FontSizePercent"/>).</summary>
    public int FontSizeMaxPercent { get; set; } = 120;

    /// <summary>
    /// On Start in town, in the same Settings visit as the font size: VISUAL -> "TAB target" (at the
    /// bottom of the list, scrolled to) is set to Default with the row's left chevron - Default is
    /// the first of Default, Nearest enemy, Enemy worth CP, Nearest monster (user, 2026-10-24) -
    /// then SAVE. Off = leave it alone.
    /// </summary>
    public bool TabTargetDefault { get; set; } = true;

    /// <summary>
    /// In town, before every teleport (user, 2026-10-25): a restart after an error, a closed stage 5,
    /// the LOD timer or a re-login can leave the autohunt running in town. The pilot taps the
    /// autohunt key and reads the combat log: <see cref="AutohuntEndedText"/> = it was on and is
    /// off now; <see cref="AutohuntStartedText"/> = it was off, so the key is tapped again. Nothing
    /// readable after the tap: one more tap (the first key after focusing is sometimes eaten; two
    /// unread taps leave the state as it was), then a warning and the run carries on.
    /// </summary>
    public bool AutohuntOffInTown { get; set; } = true;

    /// <summary>
    /// In town, before every teleport (user, 2026-10-25): the Run skill must be on. Its icon
    /// (<see cref="RunSkillTemplate"/>, the user's flame icon cropped inside the ring) is looked for
    /// on the skill bar (<see cref="RegionSettings.Hotbar"/>); an active skill has a gold ring round
    /// it (<see cref="ScreenAnalyzer.RingFraction"/> at least <see cref="RunSkillRingMin"/>). No ring
    /// -> the icon is clicked, up to 3 times, a fresh frame after each. Icon not on the bar ->
    /// <see cref="RunSkillKey"/> once per Start (the key is a toggle and without the icon the state
    /// is unknown, so it is never pressed twice). Off = never look.
    /// </summary>
    public bool RunSkillCheck { get; set; } = true;

    /// <summary>The Run skill icon (runskill.png, 33 px inside the ring of the user's 54 px crop). Empty = never look.</summary>
    public string RunSkillTemplate { get; set; } = "runskill";

    /// <summary>
    /// Minimum template score for the Run icon on the bar (saved frames: 0.975-1.000 on every bar
    /// from 1024 to 2304 wide; a login screen scored 0.81 at x0.5, nothing else above 0.74).
    /// </summary>
    public double RunSkillMatchScore { get; set; } = 0.88;

    /// <summary>
    /// Fallback when the icon is not on the bar: the hotbar key of the slot the player put the Run
    /// skill in (UI "Run key", user: W). Empty = only the icon.
    /// </summary>
    public string RunSkillKey { get; set; } = "W";

    /// <summary>
    /// Share of the ring (36 directions round the icon) that must read gold for "on": the user's
    /// active icon scores ~0.5 (the ring is drawn under parts of the icon), an inactive one 0.
    /// </summary>
    public double RunSkillRingMin { get; set; } = 0.25;

    /// <summary>Combat-log line printed when the autohunt turns on.</summary>
    public string AutohuntStartedText { get; set; } = "Auto hunt started";

    /// <summary>Combat-log line printed when the autohunt turns off.</summary>
    public string AutohuntEndedText { get; set; } = "Auto hunt ended";

    /// <summary>
    /// The other "autohunt off" line, printed in town: "Auto hunt stopped; Smart Farm journey
    /// cancelled." (2026-10-27, autohunt_unread frame at 1440x900 - J in town starts a Smart Farm
    /// journey back to the hunting ground, the second J cancels it). Matched by its start.
    /// </summary>
    public string AutohuntStoppedText { get; set; } = "Auto hunt stopped";

    /// <summary>Text on the destination row to click (OCR, fuzzy matching).</summary>
    public string DestinationText { get; set; } = "Labyrinth";

    public int InventoryOpenMs { get; set; } = 750;
    public int RetryDelayMs { get; set; } = 350;
    public int MaxRetries { get; set; } = 12;
    public int LoadingMinMs { get; set; } = 1600;
    public int LoadingSettleMs { get; set; } = 550;
    public int LoadingTimeoutMs { get; set; } = 20000;

    public RotationMode RotationMode { get; set; } = RotationMode.Key;

    /// <summary>Milliseconds to hold the rotate key per nudge.</summary>
    public int RotateTapMs { get; set; } = 160;

    /// <summary>Time for the camera to stop after a nudge.</summary>
    public int RotateSettleMs { get; set; } = 280;

    public int AlignMaxAttempts { get; set; } = 60;

    /// <summary>Mouse drag pixels per degree when RotationMode is MouseDrag.</summary>
    public double DragPixelsPerDegree { get; set; } = 6.0;

    public GateDetectionMode GateMode { get; set; } = GateDetectionMode.Delay;
    public int GateDelayMs { get; set; } = 1500;
    public int GateTimeoutMs { get; set; } = 20000;
    public string GateText { get; set; } = "open";

    public bool Loop { get; set; }
    public int LoopDelayMs { get; set; } = 5000;

    /// <summary>Don't send input, only log what would happen.</summary>
    public bool DryRun { get; set; }
}

/// <summary>
/// The multi-stage Labyrinth run: gate banner -> map window -> next stage -> boss alive/dead,
/// repeated for every stage, then face north and run to the target coordinate.
/// </summary>
public sealed class DungeonSettings
{
    /// <summary>Run the stage loop after teleporting in. Off = the old single-shot behaviour.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Boss name for each stage, in order. The last entry ends the loop.</summary>
    public List<string> Stages { get; set; } = new()
    {
        "Sanctified Unicorn",
        "Spectral",
        "Sundersword",
        "Ragesoul Heathen",
    };

    /// <summary>Template that announces the open gate (Assets or %LOCALAPPDATA%\WuxAuto\templates).</summary>
    public string GateTemplate { get; set; } = "dungeongateopen";

    /// <summary>OCR text used when the template is not available or does not match.</summary>
    public string GateText { get; set; } = "DUNGEON GATE OPENED";

    public int GateTimeoutMs { get; set; } = 40000;
    public int GateGiveUpMs { get; set; } = 180000;
    public int GatePollMs { get; set; } = 500;
    public int GateOcrEveryNPolls { get; set; } = 2;

    /// <summary>
    /// Between stages the banner is only on screen for a few seconds and can be missed. After
    /// this long without it the map is opened anyway (the first gate, after the teleport, takes
    /// ~35 s and always waits for the banner). 0 = always wait for the banner.
    /// </summary>
    public int GateFallbackMs { get; set; } = 15000;

    /// <summary>
    /// Pause after the gate banner appears before the map button is clicked. The character is
    /// still finishing the kill / walking to the gate, and "no walkable path" shows up if the
    /// map is opened too early.
    /// </summary>
    public int GateToMapDelayMs { get; set; } = 3000;

    /// <summary>Time to let the map window open after clicking the map button.</summary>
    public int MapOpenDelayMs { get; set; } = 1000;

    /// <summary>How often the map button is clicked again when the map window does not appear.</summary>
    public int MapReopenRetries { get; set; } = 3;

    /// <summary>
    /// System notice that means the character could not walk where it was sent. Logged when it is
    /// visible while the map is opened; leave empty to skip the check.
    /// </summary>
    public string NoPathText { get; set; } = "no walkable path";

    /// <summary>Time to let the game react to the '>' / next-stage click.</summary>
    public int StageDelayMs { get; set; } = 2000;

    /// <summary>How long to wait for the boss before pressing Tab and looking for it.</summary>
    public int BossSpawnWaitMs { get; set; } = 60000;

    /// <summary>How often the boss name/marker is re-checked while it is alive.</summary>
    public int BossPollMs { get; set; } = 2000;

    /// <summary>
    /// Without the gate banner, the boss only counts as dead once its name/marker has been gone
    /// this long without a break. The name flickers away for a few seconds while the boss is
    /// off camera or hidden by effects, and two quick misses once ended a fight early.
    /// </summary>
    public int BossGoneMs { get; set; } = 10000;

    public string TabKey { get; set; } = "Tab";
    public int TabSettleMs { get; set; } = 800;

    /// <summary>Key that turns the game's autohunt on, pressed once the boss should have spawned.</summary>
    public string AutohuntKey { get; set; } = "J";

    /// <summary>Click the boss name row in the map window (it selects/enters the stage).</summary>
    public bool ClickBossName { get; set; } = true;

    /// <summary>Time to let the game react to the boss-name click before looking for the '>'.</summary>
    public int BossNameClickDelayMs { get; set; } = 800;

    /// <summary>Scroll wheel notches per step when paging the map's monster list.</summary>
    public int MapScrollNotches { get; set; } = 3;

    /// <summary>How many scroll steps to try before giving up on finding a boss name.</summary>
    public int MapScrollMaxSteps { get; set; } = 8;

    public int MapScrollSettleMs { get; set; } = 600;

    /// <summary>Client fractions where the wheel is aimed (inside the monster list).</summary>
    [JsonIgnore]
    public double MapWheelX { get; set; } = 0.75;
    [JsonIgnore]
    public double MapWheelY { get; set; } = 0.55;

    /// <summary>Client fraction of the per-row '>' chevron at the right edge of the list.</summary>
    [JsonIgnore]
    public double MapRowChevronX { get; set; } = 0.96;

    /// <summary>
    /// Template for the '>' of the row that is currently selected. Clicking the boss name
    /// highlights the row and changes the chevron's colours, so this is a separate crop.
    /// </summary>
    public string RowChevronTemplate { get; set; } = "rightchevronfoc";

    public double RowChevronThreshold { get; set; } = 0.55;

    /// <summary>
    /// The game only starts hitting the boss once it has been selected: after the boss has been
    /// visible for this long the pilot left clicks the middle of the window.
    /// </summary>
    public int BossSelectDelayMs { get; set; } = 2000;

    /// <summary>Where to click to select the boss, as a fraction of the client area.</summary>
    [JsonIgnore]
    public double BossSelectX { get; set; } = 0.5;
    [JsonIgnore]
    public double BossSelectY { get; set; } = 0.5;

    /// <summary>
    /// Distance between select-click attempts, as a fraction of the client height. Attempt 1 is
    /// the centre, then a ring of 8 points one step out, then a ring two steps out.
    /// </summary>
    [JsonIgnore]
    public double BossSelectStep { get; set; } = 0.06;

    /// <summary>
    /// Start of the combat-log line the game prints on the left for every hit
    /// ("Attack succeeded!Normal damage(+46819)..."). The pilot keeps clicking around the centre
    /// until a new one scrolls in. Empty = click the centre once, no check.
    /// </summary>
    public string AttackConfirmText { get; set; } = "Attack succeeded";

    /// <summary>How long to watch for <see cref="AttackConfirmText"/> after each click.</summary>
    public int AttackConfirmMs { get; set; } = 1200;

    /// <summary>Select clicks to try before giving up and just watching the boss.</summary>
    public int BossSelectMaxTries { get; set; } = 17;

    /// <summary>If the boss name is not visible, the name is only trusted after this many polls.</summary>
    public int BossNameConfirmPolls { get; set; } = 2;

    /// <summary>
    /// Searchlight template for "facing north" (Assets or the templates folder). flashlight2.png
    /// is a straight-up beam cropped at 1024x768; falls back to flashlight.png when missing.
    /// </summary>
    public string FlashlightTemplate { get; set; } = "flashlight3";

    /// <summary>Flashlight score that counts as "facing north".</summary>
    public double FlashlightMatchScore { get; set; } = 0.85;

    /// <summary>
    /// The labyrinth always loads the camera at the same angle, so the pilot first turns to the
    /// left for this long to park the searchlight on north before it starts fine-tuning.
    /// </summary>
    public int RotateLeftSweepMs { get; set; } = 0;

    /// <summary>
    /// Facing north is done in short taps of <see cref="RotateLeftKey"/>: hold this long, let the
    /// camera settle (Sequence.RotateSettleMs), score the searchlight, repeat until it matches.
    /// </summary>
    public int RotateStepMs { get; set; } = 180;

    /// <summary>
    /// After the 180 ms taps found the peak, fine taps of this length try both directions and keep
    /// whichever scores higher: a 180 ms tap turns ~15 degrees, so the coarse peak could still be
    /// NNW (user, run 25). 0 = off.
    /// </summary>
    public int NorthRefineTapMs { get; set; } = 40;

    /// <summary>
    /// Right after the labyrinth has loaded: zoom out (mouse wheel towards you, this many notches)
    /// and face north straight away, so the camera already looks at the stage 4 gate later.
    /// 0 = no zoom.
    /// </summary>
    public int ZoomOutNotches { get; set; } = 15;

    /// <summary>
    /// Short pause after the load check. The zoom itself now waits for the zone name on the
    /// minimap (the still loading screen passes the load check, and loads are sometimes slow).
    /// </summary>
    public int ArrivalDelayMs { get; set; } = 300;

    /// <summary>Face north right after the labyrinth has loaded (not only after stage 4).</summary>
    public bool NorthOnArrival { get; set; } = true;

    /// <summary>Key held during the left sweep (A in the default layout).</summary>
    public string RotateLeftKey { get; set; } = "A";

    /// <summary>
    /// Points walked to, in order, before <see cref="TargetX"/>/<see cref="TargetY"/>. The direct
    /// line from stage 4 to the last boss runs into a wall, so the run first goes straight north
    /// to 3603,1973 and only then turns left to the spawn.
    /// </summary>
    public List<RoutePoint> Route { get; set; } = new() { new RoutePoint(3603, 1973) };

    /// <summary>
    /// After facing north: tilt the camera up (right-drag upwards by <see cref="CameraTiltPx"/>) so
    /// the next stage's gate shows as a red/orange glow, and click it - one click walks most of the
    /// way. The coordinate steering takes over once the character stops. On again since
    /// 2026-10-21 (user, settings v14): "right click up 60px ... see the stage 5 gate ... run towards it".
    /// </summary>
    public bool AimGate { get; set; } = true;

    /// <summary>
    /// Drag the camera back down after the gate search. Off (user, 2026-10-21: "going back down is
    /// not needed"); turn on if the tilt ever adds up run after run.
    /// </summary>
    public bool UntiltAfterGate { get; set; }

    /// <summary>
    /// At the last <see cref="Route"/> point, turn to face west (the final boss is west of it) so
    /// the boss is in view for the last stretch (user, 2026-10-21). Steering then starts from a
    /// known 90 degree offset.
    /// </summary>
    public bool FaceWestAtWaypoint { get; set; }

    /// <summary>
    /// At the last <see cref="Route"/> point: stop walking, Tab until <see cref="FinalBoss"/>'s name
    /// shows and press <see cref="SkillKey"/> - the character walks to the targeted boss and attacks
    /// (user, 2026-10-21). Not seen within 15 s -> the coordinate walk to TargetX/Y as before.
    /// </summary>
    public bool TabAtWaypoint { get; set; } = true;

    /// <summary>The skill key that walks to the targeted boss and attacks it (user: 1).</summary>
    public string SkillKey { get; set; } = "1";

    /// <summary>
    /// Every manual skill press - Laby stage 5 (the held press and the manual attack taps) and the LOD
    /// skill spam - sends <see cref="SkillKey"/> and then Ctrl + <see cref="SkillKey"/>, e.g. 1 then
    /// Ctrl+1 (user, 2026-10-26: "when manually attacking couple it with ctrl + skill key"). With
    /// <see cref="LodSettings.HoldSkill"/> the held key is let go for a Ctrl + skill tap every
    /// <see cref="LodSettings.SkillEveryMs"/>. UI "Ctrl + skill key too".
    /// </summary>
    public bool CtrlSkill { get; set; } = true;

    /// <summary>
    /// At the last waypoint no boss name 15 s after the gate banner (60 s without one): walk on to
    /// TargetX/Y. Off (user, 2026-10-22): keep pressing Tab until the boss shows, never walk west.
    /// </summary>
    public bool WaypointWalkFallback { get; set; }

    /// <summary>
    /// Stage 5, the boss in view: after the held press (<see cref="HoldSkillMs"/>) tap the skill key
    /// this many times before turning the autohunt on (user, 2026-10-27: "Attack Hold skill, then do
    /// 10x attack (total 11)"; the autohunt alone did not attack the boss). 0 = autohunt straight away.
    /// </summary>
    public int ManualAttacks { get; set; } = 10;

    /// <summary>
    /// Stage 5: the first manual attack holds the skill key down this long (user, 2026-10-27: the
    /// character "didn't get near or inside the boss" with taps alone - a held skill keeps it running
    /// at the target); stages 1-4 hold it this long before the autohunt (<see cref="HoldBeforeAutohunt"/>).
    /// 0 = no hold: taps only on stage 5, J right after the spawn wait on stages 1-4. UI "Hold skill (ms)".
    /// </summary>
    public int HoldSkillMs { get; set; } = 3000;

    /// <summary>
    /// Stages 1-4: with the boss in view, hold the skill key (<see cref="HoldSkillMs"/>) once before
    /// the autohunt goes on, then J and the select clicks (user, 2026-10-27: "on other stages, before
    /// going autohunt, try to skill hold first"). Off = J right after the spawn wait, as before. UI
    /// "Hold skill before autohunt (stages 1-4)".
    /// </summary>
    public bool HoldBeforeAutohunt { get; set; } = true;

    /// <summary>At least this long between two skill presses (user, 2026-10-26: 300 ms or more).</summary>
    public int ManualAttackIntervalMs { get; set; } = 300;

    /// <summary>After the last manual press: wait this long, then look for "Attack succeeded" (user: 500 ms).</summary>
    public int ManualAttackWaitMs { get; set; } = 500;

    /// <summary>
    /// Stage 5, watching the boss with the autohunt on: no new "Attack succeeded" line for this long
    /// while the boss is up = the autohunt is not attacking it -> J off, select and attack by hand
    /// again (user, 2026-10-27: "goes directly below the boss with autohunt on but doesn't target or
    /// attack it").
    /// </summary>
    public int AutohuntStallMs { get; set; } = 15000;

    /// <summary>How far the right-drag moves the mouse upwards to tilt the camera (user: 60 px).</summary>
    [JsonIgnore]
    public int CameraTiltPx { get; set; } = 60;

    /// <summary>How close a <see cref="Route"/> point has to be reached before heading for the next one.</summary>
    public int WaypointTolerance { get; set; } = 60;

    /// <summary>
    /// The 5th boss, standing at <see cref="TargetX"/>/<see cref="TargetY"/>. It cannot be picked
    /// on the map, so the pilot runs there, selects it and watches it like the map stages.
    /// Empty = stop when the coordinate is reached.
    /// </summary>
    public string FinalBoss { get; set; } = "Sacred Sky Lord";

    /// <summary>
    /// Autohunt off (<see cref="AutohuntKey"/>, a toggle) right after stage 4 dies and on again on
    /// arrival at the final boss (user, 2026-10-21: its auto-buff stopped the gate walk). Off = leave
    /// autohunt running from stage 4 (the old behaviour).
    /// </summary>
    public bool FinalBossPressAutohunt { get; set; } = true;

    /// <summary>After the final boss: turn the autohunt off and teleport here. Empty = stay.</summary>
    public string ReturnDestination { get; set; } = "Piercing City";

    /// <summary>
    /// Every clan's home town (user, 2026-10-21): any of them on the minimap counts as "in town". The
    /// one the character is seen in becomes the return destination (ReturnDestination is only the
    /// fallback until a town has been seen).
    /// </summary>
    public List<string> HomeTowns { get; set; } = new() { "Piercing City", "Molten City", "Wandering City" };

    // Final run to the staging point (the last boss).
    public int TargetX { get; set; } = 3325;
    public int TargetY { get; set; } = 1932;
    public int TargetTolerance { get; set; } = 30;
    public int RunClickPx { get; set; } = 200;

    /// <summary>
    /// Facing north, screen-up is world-north and screen-right world-east: click from the character
    /// straight towards the target (user: "just click north"), no learned mapping. Off = the
    /// least-squares steering.
    /// </summary>
    public bool SimpleSteer { get; set; } = true;

    /// <summary>Pause between clicks with <see cref="SimpleSteer"/>: short enough that the character keeps walking.</summary>
    public int RunDashWaitMs { get; set; } = 900;
    public int RunStepWaitMs { get; set; } = 1500;
    public int RunMaxSteps { get; set; } = 200;

    /// <summary>Screen positions are not saved (see <see cref="AppSettings.Regions"/>); restores their defaults.</summary>
    public void ResetPositions()
    {
        var defaults = new DungeonSettings();
        MapWheelX = defaults.MapWheelX;
        MapWheelY = defaults.MapWheelY;
        MapRowChevronX = defaults.MapRowChevronX;
        BossSelectX = defaults.BossSelectX;
        BossSelectY = defaults.BossSelectY;
        BossSelectStep = defaults.BossSelectStep;
        CameraTiltPx = defaults.CameraTiltPx;
    }
}

/// <summary>A world position read off the minimap's "x , y" label.</summary>
public sealed class RoutePoint
{
    public int X { get; set; }
    public int Y { get; set; }

    public RoutePoint() { }

    public RoutePoint(int x, int y)
    {
        X = x;
        Y = y;
    }

    public override string ToString() => $"{X},{Y}";
}

public sealed class UiSettings
{
    public bool ShowPreview { get; set; } = true;

    /// <summary>Frames per second handed from the capture thread to the vision loop.</summary>
    public int CaptureFps { get; set; } = 30;

    /// <summary>Preview refresh rate; analysis always runs at full capture rate.</summary>
    public int PreviewFps { get; set; } = 10;

    /// <summary>Mini mode window stays above other windows (user: always on top).</summary>
    public bool MiniAlwaysOnTop { get; set; } = true;

    /// <summary>Where the mini window opens (user, 2026-10-23: bottom left by default).</summary>
    public MiniPosition MiniPosition { get; set; } = MiniPosition.BottomLeft;
}

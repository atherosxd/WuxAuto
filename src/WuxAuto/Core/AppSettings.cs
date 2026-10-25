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

    /// <summary>Where to walk to; the boss is near and may come to the character.</summary>
    public int TargetX { get; set; } = 1191;
    public int TargetY { get; set; } = 2413;
    public int TargetTolerance { get; set; } = 60;

    /// <summary>Tab while walking (user: every 1-2 s).</summary>
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

    /// <summary>Ctrl+clicks per fight before attacking from where the character is.</summary>
    public int ApproachTries { get; set; } = 2;

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
    /// At the last waypoint no boss name 15 s after the gate banner (60 s without one): walk on to
    /// TargetX/Y. Off (user, 2026-10-22): keep pressing Tab until the boss shows, never walk west.
    /// </summary>
    public bool WaypointWalkFallback { get; set; }

    /// <summary>
    /// Once the boss is selected (magenta ring under it + its name, or a hit), press the skill key
    /// this many times before turning the autohunt on (user: autohunt alone did not attack a
    /// selected boss). 0 = autohunt straight away.
    /// </summary>
    public int ManualAttacks { get; set; } = 10;

    /// <summary>After the last manual press: wait this long, then look for "Attack succeeded" (user: 500 ms).</summary>
    public int ManualAttackWaitMs { get; set; } = 500;

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

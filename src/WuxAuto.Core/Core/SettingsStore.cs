using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WuxAuto.Core;

public static class AppPaths
{
    private static string _root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WuxAuto");

    private static string _assetsFolder = Path.Combine(AppContext.BaseDirectory, "Assets");

    /// <summary>The folders of one async flow (<see cref="IsolateThisFlow"/>); never set by the apps.</summary>
    private sealed class FlowFolders(string root, string assetsFolder)
    {
        public string Root = root;
        public string AssetsFolder = assetsFolder;
    }

    private static readonly AsyncLocal<FlowFolders?> Flow = new();

    /// <summary>Data folder. Defaults to %LOCALAPPDATA%\WuxAuto; <see cref="Configure"/> moves it (other hosts, tests).</summary>
    public static string Root => Flow.Value?.Root ?? _root;

    /// <summary>Shipped template images. Defaults to the Assets folder next to the executable.</summary>
    public static string AssetsFolder => Flow.Value?.AssetsFolder ?? _assetsFolder;

    /// <summary>Points every folder at <paramref name="root"/>; the assets folder is set to <paramref name="assetsFolder"/>, or reset to the default Assets folder next to the executable when none is given.</summary>
    public static void Configure(string root, string? assetsFolder = null)
    {
        string assets = assetsFolder ?? Path.Combine(AppContext.BaseDirectory, "Assets");
        if (Flow.Value is { } flow)
        {
            flow.Root = root;
            flow.AssetsFolder = assets;
            return;
        }

        _root = root;
        _assetsFolder = assets;
    }

    /// <summary>
    /// Tests only: from here on this async flow (the test, and every task and thread it starts) has folders of its own,
    /// starting from the current ones; <see cref="Configure"/> in it changes only those. Lets test classes that each
    /// point AppPaths somewhere else run in parallel. The apps never call it, so for them nothing changes.
    /// </summary>
    internal static void IsolateThisFlow() => Flow.Value = new FlowFolders(Root, AssetsFolder);

    /// <summary>Tests only: this async flow has folders of its own (<see cref="IsolateThisFlow"/>).</summary>
    internal static bool IsThisFlowIsolated => Flow.Value != null;

    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string DebugFolder => Path.Combine(Root, "debug");
    public static string TemplateFolder => Path.Combine(Root, "templates");
    public static string LogFolder => Path.Combine(Root, "logs");
    public static string KillsFolder => Path.Combine(Root, "kills");

    /// <summary>Copies of the sound files picked for the kill chime (created when the first one is picked).</summary>
    public static string SoundsFolder => Path.Combine(Root, "sounds");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(DebugFolder);
        Directory.CreateDirectory(KillsFolder);
        Directory.CreateDirectory(TemplateFolder);
        Directory.CreateDirectory(LogFolder);
    }
}

public static class SettingsStore
{
    /// <summary>Bump when an existing settings.json must be migrated (see <see cref="Migrate"/>).</summary>
    private const int CurrentVersion = 26;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// The saved settings, migrated and normalised. <paramref name="persistMigration"/> false (the offline probes): a
    /// migration stays in memory - a newer build must not rewrite the settings.json an older running copy shares.
    /// </summary>
    public static AppSettings Load(bool persistMigration = true)
    {
        try
        {
            AppPaths.EnsureCreated();
            if (!File.Exists(AppPaths.SettingsFile))
                return new AppSettings();

            string json = File.ReadAllText(AppPaths.SettingsFile);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            if (Migrate(settings) && persistMigration)
                Save(settings);
            Normalize(settings);
            return settings;
        }
        catch
        {
            // A corrupt settings file must never stop the app from launching.
            return new AppSettings();
        }
    }

    /// <summary>
    /// Brings an older settings file up to date.
    /// </summary>
    /// <returns><c>true</c> when anything changed and the file should be written back.</returns>
    private static bool Migrate(AppSettings s)
    {
        if (s.Version >= CurrentVersion)
            return false;

        // v3: screen positions (Regions and the map/boss click fractions) are no longer saved -
        // they are rebuilt from the defaults on every run. Rewriting the file drops the stale
        // copies older versions stored (a stale MapScroll once hid the map's scroll arrows).

        // v4: the last boss stands at 3325,1932 (read off the coordinate display in game).
        if (s.Version < 4 && s.Dungeon is { TargetX: 3326, TargetY: 1936 })
        {
            s.Dungeon.TargetX = 3325;
            s.Dungeon.TargetY = 1932;
        }

        // v5: a dead boss was noticed 5-6.5 s late and the gate banner was missed - poll the boss
        // every 2 s and OCR the banner every 2nd poll. Only untouched defaults are changed.
        if (s.Version < 5 && s.Dungeon != null)
        {
            if (s.Dungeon.BossPollMs == 5000)
                s.Dungeon.BossPollMs = 2000;
            if (s.Dungeon.GateOcrEveryNPolls == 4)
                s.Dungeon.GateOcrEveryNPolls = 2;
        }

        // v6: the combat log actually says "Attack succeeded!Normal damage(...)".
        if (s.Version < 6 && s.Dungeon is { AttackConfirmText: "Attack Successful" })
            s.Dungeon.AttackConfirmText = "Attack succeeded";

        // v7: the blind 5 s left turn is replaced by short taps that check the searchlight each time.
        if (s.Version < 7 && s.Dungeon is { RotateLeftSweepMs: 5000 })
            s.Dungeon.RotateLeftSweepMs = 0;

        // v8: the run to the last boss goes straight north first (a wall blocks the diagonal).
        if (s.Version < 8 && s.Dungeon is { Route.Count: 0 })
            s.Dungeon.Route.Add(new RoutePoint(3603, 1973));

        // v9: flashlight2 (a true-north crop) scores lower than the old template; 0.85 is only the
        // gate for the peak search now, which keeps turning until the score falls.
        if (s.Version < 9 && s.Dungeon is { FlashlightMatchScore: 0.90 })
            s.Dungeon.FlashlightMatchScore = 0.85;

        // v10: faster face-north taps (user: 30-50 ms instead of 120).
        if (s.Version < 10 && s.Dungeon is { RotateStepMs: 120 })
            s.Dungeon.RotateStepMs = 40;

        // v11: user changed their mind - 180 ms taps.
        if (s.Version < 11 && s.Dungeon is { RotateStepMs: 40 or 120 })
            s.Dungeon.RotateStepMs = 180;

        // v12: the gate click is off by default (user: steer by coordinates instead).
        if (s.Version < 12 && s.Dungeon != null)
            s.Dungeon.AimGate = false;

        // v13: the user's new searchlight crop (flashlight2 lined up NNW, run 25).
        if (s.Version < 13 && s.Dungeon is { FlashlightTemplate: "flashlight2" or "flashlight" })
            s.Dungeon.FlashlightTemplate = "flashlight3";

        // v14: tilt + gate click back on (user: right-drag up 60 px after north, run towards the gate).
        if (s.Version < 14 && s.Dungeon != null)
            s.Dungeon.AimGate = true;

        // v15: autohunt off for the run to the final boss, on again there (user).
        if (s.Version < 15 && s.Dungeon != null)
            s.Dungeon.FinalBossPressAutohunt = true;

        // v16: Tab + skill at the waypoint instead of the west turn (user: "it doesn't work well").
        if (s.Version < 16 && s.Dungeon != null)
            s.Dungeon.FaceWestAtWaypoint = false;

        // v17: a longer Space spam before a mid-fight buff (5 presses did not stop the attack, user).
        if (s.Version < 17 && s.Lod is { StopAttackPresses: 5 })
            s.Lod.StopAttackPresses = 15;

        // v18: the item-list key check read "no Temple of Fiends Key" with keys left (user) - off;
        // the teleport itself decides now.
        if (s.Version < 18 && s.Sequence != null)
            s.Sequence.CheckKeys = false;

        // v19: LOD loot at least 10 s, boss/treasure icon gone 2 s = dead (user).
        if (s.Version < 19 && s.Lod != null)
        {
            if (s.Lod.LootMs < 10000)
                s.Lod.LootMs = 10000;
            if (s.Lod.LootAfterMissingMs == 5000)
                s.Lod.LootAfterMissingMs = 2000;
        }

        // v20: at least 15 skill presses on the stage 5 boss before the autohunt (user, 2026-10-26).
        if (s.Version < 20 && s.Dungeon is { ManualAttacks: > 0 and < 15 })
            s.Dungeon.ManualAttacks = 15;

        // v21: the stage 5 attack is one held press + 10 taps (user, 2026-10-27); the v20 15 becomes 10.
        if (s.Version < 21 && s.Dungeon is { ManualAttacks: 15 })
            s.Dungeon.ManualAttacks = 10;

        // v22: Auto LOD walks forward to 1199,1782 after the south turn and waits there (user,
        // 2026-10-28: "sometimes the boss doesn't go near the char"). The old 1191,2413 default moves;
        // a spot set by hand stays.
        if (s.Version < 22 && s.Lod != null)
        {
            s.Lod.WalkToBossSpot = true;
            if (s.Lod is { TargetX: 1191, TargetY: 2413 })
            {
                s.Lod.TargetX = 1199;
                s.Lod.TargetY = 1782;
            }
        }

        // v23: warbuff2 is the sword's buff (user, 2026-10-28), one of the new WeaponBuffIcons (a new property, so it loads
        // its default, warbuff2 included). A saved BuffIcons list (warbuff1-6 in v0.19.0) wins over the new default, so
        // warbuff2 is taken out of it - the user's order and other entries stay. Removal only: the v0.19.0 copy sharing
        // settings.json requires every BuffIcons entry at Start and then just checks five.
        if (s.Version < 23 && s.War?.BuffIcons is { } buffs)
            buffs.RemoveAll(b => string.Equals(b?.Trim(), "warbuff2", StringComparison.OrdinalIgnoreCase));

        // v24: Auto Blossom's kill check 2 s -> 1 s and loot 5 s -> 2 s (user, 2026-10-30: "the check if monster is killed,
        // set it to 1s, loot for 2s"), the pillar turn 50 taps -> 15 ("reduce the turn to pillars to 15 turns or 10s"; the
        // 10 s is the new TurnMaxMs). Only the old defaults move; a value set by hand stays.
        if (s.Version < 24 && s.Blossom is { } blossom)
        {
            if (blossom.TargetGoneMs == 2000)
                blossom.TargetGoneMs = 1000;
            if (blossom.LootMs == 5000)
                blossom.LootMs = 2000;
            if (blossom.TurnMaxTaps == 50)
                blossom.TurnMaxTaps = 15;
        }

        // v25: the route the user recorded on the first island (2026-10-30, 83 waypoints, walked 1 - 83 and back, no camera
        // taps) replaces the first 17-waypoint default. Only a file still on that first route moves, and then the whole route
        // does - waypoints, loop start, walk back and camera turns belong together (the old turns were made for the old
        // waypoints); a recorded route stays.
        if (s.Version < 25 && s.Blossom is { } route
            && Automation.BlossomRoute.Parse(route.Waypoints).SequenceEqual(Automation.BlossomRoute.Parse(BlossomSettings.First17Waypoints())))
        {
            var d = new BlossomSettings();
            route.Waypoints = d.Waypoints;
            route.LoopFrom = d.LoopFrom;
            route.BackAndForth = d.BackAndForth;
            route.WaypointTurns = d.WaypointTurns;
        }

        // v26: stages 1-4 back to the old flow (user, 2026-10-31: "revert the old auto hunt > click center upon boss
        // spawn"; with the held press first the autohunt sometimes started and did not attack the boss): J after the
        // spawn wait, then Tab and the centre clicks. Every saved file has the v0.14.0 default true, so all are moved;
        // the check box still turns the hold back on.
        if (s.Version < 26 && s.Dungeon != null)
            s.Dungeon.HoldBeforeAutohunt = false;

        s.Version = CurrentVersion;
        return true;
    }

    public static void Save(AppSettings settings) => TrySave(settings, out _);

    // One writer at a time: the phone saves from its pages, the floating control and the pilot's loop thread.
    private static readonly object SaveLock = new();

    /// <summary>
    /// Writes the settings file; false (and why in <paramref name="error"/>) when it could not be written. Saves are
    /// serialised. <see cref="Save"/> ignores the outcome (non critical on the desktop).
    /// </summary>
    public static bool TrySave(AppSettings settings, out string? error)
    {
        lock (SaveLock)
        {
            try
            {
                AppPaths.EnsureCreated();
                Normalize(settings);
                File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(settings, JsonOptions));
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }

    /// <summary>
    /// The phone's touch timings and sizes (M4 review minor 8): every one within a range that keeps the gestures sane -
    /// no zero speed to divide by (<see cref="PhoneSettings.TurnDragPxPerMs"/>), no zero or negative sizes. The defaults
    /// are all inside, so a file without the section (the desktop's) is unchanged.
    /// </summary>
    private static void NormalizePhone(PhoneSettings p)
    {
        p.TapHoldMs = Math.Clamp(p.TapHoldMs, 10, 1000);
        p.AttackTapEveryMs = Math.Clamp(p.AttackTapEveryMs, 50, 2000);
        if (!Enum.IsDefined(p.AttackMode))
            p.AttackMode = PhoneAttackMode.Hold;
        p.HoldRepressMs = Math.Clamp(p.HoldRepressMs, 0, 60000);
        p.LodArrivalWalkMs = Math.Clamp(p.LodArrivalWalkMs, 0, 10000);
        p.LodBossTapAfterTabs = Math.Clamp(p.LodBossTapAfterTabs, 0, 100);
        p.FontSizePercent = p.FontSizePercent <= 0 ? 0 : Math.Clamp(p.FontSizePercent, 50, 200);
        p.InterfaceScale = double.IsFinite(p.InterfaceScale) && p.InterfaceScale > 0 ? Math.Round(Math.Clamp(p.InterfaceScale, 0.5, 2.0), 2) : 0;
        p.TapSettleMs = Math.Clamp(p.TapSettleMs, 0, 2000);
        p.JoystickStepMs = Math.Clamp(p.JoystickStepMs, 100, 5000);
        p.SwipePxPerTurnMs = ClampOr(p.SwipePxPerTurnMs, 0.05, 20, 0.5);
        p.TurnDragMinPx = Math.Clamp(p.TurnDragMinPx, 1, 1000);
        p.TurnDragEndHoldMs = Math.Clamp(p.TurnDragEndHoldMs, 0, 2000);
        p.TurnDragPxPerMs = ClampOr(p.TurnDragPxPerMs, 0.01, 20, 0.3);
        p.TurnDragMinMs = Math.Clamp(p.TurnDragMinMs, 10, 5000);
        p.PinchFromGap = Math.Clamp(p.PinchFromGap, 20, 5000);
        p.PinchToGap = Math.Clamp(p.PinchToGap, 1, 5000);
        p.PinchMs = Math.Clamp(p.PinchMs, 50, 5000);
        p.ListSwipePx = Math.Clamp(p.ListSwipePx, 10, 2000);
        p.FloatingX = ClampOr(p.FloatingX, 0, 1, 0.40);
        p.FloatingY = ClampOr(p.FloatingY, 0, 1, 0.03);
    }

    /// <summary><see cref="Math.Clamp(double, double, double)"/> that also turns NaN into <paramref name="fallback"/> (Clamp passes NaN through).</summary>
    private static double ClampOr(double value, double min, double max, double fallback) =>
        double.IsNaN(value) ? fallback : Math.Clamp(value, min, max);

    private static void NormalizeWar(AppSettings s)
    {
        s.War ??= new WarSettings();
        var w = s.War;
        var d = new WarSettings();
        static string Or(string? v, string fallback) => string.IsNullOrWhiteSpace(v) ? fallback : v.Trim();
        w.Destination = Or(w.Destination, d.Destination);
        w.TribeRowText = Or(w.TribeRowText, d.TribeRowText);
        w.MenuTileText = Or(w.MenuTileText, d.MenuTileText);
        w.TabTargetOption = Or(w.TabTargetOption, d.TabTargetOption);
        w.GateText = Or(w.GateText, d.GateText);
        w.ArrivedText = Or(w.ArrivedText, d.ArrivedText);
        w.XBuffIcon = (w.XBuffIcon ?? "").Trim();
        w.HomeMap = (w.HomeMap ?? "").Trim();
        w.HomeMonster = (w.HomeMonster ?? "").Trim();
        var icons = (w.BuffIcons ?? [])
            .Select(i => (i ?? "").Trim())
            .Where(i => i.Length > 0)
            .ToList();
        w.BuffIcons = icons.Count > 0 ? icons : WarSettings.DefaultBuffIcons();
        // No refill for these three: an empty weapon or item list is the user's choice, an empty disabled list means every
        // buff on.
        static List<string> Clean(List<string>? names) => (names ?? [])
            .Select(n => (n ?? "").Trim())
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        w.WeaponBuffIcons = Clean(w.WeaponBuffIcons);
        w.ItemBuffIcons = Clean(w.ItemBuffIcons);
        w.DisabledBuffs = Clean(w.DisabledBuffs);
        w.LeadMs = Math.Clamp(w.LeadMs, 0, 120000);
        w.DialogWaitMs = Math.Clamp(w.DialogWaitMs, 10000, 600000);
        w.BuffClickGapMs = Math.Clamp(w.BuffClickGapMs, 800, 10000);   // the buff animation needs 0.8 s (user)
        w.BuffEveryMs = Math.Clamp(w.BuffEveryMs, 30000, 3600000);
        w.ShuraHoldMs = Math.Clamp(w.ShuraHoldMs, 0, 10000);
        w.TargetGoneMs = Math.Clamp(w.TargetGoneMs, 500, 30000);
        w.ListPage = Math.Clamp(w.ListPage, 1, 20);
        w.ArriveWaitMs = Math.Clamp(w.ArriveWaitMs, 5000, 600000);
        w.IconMatchScore = Math.Clamp(w.IconMatchScore, 0.3, 1.0);
    }

    /// <summary>Auto Blossom: blanks back to the defaults, an empty or unreadable route refilled, every number clamped.</summary>
    private static void NormalizeBlossom(AppSettings s)
    {
        s.Blossom ??= new BlossomSettings();
        var b = s.Blossom;
        var d = new BlossomSettings();
        b.Destination = string.IsNullOrWhiteSpace(b.Destination) ? d.Destination : b.Destination.Trim();
        b.PillarTemplate = string.IsNullOrWhiteSpace(b.PillarTemplate) ? d.PillarTemplate : b.PillarTemplate.Trim();
        b.TargetName = string.IsNullOrWhiteSpace(b.TargetName) ? d.TargetName : b.TargetName.Trim();
        b.AfkCheckerText = (b.AfkCheckerText ?? "").Trim();
        b.TabTargetOption = Automation.TabTargetOptions.IndexOf((b.TabTargetOption ?? "").Trim()) is int tab
            ? Automation.TabTargetOptions.All[tab]
            : d.TabTargetOption;
        if (Automation.BlossomRoute.Parse(b.Waypoints).Count == 0)
            b.Waypoints = BlossomSettings.DefaultWaypoints();
        int count = Automation.BlossomRoute.Parse(b.Waypoints).Count;
        b.ListPage = Math.Clamp(b.ListPage, 1, 20);
        b.PillarMatchScore = Math.Clamp(b.PillarMatchScore, 0.30, 0.99);
        b.PillarCenterTolerance = Math.Clamp(b.PillarCenterTolerance, 0.01, 0.30);
        b.TurnTapMs = Math.Clamp(b.TurnTapMs, 20, 1000);
        b.TurnTaps = Math.Clamp(b.TurnTaps, 1, 60);
        b.TurnMaxTaps = Math.Clamp(b.TurnMaxTaps, b.TurnTaps, 120);
        b.TurnMaxMs = Math.Clamp(b.TurnMaxMs, 2000, 120000);
        b.LoopFrom = Math.Clamp(b.LoopFrom, 1, Math.Max(1, count));
        b.WaypointTolerance = Math.Clamp(b.WaypointTolerance, 10, 500);
        b.WaypointWaitMs = Math.Clamp(b.WaypointWaitMs, 0, 5000);   // the AFK checker (user: 5 s or less)
        b.TabEveryMs = Math.Clamp(b.TabEveryMs, 300, 10000);
        b.TargetGoneMs = Math.Clamp(b.TargetGoneMs, 500, 30000);
        b.FightMaxMs = Math.Clamp(b.FightMaxMs, 10000, 600000);
        b.FightStuckMs = b.FightStuckMs <= 0 ? 0 : Math.Clamp(b.FightStuckMs, 1000, 60000);
        b.RunAheadDistance = Math.Clamp(b.RunAheadDistance, 0, 3000);
        b.RandomSkipMax = Math.Clamp(b.RandomSkipMax, 0, 20);
        b.LootDelayMs = Math.Clamp(b.LootDelayMs, 0, 10000);
        b.LootMs = Math.Clamp(b.LootMs, 0, 60000);
        b.LootEveryMs = Math.Clamp(b.LootEveryMs, 50, 2000);
        b.WaypointTurns = (b.WaypointTurns ?? d.WaypointTurns).Select(t => (t ?? "").Trim()).Where(t => t.Length > 0).ToList();
        b.WalkClickEveryMs = Math.Clamp(b.WalkClickEveryMs, 100, 3000);
        b.WalkReadEveryMs = Math.Clamp(b.WalkReadEveryMs, 100, 5000);
        b.WalkClickPx = Math.Clamp(b.WalkClickPx, 60, 600);
    }

    internal static void Normalize(AppSettings s)
    {
        s.Version = CurrentVersion;
        s.Input ??= new InputSettings();
        s.Detection ??= new DetectionSettings();
        s.Regions ??= new RegionSettings();
        s.Sequence ??= new SequenceSettings();
        s.Dungeon ??= new DungeonSettings();
        s.Ui ??= new UiSettings();

        s.Input.KeyHoldMs = Math.Clamp(s.Input.KeyHoldMs, 5, 2000);
        s.Input.FocusSettleMs = Math.Clamp(s.Input.FocusSettleMs, 0, 10000);
        s.Input.MoveSettleMs = Math.Clamp(s.Input.MoveSettleMs, 0, 2000);
        s.Input.ClickHoldMs = Math.Clamp(s.Input.ClickHoldMs, 5, 2000);
        s.Input.AfterClickMs = Math.Clamp(s.Input.AfterClickMs, 0, 10000);

        s.Detection.TemplateThreshold = Math.Clamp(s.Detection.TemplateThreshold, 0.05, 0.999);
        s.Detection.CompassThreshold = Math.Clamp(s.Detection.CompassThreshold, 0.05, 0.999);
        s.Detection.CompassToleranceDeg = Math.Clamp(s.Detection.CompassToleranceDeg, 0.5, 45);
        s.Detection.TemplateScale = Math.Clamp(s.Detection.TemplateScale, 0.25, 4);
        s.Detection.OcrScale = Math.Clamp(s.Detection.OcrScale, 0.5, 4);
        s.Detection.CompassSearchStepDeg = Math.Clamp(s.Detection.CompassSearchStepDeg, 2, 30);

        s.Ui.CaptureFps = Math.Clamp(s.Ui.CaptureFps, 1, 240);
        s.Ui.PreviewFps = Math.Clamp(s.Ui.PreviewFps, 1, 60);

        s.Sequence.MaxRetries = Math.Clamp(s.Sequence.MaxRetries, 1, 200);
        s.Sequence.ReconnectStepDelayMs = Math.Clamp(s.Sequence.ReconnectStepDelayMs, 100, 10000);
        s.Sequence.RetryDelayMs = Math.Clamp(s.Sequence.RetryDelayMs, 50, 5000);
        s.Sequence.RotateTapMs = Math.Clamp(s.Sequence.RotateTapMs, 20, 1500);
        s.Sequence.RotateSettleMs = Math.Clamp(s.Sequence.RotateSettleMs, 50, 3000);
        s.Sequence.AlignMaxAttempts = Math.Clamp(s.Sequence.AlignMaxAttempts, 1, 500);
        s.Sequence.DragPixelsPerDegree = Math.Clamp(s.Sequence.DragPixelsPerDegree, 0.5, 100);

        if (string.IsNullOrWhiteSpace(s.Sequence.DestinationText))
            s.Sequence.DestinationText = "Labyrinth";

        s.Dungeon.GatePollMs = Math.Clamp(s.Dungeon.GatePollMs, 100, 5000);
        s.Dungeon.GateGiveUpMs = Math.Clamp(s.Dungeon.GateGiveUpMs, 5000, 900000);
        s.Dungeon.GateOcrEveryNPolls = Math.Clamp(s.Dungeon.GateOcrEveryNPolls, 1, 50);
        s.Dungeon.GateFallbackMs = Math.Clamp(s.Dungeon.GateFallbackMs, 0, 180000);
        s.Dungeon.MapOpenDelayMs = Math.Clamp(s.Dungeon.MapOpenDelayMs, 100, 20000);
        s.Dungeon.GateToMapDelayMs = Math.Clamp(s.Dungeon.GateToMapDelayMs, 0, 30000);
        s.Dungeon.MapReopenRetries = Math.Clamp(s.Dungeon.MapReopenRetries, 0, 10);
        s.Dungeon.RotateLeftSweepMs = Math.Clamp(s.Dungeon.RotateLeftSweepMs, 0, 20000);
        s.Dungeon.RotateStepMs = Math.Clamp(s.Dungeon.RotateStepMs, 20, 2000);
        s.Dungeon.ZoomOutNotches = Math.Clamp(s.Dungeon.ZoomOutNotches, 0, 50);
        s.Dungeon.NorthRefineTapMs = Math.Clamp(s.Dungeon.NorthRefineTapMs, 0, 500);
        s.Sequence.FontSizePercent = s.Sequence.FontSizePercent == 0 ? 0 : Math.Clamp(s.Sequence.FontSizePercent, 50, 200);
        s.Sequence.FontSizeMaxPercent = Math.Clamp(s.Sequence.FontSizeMaxPercent, Math.Max(50, s.Sequence.FontSizePercent), 200);
        s.Phone ??= new PhoneSettings();
        NormalizePhone(s.Phone);
        s.Lod ??= new LodSettings();
        s.Lod.Destination = string.IsNullOrWhiteSpace(s.Lod.Destination) ? "Ultimate Den of Rebirth" : s.Lod.Destination.Trim();
        s.Lod.TargetTolerance = Math.Clamp(s.Lod.TargetTolerance, 10, 500);
        s.Lod.TabEveryMs = Math.Clamp(s.Lod.TabEveryMs, 300, 10000);
        s.Lod.TurnTabEveryMs = Math.Clamp(s.Lod.TurnTabEveryMs, 100, 10000);
        s.Lod.SkillEveryMs = Math.Clamp(s.Lod.SkillEveryMs, 100, 5000);
        s.Lod.BossGoneMs = Math.Clamp(s.Lod.BossGoneMs, 2000, 120000);
        s.Lod.TeleportJump = Math.Clamp(s.Lod.TeleportJump, 100, 100000);
        s.Lod.RespawnSettleMs = Math.Clamp(s.Lod.RespawnSettleMs, 0, 60000);
        s.Lod.BuffEveryMs = Math.Clamp(s.Lod.BuffEveryMs, 5000, 3600000);
        s.Lod.FlashlightMatchScore = Math.Clamp(s.Lod.FlashlightMatchScore, 0.30, 0.99);
        s.Lod.LootMs = Math.Clamp(s.Lod.LootMs, 10000, 60000);
        s.Lod.ListPage = Math.Clamp(s.Lod.ListPage, 1, 20);
        NormalizeWar(s);
        NormalizeBlossom(s);
        s.Sequence.RestartDelayMs = Math.Clamp(s.Sequence.RestartDelayMs, 1000, 120000);
        s.Sequence.MaxRestarts = Math.Clamp(s.Sequence.MaxRestarts, 1, 100);
        s.Sequence.LabyKeyWaitSeconds = Math.Clamp(s.Sequence.LabyKeyWaitSeconds, 0, 3600);
        s.Sequence.KillSound = s.Sequence.KillSound?.Trim() ?? string.Empty;
        s.Dungeon.ManualAttacks = Math.Clamp(s.Dungeon.ManualAttacks, 0, 100);
        s.Dungeon.ManualAttackIntervalMs = Math.Clamp(s.Dungeon.ManualAttackIntervalMs, 100, 5000);
        s.Dungeon.ManualAttackWaitMs = Math.Clamp(s.Dungeon.ManualAttackWaitMs, 0, 10000);
        s.Dungeon.HoldSkillMs = Math.Clamp(s.Dungeon.HoldSkillMs, 0, 30000);
        s.Dungeon.AutohuntStallMs = Math.Clamp(s.Dungeon.AutohuntStallMs, 3000, 120000);
        s.Lod.LootAfterMissingMs = Math.Clamp(s.Lod.LootAfterMissingMs, 1000, 120000);
        s.Lod.StopAttackPresses = Math.Clamp(s.Lod.StopAttackPresses, 0, 20);
        s.Lod.ShuraMatchScore = Math.Clamp(s.Lod.ShuraMatchScore, 0.3, 1.0);
        s.Lod.ShuraPresses = Math.Clamp(s.Lod.ShuraPresses, 1, 20);
        s.Lod.ShuraEveryMs = Math.Clamp(s.Lod.ShuraEveryMs, 50, 5000);
        s.Lod.ShuraTemplate = (s.Lod.ShuraTemplate ?? "").Trim();
        s.Lod.ShuraKey = (s.Lod.ShuraKey ?? "").Trim();
        s.Sequence.RunSkillTemplate = (s.Sequence.RunSkillTemplate ?? "").Trim();
        s.Sequence.RunSkillKey = (s.Sequence.RunSkillKey ?? "").Trim();
        s.Sequence.RunSkillMatchScore = Math.Clamp(s.Sequence.RunSkillMatchScore, 0.3, 1.0);
        s.Sequence.RunSkillRingMin = Math.Clamp(s.Sequence.RunSkillRingMin, 0.05, 1.0);
        s.Lod.ApproachStopPresses = Math.Clamp(s.Lod.ApproachStopPresses, 0, 20);
        s.Lod.ApproachWaitMs = Math.Clamp(s.Lod.ApproachWaitMs, 300, 10000);
        s.Lod.ApproachTries = Math.Clamp(s.Lod.ApproachTries, 1, 10);
        s.Lod.RingCheckEveryMs = s.Lod.RingCheckEveryMs <= 0 ? 0 : Math.Clamp(s.Lod.RingCheckEveryMs, 500, 60000);
        s.Lod.RingStayMax = Math.Clamp(s.Lod.RingStayMax, 0.6, 3.0);
        s.Dungeon.ArrivalDelayMs = Math.Clamp(s.Dungeon.ArrivalDelayMs, 0, 10000);
        s.Dungeon.RowChevronThreshold = Math.Clamp(s.Dungeon.RowChevronThreshold, 0.05, 0.99);
        s.Dungeon.RowChevronTemplate = string.IsNullOrWhiteSpace(s.Dungeon.RowChevronTemplate)
            ? "rightchevronfoc"
            : s.Dungeon.RowChevronTemplate.Trim();
        s.Dungeon.RotateLeftKey = string.IsNullOrWhiteSpace(s.Dungeon.RotateLeftKey) ? "A" : s.Dungeon.RotateLeftKey.Trim();
        s.Dungeon.StageDelayMs = Math.Clamp(s.Dungeon.StageDelayMs, 100, 60000);
        s.Dungeon.BossSpawnWaitMs = Math.Clamp(s.Dungeon.BossSpawnWaitMs, 0, 900000);
        s.Dungeon.BossPollMs = Math.Clamp(s.Dungeon.BossPollMs, 500, 60000);
        s.Dungeon.BossGoneMs = Math.Clamp(s.Dungeon.BossGoneMs, 2000, 300000);
        s.Dungeon.TabSettleMs = Math.Clamp(s.Dungeon.TabSettleMs, 100, 5000);
        s.Dungeon.BossNameClickDelayMs = Math.Clamp(s.Dungeon.BossNameClickDelayMs, 100, 20000);
        s.Dungeon.MapScrollNotches = Math.Clamp(s.Dungeon.MapScrollNotches, 1, 10);
        s.Dungeon.MapScrollMaxSteps = Math.Clamp(s.Dungeon.MapScrollMaxSteps, 0, 30);
        s.Dungeon.MapScrollSettleMs = Math.Clamp(s.Dungeon.MapScrollSettleMs, 100, 5000);
        s.Dungeon.MapWheelX = Math.Clamp(s.Dungeon.MapWheelX, 0.05, 0.95);
        s.Dungeon.MapWheelY = Math.Clamp(s.Dungeon.MapWheelY, 0.05, 0.95);
        s.Dungeon.MapRowChevronX = Math.Clamp(s.Dungeon.MapRowChevronX, 0.5, 0.999);
        s.Dungeon.BossSelectDelayMs = Math.Clamp(s.Dungeon.BossSelectDelayMs, 0, 30000);
        s.Dungeon.BossSelectX = Math.Clamp(s.Dungeon.BossSelectX, 0.05, 0.95);
        s.Dungeon.BossSelectY = Math.Clamp(s.Dungeon.BossSelectY, 0.05, 0.95);
        s.Dungeon.BossSelectStep = Math.Clamp(s.Dungeon.BossSelectStep, 0.01, 0.2);
        s.Dungeon.AttackConfirmMs = Math.Clamp(s.Dungeon.AttackConfirmMs, 200, 10000);
        s.Dungeon.BossSelectMaxTries = Math.Clamp(s.Dungeon.BossSelectMaxTries, 1, 50);
        s.Dungeon.AttackConfirmText ??= string.Empty;
        s.Dungeon.BossNameConfirmPolls = Math.Clamp(s.Dungeon.BossNameConfirmPolls, 1, 10);
        s.Dungeon.FlashlightMatchScore = Math.Clamp(s.Dungeon.FlashlightMatchScore, 0.3, 0.999);
        s.Dungeon.TargetTolerance = Math.Clamp(s.Dungeon.TargetTolerance, 1, 5000);
        s.Dungeon.RunClickPx = Math.Clamp(s.Dungeon.RunClickPx, 40, 800);
        s.Dungeon.RunStepWaitMs = Math.Clamp(s.Dungeon.RunStepWaitMs, 200, 20000);
        s.Dungeon.RunDashWaitMs = Math.Clamp(s.Dungeon.RunDashWaitMs, 200, 20000);
        s.Dungeon.RunMaxSteps = Math.Clamp(s.Dungeon.RunMaxSteps, 1, 5000);
        s.Dungeon.Stages ??= new List<string>();
        s.Dungeon.Route ??= new List<RoutePoint>();
        s.Dungeon.FlashlightTemplate = string.IsNullOrWhiteSpace(s.Dungeon.FlashlightTemplate) ? "flashlight3" : s.Dungeon.FlashlightTemplate.Trim();
        s.Dungeon.FinalBoss ??= string.Empty;
        s.Dungeon.ReturnDestination ??= string.Empty;
        s.Dungeon.HomeTowns = (s.Dungeon.HomeTowns ?? new List<string>()).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToList();
        if (s.Dungeon.HomeTowns.Count == 0)
            s.Dungeon.HomeTowns = new List<string> { "Piercing City", "Molten City", "Wandering City" };
        s.Dungeon.Route.RemoveAll(p => p == null);
        s.Dungeon.WaypointTolerance = Math.Clamp(s.Dungeon.WaypointTolerance, 5, 2000);
    }
}

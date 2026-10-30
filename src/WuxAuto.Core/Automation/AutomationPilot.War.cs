using System.Globalization;
using Emgu.CV;
using WuxAuto.Core;
using WuxAuto.Vision;
using LogLevel = WuxAuto.Core.LogLevel;

namespace WuxAuto.Automation;

/// <summary>
/// Auto War, the alt's own cave (design §4.1, §4.8): Start's checks - where the character is, the skill bar, the cave
/// on page 3 of the teleport scroll - remembering the cave and the targeted monster, and the trip back after a war:
/// teleport to the cave, map -> monster -> '>' to walk there, "Arrived: &lt;monster&gt;", autohunt on. Never the
/// wrong cave (user): whatever cannot be told apart stops with a message instead of a guess.
/// </summary>
public sealed partial class AutomationPilot
{
    // The teleport phases page to the caves' page for Start's check instead of clicking an entry.
    private bool _warListCheck;

    // Map pick (ClickMap / ReadStage / AdvanceStage) for Auto War: at Start the targeted monster is only
    // checked against the map's monster list (_warMapVerify); the map and monster found there wait in
    // the pending fields until the remember step saves them.
    private bool _warMapVerify;
    private string? _warPendingMonster;
    private string? _warPendingMap;
    private bool _warMonsterConfirmed;

    // ReadStage is closing the map instead of picking a row (BeginCloseWarMapAsync); per phase, so
    // Advance clears it. _warMapCloseClicked: the last tick clicked the button, look once more first.
    private bool _warMapClosing;
    private bool _warMapCloseClicked;

    // Per Start (Start clears them).
    private bool _warSpotChecked;          // this Start is at the saved spot: remembered it, or came back to it
    private bool _warRemembering;          // Start in a cave began remembering it, not saved yet
    private int _warRefusedTeleports;      // return teleports in a row that left the character in town
    private bool _warBarChecked;           // the skill bar was checked (once per Start: buffs on cooldown after a war score lower)
    private bool _warNoCaveTold;           // Start in Bohai with no cave saved: said so
    private bool _warZoneDispatched;       // the Start dispatch has read a zone once this Start (later ones are restarts)
    private string? _warTownHome;          // this Start waits for the wars in this home town (Start in town, or no cave saved)
    private readonly List<long> _warReturnTrips = new();   // trips back to the cave since the last war (WarReturnAllowed; a war clears it)

    // Per run (ResetRunState clears them).
    private string? _warTitle;             // the minimap title read at Start (the cave)
    private bool _warWindowsChecked;       // Start closed the war windows a Stop / Fail may have left open
    private IReadOnlyList<string>? _warArriveBaseline;   // the combat log (normalised) before the walk to the monster

    // One visit of the teleport list's page (ResetWarList): its reads, the verdict, then closing it.
    private readonly List<WarListRead?> _warListReads = new();
    private WarListVerdict _warListVerdict;
    private WarListRead? _warListOutcome;  // the read the verdict rests on
    private string _warListSeen = string.Empty;   // the entries of the last read that showed the list, for the log
    private double _warListScale;          // the OCR scale that last read the list's title
    private int _warListStep;              // 0 read the page, 1 close the list, 2 close the inventory
    private int _warListCloseClicks;
    private bool _warListCloseLooked;      // still open after a click: looked once more before clicking again
    private int _warListGoneReads;         // reads in a row without the list's title
    private int _warInventoryTaps;
    private bool _warInventoryLooked;

    /// <summary>True while Start runs the tribe war (<see cref="WuxAuto.Core.RunMode.AutoWar"/>).</summary>
    private bool IsWar => _settings.Sequence.Mode == WuxAuto.Core.RunMode.AutoWar;

    /// <summary>The cave to go back to: the one being remembered at Start, else the saved one.</summary>
    private string WarHomeMap => _warPendingMap ?? _settings.War.HomeMap;

    /// <summary>A cave and a monster are saved to go back to.</summary>
    private bool HasWarSpot => !string.IsNullOrWhiteSpace(_settings.War.HomeMap) && !string.IsNullOrWhiteSpace(_settings.War.HomeMonster);

    /// <summary>The cave-only wording: what a cave start needs.</summary>
    private string NotACaveMessage => $"Auto War must be started inside a cave on page {_settings.War.ListPage} of the teleport scroll";

    /// <summary>Where Start works: a cave, a home town, or inside the war map during a war (the Start dispatch's stops).</summary>
    private string StartPlaceMessage => $"{NotACaveMessage}, in a home town, or inside {_settings.War.Destination}";

    /// <summary>
    /// This Start waits for the wars in a home town (user, 2026-10-29: "allow auto war in city, most are afk in town anyway"):
    /// no trip to a cave, no autohunt around the war.
    /// </summary>
    private bool WarStaysInTown => _warTownHome != null;

    /// <summary>
    /// The character is in <paramref name="town"/>: Start there, the war over, or sent there while waiting. A town home
    /// (Start in town), or no cave to go back to (Start in Bohai with none saved), waits there for the next war and
    /// returns true; a cave home returns false (the caller goes back to the cave). <paramref name="startHere"/>: Start in
    /// town, which makes the town this Start's home whatever cave is saved.
    /// </summary>
    private bool WarWaitInTown(Mat frame, string town, string why, bool startHere = false)
    {
        if (!startHere && !WarStaysInTown && (HasWarSpot || _warRemembering))
            return false;

        if (_warTownHome == null)
            Log?.Invoke(LogLevel.Info, $"Auto War waits for the wars in {town} this Start (no cave trip, no autohunt).");
        _warTownHome = town;
        if (!_warBarChecked)
        {
            CheckWarSkillBar(frame);
            _warBarChecked = true;
        }

        ReleaseSweepKey();
        BeginWarWait($"{why} - staying in {town}");
        return true;
    }

    /// <summary>What the teleport list is paged for, as the chevron step names it (Start's cave check has no destination).</summary>
    private string ListPagingTarget => IsWar && _warListCheck
        ? $"page {_settings.War.ListPage} (Auto War's cave check)"
        : $"'{CurrentDestination}'";

    // War snapshots taken per tag this Start, at most 3 each (WarShot); Start clears it.
    private readonly Dictionary<string, int> _warShots = new(StringComparer.Ordinal);

    /// <summary>
    /// Saves a war snapshot (<c>war_*</c>, <c>tribe_unread</c>) with the overlays, at most 3 per tag; every war
    /// snapshot goes through here. True when it was saved.
    /// </summary>
    private bool WarShot(Mat frame, string tag, (int X, int Y)? click = null)
    {
        int taken = _warShots.GetValueOrDefault(tag);
        if (taken >= 3)
            return false;

        _warShots[tag] = taken + 1;
        DebugArtifacts.SaveFrame(frame, _overlays, tag, click);
        return true;
    }

    /// <summary>Stops Auto War with a reason the user can act on: a warning, a <c>war_stop</c> snapshot, Stop.</summary>
    private void WarStop(Mat? frame, string why)
    {
        Log?.Invoke(LogLevel.Warning, $"{why}.");
        if (frame != null)
            WarShot(frame, "war_stop");
        Stop(why);
    }

    /// <summary>
    /// The zone guard read another zone 3 times in a row during the map pick: start over from Focus (the Start
    /// dispatch sorts out where the character is), counted with Fail's restarts - <see cref="SequenceSettings.MaxRestarts"/>
    /// in 30 min - so a title that keeps reading as another zone ends in a stop instead of a loop.
    /// </summary>
    private void WarStartOver(Mat frame, string? title)
    {
        long now = Environment.TickCount64;
        int max = _settings.Sequence.MaxRestarts;
        _restartTimes.RemoveAll(t => now - t > 30 * 60 * 1000);
        if (_restartTimes.Count >= max)
        {
            WarStop(frame, $"The minimap reads '{title}', not '{Destination}', after {max} restarts in 30 min - press Start again inside the cave");
            return;
        }

        _restartTimes.Add(now);
        Log?.Invoke(LogLevel.Warning, $"In '{title}' instead of {Destination} during {_phase} - starting over ({_restartTimes.Count}/{max} in 30 min).");
        ResetRunState();
        _runStart = now;
        Advance(PilotPhase.Focus, 1500);
    }

    // ---------------------------------------------------------------- Start

    /// <summary>
    /// Start's dispatch (design §4.1, §5), the first thing of every run: every key Auto War presses must map to a key
    /// (<see cref="WarKeys"/>; an unmappable one is refused on every press, which reads like the game being behind another
    /// window - stop instead); the war windows a Stop or a Fail can leave open are closed (the war timer's close step);
    /// then the minimap title decides - Bohai Outpost: into the war (no cave saved yet: said once, the pilot stops after
    /// the war); a home town: back to the saved cave; the Labyrinth, LOD or no title: stop; a cave: the skill bar is
    /// checked for the buffs ticked on and Shura (<see cref="CheckWarSkillBar"/>, once per Start; missing ones are only
    /// warned about), then - at the saved spot already - the autohunt on and the wait, or the page check and the
    /// remember step. Only with the game in front: behind another window the capture shows that window's minimap, and a
    /// cave read there would be remembered.
    /// </summary>
    private async Task TickWarCheckZoneAsync(Mat frame)
    {
        long now = Environment.TickCount64;
        var war = _settings.War;
        if (WarKeyProblem(WarKeys(_settings), _controls.IsKnownKey) is { } keyProblem)
        {
            WarStop(frame, keyProblem);
            return;
        }

        if (!_controls.IsGameInFront)
        {
            _detail = "waiting for the game window to be in front";
            _waitUntil = now + 500;
            return;
        }

        if (!_warWindowsChecked)
        {
            if (_warTimerStep != 4)
                BeginWarClose();
            await TickWarCloseAsync(frame);
            if (_warTimerStep == 4)
                return;     // clicked an X (or the click waits for the game): look again on a fresh frame

            _warWindowsChecked = true;
            _waitUntil = now + 300;
            return;
        }

        string? title = await ReadMinimapTitleAsync(frame);
        if (title == null)
        {
            if (++_attempts < 3)
            {
                _detail = $"reading the minimap title ({_attempts}/3)";
                _waitUntil = now + 1000;
                return;
            }

            Log?.Invoke(LogLevel.Info, "The minimap title could not be read (3 tries).");
            WarStop(frame, StartPlaceMessage);
            return;
        }

        bool firstZone = !_warZoneDispatched;     // the Start itself, not a restart of it
        _warZoneDispatched = true;
        if (ZoneMatches(title, war.Destination))
        {
            if (!HasWarSpot && !WarStaysInTown && !_warNoCaveTold)
            {
                _warNoCaveTold = true;
                Log?.Invoke(LogLevel.Info, "No cave saved - after this war the pilot waits in town for the next one. Start it once inside a cave to hunt there between wars.");
            }

            BeginWarEnter($"Start inside {war.Destination}");
            return;
        }

        if (TownIn(title) is { } town)
        {
            // Only the Start itself in town makes the town home; a restart that lands in town (a Start in a cave or in
            // Bohai) goes on as after a war: back to the saved cave, else wait in town.
            if (!WarWaitInTown(frame, town, firstZone ? "Start in town" : "restarted in town", startHere: firstZone))
                BeginWarReturn(frame, "restarted in town");
            return;
        }

        if (ZoneMatches(title, _settings.Sequence.DestinationText) || ZoneMatches(title, _settings.Lod.Destination))
        {
            Log?.Invoke(LogLevel.Info, $"In '{title}' - the Labyrinth and LOD are no caves for Auto War.");
            WarStop(frame, StartPlaceMessage);
            return;
        }

        // A cave (or a map that the page check rules out).
        _warTitle = title;
        if (!_warBarChecked)
        {
            CheckWarSkillBar(frame);
            _warBarChecked = true;
        }

        if (_warSpotChecked && WarText.SameZone(title, war.HomeMap) && !string.IsNullOrWhiteSpace(war.HomeMonster))
        {
            // A restart at the spot (a reconnect, a failed trip back): the autohunt may be off - on first, then wait.
            Log?.Invoke(LogLevel.Info, $"Back in {war.HomeMap} - making sure the autohunt is on, then waiting for the war.");
            ResetAutohuntCheck();
            Advance(PilotPhase.WarHuntOn, 200);
            return;
        }

        Log?.Invoke(LogLevel.Info, $"In '{title}' - checking it is on page {war.ListPage} of the teleport scroll, then remembering it and the targeted monster.");
        _warRemembering = true;
        _warPendingMap = null;
        _warListCheck = true;
        _returning = false;
        ResetWarList();
        ResetTeleportPaging();
        Advance(PilotPhase.OpenInventory, 300);
    }

    /// <summary>
    /// The icons Start looks for on the skill bar (<see cref="CheckWarSkillBar"/>): every buff ticked on - the buffs, the
    /// weapon buffs, then the item buffs - and Shura's icon when <paramref name="shuraKey"/> is set (without it the fight
    /// skips the hold). Each once, blanks left out.
    /// </summary>
    internal static IReadOnlyList<string> WarBarIcons(WarSettings war, string shuraTemplate, string shuraKey)
    {
        var icons = war.BuffIcons.Concat(war.WeaponBuffIcons).Concat(war.ItemBuffIcons).Where(war.IsBuffOn);
        if (!string.IsNullOrWhiteSpace(shuraKey))
            icons = icons.Append(shuraTemplate);
        return icons
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The buffs ticked on and Shura (<see cref="WarBarIcons"/>) on the skill bar at <see cref="WarSettings.IconMatchScore"/>.
    /// Every one is optional (user, 2026-10-28: "make the skills optional, not required for pilot to start"): the missing
    /// ones are one warning, each named with its best score at any threshold - 0.90 is not proven on a real alt's bar
    /// yet, so a near miss must show in that line - or as having no PNG at all, and a <c>war_bar_missing</c> snapshot; the
    /// buff rounds skip them. Shura missing while its key is set: the fight still holds that key, so the warning says to
    /// clear it for a character without Shura.
    /// </summary>
    private void CheckWarSkillBar(Mat frame)
    {
        var war = _settings.War;
        var templates = WarBarIcons(war, _settings.Lod.ShuraTemplate, _settings.Lod.ShuraKey);
        if (templates.Count == 0)
        {
            Log?.Invoke(LogLevel.Info, "Skill bar: no buff is ticked on and no Shura key is set - nothing to look for.");
            return;
        }

        var bar = _settings.Regions.Hotbar.ToPixels(frame.Width, frame.Height);
        var found = new List<string>();
        var missing = new List<(string Name, double? Best)>();
        foreach (var (template, match) in _analyzer.FindSkillBarIcons(frame, templates, war.IconMatchScore))
        {
            if (match != null)
            {
                _overlays.Add(new OverlayItem(match.Bounds, $"{template} {match.Score:0.00}", OverlayColor.Green));
                found.Add($"{template} {match.Score.ToString("0.00", CultureInfo.InvariantCulture)}");
                continue;
            }

            // How close it came: any scale, so no scale is learned from a look-alike.
            var (best, _) = _analyzer.FindIconAnyScale(frame, template, bar, 0.0);
            if (best != null)
                _overlays.Add(new OverlayItem(best.Bounds, $"{template}? {best.Score:0.00}", OverlayColor.Orange));
            missing.Add((template, best?.Score));
        }

        if (missing.Count == 0)
        {
            Log?.Invoke(LogLevel.Info, $"Skill bar: all {templates.Count} icons found ({string.Join(", ", found)}).");
            return;
        }

        string shura = _settings.Lod.ShuraTemplate;
        bool shuraMissing = missing.Any(m => string.Equals(m.Name, shura, StringComparison.OrdinalIgnoreCase));
        Log?.Invoke(LogLevel.Warning, $"Not on the skill bar (score {war.IconMatchScore.ToString("0.00", CultureInfo.InvariantCulture)} needed): "
                                      + $"{DescribeMissingIcons(missing, t => _analyzer.Library.Get(t) != null)} - carrying on without them "
                                      + "(untick a buff this character does not use in Settings > Auto War > Buffs)"
                                      + (shuraMissing ? " - clear 'Shura key (LOD)' if this character has no Shura, or the fight still holds that key" : "")
                                      + $"{(found.Count > 0 ? $"; found {string.Join(", ", found)}" : "")}.");
        WarShot(frame, "war_bar_missing");
    }

    /// <summary>
    /// Every key Auto War presses (<see cref="WarKeyProblem"/> at Start): the skill, Tab, the autohunt and the inventory keys
    /// must be set; Shura (user, 2026-10-28: optional - the fight cycle skips the hold), the buff key and the autohunt settings
    /// key may be empty (the buff icons are clicked then). A key that is set must be one the pilot can press.
    /// </summary>
    internal static IEnumerable<WarKey> WarKeys(AppSettings settings) =>
    [
        new("Shura key (LOD)", settings.Lod.ShuraKey, Required: false),
        new("Skill key", settings.Dungeon.SkillKey, Required: true),
        new("Target key", settings.Dungeon.TabKey, Required: true),
        new("Autohunt key", settings.Dungeon.AutohuntKey, Required: true),
        new("Inventory key", settings.Input.InventoryKey, Required: true),
        new("Buff key (LOD)", settings.Lod.BuffKey, Required: false),
        new("Sequence.AutohuntSettingsKey in settings.json", settings.Sequence.AutohuntSettingsKey, Required: false),
    ];

    /// <summary>
    /// "warbuff3 (best 0.84), shura (not seen), warweapon2 (no warweapon2.png in templates/Assets)": each missing icon with
    /// its best score, rounded down - or, when <paramref name="hasTemplate"/> says its template was never loaded, that its
    /// PNG is missing (a crop to add, not an icon to put on the bar).
    /// </summary>
    internal static string DescribeMissingIcons(IEnumerable<(string Name, double? Best)> missing, Func<string, bool>? hasTemplate = null)
        => string.Join(", ", missing.Select(m => hasTemplate?.Invoke(m.Name) == false
            ? $"{m.Name} ({NoTemplateText(m.Name)})"
            : m.Best is double best
                ? $"{m.Name} (best {(Math.Floor(best * 100 + 1e-9) / 100).ToString("0.00", CultureInfo.InvariantCulture)})"
                : $"{m.Name} (not seen)"));

    /// <summary>"no warweapon2.png in templates/Assets": the template is in neither folder (TemplateLibrary loaded no such name).</summary>
    private static string NoTemplateText(string template) => $"no {template}.png in templates/Assets";

    // ---------------------------------------------------------------- the teleport list

    /// <summary>One read of the teleport list's page: the entry it is sure of, or why it has none (two caves fit, or none).</summary>
    internal readonly record struct WarListRead(string? Entry, bool Ambiguous, string? Why);

    /// <summary>What the reads of a page say: read again, the entry, two caves fit, none fits, or the list never showed.</summary>
    internal enum WarListVerdict
    {
        ReadAgain,
        Pick,
        Ambiguous,
        Missing,
        NoList,
    }

    /// <summary>
    /// The verdict of the reads of one page so far (null = that read did not show the list's title) and the read it rests
    /// on: two caves fitting in any read is final (a later read at another scale that loses one of them proves nothing);
    /// else the first read sure of an entry; else read again until <paramref name="maxReads"/>; then "missing" from the
    /// last read that showed the list, or "no list".
    /// </summary>
    internal static (WarListVerdict Verdict, int Read) DecideListReads(IReadOnlyList<WarListRead?> reads, int maxReads)
    {
        for (int i = 0; i < reads.Count; i++)
        {
            if (reads[i] is { Ambiguous: true })
                return (WarListVerdict.Ambiguous, i);
        }

        for (int i = 0; i < reads.Count; i++)
        {
            if (reads[i] is { Entry: not null })
                return (WarListVerdict.Pick, i);
        }

        if (reads.Count < maxReads)
            return (WarListVerdict.ReadAgain, -1);

        for (int i = reads.Count - 1; i >= 0; i--)
        {
            if (reads[i] != null)
                return (WarListVerdict.Missing, i);
        }

        return (WarListVerdict.NoList, -1);
    }

    /// <summary>
    /// One read of the list's page for <paramref name="name"/>, every comparison on <see cref="WarText.FoldedKey"/>s.
    /// Start (<paramref name="returnTrip"/> false, the name is the minimap title, maybe cut): <see cref="WarText.BestListEntryFolded"/>,
    /// and only an entry the title reads as (<see cref="WarText.SameZone"/>) - BestListEntry's score rule alone takes
    /// "[Dark Soul Cave Y]" for "[Dark Soul Cave X]". The trip back (the name is the saved entry): only an entry equal to
    /// it (<see cref="WarText.SameEntry"/>; entries are never cut); two such = ambiguous.
    /// </summary>
    internal static WarListRead JudgeListRead(string name, IReadOnlyList<string> entries, int page, bool returnTrip)
    {
        string notHere = $"'{name}' is not on page {page} of the teleport scroll";
        if (returnTrip)
        {
            var same = entries.Where(e => WarText.SameEntry(e, name)).ToList();
            if (same.Count == 1)
                return new WarListRead(same[0], false, null);
            if (same.Count > 1)
                return new WarListRead(null, true, $"'{name}' could be '{same[0]}' or '{same[1]}' on page {page} of the teleport scroll - not guessing which cave");

            string? closest = WarText.BestListEntryFolded(name, entries, out _);
            return new WarListRead(null, false, closest == null
                ? notHere
                : $"The closest entry on page {page} of the teleport scroll is '{closest}', not '{name}' - not clicking it");
        }

        string? entry = WarText.BestListEntryFolded(name, entries, out bool ambiguous);
        if (entry == null)
            return new WarListRead(null, false, notHere);
        if (ambiguous)
            return new WarListRead(null, true, $"'{name}' could be {TwoBest(name, entries, entry)} on page {page} of the teleport scroll - not guessing which cave");
        if (!WarText.SameZone(name, entry))
            return new WarListRead(null, false, $"The minimap reads '{name}', the closest entry on page {page} is '{entry}' - not sure it is this cave");
        return new WarListRead(entry, false, null);
    }

    /// <summary>"'a' or 'b'": the pick and the entry that reads next most like <paramref name="name"/>.</summary>
    private static string TwoBest(string name, IReadOnlyList<string> entries, string best)
    {
        string? second = entries
            .Where(e => e != best)
            .OrderByDescending(e => TextMatcher.Similarity(WarText.FoldedKey(e), WarText.FoldedKey(name)))
            .FirstOrDefault();
        return second == null ? $"'{best}'" : $"'{best}' or '{second}'";
    }

    /// <summary>A fresh visit of the teleport list's page: no reads, nothing to close.</summary>
    private void ResetWarList()
    {
        _warListReads.Clear();
        _warListVerdict = WarListVerdict.ReadAgain;
        _warListOutcome = null;
        _warListSeen = string.Empty;
        _warListScale = TeleportListScale(0);
        _warListStep = 0;
    }

    /// <summary>The teleport phases page from the first page; a fresh list visit starts there.</summary>
    private void ResetTeleportPaging()
    {
        _pages = 0;
        _anchor = null;
        _chevronMissing = false;
        _pagingBack = false;
        _backPages = 0;
    }

    /// <summary>OCR scale of the n-th read of the teleport list: another scale reads other noise.</summary>
    private static double TeleportListScale(int read) => read switch { 0 => 1.5, 1 => 2.0, _ => 1.0 };

    /// <summary>
    /// The open teleport list: its "TELEPORT SCROLL" title (null when it does not show) and the destinations under it
    /// (<see cref="WarText.TeleportListEntries"/>), from one OCR of <see cref="RegionSettings.Ocr"/>.
    /// </summary>
    private async Task<((string Text, PixelRect Bounds)? Title, IReadOnlyList<(string Text, PixelRect Bounds)> Entries)> ReadTeleportListAsync(Mat frame, double scale)
    {
        var roi = _settings.Regions.Ocr.ToPixels(frame.Width, frame.Height);
        var lines = ScreenAnalyzer.ToLines(await _analyzer.Ocr.RecognizeAsync(frame, roi, scale));
        var title = WarText.FindWindowTitle(lines, WarText.TeleportScrollTitle);
        return (title, title == null ? [] : WarText.TeleportListEntries(lines, title.Value.Bounds));
    }

    /// <summary>One read of the open teleport list on this frame: where its title and the picked entry are, and the judgement.</summary>
    private sealed record WarListPick(PixelRect Title, PixelRect EntryBounds, WarListRead Read, string Seen);

    /// <summary>One read of the list's page at <paramref name="scale"/> (<see cref="JudgeListRead"/>); null when its title does not show.</summary>
    private async Task<WarListPick?> ReadWarListPickAsync(Mat frame, double scale, string name, bool returnTrip)
    {
        var (title, entries) = await ReadTeleportListAsync(frame, scale);
        if (title == null)
            return null;

        var read = JudgeListRead(name, entries.Select(e => e.Text).ToList(), _settings.War.ListPage, returnTrip);
        var entryBounds = read.Entry == null ? default : entries.First(e => e.Text == read.Entry).Bounds;
        string seen = entries.Count == 0 ? "no entries read" : string.Join(" | ", entries.Select(e => e.Text));
        return new WarListPick(title.Value.Bounds, entryBounds, read, seen);
    }

    /// <summary>Adds this tick's read of the page and returns what the reads say now.</summary>
    private (WarListVerdict Verdict, int Read) AddListRead(WarListPick? pick, double scale)
    {
        _warListReads.Add(pick?.Read);
        if (pick != null)
        {
            _warListScale = scale;
            _warListSeen = pick.Seen;
        }

        return DecideListReads(_warListReads, 3);
    }

    /// <summary>
    /// Start's page check (design §4.1.1), ticked in FindDestination: page to <see cref="WarSettings.ListPage"/>, read
    /// it (<see cref="JudgeListRead"/>, up to 3 reads at other OCR scales while the cave is missing;
    /// <see cref="DecideListReads"/>), close the list with its X - never an entry - and the inventory, then remember the
    /// entry or stop.
    /// </summary>
    private async Task TickWarListCheckAsync(Mat frame)
    {
        if (_warListStep > 0)
        {
            await TickWarListCloseAsync(frame);
            return;
        }

        long now = Environment.TickCount64;
        var war = _settings.War;
        if (!_controls.IsGameInFront)
        {
            _detail = "waiting for the game window to be in front";
            _waitUntil = now + 500;
            return;
        }

        if (_pages + 1 < war.ListPage)
        {
            Advance(PilotPhase.FindChevron, _settings.Sequence.RetryDelayMs);
            return;
        }

        double scale = TeleportListScale(_warListReads.Count);
        var pick = await ReadWarListPickAsync(frame, scale, _warTitle ?? string.Empty, returnTrip: false);
        var (verdict, read) = AddListRead(pick, scale);
        if (verdict == WarListVerdict.ReadAgain)
        {
            _detail = $"reading page {war.ListPage} of the teleport scroll ({_warListReads.Count}/3)";
            _waitUntil = now + 700;
            return;
        }

        BeginWarListClose(frame, verdict, read, pick);
    }

    /// <summary>
    /// The trip back's teleport pick (design §4.8.1), ticked in FindDestination: page to
    /// <see cref="WarSettings.ListPage"/>, then click the saved map's entry (<see cref="JudgeListRead"/>: equal to it,
    /// never a guess) on the frame it was read on. Otherwise, after the list and the inventory are closed: two caves fitting
    /// stop the pilot (Start checked the cave was unique); missing or no list - an OCR or paging miss, or the cave gone
    /// from the list - fails, so the restart tries again.
    /// </summary>
    private async Task TickWarDestinationAsync(Mat frame)
    {
        if (_warListStep > 0)
        {
            await TickWarListCloseAsync(frame);
            return;
        }

        long now = Environment.TickCount64;
        var war = _settings.War;
        if (!_controls.IsGameInFront)
        {
            _detail = "waiting for the game window to be in front";
            _waitUntil = now + 500;
            return;
        }

        if (_pages + 1 < war.ListPage)
        {
            Advance(PilotPhase.FindChevron, _settings.Sequence.RetryDelayMs);
            return;
        }

        double scale = TeleportListScale(_warListReads.Count);
        var pick = await ReadWarListPickAsync(frame, scale, war.HomeMap, returnTrip: true);
        var (verdict, read) = AddListRead(pick, scale);
        if (verdict == WarListVerdict.ReadAgain)
        {
            _detail = $"reading page {war.ListPage} of the teleport scroll for '{war.HomeMap}' ({_warListReads.Count}/3)";
            _waitUntil = now + 700;
            return;
        }

        // The first read sure of the entry ends the reading, so a pick is always this frame's read.
        if (verdict == WarListVerdict.Pick && pick is { Read.Entry: { } entry })
        {
            _overlays.Add(new OverlayItem(pick.EntryBounds, entry, OverlayColor.Green));
            if (!WarClick(pick.EntryBounds, $"'{entry}' in the teleport scroll"))
            {
                // Not sent (the game went behind another window): this read decides nothing, read again.
                _warListReads.RemoveAt(_warListReads.Count - 1);
                return;
            }

            _preLoadHash = FrameSignature.Compute(frame, new PixelRect(0, 0, frame.Width, frame.Height));
            Advance(PilotPhase.WaitForLoad, _settings.Input.AfterClickMs);
            return;
        }

        BeginWarListClose(frame, verdict, read, pick);
    }

    /// <summary>
    /// The reads have decided (anything but a click on the entry): keep the verdict, then close the list - with its X on
    /// this frame when this read showed the title, else from the next frame - and the inventory, then
    /// <see cref="WarListFinish"/>.
    /// </summary>
    private void BeginWarListClose(Mat frame, WarListVerdict verdict, int read, WarListPick? pick)
    {
        _warListVerdict = verdict;
        _warListOutcome = read >= 0 ? _warListReads[read] : null;
        if (_warListReads.Any(r => r != null))
            Log?.Invoke(LogLevel.Info, $"Page {_settings.War.ListPage} of the teleport scroll: {_warListSeen}.");

        _warListStep = 1;
        _warListCloseClicks = 0;
        _warListCloseLooked = false;
        _warListGoneReads = 0;
        _warInventoryTaps = 0;
        _warInventoryLooked = false;
        if (pick != null)
            TryClickListClose(frame, pick.Title);
        else
            _waitUntil = Environment.TickCount64 + 300;
    }

    /// <summary>
    /// Closing the list (step 1), then the inventory (step 2), one look per tick, game in front only. The list counts as
    /// closed after 2 reads in a row without its title. Still there right after a click: one more look first - the window
    /// may only be slow to close, and a second click where its X was would then land in the world (a click there walks
    /// the character). At most 3 clicks; no X found = left open with a warning.
    /// </summary>
    private async Task TickWarListCloseAsync(Mat frame)
    {
        long now = Environment.TickCount64;
        if (!_controls.IsGameInFront)
        {
            _detail = "waiting for the game window to be in front";
            _waitUntil = now + 500;
            return;
        }

        if (_warListStep == 2)
        {
            TickWarInventoryClose(frame);
            return;
        }

        var (title, _) = await ReadTeleportListAsync(frame, _warListScale);
        if (title == null)
        {
            if (++_warListGoneReads < 2)
            {
                _detail = "checking the teleport scroll is closed";
                _waitUntil = now + 400;
                return;
            }

            if (_warListCloseClicks > 0)
                Log?.Invoke(LogLevel.Info, "The teleport scroll is closed.");
            _warListStep = 2;
            _waitUntil = now + 200;
            return;
        }

        _warListGoneReads = 0;
        if (_warListCloseClicks > 0 && !_warListCloseLooked)
        {
            _warListCloseLooked = true;
            _detail = "waiting for the teleport scroll to close";
            _waitUntil = now + 700;
            return;
        }

        if (_warListCloseClicks >= 3)
        {
            Log?.Invoke(LogLevel.Warning, $"The teleport scroll is still open after {_warListCloseClicks} clicks on its X - leaving it open.");
            WarShot(frame, "war_list_open");
            _warListStep = 2;
            _waitUntil = now + 200;
            return;
        }

        TryClickListClose(frame, title.Value.Bounds);
    }

    /// <summary>
    /// One click on the X right of the TELEPORT SCROLL title - a bright blob there (<see cref="ScreenAnalyzer.FindCloseBlobRightOf"/>),
    /// never a guessed point. None: the list is left open with a warning. A dry run goes on to the inventory step.
    /// </summary>
    private void TryClickListClose(Mat frame, PixelRect title)
    {
        long now = Environment.TickCount64;
        _overlays.Add(new OverlayItem(title, WarText.TeleportScrollTitle, OverlayColor.Blue));
        var close = _analyzer.FindCloseBlobRightOf(frame, title);
        if (close == null)
        {
            Log?.Invoke(LogLevel.Warning, $"No close button shows right of the {WarText.TeleportScrollTitle} title - leaving the list open.");
            WarShot(frame, "war_list_open");
            _warListStep = 2;
            _waitUntil = now + 200;
            return;
        }

        _overlays.Add(new OverlayItem(close.Value, "close", OverlayColor.Green));
        if (!WarClick(close.Value, "the X of the teleport scroll"))
            return;     // the game is not in front: the close step looks again

        if (_controls.IsDryRun)
        {
            // Nothing was clicked: looking again would only find the same window.
            _warListStep = 2;
            return;
        }

        _warListCloseClicks++;
        _warListCloseLooked = false;
        _warListGoneReads = 0;
        _waitUntil = now + 700;
    }

    /// <summary>
    /// Step 2: the inventory the teleport opened. Laby / LOD teleport away and the zone change closes it; here the alt
    /// stays. The teleport scroll still on screen -> the inventory key, checked on a later frame (one more look before a
    /// second tap - the key is a toggle), at most 2 taps, else a warning. Then <see cref="WarListFinish"/>. A dry run
    /// presses nothing.
    /// </summary>
    private void TickWarInventoryClose(Mat frame)
    {
        long now = Environment.TickCount64;
        string key = _settings.Input.InventoryKey;
        if (_controls.IsDryRun)
        {
            WarListFinish(frame);
            return;
        }

        var roi = _settings.Regions.Scroll.ToPixels(frame.Width, frame.Height);
        if (_analyzer.FindIcon(frame, "teleportscroll", roi, ScrollThreshold) == null)
        {
            if (_warInventoryTaps > 0)
                Log?.Invoke(LogLevel.Info, "The inventory is closed.");
            WarListFinish(frame);
            return;
        }

        if (_warInventoryTaps > 0 && !_warInventoryLooked)
        {
            _warInventoryLooked = true;
            _detail = "waiting for the inventory to close";
            _waitUntil = now + 700;
            return;
        }

        if (_warInventoryTaps >= 2)
        {
            Log?.Invoke(LogLevel.Warning, $"The inventory still shows the teleport scroll after {_warInventoryTaps} taps of '{key}' - leaving it open.");
            WarShot(frame, "war_inventory_open");
            WarListFinish(frame);
            return;
        }

        if (!_controls.Press(GameAction.OpenInventory))
        {
            _detail = "inventory key blocked - waiting for the game to be in front";
            _waitUntil = now + 300;
            return;
        }

        _warInventoryTaps++;
        _warInventoryLooked = false;
        Log?.Invoke(LogLevel.Info, $"Closing the inventory with '{key}'.");
        _waitUntil = now + 700;
    }

    /// <summary>
    /// The list and the inventory are closed (or left with a warning): Start's check remembers the entry or stops; the
    /// trip back stops on two fitting caves and fails (restart) on a missing one or a list that never showed.
    /// </summary>
    private void WarListFinish(Mat frame)
    {
        string why = _warListOutcome?.Why
                     ?? $"The teleport scroll shows no '{WarText.TeleportScrollTitle}' title on page {_settings.War.ListPage} - the list did not open?";
        switch (_warListVerdict)
        {
            case WarListVerdict.Pick when _warListCheck && _warListOutcome?.Entry is { } entry:
                _warPendingMap = entry;
                _warListCheck = false;
                Log?.Invoke(LogLevel.Success, $"'{_warTitle}' is '{entry}' on page {_settings.War.ListPage} of the teleport scroll.");
                Advance(PilotPhase.WarRemember, 500);
                return;
            case WarListVerdict.Ambiguous:
            case WarListVerdict.Missing when _warListCheck:
                WarStop(frame, why);
                return;
            default:
                Fail(frame, why.EndsWith('?') ? why : $"{why}.");
                return;
        }
    }

    // ---------------------------------------------------------------- remembering the spot

    /// <summary>
    /// Start's remember step (design §4.1.3): the targeted monster (target bar; Tab up to 3 times, 1 s apart, when
    /// none shows - the alt is autohunting, so one normally does) goes to the map check (ClickMap in verify mode,
    /// which also swaps in the list's spelling of the name); back here once it is confirmed, the cave and monster are
    /// saved in settings and the wait for the war begins. The target bar is only read with the game in front.
    /// </summary>
    private async Task TickWarRememberAsync(Mat frame)
    {
        long now = Environment.TickCount64;
        var war = _settings.War;
        if (_warMonsterConfirmed)
        {
            if (_warPendingMap == null || _warPendingMonster == null)
            {
                WarStop(frame, "The cave or the monster was lost before it could be saved - press Start again inside the cave");
                return;
            }

            war.HomeMap = _warPendingMap;
            war.HomeMonster = _warPendingMonster;
            SettingsStore.Save(_settings);
            ModeChanged?.Invoke();      // the settings panel's "Back to:" line reads the saved spot again
            _warPendingMap = null;
            _warPendingMonster = null;
            _warMapVerify = false;
            _warMonsterConfirmed = false;
            _warSpotChecked = true;
            _warRemembering = false;
            Log?.Invoke(LogLevel.Success, $"Remembered {war.HomeMap} / {war.HomeMonster}.");
            BeginWarWait("spot remembered");
            return;
        }

        if (!_controls.IsGameInFront)
        {
            _detail = "waiting for the game window to be in front";
            _waitUntil = now + 500;
            return;
        }

        var (name, _) = await _analyzer.ReadTargetAsync(frame);
        if (name != null)
        {
            _warPendingMonster = name;
            _warMapVerify = true;
            ResetWarMapPick();
            Log?.Invoke(LogLevel.Info, $"Targeted '{name}' - checking it against this map's monster list.");
            Advance(PilotPhase.ClickMap, 200);
            return;
        }

        if (_attempts >= 3)
        {
            WarStop(frame, "No monster targeted - let the autohunt target one and press Start again");
            return;
        }

        if (!_controls.Press(GameAction.TargetNearest))
        {
            _detail = "Tab blocked - waiting for the game to be in front";
            _waitUntil = now + 300;
            return;
        }

        _attempts++;
        _detail = $"no target in the target bar - Tab {_attempts}/3";
        _waitUntil = now + 1000;
    }

    /// <summary>
    /// The map pick's read of the monster list (<see cref="RegionSettings.MapBossList"/>, OCR at the scale the Laby pick reads
    /// it): the rows whose whole name column is <paramref name="monster"/> (<see cref="WarText.MonsterRows"/>).
    /// </summary>
    private async Task<IReadOnlyList<WarText.MonsterRow>> ReadWarMonsterRowsAsync(Mat frame, PixelRect roi, string monster)
    {
        var words = await _analyzer.Ocr.RecognizeAsync(frame, roi, _settings.Detection.OcrScale);
        return WarText.MonsterRows(ScreenAnalyzer.ToLines(words), monster);
    }

    /// <summary>
    /// The one row of <paramref name="rows"/> as the map pick's hit - the name column as its text (the list's spelling), the
    /// OCR line as its line, the line's box for the row and '&gt;' clicks; null for none or for rows that cannot be told apart.
    /// </summary>
    private static TextHit? WarRowHit(IReadOnlyList<WarText.MonsterRow> rows)
        => rows.Count == 1 ? new TextHit(rows[0].Name, rows[0].Similarity, rows[0].Bounds, rows[0].Line, rows[0].Bounds, 0) : null;

    /// <summary>A fresh map pick: no scrolls, reopens or row clicks counted from an earlier one.</summary>
    private void ResetWarMapPick()
    {
        _mapScrolls = 0;
        _mapReopens = 0;
        _mapVisibleMisses = 0;
        _chevronClicked = false;
        _chevronTaps = 0;
        _rowNameMisses = 0;
        _warMapClosing = false;
        _warMapCloseClicked = false;
    }

    // ---------------------------------------------------------------- the trip back

    /// <summary>Trips back to the cave allowed within <see cref="WarReturnWindowMs"/> without a war in between.</summary>
    internal const int WarMaxReturns = 3;

    /// <summary>The window <see cref="WarMaxReturns"/> counts in: 30 min.</summary>
    internal const int WarReturnWindowMs = 30 * 60 * 1000;

    /// <summary>
    /// One more trip back to the cave at <paramref name="now"/> (TickCount64), counted in <paramref name="trips"/> - the trips
    /// since the last war (<see cref="BeginWarEnter"/> clears them), the ones older than <see cref="WarReturnWindowMs"/>
    /// dropped. False, and not counted, when <see cref="WarMaxReturns"/> remain: a cave that takes the teleport but sends the
    /// alt back to town would loop, a teleport each round.
    /// </summary>
    internal static bool WarReturnAllowed(List<long> trips, long now)
    {
        trips.RemoveAll(t => now - t > WarReturnWindowMs);
        if (trips.Count >= WarMaxReturns)
            return false;

        trips.Add(now);
        return true;
    }

    /// <summary>
    /// <see cref="TickReconnectAsync"/> in Auto War, after START GAME: the zone the lines of the minimap band name once the
    /// login screens are gone - the war map (<paramref name="warMap"/>: a re-login during the war lands there, not in the
    /// cave the run's <c>Destination</c> names), else any zone title (<see cref="WarText.IsZoneTitleLine"/>: a cave not saved
    /// yet). Null while a login screen still shows (the server list's "[CH-01] ..." row reads like a title, the character
    /// screen shows the location) or when no line names a zone. The Start dispatch sorts the zone out after the restart.
    /// </summary>
    internal static string? WarReloginZone(IReadOnlyList<string> minimapLines, string warMap, bool loginScreen)
    {
        if (loginScreen)
            return null;

        return minimapLines.Any(l => ZoneMatches(l, warMap)) ? warMap : minimapLines.FirstOrDefault(WarText.IsZoneTitleLine);
    }

    /// <summary>
    /// Back to the saved cave (design §4.8): the teleport phases find <see cref="Destination"/> (the saved map) on
    /// page <see cref="WarSettings.ListPage"/> and their load step goes to WarBack. Stops instead when no cave is saved,
    /// when this Start began remembering a cave and was sent away before saving it - the saved one is an earlier
    /// Start's, not where the user started this one - or after <see cref="WarMaxReturns"/> trips in 30 min without a war
    /// in between (<see cref="WarReturnAllowed"/>).
    /// </summary>
    private void BeginWarReturn(Mat? frame, string why)
    {
        var war = _settings.War;
        if (WarStaysInTown)
        {
            // Only reached outside town (the town call sites wait there): a zone that is neither the war nor a town.
            WarStop(frame, $"Not in {_warTownHome} ({why}) - Auto War waits in town this Start; press Start again in town");
            return;
        }

        if (_warRemembering)
        {
            WarStop(frame, $"Sent to town ({why}) before this cave and its monster were remembered - press Start again inside the cave");
            return;
        }

        if (!HasWarSpot)
        {
            WarStop(frame, $"No cave is saved to go back to ({why}) - {NotACaveMessage}");
            return;
        }

        if (!WarReturnAllowed(_warReturnTrips, Environment.TickCount64))
        {
            WarStop(frame, $"Not going back to {war.HomeMap} again ({why}): {WarMaxReturns} trips back within {WarReturnWindowMs / 60000} min "
                           + "and no war in between - the cave does not keep the character; stopped instead of using another teleport. "
                           + "Press Start again inside the cave");
            return;
        }

        ReleaseSweepKey();
        Log?.Invoke(LogLevel.Info, $"Going back to {war.HomeMap} / {war.HomeMonster} ({why}).");
        _returning = false;
        _warListCheck = false;
        _warMapVerify = false;
        _warPendingMap = null;
        _warPendingMonster = null;
        _warMonsterConfirmed = false;
        _warArriveBaseline = null;
        ResetWarList();
        ResetTeleportPaging();
        Advance(PilotPhase.OpenInventory, 1500);
    }

    /// <summary>
    /// After the return teleport's load (design §4.8.2): the saved map on the minimap (<see cref="WarText.SameZone"/>)
    /// -> this Start is at its spot, the map pick of the saved monster. Still in a home town after 60 s: the teleport was
    /// refused (a premium pass ran out, ...) - once more, the second time in a row stop. Neither for 60 s: fail (the
    /// restart's dispatch sorts it out). The title is only read with the game in front, and the 60 s only count then
    /// (they start again when the game comes back, as the zone guard's 2 minutes do).
    /// </summary>
    private async Task TickWarBackAsync(Mat frame)
    {
        long now = Environment.TickCount64;
        var war = _settings.War;
        if (!_controls.IsGameInFront)
        {
            _phaseStart = now;
            _detail = "waiting for the game window to be in front";
            _waitUntil = now + 500;
            return;
        }

        long waited = now - _phaseStart;
        string? title = await ReadMinimapTitleAsync(frame);
        if (WarText.SameZone(title, war.HomeMap))
        {
            await WarBackInCaveAsync(frame, now);
            return;
        }

        if (waited < 60000)
        {
            _detail = $"waiting for '{war.HomeMap}' on the minimap ({waited / 1000}s)";
            _waitUntil = now + 500;
            return;
        }

        if (TownIn(title) != null)
        {
            if (++_warRefusedTeleports >= 2)
            {
                WarStop(frame, $"Could not go back to {war.HomeMap}");
                return;
            }

            Log?.Invoke(LogLevel.Warning, $"Still in {HomeTown} {waited / 1000}s after the teleport to {war.HomeMap} - trying once more.");
            BeginWarReturn(frame, "the teleport did not go through");
            return;
        }

        Fail(frame, $"'{war.HomeMap}' never showed on the minimap after the teleport ({waited / 1000}s; it reads '{title}').");
    }

    /// <summary>
    /// The saved cave is on the minimap - after the return teleport (WarBack), or the game took the character back there
    /// by itself after a war (<see cref="WarZoneCheckAsync"/>): this Start is at its spot; the map pick of the saved monster
    /// comes next.
    /// </summary>
    private async Task WarBackInCaveAsync(Mat frame, long now)
    {
        var war = _settings.War;
        _warRefusedTeleports = 0;
        _warSpotChecked = true;
        _zoneConfirmedAt = now;
        // The combat log before the walk: the arrival is the "Arrived" line that is not in it - taken now, before
        // the map covers the log, so a short walk's line right after the '>' click is not missed.
        _warArriveBaseline = Normalized(await ReadCombatLogAsync(frame));
        Log?.Invoke(LogLevel.Success, $"Back in {war.HomeMap} - walking to '{war.HomeMonster}'.");
        ResetWarMapPick();
        Advance(PilotPhase.ClickMap, 500);
    }

    /// <summary>The combat log's lines (<see cref="RegionSettings.AttackMessage"/>, OCR x1.5), top to bottom.</summary>
    private async Task<IReadOnlyList<string>> ReadCombatLogAsync(Mat frame)
    {
        var roi = _settings.Regions.AttackMessage.ToPixels(frame.Width, frame.Height);
        return ScreenAnalyzer.ToLines(await _analyzer.Ocr.RecognizeAsync(frame, roi, 1.5)).Select(l => l.Text).ToList();
    }

    /// <summary><see cref="WarText.NewLines"/> compares the lines' text: both sides normalised the same way.</summary>
    private static List<string> Normalized(IReadOnlyList<string> lines) => lines.Select(TextMatcher.Normalize).ToList();

    /// <summary>
    /// Walking to the monster (design §4.8.4): a combat-log line "Arrived: &lt;monster&gt;" that was not there before the
    /// walk (an old one from an earlier trip does not count, even re-read with a letter of noise), read every 700 ms with
    /// the game in front; none in ArriveWaitMs (counted with the game in front) -> a warning and a <c>war_noarrive</c>
    /// snapshot, and on all the same. Then the autohunt goes on.
    /// </summary>
    private async Task TickWarArriveAsync(Mat frame)
    {
        long now = Environment.TickCount64;
        var war = _settings.War;
        if (!_controls.IsGameInFront)
        {
            _phaseStart = now;
            _detail = "waiting for the game window to be in front";
            _waitUntil = now + 500;
            return;
        }

        var raw = await ReadCombatLogAsync(frame);
        var lines = Normalized(raw);
        if (_warArriveBaseline == null)
        {
            _warArriveBaseline = lines;
            _waitUntil = now + 700;
            return;
        }

        string? arrived = WarText.NewLines(_warArriveBaseline, lines)
            .FirstOrDefault(l => WarText.IsArrivedLine(l, war.ArrivedText, war.HomeMonster));
        if (arrived != null)
        {
            int at = lines.IndexOf(arrived);
            Log?.Invoke(LogLevel.Success, $"Arrived at '{war.HomeMonster}' (combat log: '{(at >= 0 ? raw[at] : arrived)}').");
            ResetAutohuntCheck();
            Advance(PilotPhase.WarHuntOn, 200);
            return;
        }

        long waited = now - _phaseStart;
        if (waited >= war.ArriveWaitMs)
        {
            Log?.Invoke(LogLevel.Warning, $"No '{war.ArrivedText}: {war.HomeMonster}' line in the combat log after {war.ArriveWaitMs / 1000}s - turning the autohunt on anyway.");
            WarShot(frame, "war_noarrive");
            ResetAutohuntCheck();
            Advance(PilotPhase.WarHuntOn, 200);
            return;
        }

        _detail = $"walking to '{war.HomeMonster}' - waiting for '{war.ArrivedText}: {war.HomeMonster}' ({waited / 1000}s of {war.ArriveWaitMs / 1000}s)";
        _waitUntil = now + 700;
    }

    /// <summary>
    /// At the monster (design §4.8.5), or Start / a restart already at the saved spot: the autohunt on, confirmed in the
    /// combat log (J is a toggle), then the wait for the next war. The check also ends when it cannot tell (no line, or
    /// still off after 3 taps - it warns and saves a snapshot itself): "hunting again" is only claimed when it read "on".
    /// The combat log is only read with the game in front (another window's log could read "on" or "off").
    /// </summary>
    private async Task TickWarHuntOnAsync(Mat frame)
    {
        var war = _settings.War;
        if (!WarInFront(Environment.TickCount64))
            return;

        if (!await EnsureAutohuntAsync(frame, "at the monster", wantOn: true, trustVisibleLine: false))
            return;

        if (_huntCheckConfirmed)
        {
            Log?.Invoke(LogLevel.Success, $"Back at '{war.HomeMonster}' in {war.HomeMap}, hunting again.");
            BeginWarWait("hunting again");
            return;
        }

        BeginWarWait("autohunt not confirmed on");
    }
}

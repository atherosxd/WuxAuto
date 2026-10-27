using System.Globalization;
using OpenCvSharp;
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
    private bool _warSpotChecked;          // this Start remembered the cave and monster
    private bool _warRemembering;          // Start in a cave began remembering it, not saved yet
    private int _warRefusedTeleports;      // return teleports in a row that left the character in town

    // Per run (ResetRunState clears them).
    private string? _warTitle;             // the minimap title read at Start (the cave)
    private bool _warWindowsChecked;       // Start closed the war windows a Stop / Fail may have left open
    private int _warListStep;              // the page check: 0 read the list, 1 close it
    private double _warListScale;          // the OCR scale that read the list's title
    private string? _warListEntry;         // the cave's entry on the page ...
    private string? _warListStopWhy;       // ... or why there is none (set by any read that showed the list)
    private bool _warListAmbiguous;        // that why is two caves fitting, not a missing one
    private string _warListSeen = string.Empty;   // that read's entries, for the log
    private int _warListCloseClicks;
    private bool _warListCloseLooked;      // still open after a click: looked once more before clicking again
    private IReadOnlyList<string>? _warArriveBaseline;   // the combat log (normalised) before the walk to the monster

    /// <summary>True while Start runs the tribe war (<see cref="WuxAuto.Core.RunMode.AutoWar"/>).</summary>
    private bool IsWar => _settings.Sequence.Mode == WuxAuto.Core.RunMode.AutoWar;

    /// <summary>The cave to go back to: the one being remembered at Start, else the saved one.</summary>
    private string WarHomeMap => _warPendingMap ?? _settings.War.HomeMap;

    /// <summary>A cave and a monster are saved to go back to.</summary>
    private bool HasWarSpot => !string.IsNullOrWhiteSpace(_settings.War.HomeMap) && !string.IsNullOrWhiteSpace(_settings.War.HomeMonster);

    private string NotACaveMessage => $"Auto War must be started inside a cave on page {_settings.War.ListPage} of the teleport scroll";

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
    /// The zone guard's test in Auto War (the cave of the map pick): <see cref="WarText.SameZone"/> - never the
    /// 10-letter run that takes "[Dark Soul Cave IX]" for "[Dark Soul Cave X]". During Start's check the title Start
    /// read also counts as it was read (no teleport since, so it is this cave even when OCR noise made the list pick
    /// it by its closest entry).
    /// </summary>
    private bool WarZoneConfirmed(string? title)
        => WarText.SameZone(title, Destination)
           || (_warMapVerify && title != null && _warTitle != null
               && TextMatcher.Normalize(title) == TextMatcher.Normalize(_warTitle));

    // ---------------------------------------------------------------- Start

    /// <summary>
    /// Start's dispatch (design §4.1, §5), the first thing of every run: the war windows a Stop or a Fail can leave
    /// open are closed first (the war timer's close step), then the minimap title decides - Bohai Outpost: into the
    /// war; a home town: back to the saved cave; the Labyrinth, LOD or no title: stop; a cave: every buff icon and
    /// Shura must be on the skill bar, then back to waiting (this Start already remembered this cave) or the page
    /// check and the remember step. Only with the game in front: behind another window the capture shows that
    /// window's minimap, and a cave read there would be remembered.
    /// </summary>
    private async Task TickWarCheckZoneAsync(Mat frame)
    {
        long now = Environment.TickCount64;
        var war = _settings.War;
        if (!_input.IsTargetForeground)
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
            WarStop(frame, NotACaveMessage);
            return;
        }

        if (ZoneMatches(title, war.Destination))
        {
            BeginWarEnter($"Start inside {war.Destination}");
            return;
        }

        if (TownIn(title) != null)
        {
            if (HasWarSpot)
            {
                BeginWarReturn(frame, "Start in town");
                return;
            }

            Log?.Invoke(LogLevel.Info, $"In {HomeTown} with no cave saved to go back to.");
            WarStop(frame, NotACaveMessage);
            return;
        }

        if (ZoneMatches(title, _settings.Sequence.DestinationText) || ZoneMatches(title, _settings.Lod.Destination))
        {
            Log?.Invoke(LogLevel.Info, $"In '{title}' - the Labyrinth and LOD are no caves for Auto War.");
            WarStop(frame, NotACaveMessage);
            return;
        }

        // A cave (or a map that the page check rules out).
        _warTitle = title;
        if (!WarSkillBarReady(frame))
            return;

        if (string.IsNullOrWhiteSpace(_settings.Lod.ShuraKey))
        {
            WarStop(frame, "Set the Shura key (the 'Shura key (LOD)' setting) - Auto War holds it in every fight");
            return;
        }

        if (_warSpotChecked && WarText.SameZone(title, war.HomeMap) && !string.IsNullOrWhiteSpace(war.HomeMonster))
        {
            BeginWarWait("back in the cave");
            return;
        }

        Log?.Invoke(LogLevel.Info, $"In '{title}' - checking it is on page {war.ListPage} of the teleport scroll, then remembering it and the targeted monster.");
        _warRemembering = true;
        _warPendingMap = null;
        _warListCheck = true;
        _warListStep = 0;
        _warListEntry = null;
        _warListStopWhy = null;
        _returning = false;
        ResetTeleportPaging();
        Advance(PilotPhase.OpenInventory, 300);
    }

    /// <summary>
    /// Every buff icon (<see cref="WarSettings.BuffIcons"/>) and Shura on the skill bar at
    /// <see cref="WarSettings.IconMatchScore"/> (design §4.1.2: "all must be there"). Missing ones stop the pilot,
    /// each named with its best score at any threshold - 0.90 is not proven on a real alt's bar yet, so a near miss
    /// must show in that one line. True when all are there.
    /// </summary>
    private bool WarSkillBarReady(Mat frame)
    {
        var war = _settings.War;
        var templates = war.BuffIcons.Append(_settings.Lod.ShuraTemplate)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
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
            return true;
        }

        WarStop(frame, $"Missing on the skill bar (score {war.IconMatchScore.ToString("0.00", CultureInfo.InvariantCulture)} needed): "
                       + $"{DescribeMissingIcons(missing)} - put them on the bar and press Start again");
        return false;
    }

    /// <summary>"warbuff2 (best 0.84), shura (not seen)": each missing icon with its best score, rounded down.</summary>
    internal static string DescribeMissingIcons(IEnumerable<(string Name, double? Best)> missing)
        => string.Join(", ", missing.Select(m => m.Best is double best
            ? $"{m.Name} (best {(Math.Floor(best * 100 + 1e-9) / 100).ToString("0.00", CultureInfo.InvariantCulture)})"
            : $"{m.Name} (not seen)"));

    // ---------------------------------------------------------------- the teleport list

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

    /// <summary>"'a' or 'b'": the pick and the entry that reads next most like <paramref name="name"/>.</summary>
    private static string TwoBest(string name, IReadOnlyList<(string Text, PixelRect Bounds)> entries, string best)
    {
        string? second = entries.Select(e => e.Text)
            .Where(e => e != best)
            .OrderByDescending(e => TextMatcher.Similarity(e, name))
            .FirstOrDefault();
        return second == null ? $"'{best}'" : $"'{best}' or '{second}'";
    }

    /// <summary>One read of the open teleport list: the title, and the entry when sure, else why there is none.</summary>
    private sealed record WarListPick(PixelRect Title, string? Entry, PixelRect EntryBounds, string? Why, bool Ambiguous, string Seen);

    /// <summary>
    /// One read of the open teleport list for <paramref name="name"/>: <see cref="WarText.BestListEntry"/>, and with
    /// <paramref name="sameZone"/> only an entry that reads as the name itself (<see cref="WarText.SameZone"/> - the
    /// prefix rule alone would take "[Dark Soul Cave XI]" for a misread "[Dark Soul Cave X]"). Missing, ambiguous or
    /// not the same: <see cref="WarListPick.Why"/>. Null when the list's title does not show.
    /// </summary>
    private async Task<WarListPick?> ReadWarListPickAsync(Mat frame, double scale, string name, bool sameZone)
    {
        var (title, entries) = await ReadTeleportListAsync(frame, scale);
        if (title == null)
            return null;

        int page = _settings.War.ListPage;
        string seen = entries.Count == 0 ? "no entries read" : string.Join(" | ", entries.Select(e => e.Text));
        string? entry = WarText.BestListEntry(name, entries.Select(e => e.Text).ToList(), out bool ambiguous);
        string? why = entry == null
            ? $"'{name}' is not on page {page} of the teleport scroll"
            : ambiguous
                ? $"'{name}' could be {TwoBest(name, entries, entry)} on page {page} of the teleport scroll - not guessing which cave"
                : sameZone && !WarText.SameZone(entry, name)
                    ? $"The closest entry on page {page} of the teleport scroll is '{entry}', not '{name}' - not clicking it"
                    : null;
        return why != null
            ? new WarListPick(title.Value.Bounds, null, default, why, entry != null && ambiguous, seen)
            : new WarListPick(title.Value.Bounds, entry, entries.First(e => e.Text == entry).Bounds, null, false, seen);
    }

    /// <summary>
    /// Start's page check (design §4.1.1), ticked in FindDestination: page to <see cref="WarSettings.ListPage"/>, read
    /// it (up to 3 reads at other OCR scales while the cave is missing or ambiguous; the last read that showed the list
    /// decides), close the list with its X - never an entry - then stop or remember the entry that
    /// <see cref="WarText.BestListEntry"/> gives for Start's title.
    /// </summary>
    private async Task TickWarListCheckAsync(Mat frame)
    {
        if (_warListStep == 1)
        {
            await TickWarListCloseAsync(frame);
            return;
        }

        long now = Environment.TickCount64;
        var war = _settings.War;
        if (_pages + 1 < war.ListPage)
        {
            Advance(PilotPhase.FindChevron, _settings.Sequence.RetryDelayMs);
            return;
        }

        double scale = TeleportListScale(_attempts);
        var pick = await ReadWarListPickAsync(frame, scale, _warTitle ?? string.Empty, sameZone: false);
        if (pick != null)
        {
            _warListScale = scale;
            _warListStopWhy = pick.Why;
            _warListSeen = pick.Seen;
        }

        if (pick?.Entry == null && ++_attempts < 3)
        {
            _detail = $"reading page {war.ListPage} of the teleport scroll ({_attempts}/3)";
            _waitUntil = now + 700;
            return;
        }

        if (pick?.Entry == null && _warListStopWhy == null)
        {
            Fail(frame, $"The teleport scroll shows no '{WarText.TeleportScrollTitle}' title on page {war.ListPage} - the list did not open?");
            return;
        }

        Log?.Invoke(LogLevel.Info, $"Page {war.ListPage} of the teleport scroll: {_warListSeen}.");
        _warListEntry = pick?.Entry;
        if (_warListEntry != null)
            _warListStopWhy = null;

        // Closed first, whatever the outcome: its X, never an entry. This frame's title when it shows one,
        // else the close step looks for it on the next frame.
        _warListStep = 1;
        _warListCloseClicks = 0;
        _warListCloseLooked = false;
        if (pick != null)
            ClickTeleportListClose(frame, pick.Title);
        else
            _waitUntil = now + 300;
    }

    /// <summary>
    /// The list check's close: the title gone = closed, then on with the decision. Still there right after a click:
    /// one more look first - the window may only be slow to close, and a second click where its X was would then land
    /// in the world (a click there walks the character). At most 3 clicks, each decided on a fresh frame.
    /// </summary>
    private async Task TickWarListCloseAsync(Mat frame)
    {
        long now = Environment.TickCount64;
        var (title, _) = await ReadTeleportListAsync(frame, _warListScale);
        if (title == null)
        {
            if (_warListCloseClicks > 0)
                Log?.Invoke(LogLevel.Info, "The teleport scroll is closed.");
            WarListDecided(frame);
            return;
        }

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
            WarListDecided(frame);
            return;
        }

        _warListCloseLooked = false;
        ClickTeleportListClose(frame, title.Value.Bounds);
    }

    /// <summary>One click on the X right of the TELEPORT SCROLL title; a dry run goes on with the decision.</summary>
    private void ClickTeleportListClose(Mat frame, PixelRect title)
    {
        var close = _analyzer.FindCloseButtonRightOf(frame, title);
        _overlays.Add(new OverlayItem(title, WarText.TeleportScrollTitle, OverlayColor.Blue));
        _overlays.Add(new OverlayItem(close, "close", OverlayColor.Green));
        if (!WarClick(close, "the X of the teleport scroll"))
            return;     // the game is not in front: the close step looks again

        if (_input.IsDryRun)
        {
            // Nothing was clicked: looking again would only find the same window.
            WarListDecided(frame);
            return;
        }

        _warListCloseClicks++;
        _waitUntil = Environment.TickCount64 + 700;
    }

    /// <summary>The list is closed (or left open with a warning): stop, or remember the cave's entry.</summary>
    private void WarListDecided(Mat frame)
    {
        if (_warListStopWhy != null || _warListEntry == null)
        {
            WarStop(frame, _warListStopWhy ?? NotACaveMessage);
            return;
        }

        _warPendingMap = _warListEntry;
        _warListCheck = false;
        Log?.Invoke(LogLevel.Success, $"'{_warTitle}' is '{_warListEntry}' on page {_settings.War.ListPage} of the teleport scroll.");
        Advance(PilotPhase.WarRemember, 500);
    }

    /// <summary>
    /// The trip back's teleport pick (design §4.8.1), ticked in FindDestination: page to
    /// <see cref="WarSettings.ListPage"/>, then click the saved map's entry - only one that reads as that map
    /// (<see cref="ReadWarListPickAsync"/> with <c>sameZone</c>), on the frame it was read on; never a guess. After 3
    /// reads at other OCR scales: two caves that fit stop the pilot (Start checked the cave was unique); missing or
    /// not the same - an OCR or paging miss, or the cave gone from the list - fails, so the restart tries again.
    /// </summary>
    private async Task TickWarDestinationAsync(Mat frame)
    {
        long now = Environment.TickCount64;
        var war = _settings.War;
        if (_pages + 1 < war.ListPage)
        {
            Advance(PilotPhase.FindChevron, _settings.Sequence.RetryDelayMs);
            return;
        }

        string home = war.HomeMap;
        var pick = await ReadWarListPickAsync(frame, TeleportListScale(_attempts), home, sameZone: true);
        if (pick != null)
        {
            _warListStopWhy = pick.Why;
            _warListAmbiguous = pick.Ambiguous;
            _warListSeen = pick.Seen;
        }

        if (pick?.Entry == null)
        {
            if (++_attempts < 3)
            {
                _detail = $"reading page {war.ListPage} of the teleport scroll for '{home}' ({_attempts}/3)";
                _waitUntil = now + 700;
                return;
            }

            if (_warListStopWhy == null)
            {
                Fail(frame, $"The teleport scroll shows no '{WarText.TeleportScrollTitle}' title on page {war.ListPage} - the list did not open?");
                return;
            }

            Log?.Invoke(LogLevel.Info, $"Page {war.ListPage} of the teleport scroll: {_warListSeen}.");
            if (_warListAmbiguous)
                WarStop(frame, _warListStopWhy);
            else
                Fail(frame, $"{_warListStopWhy}.");
            return;
        }

        _overlays.Add(new OverlayItem(pick.EntryBounds, pick.Entry, OverlayColor.Green));
        if (!WarClick(pick.EntryBounds, $"'{pick.Entry}' in the teleport scroll"))
            return;

        _preLoadHash = FrameSignature.Compute(frame, new PixelRect(0, 0, frame.Width, frame.Height));
        Advance(PilotPhase.WaitForLoad, _settings.Input.AfterClickMs);
    }

    // ---------------------------------------------------------------- remembering the spot

    /// <summary>
    /// Start's remember step (design §4.1.3): the targeted monster (target bar; Tab up to 3 times, 1 s apart, when
    /// none shows - the alt is autohunting, so one normally does) goes to the map check (ClickMap in verify mode,
    /// which also swaps in the list's spelling of the name); back here once it is confirmed, the cave and monster are
    /// saved in settings and the wait for the war begins.
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

        if (!_input.KeyTap(_settings.Dungeon.TabKey))
        {
            _detail = "Tab blocked - waiting for the game to be in front";
            _waitUntil = now + 300;
            return;
        }

        _attempts++;
        _detail = $"no target in the target bar - Tab {_attempts}/3";
        _waitUntil = now + 1000;
    }

    /// <summary>A fresh map pick: no scrolls, reopens or row clicks counted from an earlier one.</summary>
    private void ResetWarMapPick()
    {
        _mapScrolls = 0;
        _mapReopens = 0;
        _mapVisibleMisses = 0;
        _chevronClicked = false;
        _rowNameMisses = 0;
        _warMapClosing = false;
        _warMapCloseClicked = false;
    }

    // ---------------------------------------------------------------- the trip back

    /// <summary>
    /// Back to the saved cave (design §4.8): the teleport phases find <see cref="Destination"/> (the saved map) on
    /// page <see cref="WarSettings.ListPage"/> and their load step goes to WarBack. Stops instead when no cave is saved,
    /// or when this Start began remembering a cave and was sent away before saving it - the saved one is an earlier
    /// Start's, not where the user started this one.
    /// </summary>
    private void BeginWarReturn(Mat? frame, string why)
    {
        var war = _settings.War;
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

        ReleaseSweepKey();
        Log?.Invoke(LogLevel.Info, $"Going back to {war.HomeMap} / {war.HomeMonster} ({why}).");
        _returning = false;
        _warListCheck = false;
        _warMapVerify = false;
        _warPendingMap = null;
        _warPendingMonster = null;
        _warMonsterConfirmed = false;
        _warListStopWhy = null;
        _warListAmbiguous = false;
        _warArriveBaseline = null;
        ResetTeleportPaging();
        Advance(PilotPhase.OpenInventory, 1500);
    }

    /// <summary>
    /// After the return teleport's load (design §4.8.2): the saved map on the minimap (<see cref="WarText.SameZone"/>)
    /// -> the map pick of the saved monster. Still in a home town after 60 s: the teleport was refused (a premium pass
    /// ran out, ...) - once more, the second time in a row stop. Neither for 60 s: fail (the restart's dispatch sorts
    /// it out). The title is only read with the game in front.
    /// </summary>
    private async Task TickWarBackAsync(Mat frame)
    {
        long now = Environment.TickCount64;
        var war = _settings.War;
        long waited = now - _phaseStart;
        string? title = _input.IsTargetForeground ? await ReadMinimapTitleAsync(frame) : null;
        if (WarText.SameZone(title, war.HomeMap))
        {
            _warRefusedTeleports = 0;
            _zoneConfirmedAt = now;
            // The combat log before the walk: the arrival is the "Arrived" line that is not in it - taken now, before
            // the map covers the log, so a short walk's line right after the '>' click is not missed.
            _warArriveBaseline = Normalized(await ReadCombatLogAsync(frame));
            Log?.Invoke(LogLevel.Success, $"Back in {war.HomeMap} - walking to '{war.HomeMonster}'.");
            ResetWarMapPick();
            Advance(PilotPhase.ClickMap, 500);
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

    /// <summary>The combat log's lines (<see cref="RegionSettings.AttackMessage"/>, OCR x1.5), top to bottom.</summary>
    private async Task<IReadOnlyList<string>> ReadCombatLogAsync(Mat frame)
    {
        var roi = _settings.Regions.AttackMessage.ToPixels(frame.Width, frame.Height);
        return ScreenAnalyzer.ToLines(await _analyzer.Ocr.RecognizeAsync(frame, roi, 1.5)).Select(l => l.Text).ToList();
    }

    /// <summary><see cref="WarText.NewLines"/> compares exact strings: both sides normalised the same way.</summary>
    private static List<string> Normalized(IReadOnlyList<string> lines) => lines.Select(TextMatcher.Normalize).ToList();

    /// <summary>
    /// Walking to the monster (design §4.8.4): a combat-log line "Arrived: &lt;monster&gt;" that was not there before the
    /// walk (an old one from an earlier trip does not count), read every 700 ms; none in ArriveWaitMs -> a warning and
    /// a <c>war_noarrive</c> snapshot, and on all the same. Then the autohunt goes on.
    /// </summary>
    private async Task TickWarArriveAsync(Mat frame)
    {
        long now = Environment.TickCount64;
        var war = _settings.War;
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
    /// At the monster (design §4.8.5): the autohunt on, confirmed in the combat log (J is a toggle), then the wait for
    /// the next war. The check also ends when it cannot tell (no line, or still off after 3 taps - it warns and saves
    /// a snapshot itself): "hunting again" is only claimed when it read "on".
    /// </summary>
    private async Task TickWarHuntOnAsync(Mat frame)
    {
        var war = _settings.War;
        if (!await EnsureAutohuntAsync(frame, "at the monster", wantOn: true, trustVisibleLine: false))
            return;

        if (_huntCheckConfirmed)
        {
            Log?.Invoke(LogLevel.Success, $"Back at '{war.HomeMonster}' in {war.HomeMap}, hunting again.");
            BeginWarWait("hunting again");
            return;
        }

        Log?.Invoke(_input.IsDryRun ? LogLevel.Info : LogLevel.Warning,
            $"Back at '{war.HomeMonster}' in {war.HomeMap}, but the autohunt was not read as on - waiting for the next war anyway.");
        BeginWarWait("autohunt not confirmed on");
    }

    // stubs - replaced by Tasks 6-8
    private void BeginWarEnter(string why) => Fail(null, $"Auto War step not built yet: {why}");
}

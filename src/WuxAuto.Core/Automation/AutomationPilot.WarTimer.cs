using Emgu.CV;
using WuxAuto.Core;
using WuxAuto.Vision;
using LogLevel = WuxAuto.Core.LogLevel;

namespace WuxAuto.Automation;

/// <summary>
/// Auto War, between wars (design §4.2, §4.3): the next Bohai war's countdown is read from the MENU button
/// (top centre) -> Tribe Status -> NEXT WARS while the alt hunts; LeadMs before it the autohunt goes off and
/// the "[Bohai Outpost (Adept)] Confirm to move?" dialog is answered with OK. The dialog is also answered
/// when it comes without the timer (the 5 s whole-screen check calls <see cref="BeginWarJoin"/>).
/// </summary>
public sealed partial class AutomationPilot
{
    /// <summary>
    /// The MENU button (menubutton.png in <see cref="RegionSettings.MenuButton"/>): 0.836-1.000 on every clear real
    /// game frame (lowest 1440x900), at most 0.451 on frames without the game (561 saved frames, Task 4's probe).
    /// </summary>
    private const double MenuButtonScore = 0.70;

    private long? _warAt;              // TickCount64 of the next war's start; null = unknown or in progress
    private long _warNextReadAt;       // when the timer is read again; long.MaxValue = not before the war
    private int _warTimerStep;         // WarWait: 0 wait, 1 open the menu, 2 Tribe Status tile, 3 read, 4 close
    private int _warTimerFails;        // timer reads in a row that told nothing
    private int _warStepTries;         // WarWait: looks / clicks within the current step
    private bool _warMenuToggled;      // WarWait step 4: the MENU button was clicked to close a menu whose title did not read
    private long _warZoneCheckAt;      // WarWait: next minimap title read
    private bool _warInProgress;       // the last read said "In progress" (logged once)
    private bool _warJoinHuntOff;      // WarJoin: the autohunt is off (or the check gave up)
    private bool _warJoinGaveUp;       // WarJoin: no dialog in time - turning the autohunt back on
    private long _warJoinUntil;        // WarJoin: the dialog is waited for until then (war clock: time with the game in front)
    private long _warJoinZoneAt;       // WarJoin: next minimap title read

    /// <summary>Waits for the next Bohai war (<see cref="PilotPhase.WarWait"/>), reading its timer at once.</summary>
    private void BeginWarWait(string why)
    {
        Log?.Invoke(LogLevel.Info, $"Waiting for the next {_settings.War.Destination} war ({why}).");
        _warAt = null;
        _warTimerStep = 0;
        _warStepTries = 0;
        _warTimerFails = 0;
        _warInProgress = false;
        _warNextReadAt = Environment.TickCount64;
        _warZoneCheckAt = 0;
        Advance(PilotPhase.WarWait, 300);
    }

    /// <summary>
    /// What one timer read means for the wait: the war's start, when to read again and the unread count.
    /// A countdown: start = now + the time left; read again 2 min before the war when it is more than 3 min
    /// away (a misread shows up then), but no later than 15 min from now; 3 min or less, not again (the lead
    /// takes over). "In progress": this war is missed, read again in 5 min. Nothing read: the known start is
    /// kept and the read is tried again at once - from the third in a row on, 2 min later.
    /// </summary>
    internal static (long? WarAt, long NextReadAt, int Fails) PlanTimerRead(long now, WarCountdown? read, long? warAt, int fails)
    {
        if (read is { Remaining: { } left })
        {
            long at = now + (long)left.TotalMilliseconds;
            long next = at - now > 180_000 ? Math.Min(at - 120_000, now + 900_000) : long.MaxValue;
            return (at, next, 0);
        }

        if (read is { InProgress: true })
            return (null, now + 300_000, 0);

        fails++;
        return (warAt, fails >= 3 ? now + 120_000 : now, fails);
    }

    /// <summary>
    /// Until when the war dialog is waited for: <paramref name="dialogWaitMs"/> after the timer's start when the
    /// lead began the join (the start lies 0 to <paramref name="leadMs"/> ahead), else after now - the dialog is
    /// up already, or the start is stale (an earlier wait's) or far ahead (the timer was wrong).
    /// </summary>
    internal static long WarJoinDeadline(long now, long? warAt, int leadMs, int dialogWaitMs)
    {
        long basis = warAt is long at && at >= now && at - now <= leadMs ? at : now;
        return basis + dialogWaitMs;
    }

    private async Task TickWarWaitAsync(Mat frame)
    {
        // The timer read's steps look at the screen and click: only with the game in front (a second game client's
        // MENU / TRIBE STATUS must never be read). The wait itself (step 0) runs on: the war starts in real time.
        if (_warTimerStep != 0 && !WarInFront(Environment.TickCount64))
            return;

        switch (_warTimerStep)
        {
            case 1:
                TickWarOpenMenu(frame);
                return;
            case 2:
                await TickWarTribeTileAsync(frame);
                return;
            case 3:
                await TickWarReadTimerAsync(frame);
                return;
            case 4:
                await TickWarCloseAsync(frame);
                return;
        }

        long now = Environment.TickCount64;
        var war = _settings.War;

        // Where is the character? Only with the game in front: behind another window the capture shows that
        // window's minimap (a second game client once read as "sent to town", 2026-10-27).
        if (now >= _warZoneCheckAt)
        {
            _warZoneCheckAt = now + 10000;
            if (_controls.IsGameInFront)
            {
                string? title = await ReadMinimapTitleAsync(frame);
                // Waiting in town already: nothing to do there. Otherwise back to the cave (or, none saved, wait in town).
                if (TownIn(title) is { } town && !WarStaysInTown)
                {
                    if (!WarWaitInTown(frame, town, "sent to town while waiting"))
                        BeginWarReturn(frame, "sent to town while waiting");
                    return;
                }

                if (ZoneMatches(title, war.Destination))
                {
                    BeginWarEnter($"already in {war.Destination}");
                    return;
                }
            }
        }

        if (_warAt is long at && at - now <= war.LeadMs)
        {
            BeginWarJoin($"the war starts in {Math.Max(0, at - now) / 1000} s");
            return;
        }

        if (now >= _warNextReadAt)
        {
            // The menu button is looked for on the next tick's frame.
            _warTimerStep = 1;
            _warStepTries = 0;
            return;
        }

        string spot = WarStaysInTown
            ? $"staying in {_warTownHome}"
            : "back to " + (string.IsNullOrWhiteSpace(war.HomeMap) ? "the cave" : $"{war.HomeMap} / {(string.IsNullOrWhiteSpace(war.HomeMonster) ? "?" : war.HomeMonster)}");
        _detail = _warAt is long start
            ? $"waiting for the {war.Destination} war in {Clock(start - now)} - {spot}"
            : _warInProgress
                ? $"the {war.Destination} war is in progress (missed) - timer read again in {Clock(_warNextReadAt - now)}; {spot}"
                : _warTimerFails > 0
                    ? $"waiting for the {war.Destination} war (timer unread, next try in {Clock(_warNextReadAt - now)}) - {spot}"
                    : $"waiting for the {war.Destination} war (timer not read yet) - {spot}";
        _waitUntil = now + 500;
    }

    /// <summary>Step 1: the MENU button (top centre) opens the menu grid.</summary>
    private void TickWarOpenMenu(Mat frame)
    {
        long now = Environment.TickCount64;
        var roi = _settings.Regions.MenuButton.ToPixels(frame.Width, frame.Height);
        var button = _analyzer.FindIcon(frame, "menubutton", roi, MenuButtonScore);
        if (button == null)
        {
            if (++_warStepTries < 3)
            {
                _detail = $"looking for the menu button ({_warStepTries}/3)";
                _waitUntil = now + 700;
                return;
            }

            Log?.Invoke(LogLevel.Warning, "The menu button (top centre) was not found 3 times - the war timer is not read; trying again in 2 min.");
            WarShot(frame, "war_menu_missing");
            _warNextReadAt = now + 120000;
            _warTimerStep = 0;
            return;
        }

        _overlays.Add(new OverlayItem(button.Bounds, $"menu {button.Score:0.00}", OverlayColor.Green));
        if (!WarClick(button.Bounds, $"the menu button ({button.Score:0.00})"))
            return;

        _warTimerStep = 2;
        _warStepTries = 0;
        _waitUntil = now + 900;
    }

    /// <summary>Step 2: the Tribe Status tile of the menu grid opens TRIBE STATUS.</summary>
    private async Task TickWarTribeTileAsync(Mat frame)
    {
        long now = Environment.TickCount64;
        string tile = _settings.War.MenuTileText;
        var full = new PixelRect(0, 0, frame.Width, frame.Height);
        var (hit, _, _) = await _analyzer.FindTextAsync(frame, full, tile);
        // The hit's own text must cover the tile's: its line would also pass for the "status" of the
        // "Hide item apply status" check box, the best hit on every in-game frame without the menu (490 of 561).
        if (hit != null && TextMatcher.CoversMost(hit.Text, tile))
        {
            _overlays.Add(new OverlayItem(hit.Bounds, tile, OverlayColor.Green));
            if (!WarClick(hit.Bounds, $"the '{tile}' tile"))
                return;

            _warTimerStep = 3;
            _warStepTries = 0;
            _waitUntil = now + 1200;
            return;
        }

        if (++_warStepTries < 3)
        {
            _detail = $"looking for the '{tile}' tile ({_warStepTries}/3)";
            _waitUntil = now + 600;
            return;
        }

        ApplyTimerRead(frame, now, null, $"the menu showed no '{tile}' tile");
        BeginWarClose();
    }

    /// <summary>
    /// Step 3: the war row's countdown in TRIBE STATUS. While neither the window's title nor the row shows (still
    /// opening), up to 2 more reads 600 ms apart, each on a fresh frame, before the read counts as failed.
    /// </summary>
    private async Task TickWarReadTimerAsync(Mat frame)
    {
        long now = Environment.TickCount64;
        string row = _settings.War.TribeRowText;
        var full = new PixelRect(0, 0, frame.Width, frame.Height);
        var lines = ScreenAnalyzer.ToLines(await _analyzer.Ocr.RecognizeAsync(frame, full, 1.0));
        var read = await _analyzer.ReadWarCountdownAsync(frame, lines, row);
        bool shows = WarText.TribeStatusShows(lines, row);
        if (read == null && !shows && ++_warStepTries < 3)
        {
            _detail = $"waiting for TRIBE STATUS to show ({_warStepTries}/3)";
            _waitUntil = now + 600;
            return;
        }

        ApplyTimerRead(frame, now, read, shows ? $"TRIBE STATUS showed no '{row}' countdown" : "TRIBE STATUS did not show");
        BeginWarClose();
    }

    private void ApplyTimerRead(Mat frame, long now, WarCountdown? read, string unreadWhy)
    {
        string war = _settings.War.Destination;
        (_warAt, _warNextReadAt, _warTimerFails) = PlanTimerRead(now, read, _warAt, _warTimerFails);
        if (read is { Remaining: { } left })
        {
            _warInProgress = false;
            string again = _warNextReadAt == long.MaxValue ? "" : $"; read again in {Clock(_warNextReadAt - now)}";
            Log?.Invoke(LogLevel.Info, $"Next {war} war in {HoursClock(left)} (at {DateTime.Now + left:HH:mm:ss} PC time){again}.");
        }
        else if (read is { InProgress: true })
        {
            if (!_warInProgress)
                Log?.Invoke(LogLevel.Info, $"The {war} war is in progress - this one is missed; reading the timer again every 5 min.");
            _warInProgress = true;
        }
        else
        {
            bool giveUp = _warTimerFails >= 3;
            Log?.Invoke(LogLevel.Warning, $"War timer not read: {unreadWhy} ({_warTimerFails} in a row) - "
                                          + (giveUp ? "trying again in 2 min." : "trying again."));
            if (giveUp)
                WarShot(frame, "tribe_unread");
        }
    }

    /// <summary>Step 4 starts: nothing clicked yet.</summary>
    private void BeginWarClose()
    {
        _warTimerStep = 4;
        _warStepTries = 0;
        _warMenuToggled = false;
    }

    /// <summary>
    /// Step 4: TRIBE STATUS, then the MENU, closed with their X. The MENU up without a visible X - its title read
    /// but no close button seen, or no title at all but its Tribe Status tile (<see cref="WarText.IsMenuTileHit"/>;
    /// the grid scrolled under the title, the user's screenshot) - gets one click on the MENU button; still up after
    /// that = warning, left open. TRIBE STATUS without a visible X, or a hit that reads like its title rather than
    /// the tile, gets no click at all (no blind clicks; the MENU button would open the menu over it): warning, left
    /// open. One click per tick, each decided on a fresh frame, at most 4; nothing open = done. A dry run logs the
    /// first click it would make and ends.
    /// </summary>
    private async Task TickWarCloseAsync(Mat frame)
    {
        long now = Environment.TickCount64;
        string tile = _settings.War.MenuTileText;
        var full = new PixelRect(0, 0, frame.Width, frame.Height);
        // One read: its lines for the window titles, its best hit for the menu's tile.
        var (hit, _, words) = await _analyzer.FindTextAsync(frame, full, tile);
        var lines = ScreenAnalyzer.ToLines(words);
        var open = _analyzer.FindWarWindowClose(frame, lines);
        bool tileTextSeen = open == null && hit != null && TextMatcher.CoversMost(hit.Text, tile);
        if (open == null && !tileTextSeen)
        {
            _warTimerStep = 0;
            return;
        }

        _overlays.Add(open != null
            ? new OverlayItem(open.TitleBounds, open.Title, OverlayColor.Blue)
            : new OverlayItem(hit!.Bounds, tile, OverlayColor.Blue));

        bool menuUp = open is { Window: WarText.MenuTitle, Close: null }
                      || (open == null && WarText.IsMenuTileHit(hit!.Text, hit.Bounds, lines, tile));
        if (!menuUp && open?.Close == null)
        {
            Log?.Invoke(LogLevel.Warning, open != null
                ? $"'{open.Title}' is open, but no close button shows right of its title - leaving it."
                : $"'{hit!.Text}' reads like the {WarText.TribeStatusTitle} title (capitals), not the menu's tile - no close button to click; leaving it.");
            WarShot(frame, "war_close_stuck");
            _warTimerStep = 0;
            return;
        }

        if (menuUp && _warMenuToggled)
        {
            Log?.Invoke(LogLevel.Warning, "The menu is still open after a click on the menu button - leaving it.");
            WarShot(frame, "war_menu_open");
            _warTimerStep = 0;
            return;
        }

        if (_warStepTries >= 4)
        {
            Log?.Invoke(LogLevel.Warning, $"'{open?.Title ?? "MENU"}' is still open after {_warStepTries} clicks - leaving it.");
            WarShot(frame, menuUp ? "war_menu_open" : "war_close_stuck");
            _warTimerStep = 0;
            return;
        }

        PixelRect target;
        string what;
        if (!menuUp)
        {
            target = open!.Close!.Value;
            what = $"the X of '{open.Title}'";
        }
        else
        {
            // Whether the MENU button also closes the menu (a toggle) is not seen live yet.
            var roi = _settings.Regions.MenuButton.ToPixels(frame.Width, frame.Height);
            var button = _analyzer.FindIcon(frame, "menubutton", roi, MenuButtonScore);
            if (button == null)
            {
                Log?.Invoke(LogLevel.Warning, "The menu is still open without a visible close button, and the menu button is not found - leaving it.");
                WarShot(frame, "war_menu_open");
                _warTimerStep = 0;
                return;
            }

            target = button.Bounds;
            what = open != null
                ? $"the menu button ({button.Score:0.00}) - the menu's X does not show"
                : $"the menu button ({button.Score:0.00}) - the menu is still open, its title unread";
        }

        _overlays.Add(new OverlayItem(target, "close", OverlayColor.Green));
        if (!WarClick(target, what))
            return;

        if (_controls.IsDryRun)
        {
            // Nothing was clicked: looking again would only find the same window.
            _warTimerStep = 0;
            return;
        }

        _warMenuToggled |= menuUp;
        _warStepTries++;
        _waitUntil = now + (menuUp ? 900 : 700);
    }

    /// <summary>
    /// Joins the Bohai war (<see cref="PilotPhase.WarJoin"/>): autohunt off (so the alt is not mid-fight when the
    /// dialog comes), then OK on the war dialog. Called LeadMs before the timer's start, or when the dialog is up.
    /// The dialog wait (<see cref="WarJoinDeadline"/>) runs on the war clock: it stands still while the game is behind
    /// another window, when nothing is read (the clock never jumps, so a stop left over from an earlier phase only means
    /// it goes on from there).
    /// </summary>
    private void BeginWarJoin(string why)
    {
        long now = Environment.TickCount64;
        var war = _settings.War;
        _warJoinUntil = _warClock.Now(now) + (WarJoinDeadline(now, _warAt, war.LeadMs, war.DialogWaitMs) - now);
        // In town the autohunt is not on, and J there starts a Smart Farm journey: no autohunt step around the war.
        _warJoinHuntOff = WarStaysInTown;
        _warJoinGaveUp = false;
        _warJoinZoneAt = now + 5000;
        ResetAutohuntCheck();
        Log?.Invoke(LogLevel.Info, WarStaysInTown
            ? $"Joining the {war.Destination} war ({why}): OK on the war dialog."
            : $"Joining the {war.Destination} war ({why}): autohunt off, then OK on the war dialog.");
        Advance(PilotPhase.WarJoin, 200);
    }

    private async Task TickWarJoinAsync(Mat frame)
    {
        long now = Environment.TickCount64;
        var war = _settings.War;
        var seq = _settings.Sequence;

        // The combat log, the minimap and the dialog are read only with the game in front; the dialog wait stands still.
        if (!WarInFront(now))
            return;

        if (_warJoinGaveUp)
        {
            if (await EnsureAutohuntAsync(frame, "after waiting for the war dialog", wantOn: true, trustVisibleLine: false))
                BeginWarWait("no war dialog");
            return;
        }

        if (_warClock.Now(now) >= _warJoinUntil)
        {
            if (WarStaysInTown)
            {
                Log?.Invoke(LogLevel.Warning, $"No {war.Destination} war dialog came - waiting for the next war in {_warTownHome}.");
                BeginWarWait("no war dialog");
                return;
            }

            Log?.Invoke(LogLevel.Warning, $"No {war.Destination} war dialog came - turning the autohunt back on and waiting for the next war.");
            ResetAutohuntCheck();
            _warJoinGaveUp = true;
            return;
        }

        if (!_warJoinHuntOff)
        {
            if (!await EnsureAutohuntAsync(frame, "before the war", wantOn: false, trustVisibleLine: false))
                return;

            _warJoinHuntOff = true;
            _waitUntil = now + 200;
            return;
        }

        // Moved there without our OK (the user clicked it, or the game's own "Regular War" joined): go on inside.
        if (now >= _warJoinZoneAt)
        {
            _warJoinZoneAt = now + 5000;
            if (ZoneMatches(await ReadMinimapTitleAsync(frame), war.Destination))
            {
                BeginWarEnter($"already in {war.Destination}");
                return;
            }
        }

        var dialog = await _analyzer.FindConfirmDialogAsync(frame, seq.AutoConfirmText, seq.AutoConfirmButton);
        if (dialog != null && WarText.IsWarDialogLine(dialog.Message, war.Destination))
        {
            _overlays.Add(new OverlayItem(dialog.MessageBounds, "war dialog", OverlayColor.Blue));
            _overlays.Add(new OverlayItem(dialog.Button, seq.AutoConfirmButton, OverlayColor.Green));
            if (!WarClick(dialog.Button, $"'{seq.AutoConfirmButton}' on the war dialog ({dialog.How})"))
                return;

            if (!_controls.IsDryRun)
                Log?.Invoke(LogLevel.Success, $"Answered '{dialog.Message}' with {seq.AutoConfirmButton}.");
            WarShot(frame, "war_dialog", (dialog.Button.CenterX, dialog.Button.CenterY));
            BeginWarEnter("clicked OK on the war dialog");
            return;
        }

        _detail = $"{(WarStaysInTown ? "in town" : "autohunt off")} - waiting for the {war.Destination} war dialog ({Clock(_warJoinUntil - _warClock.Now(now))} left)";
        _waitUntil = now + 700;
    }

    /// <summary>
    /// A click of the war steps; a dry run logs where it would click and sends nothing. False when the game is not in
    /// front (<see cref="Click"/> waits; the tick tries again). <paramref name="level"/>: the log line's level (Trace for
    /// the clicks of every fight cycle and buff round).
    /// </summary>
    private bool WarClick(PixelRect target, string what, LogLevel level = LogLevel.Info)
    {
        if (_controls.IsDryRun)
        {
            Log?.Invoke(level, $"Dry run - would click {what} at {target.CenterX},{target.CenterY}.");
            return true;
        }

        if (!Click(target))
            return false;

        Log?.Invoke(level, $"Clicked {what} at {target.CenterX},{target.CenterY}.");
        return true;
    }

    /// <summary>A time left as mm:ss ("04:25", "75:00"), for the status line; never negative.</summary>
    private static string Clock(long ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Clamp(ms, 0, 100L * 86_400_000));
        return $"{(int)t.TotalMinutes:00}:{t.Seconds:00}";
    }

    /// <summary>A time left as hh:mm:ss, the way TRIBE STATUS shows it ("00:04:25").</summary>
    private static string HoursClock(TimeSpan t) => $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
}

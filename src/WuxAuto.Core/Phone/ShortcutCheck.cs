using Emgu.CV;
using WuxAuto.Core;
using WuxAuto.Vision;
using LogLevel = WuxAuto.Core.LogLevel;

namespace WuxAuto.Phone;

/// <summary>What one <see cref="ShortcutCheck.TickAsync"/> decided.</summary>
public enum ShortcutCheckOutcome
{
    /// <summary>Still working: tick again after <see cref="ShortcutCheckStep.WaitMs"/>.</summary>
    Working,

    /// <summary>Every needed shortcut is on the ring (or could not be checked); the ring is as the player had it or locked.</summary>
    Done,

    /// <summary>A needed shortcut is missing and cannot be added: stop and tell the player (<see cref="ShortcutCheckStep.Reason"/>).</summary>
    Stop,
}

/// <param name="WaitMs">Working: how long until the next tick.</param>
/// <param name="Detail">Working: the status line.</param>
/// <param name="Reason">Stop: the message for the player.</param>
public readonly record struct ShortcutCheckStep(ShortcutCheckOutcome Outcome, int WaitMs = 0, string Detail = "", string? Reason = null)
{
    public static ShortcutCheckStep Wait(int ms, string detail) => new(ShortcutCheckOutcome.Working, ms, detail);
}

/// <summary>
/// The phone's shortcut check (spec §6.1, every Start before the zone check): each shortcut the run needs must be on the
/// ring round the joystick. A missing one is added in a free slot: unlock the ring (padlock), tap a (+), pick the entry
/// by the OCR of its name in the picker, then lock the ring again. Never a filled slot: a (+) shows only on an empty
/// slot of an unlocked ring, so the (+) is only looked for once the padlock reads open (on a locked ring the (+)
/// template scored 0.932 on a blank slot, Task 4 probe), and a (+) on a shortcut that is already found is ignored.
/// Missing and no (+) left → <see cref="ShortcutCheckOutcome.Stop"/> with "Add the '&lt;name&gt;' shortcut to the ring".
/// A shortcut without a template yet (no crop) cannot be seen, so it is not checked (Info line) - adding it would put
/// a second copy on the ring on every Start. A dry run only reports what is missing. Loop thread only.
/// Fresh looks only (Xiaomi, 2026-10-30: after a re-login the check took the login and loading screens' "not there"
/// for the town's ring and stopped with every shortcut on it): <see cref="Begin"/> forgets every position, and a
/// shortcut only counts as missing when two searches in a row on frames whose zone title reads did not find it.
/// </summary>
/// <param name="titleReads">Whether the minimap's zone title reads on a frame (the game screen, not a login or loading screen); null = not checked.</param>
public sealed class ShortcutCheck(ScreenAnalyzer analyzer, IGameControls controls, IShortcutSource? shortcuts, Action<LogLevel, string> log,
    Func<Mat, Task<bool>>? titleReads = null)
{
    /// <summary>How many looks wait for the zone title; then the check stops (the game screen was not seen).</summary>
    public const int MaxTitleLooks = 20;

    /// <summary>The two searches that make a shortcut missing are at least this far apart.</summary>
    public const int MinLookGapMs = 1500;

    /// <summary>The stop reason when the zone title never read during the check.</summary>
    public const string NoGameScreenText = "the game screen (zone title) was not seen";

    /// <summary>The wait between two looks (the next frame's search runs in between).</summary>
    public const int LookAgainMs = 500;

    public const string PlusTemplate = "phone_ringplus";
    public const string LockTemplate = "phone_ringlock";
    public const string UnlockTemplate = "phone_ringunlock";

    /// <summary>A (+) on an unlocked ring scores 0.854-1.000 (Task 4 probe).</summary>
    public const double PlusThreshold = 0.85;

    /// <summary>
    /// The padlock templates are the gold lock only (2026-10-30; the see-through slot round it made a padlock on a light
    /// floor score 0.62-0.64). Own state 0.82-1.00 (MuMu ring / town / LOD / buff frames, Xiaomi), the other state's
    /// template 0.72-0.88 on it, at least 0.067 lower.
    /// </summary>
    public const double PadlockThreshold = 0.80;

    /// <summary>How much higher the padlock's own state must score than the other state.</summary>
    public const double PadlockMargin = 0.05;

    /// <summary>The picker panel "MENU" (717-1200 x 10-1065 at 1920x1080, centred) and its close X (1164,38).</summary>
    public static PixelRect PickerArea(int width, int height) => PhoneLayout.ToPixels(717, 10, 483, 1055, PhoneAnchor.Centre, width, height);

    public static (int X, int Y) PickerClose(int width, int height)
    {
        var box = PhoneLayout.ToPixels(1164, 38, 1, 1, PhoneAnchor.Centre, width, height);
        return (box.X, box.Y);
    }

    /// <summary>The picker's labels sit about 50 px (at 1080) under their icons; the icon is what is tapped.</summary>
    public const int LabelBelowIconPx = 50;

    /// <summary>The shortcuts a run needs, with the picker entry that adds each (Task 1 note: the picker's labels).</summary>
    public static IReadOnlyList<(GameAction Action, string Entry)> Required(bool lod)
    {
        var list = new List<(GameAction, string)> { (GameAction.TeleportScroll, "Teleport Scroll") };
        if (lod)
            list.Add((GameAction.Loot, "Pick up"));
        list.Add((GameAction.Buff, "AUTO BUFF"));
        // HUNT ("HUNT (J: sta...)" in the picker) is not needed: LOD has no autohunt, and a full ring must not stop a
        // Start for it. Used where it is found, never added (review of 2026-10-30).
        return list;
    }

    private enum State
    {
        Scan,
        Unlocking,
        Picking,
        Verify,
        Locking,
        Unwind,
    }

    private enum Padlock
    {
        Unknown,
        Locked,
        Unlocked,
    }

    private List<(GameAction Action, string Entry)> _needed = [];
    private State _state;
    private bool _ringOpenedByUs;
    private bool _pickerOpen;
    private bool _relockTried;
    private int _padlockMisses;
    private int _unlockTaps;
    private int _lockTaps;
    private int _pickerReads;
    private int _verifyTicks;
    private (GameAction Action, string Entry) _adding;
    private string? _stopReason;
    private readonly List<string> _added = [];

    /// <summary>Starts a check: the needed shortcuts for this mode, nothing tapped yet.</summary>
    public void Begin(bool lod)
    {
        _needed = [];
        _toAdd = null;
        _firstLook = null;
        _firstLookAt = 0;
        _looks = 0;
        // No position or "not there" from before this Start (a login screen, a loading screen) may decide anything.
        shortcuts?.Invalidate();
        foreach (var need in Required(lod))
        {
            if (ShortcutMap.ActionTemplates.TryGetValue(need.Action, out string? template) && analyzer.Library.Get(template) != null)
                _needed.Add(need);
            else
                log(LogLevel.Info, $"Shortcut check: no picture of the '{need.Entry}' shortcut yet - not checked (add it to the ring yourself if the run needs it).");
        }

        _state = State.Scan;
        _ringOpenedByUs = false;
        _pickerOpen = false;
        _relockTried = false;
        _padlockMisses = 0;
        _unlockTaps = 0;
        _lockTaps = 0;
        _pickerReads = 0;
        _verifyTicks = 0;
        _stopReason = null;
        _added.Clear();
    }

    public async Task<ShortcutCheckStep> TickAsync(Mat frame)
    {
        _frameSize = (frame.Width, frame.Height);
        return _state switch
        {
            State.Scan or State.Unlocking => await ScanAsync(frame),
            State.Picking => await PickAsync(frame),
            State.Verify => Verify(),
            State.Locking => Lock(frame),
            _ => Unwind(frame),
        };
    }

    /// <summary>
    /// What is still to add: the shortcuts missing at the first look (decided once, on the ring as the player left it -
    /// an unlocked ring's icons could look different and must not make a shortcut that is there look missing, which
    /// would add a second copy), less those added since.
    /// </summary>
    private List<(GameAction Action, string Entry)> Missing() =>
        (_toAdd ?? []).Where(n => !_added.Contains(n.Entry)).ToList();

    private List<(GameAction Action, string Entry)>? _toAdd;
    private HashSet<string>? _firstLook;
    private long _firstLookAt;
    private int _looks;

    /// <summary>
    /// Decides once what is missing: every needed shortcut found = nothing; otherwise the ones not found on two looks in
    /// a row, each on a frame whose zone title reads (after <see cref="MaxTitleLooks"/> looks without it, on two looks
    /// anyway). Between two looks the shortcuts not found are searched again. Null = decided; a step = look again.
    /// </summary>
    private async Task<ShortcutCheckStep?> DecideAsync(Mat frame)
    {
        var notFound = _needed.Where(n => !controls.Has(n.Action)).ToList();
        if (notFound.Count == 0)
        {
            _toAdd = [];
            return null;
        }

        long now = Environment.TickCount64;
        if (_firstLook != null && now - _firstLookAt < MinLookGapMs)
            return ShortcutCheckStep.Wait((int)(MinLookGapMs - (now - _firstLookAt)), "looking for the shortcuts again");

        _looks++;
        bool title = titleReads == null || await titleReads(frame);
        if (!title)
        {
            if (_looks < MaxTitleLooks)
            {
                SearchAgain(notFound);
                return ShortcutCheckStep.Wait(LookAgainMs, "waiting for the game screen (zone title) before deciding a shortcut is missing");
            }

            // Never decided "missing" without the game screen: a login or loading screen hides the ring.
            _stopReason = $"Shortcut check: {NoGameScreenText} in {_looks} looks - open the game in town and press Start again.";
            _state = State.Unwind;
            return ShortcutCheckStep.Wait(0, "stopping the shortcut check");
        }

        if (_firstLook == null)
        {
            _firstLook = notFound.Select(n => n.Entry).ToHashSet(StringComparer.Ordinal);
            _firstLookAt = now;
            SearchAgain(notFound);
            return ShortcutCheckStep.Wait(MinLookGapMs, "looking for the shortcuts again");
        }

        // Found on either look = there: a second copy on the ring is worse than a missed check.
        _toAdd = notFound.Where(n => _firstLook.Contains(n.Entry)).ToList();
        if (_toAdd.Count > 0)
            log(LogLevel.Info, $"Shortcut check: {string.Join(", ", _toAdd.Select(n => $"'{n.Entry}'"))} not found on two searches in a row.");

        return null;
    }

    private void SearchAgain(List<(GameAction Action, string Entry)> notFound)
    {
        foreach (var n in notFound)
            shortcuts?.Invalidate(n.Action);
    }

    private async Task<ShortcutCheckStep> ScanAsync(Mat frame)
    {
        if (_toAdd == null && await DecideAsync(frame) is { } look)
            return look;

        var missing = Missing();
        if (missing.Count == 0)
        {
            // Locked again after adding (user: "tap lock -> tap (+) -> pick -> tap lock"), also when the player had left it open.
            if (_ringOpenedByUs || _added.Count > 0)
            {
                _state = State.Locking;
                return Lock(frame);
            }

            log(LogLevel.Info, $"Shortcut check: {string.Join(", ", _needed.Select(n => $"'{n.Entry}'"))} on the ring.");
            return new ShortcutCheckStep(ShortcutCheckOutcome.Done);
        }

        string names = string.Join(", ", missing.Select(m => $"'{m.Entry}'"));
        if (controls.IsDryRun)
        {
            log(LogLevel.Warning, $"Dry run - shortcut(s) {names} not on the ring; would add them in free (+) slots.");
            return new ShortcutCheckStep(ShortcutCheckOutcome.Done);
        }

        var padlock = ReadPadlock(frame, out var lockBox);
        if (padlock == Padlock.Unknown)
        {
            if (++_padlockMisses < 3)
                return ShortcutCheckStep.Wait(500, "looking for the shortcut ring's padlock");
            return BeginStop("the ring's padlock was not found, so no free slot could be checked");
        }

        _padlockMisses = 0;
        if (padlock == Padlock.Locked)
        {
            if (_unlockTaps >= 2)
                return BeginStop("the ring did not unlock");
            if (!controls.Tap(lockBox.CenterX, lockBox.CenterY))
                return ShortcutCheckStep.Wait(300, "padlock tap blocked - waiting for the game to be in front");
            _unlockTaps++;
            _ringOpenedByUs = true;
            _state = State.Unlocking;
            log(LogLevel.Info, $"Shortcut(s) {names} not on the ring - unlocking it (padlock at {lockBox.CenterX},{lockBox.CenterY}).");
            return ShortcutCheckStep.Wait(700, "unlocking the shortcut ring");
        }

        var free = FreeSlots(frame, lockBox);
        if (free.Count == 0)
            return BeginStop("no free (+) slot is left on it");

        var slot = free[0];
        if (!controls.Tap(slot.CenterX, slot.CenterY))
            return ShortcutCheckStep.Wait(300, "(+) tap blocked - waiting for the game to be in front");
        _adding = missing[0];
        _pickerOpen = false; // only once it reads: its close X sits next to the ☰ button
        _pickerReads = 0;
        _state = State.Picking;
        log(LogLevel.Info, $"Adding '{_adding.Entry}' to the ring: tapped the free slot (+) at {slot.CenterX},{slot.CenterY}.");
        return ShortcutCheckStep.Wait(800, $"picking '{_adding.Entry}' for the ring");
    }

    private async Task<ShortcutCheckStep> PickAsync(Mat frame)
    {
        var area = PickerArea(frame.Width, frame.Height).Clamp(frame.Width, frame.Height);
        var words = await analyzer.Ocr.RecognizeAsync(frame, area, 1.0);
        // An entry is only tapped on a read that shows the picker itself: the same words elsewhere are not its entries.
        _pickerOpen = IsPicker(words);
        var label = _pickerOpen ? FindEntry(words, _adding.Entry) : null;
        if (label == null)
        {
            if (++_pickerReads < 3)
                return ShortcutCheckStep.Wait(600, $"reading the shortcut picker for '{_adding.Entry}'");
            return BeginStop(_pickerOpen
                ? $"'{_adding.Entry}' was not found in the shortcut picker"
                : "the shortcut picker did not open");
        }

        var l = label.Value;
        int x = l.CenterX;
        int y = Math.Max(area.Y, l.CenterY - (int)Math.Round(LabelBelowIconPx * frame.Height / 1080.0));
        if (!controls.Tap(x, y))
            return ShortcutCheckStep.Wait(300, "picker tap blocked - waiting for the game to be in front");

        // A tap on an entry fills the slot and closes the picker (Task 1). Look for the new shortcut on the next frames.
        _pickerOpen = false;
        shortcuts?.Invalidate(_adding.Action);
        _verifyTicks = 0;
        _state = State.Verify;
        log(LogLevel.Info, $"Picked '{_adding.Entry}' in the shortcut picker (label at {l.CenterX},{l.CenterY}, tapped {x},{y}).");
        return ShortcutCheckStep.Wait(900, $"checking '{_adding.Entry}' is on the ring");
    }

    private ShortcutCheckStep Verify()
    {
        if (controls.Has(_adding.Action))
        {
            _added.Add(_adding.Entry);
            log(LogLevel.Success, $"Added the '{_adding.Entry}' shortcut to the ring.");
            _state = State.Scan;
            return ShortcutCheckStep.Wait(300, "checking the shortcut ring");
        }

        if (++_verifyTicks < 4)
            return ShortcutCheckStep.Wait(700, $"checking '{_adding.Entry}' is on the ring");
        return BeginStop($"'{_adding.Entry}' was picked but its icon does not show on the ring");
    }

    private ShortcutCheckStep Lock(Mat frame)
    {
        var padlock = ReadPadlock(frame, out var lockBox);
        if (padlock == Padlock.Locked)
        {
            log(LogLevel.Info, _added.Count > 0
                ? $"Shortcut check: added {string.Join(", ", _added.Select(a => $"'{a}'"))}; the ring is locked again."
                : "Shortcut check: the ring is locked again.");
            return new ShortcutCheckStep(ShortcutCheckOutcome.Done);
        }

        if (padlock == Padlock.Unknown || _lockTaps >= 2)
        {
            log(LogLevel.Warning, "Shortcut check: could not lock the ring again - lock it yourself (the padlock bottom right of the ring).");
            return new ShortcutCheckStep(ShortcutCheckOutcome.Done);
        }

        if (!controls.Tap(lockBox.CenterX, lockBox.CenterY))
            return ShortcutCheckStep.Wait(300, "padlock tap blocked - waiting for the game to be in front");
        _lockTaps++;
        return ShortcutCheckStep.Wait(700, "locking the shortcut ring again");
    }

    /// <summary>
    /// Stops the check: on the next ticks the picker is closed and the ring locked again (if this check opened them)
    /// before the Stop goes out.
    /// </summary>
    private ShortcutCheckStep BeginStop(string why)
    {
        var names = Missing().Select(m => $"'{m.Entry}'").ToList();
        string what = names.Count == 1 ? $"the {names[0]} shortcut" : $"the {string.Join(", ", names)} shortcuts";
        _stopReason = $"Add {what} to the ring - {why}. The pilot needs {(names.Count == 1 ? "it" : "them")} and never replaces a filled slot.";
        _state = State.Unwind;
        return ShortcutCheckStep.Wait(0, "stopping the shortcut check");
    }

    private ShortcutCheckStep Unwind(Mat frame)
    {
        var (w, h) = _frameSize;
        if (_pickerOpen && w > 0 && h > 0)
        {
            var (x, y) = PickerClose(w, h);
            if (!controls.Tap(x, y))
                return ShortcutCheckStep.Wait(300, "picker close blocked - waiting for the game to be in front");
            _pickerOpen = false;
            return ShortcutCheckStep.Wait(700, "closing the shortcut picker");
        }

        // Only a padlock that reads open is tapped: "the ring did not unlock" must not unlock it now.
        if (_ringOpenedByUs && !_relockTried)
        {
            if (ReadPadlock(frame, out var box) == Padlock.Unlocked)
            {
                if (!controls.Tap(box.CenterX, box.CenterY))
                    return ShortcutCheckStep.Wait(300, "padlock tap blocked - waiting for the game to be in front");
                _relockTried = true;
                return ShortcutCheckStep.Wait(700, "locking the shortcut ring again");
            }

            _relockTried = true;
        }

        return new ShortcutCheckStep(ShortcutCheckOutcome.Stop, Reason: _stopReason);
    }

    private (int W, int H) _frameSize;

    /// <summary>
    /// The padlock's state: whichever padlock template scores higher, at least <see cref="PadlockThreshold"/> and
    /// <see cref="PadlockMargin"/> above the other. Both states passing in two different places = two padlocks that
    /// disagree (the Xiaomi's ring, 2026-10-30: one reads open top left, one locked bottom right, and no (+) shows) =
    /// unknown: nothing is tapped. Full scale search each time: no scale learned from a miss.
    /// </summary>
    private Padlock ReadPadlock(Mat frame, out PixelRect box)
    {
        var zone = ShortcutMap.RingZone(frame.Width, frame.Height).Clamp(frame.Width, frame.Height);
        var (locked, _) = analyzer.FindIconAnyScale(frame, LockTemplate, zone, 0.50);
        var (open, _) = analyzer.FindIconAnyScale(frame, UnlockTemplate, zone, 0.50);
        double lockScore = locked?.Score ?? 0, openScore = open?.Score ?? 0;
        box = default;
        if (lockScore >= PadlockThreshold && openScore >= PadlockThreshold && !SameSpot(locked!.Bounds, open!.Bounds))
            return Padlock.Unknown;

        if (lockScore >= PadlockThreshold && lockScore > openScore + PadlockMargin)
        {
            box = locked!.Bounds;
            return Padlock.Locked;
        }

        if (openScore >= PadlockThreshold && openScore > lockScore + PadlockMargin)
        {
            box = open!.Bounds;
            return Padlock.Unlocked;
        }

        return Padlock.Unknown;

        static bool SameSpot(PixelRect a, PixelRect b) =>
            Math.Abs(a.CenterX - b.CenterX) <= Math.Max(a.Width, b.Width) && Math.Abs(a.CenterY - b.CenterY) <= Math.Max(a.Height, b.Height);
    }

    /// <summary>
    /// The (+) slots of the unlocked ring, top first, without a hit on the padlock or on a shortcut already found there
    /// (never a filled slot).
    /// </summary>
    private List<PixelRect> FreeSlots(Mat frame, PixelRect padlock)
    {
        var zone = ShortcutMap.RingZone(frame.Width, frame.Height).Clamp(frame.Width, frame.Height);
        var filled = new List<PixelRect> { padlock };
        if (shortcuts != null)
        {
            foreach (var action in ShortcutMap.ActionTemplates.Keys)
            {
                if (shortcuts.Find(action) is { } box)
                    filled.Add(box);
            }

            if (shortcuts.Joystick is { } stick)
                filled.Add(new PixelRect(stick.Cx - stick.Radius, stick.Cy - stick.Radius, stick.Radius * 2, stick.Radius * 2));
        }

        return analyzer.FindIcons(frame, PlusTemplate, zone, PlusThreshold, maxResults: 14)
            .Select(m => m.Bounds)
            .Where(b => !filled.Any(f => Overlaps(f, b)))
            .OrderBy(b => b.Y)
            .ThenBy(b => b.X)
            .ToList();

        static bool Overlaps(PixelRect a, PixelRect b) =>
            Math.Abs(a.CenterX - b.CenterX) < (a.Width + b.Width) / 2 && Math.Abs(a.CenterY - b.CenterY) < (a.Height + b.Height) / 2;
    }

    /// <summary>The picker is up: its title "MENU" or its first entry "CLEAR" reads (the ☰ panel, also "MENU", is right of the picker area).</summary>
    private static bool IsPicker(IReadOnlyList<OcrWord> words) =>
        words.Any(w => TextMatcher.Normalize(w.LineText) == "menu") || FindEntry(words, "CLEAR") != null;

    /// <summary>
    /// The picker label for <paramref name="entry"/>: a run of words on one OCR line whose letters start with the entry's
    /// (one letter off allowed from 6 letters on). Words, not whole lines: the five labels of a picker row can come back
    /// as one line. Null = not read.
    /// </summary>
    public static PixelRect? FindEntry(IReadOnlyList<OcrWord> words, string entry)
    {
        string want = TextMatcher.Normalize(entry);
        if (want.Length == 0)
            return null;

        foreach (var line in words.GroupBy(w => w.LineBounds))
        {
            var row = line.OrderBy(w => w.Bounds.X).ToList();
            for (int i = 0; i < row.Count; i++)
            {
                string text = string.Empty;
                for (int j = i; j < row.Count; j++)
                {
                    text += TextMatcher.Normalize(row[j].Text);
                    if (text.Length < want.Length)
                        continue;

                    string head = text[..want.Length];
                    bool match = head == want || (want.Length >= 6 && TextMatcher.Levenshtein(head, want) <= 1);
                    // "HUNT" is not the "Auto-Hunt (J)" entry, also when OCR splits it into "Auto-" "Hunt".
                    if (match && i > 0 && TextMatcher.Normalize(row[i - 1].Text).EndsWith("auto", StringComparison.Ordinal))
                        match = false;
                    if (match)
                    {
                        var first = row[i].Bounds;
                        var last = row[j].Bounds;
                        return PixelRect.FromBounds(first.X, Math.Min(first.Y, last.Y), last.Right, Math.Max(first.Bottom, last.Bottom));
                    }

                    break;
                }
            }
        }

        return null;
    }
}

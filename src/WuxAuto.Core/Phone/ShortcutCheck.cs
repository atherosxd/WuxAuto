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
/// </summary>
public sealed class ShortcutCheck(ScreenAnalyzer analyzer, IGameControls controls, IShortcutSource? shortcuts, Action<LogLevel, string> log)
{
    public const string PlusTemplate = "phone_ringplus";
    public const string LockTemplate = "phone_ringlock";
    public const string UnlockTemplate = "phone_ringunlock";

    /// <summary>A (+) on an unlocked ring scores 0.854-1.000 (Task 4 probe).</summary>
    public const double PlusThreshold = 0.85;

    /// <summary>The padlock's own state scores 1.00, the other state's template 0.84 on it (Task 4 probe).</summary>
    public const double PadlockThreshold = 0.90;

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
        // "HUNT (J: sta...)" in the picker; "Auto-Hunt (J)" is another entry (it opens a window).
        list.Add((GameAction.ToggleAutohunt, "HUNT"));
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
            State.Scan or State.Unlocking => Scan(frame),
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
    private List<(GameAction Action, string Entry)> Missing()
    {
        _toAdd ??= _needed.Where(n => !controls.Has(n.Action)).ToList();
        return _toAdd.Where(n => !_added.Contains(n.Entry)).ToList();
    }

    private List<(GameAction Action, string Entry)>? _toAdd;

    private ShortcutCheckStep Scan(Mat frame)
    {
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
            return BeginStop(missing[0].Entry, "the ring's padlock was not found, so no free slot could be checked");
        }

        _padlockMisses = 0;
        if (padlock == Padlock.Locked)
        {
            if (_unlockTaps >= 2)
                return BeginStop(missing[0].Entry, "the ring did not unlock");
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
            return BeginStop(missing[0].Entry, "no free (+) slot is left on it");

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
        if (IsPicker(words))
            _pickerOpen = true;
        var label = FindEntry(words, _adding.Entry);
        if (label == null)
        {
            if (++_pickerReads < 3)
                return ShortcutCheckStep.Wait(600, $"reading the shortcut picker for '{_adding.Entry}'");
            return BeginStop(_adding.Entry, $"'{_adding.Entry}' was not found in the shortcut picker");
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
        return BeginStop(_adding.Entry, $"'{_adding.Entry}' was picked but its icon does not show on the ring");
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
    private ShortcutCheckStep BeginStop(string entry, string why)
    {
        _stopReason = $"Add the '{entry}' shortcut to the ring - {why}. The pilot needs it and never replaces a filled slot.";
        _state = State.Unwind;
        return ShortcutCheckStep.Wait(0, "stopping the shortcut check");
    }

    private ShortcutCheckStep Unwind(Mat frame)
    {
        var (w, h) = _frameSize;
        if (_pickerOpen && w > 0 && h > 0)
        {
            _pickerOpen = false;
            var (x, y) = PickerClose(w, h);
            controls.Tap(x, y);
            return ShortcutCheckStep.Wait(700, "closing the shortcut picker");
        }

        // Only a padlock that reads open is tapped: "the ring did not unlock" must not unlock it now.
        if (_ringOpenedByUs && !_relockTried)
        {
            _relockTried = true;
            if (ReadPadlock(frame, out var box) == Padlock.Unlocked)
            {
                controls.Tap(box.CenterX, box.CenterY);
                return ShortcutCheckStep.Wait(700, "locking the shortcut ring again");
            }
        }

        return new ShortcutCheckStep(ShortcutCheckOutcome.Stop, Reason: _stopReason);
    }

    private (int W, int H) _frameSize;

    /// <summary>
    /// The padlock's state: whichever padlock template scores higher, at least <see cref="PadlockThreshold"/> and clearly
    /// above the other (1.00 vs 0.84 on the probe frames). Full scale search each time: no scale learned from a miss.
    /// </summary>
    private Padlock ReadPadlock(Mat frame, out PixelRect box)
    {
        var zone = ShortcutMap.RingZone(frame.Width, frame.Height).Clamp(frame.Width, frame.Height);
        var (locked, _) = analyzer.FindIconAnyScale(frame, LockTemplate, zone, 0.50);
        var (open, _) = analyzer.FindIconAnyScale(frame, UnlockTemplate, zone, 0.50);
        double lockScore = locked?.Score ?? 0, openScore = open?.Score ?? 0;
        box = default;
        if (lockScore >= PadlockThreshold && lockScore > openScore + 0.03)
        {
            box = locked!.Bounds;
            return Padlock.Locked;
        }

        if (openScore >= PadlockThreshold && openScore > lockScore + 0.03)
        {
            box = open!.Bounds;
            return Padlock.Unlocked;
        }

        return Padlock.Unknown;
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

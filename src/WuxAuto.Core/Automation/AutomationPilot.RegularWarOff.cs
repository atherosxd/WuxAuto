using Emgu.CV;
using WuxAuto.Core;
using WuxAuto.Vision;
using LogLevel = WuxAuto.Core.LogLevel;

namespace WuxAuto.Automation;

/// <summary>
/// Auto Laby, in town before the first teleport of a Start (user, 2026-10-29: "check if the regular war is turned on
/// in the autohunt, disable it if enabled, as it may get teleported to the war when the autohunt is on while attacking
/// the boss"). The autohunt settings key (S, at most 2 presses) opens the window; the AUTO BUFF dialog is closed if it covers the list;
/// a ticked "Regular War" box (found and read the way the keys-out <see cref="TickRegularWarAsync"/> does) is clicked
/// until it reads empty - at most <see cref="RegularWarOffClicks"/> clicks, an even number, so a box that only
/// <i>reads</i> ticked ends as it was; then S closes the window. Nothing readable -> a warning, a snapshot and the run
/// carries on to the teleport. The Regular War row has not been seen live yet.
/// </summary>
public sealed partial class AutomationPilot
{
    private const int RegularWarOffClicks = 2;
    private const int RegularWarOffOpenPresses = 2;

    private bool _regularWarOffChecked;    // once per Start
    private bool _regularWarOffDue;        // this town visit
    private long _rwoStartedAt;
    private int _rwoOpenPresses;
    private long _rwoKeyAt;
    private int _rwoBoxClicks;
    private int _rwoBuffCloses;
    private bool _rwoSeenOpen;             // the settings window (label or AUTO BUFF title) has been read this visit
    private long? _rwoLostSince;
    private bool _rwoClosing;
    private int _rwoClosePresses;
    private string _rwoResult = string.Empty;
    private LogLevel _rwoLevel;

    /// <summary>Phase entry: the counters of one visit.</summary>
    private void BeginRegularWarOff()
    {
        _regularWarOffDue = false;
        _regularWarOffChecked = true;
        _rwoStartedAt = Environment.TickCount64;
        _rwoOpenPresses = 0;
        _rwoKeyAt = 0;
        _rwoBoxClicks = 0;
        _rwoBuffCloses = 0;
        _rwoSeenOpen = false;
        _rwoLostSince = null;
        _rwoClosing = false;
        _rwoClosePresses = 0;
        _rwoResult = string.Empty;
    }

    private async Task TickRegularWarOffAsync(Mat frame)
    {
        long now = Environment.TickCount64;
        string key = _settings.Sequence.AutohuntSettingsKey;

        if (_controls.IsDryRun || !_controls.Has(GameAction.OpenAutohuntSettings))
        {
            Log?.Invoke(_controls.IsDryRun ? LogLevel.Info : LogLevel.Warning, _controls.IsDryRun
                ? $"Dry run - not opening the autohunt settings ('{key}') to check Regular War."
                : "No autohunt settings key is set - cannot check Regular War before the Labyrinth.");
            Advance(AfterTownChecks(), 120);
            return;
        }

        if (_rwoClosing)
        {
            await TickRegularWarOffCloseAsync(frame, now, key);
            return;
        }

        var (lines, words) = await ReadWholeScreenAsync(frame);
        var regular = lines.FirstOrDefault(l => WarText.IsRegularWarLine(l.Text));
        if (regular.Text != null)
        {
            _rwoSeenOpen = true;
            _rwoLostSince = null;
            // Measured from the word "Regular", as TickRegularWarAsync does.
            var word = words.FirstOrDefault(w => w.LineBounds == regular.Bounds &&
                                                 TextMatcher.Normalize(w.Text).StartsWith("regul", StringComparison.Ordinal));
            var label = word == null ? regular.Bounds
                : new PixelRect(word.Bounds.X, regular.Bounds.Y, regular.Bounds.Right - word.Bounds.X, regular.Bounds.Height);
            var box = _analyzer.FindCheckboxLeftOf(frame, label);
            bool ticked = _analyzer.IsCheckboxTicked(frame, box);
            _overlays.Add(new OverlayItem(label, "Regular War", OverlayColor.Blue));
            _overlays.Add(new OverlayItem(box, ticked ? "ticked" : "empty", ticked ? OverlayColor.Yellow : OverlayColor.Green));

            if (!ticked)
            {
                BeginRegularWarOffClose(now, LogLevel.Success, _rwoBoxClicks == 0
                    ? "Regular War is already off in the autohunt settings"
                    : $"Regular War unticked in the autohunt settings ({_rwoBoxClicks} click(s))");
                return;
            }

            if (_rwoBoxClicks >= RegularWarOffClicks)
            {
                DebugArtifacts.SaveFrame(frame, _overlays, "regularwar_on");
                BeginRegularWarOffClose(now, LogLevel.Warning,
                    $"The Regular War box still reads ticked after {_rwoBoxClicks} clicks - the autohunt may join a war during the Labyrinth");
                return;
            }

            if (!Click(box))
                return;

            _rwoBoxClicks++;
            Log?.Invoke(LogLevel.Info, $"Regular War is ticked - clicked its box at {box.CenterX},{box.CenterY} ({_rwoBoxClicks}/{RegularWarOffClicks}).");
            _waitUntil = now + 900;
            return;
        }

        // The AUTO BUFF dialog covers the list on lower resolutions: its X (a blob right of the title, never a guess).
        var buff = lines.FirstOrDefault(l => WarText.IsAutoBuffTitle(l.Text));
        if (buff.Text != null)
        {
            _rwoSeenOpen = true;
            _rwoLostSince = null;
            if (_rwoBuffCloses < 3 && _analyzer.FindCloseBlobRightOf(frame, buff.Bounds) is { } x)
            {
                if (!Click(x))
                    return;

                _rwoBuffCloses++;
                _overlays.Add(new OverlayItem(x, "close", OverlayColor.Green));
                Log?.Invoke(LogLevel.Info, $"Closed the AUTO BUFF dialog over the autohunt settings (X at {x.CenterX},{x.CenterY}).");
                _waitUntil = now + 900;
                return;
            }

            DebugArtifacts.SaveFrame(frame, _overlays, "regularwar_unread");
            BeginRegularWarOffClose(now, LogLevel.Warning, _rwoBuffCloses < 3
                ? "The AUTO BUFF dialog covers the autohunt settings and shows no close button - Regular War not checked"
                : "The AUTO BUFF dialog stayed up after 3 clicks on its X - Regular War not checked");
            return;
        }

        // Seen open, now nothing reads (one OCR miss after a click, or the window shut by itself): never press the
        // toggle blind - read again for up to 5 s, then carry on without touching it.
        if (_rwoSeenOpen)
        {
            _rwoLostSince ??= now;
            if (now - _rwoLostSince.Value < 5000)
            {
                _detail = "the autohunt settings went out of view - reading again";
                _waitUntil = now + 400;
                return;
            }

            Log?.Invoke(LogLevel.Warning,
                $"The autohunt settings went out of view mid-check ({_rwoBoxClicks} click(s) on Regular War) - not pressing '{key}' blind; carrying on.");
            DebugArtifacts.SaveFrame(frame, _overlays, "regularwar_unread");
            Advance(AfterTownChecks(), 120);
            return;
        }

        // Not open (yet): S, at most RegularWarOffOpenPresses presses 2.5 s apart. The key is a toggle and an even
        // count leaves a window that opened but never read (the Regular War row is not seen live yet) shut again.
        if (now - _rwoKeyAt < 2500)
        {
            _detail = $"waiting for the autohunt settings ('{key}' x{_rwoOpenPresses})";
            _waitUntil = now + 400;
            return;
        }

        if (_rwoOpenPresses >= RegularWarOffOpenPresses || now - _rwoStartedAt > 30000)
        {
            Log?.Invoke(LogLevel.Warning,
                $"The autohunt settings could not be read after '{key}' x{_rwoOpenPresses} - Regular War not checked; carrying on.");
            DebugArtifacts.SaveFrame(frame, _overlays, "regularwar_unread");
            Advance(AfterTownChecks(), 120);
            return;
        }

        if (!_controls.Press(GameAction.OpenAutohuntSettings))
        {
            _detail = "autohunt settings key blocked - waiting for the game to be in front";
            _waitUntil = now + 300;
            return;
        }

        _rwoOpenPresses++;
        _rwoKeyAt = now;
        Log?.Invoke(LogLevel.Info, $"Pressed '{key}' for the autohunt settings - is Regular War on? ({_rwoOpenPresses}/{RegularWarOffOpenPresses})");
        _waitUntil = now + 1200;
    }

    private void BeginRegularWarOffClose(long now, LogLevel level, string result)
    {
        _rwoClosing = true;
        _rwoClosePresses = 0;
        _rwoLevel = level;
        _rwoResult = result;
        _waitUntil = now + 300;
    }

    /// <summary>
    /// The window was seen open, so the first press goes out without a read; after it a fresh frame 1.2 s later decides:
    /// the label still there -> one more press (at most 2), then a warning. The result is logged once the window is shut.
    /// </summary>
    private async Task TickRegularWarOffCloseAsync(Mat frame, long now, string key)
    {
        if (_rwoClosePresses > 0)
        {
            if (now - _rwoKeyAt < 1200)
            {
                _waitUntil = _rwoKeyAt + 1200;
                return;
            }

            var (lines, _) = await ReadWholeScreenAsync(frame);
            bool open = lines.Any(l => WarText.IsRegularWarLine(l.Text));
            if (!open || _rwoClosePresses >= 2)
            {
                Log?.Invoke(_rwoLevel, $"{_rwoResult}.");
                if (open)
                {
                    Log?.Invoke(LogLevel.Warning, $"The autohunt settings are still open after '{key}' x2 - leaving them.");
                    DebugArtifacts.SaveFrame(frame, _overlays, "regularwar_open");
                }

                Advance(AfterTownChecks(), 120);
                return;
            }
        }

        if (!_controls.Press(GameAction.OpenAutohuntSettings))
        {
            _detail = "autohunt settings key blocked - waiting for the game to be in front";
            _waitUntil = now + 300;
            return;
        }

        _rwoClosePresses++;
        _rwoKeyAt = now;
        _detail = $"closing the autohunt settings ('{key}' {_rwoClosePresses}/2)";
        _waitUntil = now + 1200;
    }

    /// <summary>The whole client at OCR x2 (x1 when that reads almost nothing), as words and lines.</summary>
    private async Task<(IReadOnlyList<(string Text, PixelRect Bounds)> Lines, IReadOnlyList<OcrWord> Words)> ReadWholeScreenAsync(Mat frame)
    {
        var full = new PixelRect(0, 0, frame.Width, frame.Height);
        var words = await _analyzer.Ocr.RecognizeAsync(frame, full, 2.0);
        if (words.Count < 3)
            words = await _analyzer.Ocr.RecognizeAsync(frame, full, 1.0);
        return (ScreenAnalyzer.ToLines(words), words);
    }
}

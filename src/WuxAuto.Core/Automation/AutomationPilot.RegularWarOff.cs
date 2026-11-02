using Emgu.CV;
using WuxAuto.Core;
using WuxAuto.Vision;
using LogLevel = WuxAuto.Core.LogLevel;

namespace WuxAuto.Automation;

/// <summary>
/// In town, once per Start, before the autohunt check, every mode (user, 2026-10-29: "check if the regular war is turned
/// on in the autohunt, disable it if enabled, as it may get teleported to the war when the autohunt is on while attacking
/// the boss"; 2026-11-02: "so before trying to turn off auto hunt check if regular war and smart farm is off first" - with
/// Smart Farm on, 'J' in town starts a journey back to a hunting ground). The autohunt settings key (S, at most 2 presses)
/// opens the window; the AUTO BUFF dialog is closed if it covers the list; a ticked "Regular War" or "Smart Farm" box
/// (found and read the way the keys-out <see cref="TickRegularWarAsync"/> does) is clicked until it reads empty - after a
/// click the box gets <see cref="HuntOptionSettleMs"/> to show the change before another click (live 2026-11-01 16:51: two
/// clicks 0.9 s apart and Regular War still read ticked), at most <see cref="RegularWarOffClicks"/> clicks, an even number,
/// so a box that only <i>reads</i> ticked ends as it was; then S closes the window. Nothing readable -> a warning, a
/// snapshot and the run carries on.
/// </summary>
public sealed partial class AutomationPilot
{
    private const int RegularWarOffClicks = 2;
    private const int RegularWarOffOpenPresses = 2;
    private const int HuntOptionSettleMs = 2500;   // a click on a box gets this long to show before the next one
    private const int HuntOptionMissReads = 3;     // reads of the open window without a row's label -> that row not read

    /// <summary>The boxes to untick, top to bottom, with the first word of each label (where the box is measured from).</summary>
    private static readonly (string Name, Func<string, bool> IsLine, string WordStart)[] HuntOptionRows =
    [
        ("Regular War", WarText.IsRegularWarLine, "regul"),
        ("Smart Farm", WarText.IsSmartFarmLine, "smart"),
    ];

    private bool _regularWarOffChecked;    // once per Start
    private bool _regularWarOffDue;        // this town visit
    private long _rwoStartedAt;
    private int _rwoOpenPresses;
    private long _rwoKeyAt;
    private readonly int[] _rwoClicks = new int[HuntOptionRows.Length];
    private readonly long[] _rwoClickAt = new long[HuntOptionRows.Length];
    private readonly bool[] _rwoDone = new bool[HuntOptionRows.Length];
    private readonly int[] _rwoMisses = new int[HuntOptionRows.Length];
    private int _rwoBuffCloses;
    private bool _rwoSeenOpen;             // the settings window (a label or the AUTO BUFF title) has been read this visit
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
        Array.Clear(_rwoClicks);
        Array.Clear(_rwoClickAt);
        Array.Clear(_rwoDone);
        Array.Clear(_rwoMisses);
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
                ? $"Dry run - not opening the autohunt settings ('{key}') to check Regular War and Smart Farm."
                : "No autohunt settings key is set - cannot check Regular War and Smart Farm.");
            Advance(AfterHuntOptions(), 120);
            return;
        }

        if (_rwoClosing)
        {
            await TickRegularWarOffCloseAsync(frame, now, key);
            return;
        }

        var (lines, words) = await ReadWholeScreenAsync(frame);
        if (HuntOptionRows.Any(r => lines.Any(l => r.IsLine(l.Text))))
        {
            _rwoSeenOpen = true;
            _rwoLostSince = null;
            for (int i = 0; i < HuntOptionRows.Length; i++)
            {
                if (_rwoDone[i])
                    continue;

                var (name, isLine, wordStart) = HuntOptionRows[i];
                var row = lines.FirstOrDefault(l => isLine(l.Text));
                if (row.Text == null)
                {
                    // Not read while another row is: a few more reads, then carry on without it.
                    if (++_rwoMisses[i] >= HuntOptionMissReads)
                    {
                        _rwoDone[i] = true;
                        Log?.Invoke(LogLevel.Warning, $"The '{name}' box was not read in the autohunt settings - not checked.");
                        DebugArtifacts.SaveFrame(frame, _overlays, "regularwar_unread");
                    }

                    continue;
                }

                // Measured from the label's first word, as TickRegularWarAsync does.
                var word = words.FirstOrDefault(w => w.LineBounds == row.Bounds &&
                                                     TextMatcher.Normalize(w.Text).StartsWith(wordStart, StringComparison.Ordinal));
                var label = word == null ? row.Bounds
                    : new PixelRect(word.Bounds.X, row.Bounds.Y, row.Bounds.Right - word.Bounds.X, row.Bounds.Height);
                var box = _analyzer.FindCheckboxLeftOf(frame, label);
                bool ticked = _analyzer.IsCheckboxTicked(frame, box);
                _overlays.Add(new OverlayItem(label, name, OverlayColor.Blue));
                _overlays.Add(new OverlayItem(box, ticked ? "ticked" : "empty", ticked ? OverlayColor.Yellow : OverlayColor.Green));

                if (!ticked)
                {
                    _rwoDone[i] = true;
                    Log?.Invoke(LogLevel.Success, _rwoClicks[i] == 0
                        ? $"{name} is already off in the autohunt settings."
                        : $"{name} unticked in the autohunt settings ({_rwoClicks[i]} click(s)).");
                    continue;
                }

                // Clicked and still ticked: give the click time to show before another one.
                if (_rwoClicks[i] > 0 && now - _rwoClickAt[i] < HuntOptionSettleMs)
                {
                    _detail = $"waiting for the {name} box to change";
                    _waitUntil = now + 300;
                    return;
                }

                if (_rwoClicks[i] >= RegularWarOffClicks)
                {
                    _rwoDone[i] = true;
                    Log?.Invoke(LogLevel.Warning,
                        $"The {name} box still reads ticked after {_rwoClicks[i]} clicks - the autohunt may {(i == 0 ? "join a war" : "start a Smart Farm journey")}.");
                    DebugArtifacts.SaveFrame(frame, _overlays, "regularwar_on");
                    continue;
                }

                if (!Click(box))
                    return;

                _rwoClicks[i]++;
                _rwoClickAt[i] = now;
                Log?.Invoke(LogLevel.Info, $"{name} is ticked - clicked its box at {box.CenterX},{box.CenterY} ({_rwoClicks[i]}/{RegularWarOffClicks}).");
                _waitUntil = now + 500;
                return;
            }

            if (_rwoDone.All(d => d))
            {
                BeginRegularWarOffClose(now, LogLevel.Info, string.Empty);
                return;
            }

            _waitUntil = now + 400;
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
                ? "The AUTO BUFF dialog covers the autohunt settings and shows no close button - Regular War and Smart Farm not checked"
                : "The AUTO BUFF dialog stayed up after 3 clicks on its X - Regular War and Smart Farm not checked");
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
                $"The autohunt settings went out of view mid-check ({_rwoClicks.Sum()} box click(s)) - not pressing '{key}' blind; carrying on.");
            DebugArtifacts.SaveFrame(frame, _overlays, "regularwar_unread");
            Advance(AfterHuntOptions(), 120);
            return;
        }

        // Not open (yet): S, at most RegularWarOffOpenPresses presses 2.5 s apart. The key is a toggle and an even
        // count leaves a window that opened but never read shut again.
        if (now - _rwoKeyAt < 2500)
        {
            _detail = $"waiting for the autohunt settings ('{key}' x{_rwoOpenPresses})";
            _waitUntil = now + 400;
            return;
        }

        if (_rwoOpenPresses >= RegularWarOffOpenPresses || now - _rwoStartedAt > 30000)
        {
            Log?.Invoke(LogLevel.Warning,
                $"The autohunt settings could not be read after '{key}' x{_rwoOpenPresses} - Regular War and Smart Farm not checked; carrying on.");
            DebugArtifacts.SaveFrame(frame, _overlays, "regularwar_unread");
            Advance(AfterHuntOptions(), 120);
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
        Log?.Invoke(LogLevel.Info,
            $"Pressed '{key}' for the autohunt settings - are Regular War and Smart Farm on? ({_rwoOpenPresses}/{RegularWarOffOpenPresses})");
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
    /// a label still there -> one more press (at most 2), then a warning. A result is logged once the window is shut.
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
            bool open = HuntOptionRows.Any(r => lines.Any(l => r.IsLine(l.Text)));
            if (!open || _rwoClosePresses >= 2)
            {
                if (_rwoResult.Length > 0)
                    Log?.Invoke(_rwoLevel, $"{_rwoResult}.");
                if (open)
                {
                    Log?.Invoke(LogLevel.Warning, $"The autohunt settings are still open after '{key}' x2 - leaving them.");
                    DebugArtifacts.SaveFrame(frame, _overlays, "regularwar_open");
                }

                Advance(AfterHuntOptions(), 120);
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

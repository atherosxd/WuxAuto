using OpenCvSharp;
using WuxAuto.Core;
using WuxAuto.Vision;
using LogLevel = WuxAuto.Core.LogLevel;

namespace WuxAuto.Automation;

/// <summary>
/// Both kinds of keys used up: the game's own auto war (user, 2026-10-22). Players who use the
/// pilot get "Regular War" switched off in the autohunt settings, so the character stayed in town
/// even in a war. Fix: open the autohunt settings with S, close the AUTO BUFF dialog if it covers
/// the list (lower resolutions; its X sits right of its title), tick the "Regular War" box left of
/// its label, then J (autohunt on) and stop.
/// </summary>
public sealed partial class AutomationPilot
{
    private long _warStartedAt;
    private int _warSPresses;
    private long _lastWarSAt;
    private int _warBoxClicks;
    private int _warBuffCloses;

    private void BeginAutoWar(string why)
    {
        Log?.Invoke(LogLevel.Warning, $"{why} - turning the game's auto war on (Regular War + '{_settings.Dungeon.AutohuntKey}').");
        _warStartedAt = Environment.TickCount64;
        _warSPresses = 0;
        _lastWarSAt = 0;
        _warBoxClicks = 0;
        _warBuffCloses = 0;
        Advance(PilotPhase.AutoWar, 800);
    }

    private async Task TickAutoWarAsync(Mat frame)
    {
        long now = Environment.TickCount64;
        string settingsKey = _settings.Sequence.AutohuntSettingsKey;

        if (now - _warStartedAt > 60000)
        {
            FinishAutoWar(frame, ticked: false, "the Regular War box was not found in 60s");
            return;
        }

        var full = new PixelRect(0, 0, frame.Width, frame.Height);
        var words = await _analyzer.Ocr.RecognizeAsync(frame, full, 2.0);
        if (words.Count < 3)
            words = await _analyzer.Ocr.RecognizeAsync(frame, full, 1.0);
        var lines = words
            .GroupBy(w => w.LineBounds)
            .Select(g => (Bounds: g.Key, Text: TextMatcher.Normalize(g.First().LineText)))
            .ToList();

        var regular = lines.FirstOrDefault(l => l.Text.Length <= 14 &&
            (l.Text.StartsWith("regular", StringComparison.Ordinal) || TextMatcher.Levenshtein(l.Text, "regularwar") <= 2));
        if (regular.Text != null)
        {
            // Measure from the word "Regular", not the line: OCR may read the box frame as a letter
            // at the start of the line, and the line would then begin at the box itself.
            var word = words.FirstOrDefault(w => w.LineBounds == regular.Bounds &&
                                                 TextMatcher.Normalize(w.Text).StartsWith("regul", StringComparison.Ordinal));
            var label = word == null ? regular.Bounds
                : new PixelRect(word.Bounds.X, regular.Bounds.Y, regular.Bounds.Right - word.Bounds.X, regular.Bounds.Height);
            var box = _analyzer.FindCheckboxLeftOf(frame, label);
            bool ticked = _analyzer.IsCheckboxTicked(frame, box);
            _overlays.Add(new OverlayItem(label, "Regular War", OverlayColor.Blue));
            _overlays.Add(new OverlayItem(box, ticked ? "ticked" : "empty", ticked ? OverlayColor.Green : OverlayColor.Yellow));

            if (ticked)
            {
                FinishAutoWar(frame, ticked: true, _warBoxClicks == 0 ? "Regular War was already ticked" : "Regular War ticked");
                return;
            }

            if (_warBoxClicks >= 3)
            {
                FinishAutoWar(frame, ticked: false, "the Regular War box did not tick after 3 clicks");
                return;
            }

            if (!Click(box))
                return;

            _warBoxClicks++;
            Log?.Invoke(LogLevel.Info, $"Clicked the Regular War box at {box.CenterX},{box.CenterY} ({_warBoxClicks}/3).");
            _waitUntil = now + 900;
            return;
        }

        // The AUTO BUFF dialog covers the list (user): close it with the X right of its title. Only
        // the title itself counts - the item list has an "[Auto buff scroll]" line.
        var buff = lines.FirstOrDefault(l => l.Text == "autobuff" || TextMatcher.Levenshtein(l.Text, "autobuff") <= 1);
        if (buff.Text != null && _warBuffCloses < 3)
        {
            var x = _analyzer.FindCloseButtonRightOf(frame, buff.Bounds);
            if (!Click(x))
                return;

            _warBuffCloses++;
            Log?.Invoke(LogLevel.Info, $"Closed the AUTO BUFF dialog (X at {x.CenterX},{x.CenterY}).");
            _overlays.Add(new OverlayItem(x, "close", OverlayColor.Green));
            _waitUntil = now + 900;
            return;
        }

        // The autohunt settings are not open (S toggles them): press S, at most 3 times.
        if (now - _lastWarSAt > 2500)
        {
            if (_warSPresses >= 3 || string.IsNullOrWhiteSpace(settingsKey))
            {
                FinishAutoWar(frame, ticked: false, "the autohunt settings did not show a Regular War box");
                return;
            }

            if (!_input.KeyTap(settingsKey))
            {
                _detail = "key blocked - waiting for the game to be in front";
                _waitUntil = now + 300;
                return;
            }

            _warSPresses++;
            _lastWarSAt = now;
            Log?.Invoke(LogLevel.Info, $"Pressed '{settingsKey}' for the autohunt settings ({_warSPresses}/3).");
            _waitUntil = now + 1200;
            return;
        }

        _detail = "looking for the Regular War box";
        _waitUntil = now + 500;
    }

    private void FinishAutoWar(Mat frame, bool ticked, string what)
    {
        string key = _settings.Dungeon.AutohuntKey;
        if (!string.IsNullOrWhiteSpace(key) && !_input.KeyTap(key))
        {
            _detail = "key blocked - waiting for the game to be in front";
            _waitUntil = Environment.TickCount64 + 300;
            return;
        }

        if (!ticked)
            DebugArtifacts.SaveFrame(frame, _overlays, "autowar_unticked");
        Log?.Invoke(ticked ? LogLevel.Success : LogLevel.Warning, $"{what} - pressed '{key}' for the auto war.");
        Stop($"out of Temple of Fiends Keys and Den of Rebirth entries - auto war on ({what})");
    }
}

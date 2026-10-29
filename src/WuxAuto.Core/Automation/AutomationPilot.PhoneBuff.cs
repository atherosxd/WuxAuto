using System.Drawing;
using System.Text.RegularExpressions;
using Emgu.CV;
using Emgu.CV.CvEnum;
using WuxAuto.Core;
using WuxAuto.Phone;
using WuxAuto.Vision;
using LogLevel = WuxAuto.Core.LogLevel;

namespace WuxAuto.Automation;

/// <summary>
/// Phone only (2026-10-30, owner: "no autobuff seen" in a MuMu LOD run whose log shows 15 buff presses): every AUTO BUFF
/// tap and every loot round says what it did; a buff is checked on the buff row under the HP / MP bars ~3 s after the
/// tap (tapped once more when nothing changed, then a warning); the game's refusal ("Force 90% and over required", M0)
/// is reported once instead of tapping on in silence. The desktop's buff and loot are unchanged.
/// </summary>
public sealed partial class AutomationPilot
{
    /// <summary>What the game says when AUTO BUFF is refused (M0: the character had 83 % Force).</summary>
    public const string AutoBuffRefusedText = "Force 90% and over required";

    /// <summary>A refusal line counts this long after a buff tap.</summary>
    private const int BuffRefusalWindowMs = 8000;

    /// <summary>
    /// The buff row "changed": this share of its pixels moved by more than <see cref="BuffRowPixelDiff"/> grey levels. MuMu
    /// buff frames (town, 2026-10-30): 0.069 between the frame before the tap and 4 s after it (new icons, timers back to
    /// 6m), 0.001 at 0.7 s (nothing yet), 0.000 between two frames without a tap.
    /// </summary>
    public const double BuffRowChangedShare = 0.02;

    private const int BuffRowPixelDiff = 40;

    /// <summary>How long after the tap the buff row is looked at again (the buffs showed ~4 s after a tap in town).</summary>
    internal int PhoneBuffCheckDelayMs { get; set; } = 3000;

    // The buff row(s) under the HP / MP bars, at 1920x1080 (MuMu buff frames: icons with "3m" / "6m" at y 128-178, a
    // second row below; left-anchored like the bars).
    private const int BuffRowX = 0, BuffRowY = 120, BuffRowW = 480, BuffRowH = 110;

    // "3m", "6m", "1h" under a buff icon ("11d" is an item's time, not a buff's - not counted).
    private static readonly Regex BuffTimer = new(@"^(\d{1,3})\s*([mh])$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private BuffLook? _phoneBuffBefore;    // the buff row before the tap (null = none: the arrival buff)
    private BuffLook? _phoneBuffPrepared;  // taken by PrepareBuffCheckAsync for the tap about to go out
    private long _phoneBuffCheckAt;        // when to look again (0 = no check pending)
    private bool _phoneBuffRetried;
    private bool _phoneBuffMissingWarned;
    private bool _phoneBuffRefusedWarned;
    private bool _phoneBuffUnreadTold;
    private long _phoneBuffTappedAt;
    private int _phoneLootTaps;

    /// <summary>The buff row at one moment: its timers as read (often nothing - the "3m" is tiny) and its grey pixels.</summary>
    private sealed record BuffLook(IReadOnlyList<int> Timers, Mat Grey) : IDisposable
    {
        public void Dispose() => Grey.Dispose();
    }

    private bool PhoneBuffChecks => Profile == PlatformProfile.Phone;

    /// <summary>The buff row on a <paramref name="width"/> x <paramref name="height"/> frame.</summary>
    public static PixelRect BuffTimersRegion(int width, int height) =>
        PhoneLayout.ToPixels(BuffRowX, BuffRowY, BuffRowW, BuffRowH, PhoneAnchor.Left, width, height).Clamp(width, height);

    /// <summary>The minutes of every word that reads as a buff timer ("6m" = 6, "1h" = 60).</summary>
    public static IReadOnlyList<int> BuffTimers(IEnumerable<string> words)
    {
        var minutes = new List<int>();
        foreach (string word in words)
        {
            var m = BuffTimer.Match(word.Trim());
            if (m.Success)
                minutes.Add(int.Parse(m.Groups[1].Value) * (char.ToLowerInvariant(m.Groups[2].Value[0]) == 'h' ? 60 : 1));
        }

        return minutes;
    }

    /// <summary>
    /// Whether a buff took, from the timers before and after the tap: a timer longer than any before (a fresh buff
    /// restarts at its full time; timers only count down otherwise). No "before" (the arrival buff): a timer of 5 min or
    /// more. Null = the timers cannot tell (nothing reads after, or the buffs were already full).
    /// </summary>
    public static bool? BuffTook(IReadOnlyList<int>? before, IReadOnlyList<int> after)
    {
        if (after.Count == 0)
            return null;
        if (before == null || before.Count == 0)
            return after.Max() >= 5;
        if (after.Max() > before.Max())
            return true;
        return before.Max() >= 5 ? null : false;
    }

    /// <summary>The share of the buff row's pixels that changed by more than <see cref="BuffRowPixelDiff"/> grey levels (0 for different sizes).</summary>
    public static double BuffRowChange(Mat beforeGrey, Mat afterGrey)
    {
        if (beforeGrey.Size != afterGrey.Size || beforeGrey.Width == 0 || beforeGrey.Height == 0)
            return 0;
        using var diff = new Mat();
        CvInvoke.AbsDiff(beforeGrey, afterGrey, diff);
        using var moved = new Mat();
        CvInvoke.Threshold(diff, moved, BuffRowPixelDiff, 255, ThresholdType.Binary);
        return CvInvoke.CountNonZero(moved) / (double)(diff.Width * diff.Height);
    }

    /// <summary>The buff row's grey pixels (a copy).</summary>
    public static Mat BuffRowGrey(Mat frame)
    {
        var r = BuffTimersRegion(frame.Width, frame.Height);
        using var crop = new Mat(frame, new Rectangle(r.X, r.Y, r.Width, r.Height));
        var grey = new Mat();
        CvInvoke.CvtColor(crop, grey, crop.NumberOfChannels == 4 ? ColorConversion.Bgra2Gray : ColorConversion.Bgr2Gray);
        return grey;
    }

    /// <summary>True when the line reads like the game's AUTO BUFF refusal.</summary>
    public static bool IsBuffRefusal(string line)
    {
        string n = TextMatcher.Normalize(line);
        return n.Contains("force", StringComparison.Ordinal) && (n.Contains("required", StringComparison.Ordinal) || n.Contains("90", StringComparison.Ordinal))
               || TextMatcher.Similarity(line, AutoBuffRefusedText) >= 0.8;
    }

    private async Task<BuffLook> LookAtBuffRowAsync(Mat frame)
    {
        var words = await _analyzer.Ocr.RecognizeAsync(frame, BuffTimersRegion(frame.Width, frame.Height), 2.0);
        return new BuffLook(BuffTimers(words.Select(w => w.Text)), BuffRowGrey(frame));
    }

    private void ResetPhoneBuffState()
    {
        _phoneBuffBefore?.Dispose();
        _phoneBuffBefore = null;
        _phoneBuffPrepared?.Dispose();
        _phoneBuffPrepared = null;
        _phoneBuffCheckAt = 0;
        _phoneBuffRetried = false;
        _phoneBuffMissingWarned = false;
        _phoneBuffRefusedWarned = false;
        _phoneBuffUnreadTold = false;
        _phoneBuffTappedAt = 0;
        _phoneLootTaps = 0;
    }

    /// <summary>Phone: no AUTO BUFF on screen - said once per Start, not skipped in silence.</summary>
    private void PhoneBuffMissing()
    {
        if (!PhoneBuffChecks || _phoneBuffMissingWarned)
            return;
        _phoneBuffMissingWarned = true;
        Log?.Invoke(LogLevel.Warning, "Auto buff: the AUTO BUFF button is not on screen - no buff (put it back on the screen, or check the shortcut ring).");
    }

    /// <summary>
    /// Phone, before a tick that may buff (<see cref="TryLodBuff"/>): the buff row as it is now, when the buff is due -
    /// one OCR of the row per buff.
    /// </summary>
    private async Task PrepareBuffCheckAsync(Mat frame, long now, bool force)
    {
        _phoneBuffPrepared?.Dispose();
        _phoneBuffPrepared = null;
        if (!PhoneBuffChecks || (!force && now - _lodBuffAt < _settings.Lod.BuffEveryMs) || !_controls.Has(GameAction.Buff))
            return;
        _phoneBuffPrepared = await LookAtBuffRowAsync(frame);
    }

    /// <summary>Phone, right after a buff tap: where it went, and the check ~3 s later.</summary>
    private void PhoneBuffTapped(long now)
    {
        if (!PhoneBuffChecks)
            return;

        var box = _shortcuts?.Find(GameAction.Buff);
        Log?.Invoke(LogLevel.Info, box is { } b
            ? $"Auto buff tapped at {b.CenterX},{b.CenterY}."
            : "Auto buff tapped.");
        _phoneBuffBefore?.Dispose();
        _phoneBuffBefore = _phoneBuffPrepared;
        _phoneBuffPrepared = null;
        _phoneBuffCheckAt = now + PhoneBuffCheckDelayMs;
        _phoneBuffRetried = false;
        _phoneBuffTappedAt = now;
    }

    /// <summary>The whole-screen read of an LOD tick: the refusal text within <see cref="BuffRefusalWindowMs"/> of a buff tap, once per Start.</summary>
    private void CheckBuffRefusal(IEnumerable<string> lines, long now)
    {
        if (!PhoneBuffChecks || _phoneBuffRefusedWarned || _phoneBuffTappedAt == 0 || now - _phoneBuffTappedAt > BuffRefusalWindowMs)
            return;

        string? refusal = lines.FirstOrDefault(IsBuffRefusal);
        if (refusal == null)
            return;

        _phoneBuffRefusedWarned = true;
        Log?.Invoke(LogLevel.Warning, $"Auto buff refused by the game: Force below 90% ('{refusal.Trim()}').");
    }

    /// <summary>
    /// Phone, on LOD ticks: looks at the buff row when the check is due. The timers decide when they read; otherwise the
    /// row's pixels (<see cref="BuffRowChangedShare"/>) - no "before" (the arrival buff) = cannot tell. Nothing changed =
    /// one more tap, then a warning.
    /// </summary>
    private async Task TickPhoneBuffCheckAsync(Mat frame, long now)
    {
        if (!PhoneBuffChecks || _phoneBuffCheckAt == 0 || now < _phoneBuffCheckAt)
            return;

        _phoneBuffCheckAt = 0;
        using var after = await LookAtBuffRowAsync(frame);
        bool? took = BuffTook(_phoneBuffBefore?.Timers, after.Timers);
        string how = took != null ? $"timers {string.Join(" ", after.Timers.Select(m => m + "m"))}" : "";
        if (took == null && _phoneBuffBefore != null)
        {
            double change = BuffRowChange(_phoneBuffBefore.Grey, after.Grey);
            took = change >= BuffRowChangedShare;
            how = $"buff row {change:P1} changed";
        }

        if (took == true)
        {
            Log?.Invoke(LogLevel.Info, $"Auto buff took ({how}).");
        }
        else if (took == null)
        {
            if (!_phoneBuffUnreadTold)
            {
                _phoneBuffUnreadTold = true;
                Log?.Invoke(LogLevel.Info, "Auto buff: cannot check this buff (no picture of the buff row before the tap, and its timers do not read).");
            }
        }
        else if (!_phoneBuffRetried)
        {
            _phoneBuffRetried = true;
            if (_controls.Press(GameAction.Buff))
            {
                Log?.Invoke(LogLevel.Info, $"Auto buff: nothing changed under the HP bar ({how}) - tapped it once more.");
                _phoneBuffTappedAt = now;
                _phoneBuffCheckAt = now + PhoneBuffCheckDelayMs;
                return;
            }

            Log?.Invoke(LogLevel.Warning, "Auto buff did not take, and the second tap was blocked.");
        }
        else
        {
            Log?.Invoke(LogLevel.Warning, $"Auto buff did not take (tapped twice, {how}) - Force below 90%, or the tap does not reach the button?");
        }

        _phoneBuffBefore?.Dispose();
        _phoneBuffBefore = null;
    }
}

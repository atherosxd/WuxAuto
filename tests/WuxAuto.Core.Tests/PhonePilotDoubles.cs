using System.Drawing;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using WuxAuto.Automation;
using WuxAuto.Core;
using WuxAuto.Phone;
using WuxAuto.Vision;

namespace WuxAuto.Core.Tests;

/// <summary>
/// Records every gesture in order ("Tap 1655,815", "Pinch 960,432 720->192", "Joy 262,816 dx,dy", "Down x,y",
/// "Release"); the game is in front unless <see cref="InFront"/> is cleared. <see cref="OnTap"/> lets a test's fake game
/// react to a tap (the ring unlocking, the picker opening).
/// </summary>
sealed class RecordingTouch : ITouchInput
{
    public List<string> Calls { get; } = [];
    public bool InFront { get; set; } = true;
    public bool DryRun { get; set; }
    public Action<int, int>? OnTap { get; set; }

    public bool IsDryRun => DryRun;
    public bool IsGameInFront => InFront;
    public bool BringGameToFront() => true;

    public bool Tap(int x, int y, int holdMs)
    {
        Calls.Add($"Tap {x},{y}");
        OnTap?.Invoke(x, y);
        return true;
    }

    public bool Swipe(int x1, int y1, int x2, int y2, int durationMs) => Record($"Swipe {x1},{y1}->{x2},{y2}");
    public bool Drag(int x1, int y1, int x2, int y2, int durationMs, int endHoldMs) => Record($"Drag {x1},{y1}->{x2},{y2}");
    public bool Pinch(int cx, int cy, int fromGap, int toGap, int durationMs) => Record($"Pinch {cx},{cy} {fromGap}->{toGap}");
    public bool Joystick(int cx, int cy, int dx, int dy, int durationMs) => Record($"Joy {cx},{cy} {dx},{dy}");
    public bool PressDown(int x, int y) => Record($"Down {x},{y}");
    public void Release() => Calls.Add("Release");

    /// <summary>The taps only, as points.</summary>
    public List<Point> Taps => Calls.Where(c => c.StartsWith("Tap ", StringComparison.Ordinal))
        .Select(c => c[4..].Split(','))
        .Select(p => new Point(int.Parse(p[0]), int.Parse(p[1])))
        .ToList();

    private bool Record(string call)
    {
        Calls.Add(call);
        return true;
    }
}

/// <summary>The phone's buttons where a test puts them (what <see cref="ShortcutMap"/> would have found); records invalidations.</summary>
sealed class ScriptedShortcuts : IShortcutSource
{
    public Dictionary<GameAction, PixelRect> Icons { get; } = [];
    public Dictionary<string, PixelRect> Skills { get; } = [];
    public (int Width, int Height) FrameSize { get; set; } = (1920, 1080);
    public (int Cx, int Cy, int Radius)? Joystick { get; set; }
    public PixelRect? Menu { get; set; }
    public List<string> Invalidated { get; } = [];

    public PixelRect? Find(GameAction action) => Icons.TryGetValue(action, out var r) ? r : null;
    public PixelRect? Skill(string skill) => Skills.TryGetValue(skill, out var r) ? r : null;
    public void Invalidate() => Invalidated.Add("all");
    public void Invalidate(GameAction action) => Invalidated.Add($"action:{action}");
    public void InvalidateSkill(string skill) => Invalidated.Add($"skill:{skill}");
    public void InvalidateJoystick() => Invalidated.Add("joystick");
    public void InvalidateMenu() => Invalidated.Add("menu");
}

/// <summary>
/// A phone pilot (<see cref="PlatformProfile.Phone"/>, <see cref="TouchControls"/> over a <see cref="RecordingTouch"/>,
/// scripted shortcut positions, the phone templates of src\WuxAuto.Mobile\Assets\phone as the only assets) ticked over
/// the frame a test hands it on every tick.
/// </summary>
sealed class PhonePilot : IDisposable
{
    public static string PhoneAssets => Path.Combine(AppContext.BaseDirectory, "Assets", "phone");

    public AppSettings Settings { get; }
    public RecordingTouch Touch { get; } = new();
    public ScriptedShortcuts Shortcuts { get; } = new();
    public FakeOcrEngine Ocr { get; } = new();
    public ScreenAnalyzer Analyzer { get; }
    public TouchControls Controls { get; }
    public AutomationPilot Pilot { get; }
    public List<string> Lines { get; } = [];
    public List<PilotPhase> Phases { get; } = [];

    /// <param name="root">An empty folder for the settings / debug files (AppPaths).</param>
    public PhonePilot(string root, AppSettings? settings = null)
    {
        AppPaths.Configure(root, PhoneAssets);
        Settings = settings ?? PhoneLodSettings();
        Analyzer = new ScreenAnalyzer(Settings, Ocr);
        Controls = new TouchControls(Settings, Touch, Shortcuts, sleep: _ => { });
        Pilot = new AutomationPilot(Settings, Analyzer, Controls, PlatformProfile.Phone, Shortcuts);
        Pilot.Log += (_, text) => Lines.Add(text);
        Pilot.StateChanged += () =>
        {
            if (Phases.Count == 0 || Phases[^1] != Pilot.Phase)
                Phases.Add(Pilot.Phase);
        };
    }

    /// <summary>Auto LOD with quick timings: no focus settle, no settings visit delays, keys as the defaults.</summary>
    public static AppSettings PhoneLodSettings()
    {
        var settings = new AppSettings();
        settings.Sequence.Mode = RunMode.AutoLod;
        settings.Input.FocusSettleMs = 0;
        settings.Sequence.InventoryOpenMs = 100;
        return settings;
    }

    public string Log => string.Join("\n", Lines);

    /// <summary>Starts at <paramref name="startAt"/> and ticks on <paramref name="frame"/>() until <paramref name="done"/>, the pilot stops, or the time-out.</summary>
    public async Task<PilotPhase> RunAsync(Func<Mat> frame, PilotPhase startAt, Func<bool> done, int timeoutMs = 15000)
    {
        Pilot.Start(startAt);
        long deadline = Environment.TickCount64 + timeoutMs;
        while (!done() && Pilot.IsRunning && Environment.TickCount64 < deadline)
        {
            await Pilot.TickAsync(frame(), CancellationToken.None);
            await Task.Delay(15);
        }

        return Pilot.Phase;
    }

    public void Dispose() => Analyzer.Dispose();
}

/// <summary>Synthetic 1920x1080 phone frames: noisy grey, with the real phone crops pasted where the game draws them.</summary>
static class PhoneFrames
{
    public const int Width = 1920;
    public const int Height = 1080;

    public static Mat Grey(int width = Width, int height = Height)
    {
        var frame = new Mat(height, width, DepthType.Cv8U, 4);
        frame.SetTo(new MCvScalar(110, 110, 110, 255));
        using var noise = new Mat(height, width, DepthType.Cv8U, 4);
        CvInvoke.Randu(noise, new MCvScalar(0, 0, 0, 0), new MCvScalar(40, 40, 40, 0));
        CvInvoke.Add(frame, noise, frame);
        return frame;
    }

    /// <summary>Pastes the crop with its top-left at (<paramref name="x"/>, <paramref name="y"/>); returns its box.</summary>
    public static PixelRect Paste(Mat frame, string template, int x, int y)
    {
        using var crop = CvInvoke.Imread(Path.Combine(PhonePilot.PhoneAssets, template + ".png"), ImreadModes.ColorBgr);
        using var bgra = new Mat();
        CvInvoke.CvtColor(crop, bgra, ColorConversion.Bgr2Bgra);
        using (var target = new Mat(frame, new Rectangle(x, y, crop.Width, crop.Height)))
            bgra.CopyTo(target);
        return new PixelRect(x, y, crop.Width, crop.Height);
    }

    /// <summary>Pastes the crop centred on (<paramref name="cx"/>, <paramref name="cy"/>).</summary>
    public static PixelRect PasteCentred(Mat frame, string template, int cx, int cy)
    {
        using var crop = CvInvoke.Imread(Path.Combine(PhonePilot.PhoneAssets, template + ".png"), ImreadModes.Unchanged);
        return Paste(frame, template, cx - crop.Width / 2, cy - crop.Height / 2);
    }
}

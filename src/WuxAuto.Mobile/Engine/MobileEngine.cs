using Emgu.CV;
using WuxAuto.Automation;
using WuxAuto.Core;
using WuxAuto.Phone;
using WuxAuto.Vision;
using LogLevel = WuxAuto.Core.LogLevel;

namespace WuxAuto.Mobile.Engine;

/// <summary>What the floating control and the notification show: grey, green or red.</summary>
public enum EngineState
{
    Idle,
    Running,
    Error,
}

/// <summary>
/// The phone's counterpart of the desktop WuxEngine: owns the analyzer, the shortcut map, the touch controls and the
/// pilot (<see cref="PlatformProfile.Phone"/>), and runs the loop that feeds the pilot - all game-touching work happens
/// on that one loop thread; the pages, the floating control and the notification only observe the events and call
/// <see cref="Start"/>, <see cref="Stop"/> and <see cref="Panic"/>.
/// <para>Each pass: newest frame → <see cref="PhoneLoopGate"/> (capture alive, frame not stale, game in front, panel
/// shut, zone title read) → phone regions re-applied on a new frame size → <see cref="ShortcutMap.Refresh"/> →
/// <see cref="AutomationPilot.TickAsync"/>. MediaProjection only delivers a frame when the screen changes, so the pilot
/// is ticked on the newest frame at a fixed rate whether or not it is new (its waits are time-based).</para>
/// </summary>
public sealed class MobileEngine : IDisposable
{
    /// <summary>Time between two loop passes (the capture copies a frame at most every 100 ms).</summary>
    public const int PassIntervalMs = 120;

    /// <summary>How long Start waits for the capture's first frame after the consent dialog.</summary>
    public const int CaptureStartTimeoutMs = 60_000;

    private const string Tag = MainApplication.Tag;
    private const int MaxRecent = 500;

    private readonly AppSettings _settings;
    private readonly Func<CaptureService?> _capture;
    private readonly ITouchInput _touch;
    private readonly ScreenAnalyzer _analyzer;
    private readonly ShortcutMap _shortcuts;
    private readonly TouchControls _controls;
    private readonly AutomationPilot _pilot;
    private readonly PhoneLoopGate _gate = new();
    private readonly object _logLock = new();
    private readonly LinkedList<LogEntry> _recent = new();
    private readonly string _logFile;

    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private volatile bool _startRequested;
    private long _startRequestedAt;
    private volatile string? _error;
    private volatile bool _captureStopExpected;

    // Every Stop / Panic / capture loss counts up; a start that began before one of them is undone (BeginRun).
    private int _stops;
    private int _stopsAtStart;
    private volatile string _pause = string.Empty;
    private (int W, int H) _regionsSize;
    private bool _ocrReady;
    private int _disposed;

    /// <param name="capture">The running capture, or null (it comes and goes with the consent: one per session).</param>
    /// <param name="ocr">Owned: disposed with the engine (through the analyzer).</param>
    public MobileEngine(AppSettings settings, Func<CaptureService?> capture, ITouchInput touch, IOcrEngine ocr)
    {
        _settings = settings;
        _capture = capture;
        _touch = touch;
        if (touch is AndroidTouchInput android)
            android.DryRunSource = () => _settings.Sequence.DryRun;

        _analyzer = new ScreenAnalyzer(settings, ocr);
        _shortcuts = new ShortcutMap(_analyzer);
        _controls = new TouchControls(settings, touch, _shortcuts);
        _pilot = new AutomationPilot(settings, _analyzer, _controls, PlatformProfile.Phone);

        Directory.CreateDirectory(AppPaths.LogFolder);
        _logFile = Path.Combine(AppPaths.LogFolder, $"wuxauto_{DateTime.Now:yyyyMMdd_HHmmss}.log");

        _pilot.Log += Emit;
        _pilot.StateChanged += RaiseState;
        _pilot.ModeChanged += () => ModeChanged?.Invoke();
        _pilot.BossKilled += OnBossKilled;
        CaptureService.Stopped += OnCaptureStopped;

        Emit(LogLevel.Trace, $"Log file: {_logFile}");
    }

    /// <summary>Every log line (pilot and engine); raised on whichever thread logged it.</summary>
    public event Action<LogEntry>? Log;

    /// <summary>Running / stopped / paused / error changed; raised on the loop thread or the caller's.</summary>
    public event Action? StateChanged;

    /// <summary>The pilot switched the run mode itself (out of entries) and saved it.</summary>
    public event Action? ModeChanged;

    /// <summary>A boss died (the pilot's text, e.g. "Bai Gu Great down").</summary>
    public event Action<string>? BossKilled;

    public AppSettings Settings => _settings;
    public bool IsRunning => _pilot.IsRunning;

    /// <summary>Start was pressed and the pilot waits for the capture's first frame.</summary>
    public bool IsStarting => _startRequested;

    public EngineState State => _pilot.IsRunning || _startRequested ? EngineState.Running
        : _error != null ? EngineState.Error : EngineState.Idle;

    /// <summary>Why the pilot is not ticking although it runs (empty while it ticks).</summary>
    public string PauseReason => _pilot.IsRunning ? _pause : string.Empty;

    /// <summary>"14:32:05 Bai Gu Great down - check the loot"; empty before the first kill.</summary>
    public string LastKill { get; private set; } = string.Empty;

    public string LogFile => _logFile;

    /// <summary>True while the floating control's panel covers the game (set on the UI thread; the pilot waits).</summary>
    public volatile bool PanelOpen;

    /// <summary>The three status lines of the floating panel and the main page.</summary>
    public string StatusText
    {
        get
        {
            string mode = _settings.Sequence.Mode switch
            {
                RunMode.AutoLod => "LOD",
                RunMode.AutoWar => "War",
                _ => "Laby",
            };
            string dry = _settings.Sequence.DryRun ? " (dry run)" : string.Empty;
            if (_startRequested)
                return $"Starting {mode}{dry}\nwaiting for the screen capture\n";
            if (_pilot.IsRunning)
            {
                string second = string.IsNullOrEmpty(_pause) ? _pilot.PhaseText : $"paused: {_pause}";
                return $"Running {mode}{dry}\n{second}\n{_pilot.Detail}";
            }

            return _error != null ? $"Stopped - error\n{_error}\n" : $"Idle - {mode}{dry}\n\n";
        }
    }

    /// <summary>The newest log lines, oldest first.</summary>
    public IReadOnlyList<LogEntry> RecentLog()
    {
        lock (_logLock)
            return _recent.ToList();
    }

    /// <summary>
    /// Starts <paramref name="mode"/>. Without a running capture the consent dialog comes up first (it brings the game
    /// back), and the pilot starts with the first frame. Needs the accessibility service (touches, which app is in front).
    /// </summary>
    public void Start(RunMode mode)
    {
        if (PilotAccessibilityService.Instance == null)
        {
            Fail("The WuxAuto accessibility service is off - turn it on (Setup page) first.");
            return;
        }

        if (_pilot.IsRunning || _startRequested)
        {
            Emit(LogLevel.Info, "Already running.");
            return;
        }

        if (_settings.Sequence.Mode != mode)
        {
            _settings.Sequence.Mode = mode;
            Save();
        }

        _error = null;
        _startRequestedAt = Environment.TickCount64;
        _stopsAtStart = Volatile.Read(ref _stops);
        _startRequested = true;
        EnsureLoop();
        if (_capture() == null)
        {
            Emit(LogLevel.Info, "Asking for screen capture - tap \"Start now\"; the game comes back by itself.");
            CaptureService.RequestConsent(Android.App.Application.Context, bringGame: true);
        }

        RaiseState();
    }

    /// <summary>Stops the pilot (off the caller's thread: lifting a held touch can take a few seconds).</summary>
    public void Stop(string reason)
    {
        Interlocked.Increment(ref _stops);
        bool pending = _startRequested;
        _startRequested = false;
        if (pending && !_pilot.IsRunning)
        {
            Emit(LogLevel.Info, $"Start cancelled: {reason}.");
            RaiseState();
        }

        _ = Task.Run(() =>
        {
            try
            {
                _pilot.Stop(reason);
            }
            catch (Exception ex)
            {
                Emit(LogLevel.Warning, $"Stop: {ex.Message}");
            }

            RaiseState();
        });
    }

    /// <summary>F10 equivalent: stop, then lift every held touch (skill, joystick) - off the caller's thread.</summary>
    public void Panic()
    {
        Interlocked.Increment(ref _stops);
        _startRequested = false;
        Emit(LogLevel.Warning, "PANIC - stopping and lifting every touch.");
        _ = Task.Run(() =>
        {
            try
            {
                _pilot.Stop("panic");
            }
            catch (Exception ex)
            {
                Emit(LogLevel.Warning, $"Panic stop: {ex.Message}");
            }

            try
            {
                _controls.ReleaseAll();
            }
            catch (Exception ex)
            {
                Emit(LogLevel.Warning, $"Panic release: {ex.Message}");
            }

            RaiseState();
        });
    }

    /// <summary>Ends the screen capture on purpose (the main page's button): the pilot stops without an error.</summary>
    public void StopCapture()
    {
        _captureStopExpected = true;
        try
        {
            if (_pilot.IsRunning || _startRequested)
                Stop("screen capture stopped from the app");
            CaptureService.StopCapture();
        }
        finally
        {
            _captureStopExpected = false;
        }
    }

    /// <summary>Saves the settings (the pages and the floating control change them); a failed write is logged.</summary>
    public void Save()
    {
        try
        {
            SettingsStore.Save(_settings);
        }
        catch (Exception ex)
        {
            Emit(LogLevel.Warning, $"Settings not saved: {ex.Message}");
        }
    }

    private void EnsureLoop()
    {
        if (_loop is { IsCompleted: false })
            return;
        var token = _cts.Token;
        _loop = Task.Factory.StartNew(() => LoopAsync(token), token, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            long started = Environment.TickCount64;
            try
            {
                await PassAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Emit(LogLevel.Error, $"Loop error: {ex.Message}");
                Android.Util.Log.Error(Tag, ex.ToString());
                _error = $"loop error: {ex.Message}";
                _pilot.Stop("loop error");
                ReleaseQuietly();
                RaiseState();
            }

            int wait = PassIntervalMs - (int)(Environment.TickCount64 - started);
            try
            {
                await Task.Delay(Math.Max(10, wait), token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task PassAsync(CancellationToken token)
    {
        var capture = _capture();
        if (_startRequested)
        {
            if (!BeginRun(capture))
                return;
        }

        if (!_pilot.IsRunning)
            return;

        long now = FrameGate.NowTicks();
        bool alive = capture != null;
        long frameTicks = capture?.LatestTicks ?? 0;
        bool front = _touch.IsGameInFront;

        Mat? frame = null;
        try
        {
            if (alive && frameTicks > 0 && front && capture!.LatestFrame is { } latest)
            {
                frame = latest.Bgra;
                frameTicks = latest.Ticks;
                ApplyRegionsFor(frame);
                if (_gate.TitleCheckDue(now))
                    _gate.TitleChecked(now, await TitleReadsAsync(frame));
            }

            // Read the clock after the frame: a frame stamped between an earlier reading and the copy would look like it
            // came from the future (FrameGate: not fresh) - seen on MuMu as a stray "no recent frame" pause.
            now = FrameGate.NowTicks();
            var decision = _gate.Decide(now, alive, frameTicks, front, PanelOpen, _pilot.Phase);
            SetPause(decision.Tick ? string.Empty : decision.Reason);
            if (!decision.Tick || frame == null)
                return;

            // Loop thread only (IShortcutSource): before every tick, so a size change or a missed press is re-found
            // before the pilot presses anything.
            _shortcuts.Refresh(frame);
            await _pilot.TickAsync(frame, token);
        }
        finally
        {
            frame?.Dispose();
        }

        if (!_pilot.IsRunning && _pilot.Phase == PilotPhase.Failed && _error == null)
        {
            _error = "the run failed - see the log";
            RaiseState();
        }
    }

    /// <summary>The pilot starts once the capture has a frame; true when it runs now.</summary>
    private bool BeginRun(CaptureService? capture)
    {
        if (capture == null || capture.LatestTicks <= 0)
        {
            if (Environment.TickCount64 - _startRequestedAt > CaptureStartTimeoutMs)
            {
                _startRequested = false;
                Fail("No screen capture after 60 s (the consent was not given?) - press Start again.");
            }

            return false;
        }

        _startRequested = false;
        if (!_ocrReady)
        {
            _ocrReady = _analyzer.Ocr.EnsureInitialized();
            Emit(_ocrReady ? LogLevel.Info : LogLevel.Warning, $"OCR: {_analyzer.Ocr.Status}");
        }

        _gate.Reset();
        _pause = string.Empty;
        _regionsSize = default;
        _shortcuts.Invalidate();
        // The pilot's Focus phase would bring the game back, but the loop gate holds every tick while another app is
        // in front (a Start from the launcher or this app's page): bring it here, before the first tick.
        if (!_touch.IsGameInFront && !_touch.BringGameToFront())
            Emit(LogLevel.Warning, "Could not bring the game to the front - open it; the pilot waits until it is in front.");
        Emit(LogLevel.Info, $"Capture {capture.Width}x{capture.Height}; starting {_settings.Sequence.Mode}.");
        _pilot.Start();
        // A Stop that came in between the check above and this Start found nothing running to stop: undo it here.
        if (Volatile.Read(ref _stops) != _stopsAtStart)
            _pilot.Stop("stopped while starting");
        RaiseState();
        return _pilot.IsRunning;
    }

    /// <summary>Phone regions follow the frame size (MuMu restarted at another size, a foldable): set again on a change.</summary>
    private void ApplyRegionsFor(Mat frame)
    {
        if (_regionsSize == (frame.Width, frame.Height))
            return;
        bool first = _regionsSize == default;
        _regionsSize = (frame.Width, frame.Height);
        PhoneRegions.Apply(_settings, frame.Width, frame.Height);
        if (!first)
            Emit(LogLevel.Warning, $"The screen is now {frame.Width}x{frame.Height}: regions set again, shortcuts searched again.");
    }

    private async Task<bool> TitleReadsAsync(Mat frame)
    {
        try
        {
            var band = _settings.Regions.Minimap.ToPixels(frame.Width, frame.Height);
            var words = await _analyzer.Ocr.RecognizeAsync(frame, band, 2.0);
            return PhoneLoopGate.TitleReads(words.Select(w => w.LineText).Distinct());
        }
        catch (Exception ex)
        {
            Emit(LogLevel.Trace, $"zone title check: {ex.Message}");
            return false;
        }
    }

    private void SetPause(string reason)
    {
        if (reason == _pause)
            return;
        string was = _pause;
        _pause = reason;
        if (reason.Length > 0)
            Emit(LogLevel.Info, $"Pilot waiting: {reason}.");
        else if (was.Length > 0)
            Emit(LogLevel.Info, "Pilot going on.");
        RaiseState();
    }

    private void OnCaptureStopped()
    {
        if (_captureStopExpected || (!_pilot.IsRunning && !_startRequested))
            return;
        Interlocked.Increment(ref _stops);
        _startRequested = false;
        _error = "the screen capture stopped";
        Emit(LogLevel.Error, "The screen capture stopped - the pilot stops (nothing is tapped without a picture). Press Start to allow it again.");
        _ = Task.Run(() =>
        {
            _pilot.Stop("screen capture stopped");
            ReleaseQuietly();
            RaiseState();
        });
    }

    private void OnBossKilled(string what)
    {
        LastKill = $"{DateTime.Now:HH:mm:ss} {what} - check the loot";
        BossKilled?.Invoke(what);
        RaiseState();
    }

    private void Fail(string message)
    {
        _error = message;
        Emit(LogLevel.Error, message);
        RaiseState();
    }

    private void ReleaseQuietly()
    {
        try
        {
            _controls.ReleaseAll();
        }
        catch (Exception ex)
        {
            Emit(LogLevel.Warning, $"Release: {ex.Message}");
        }
    }

    private void RaiseState()
    {
        try
        {
            StateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Android.Util.Log.Warn(Tag, $"StateChanged handler: {ex.Message}");
        }
    }

    private void Emit(LogLevel level, string message) => Emit(new LogEntry(level, message));

    private void Emit(LogEntry entry)
    {
        lock (_logLock)
        {
            _recent.AddLast(entry);
            while (_recent.Count > MaxRecent)
                _recent.RemoveFirst();
            try
            {
                File.AppendAllText(_logFile, entry + Environment.NewLine);
            }
            catch (Exception)
            {
                // the log file is a convenience; logcat still has the line
            }
        }

        switch (entry.Level)
        {
            case LogLevel.Error:
                Android.Util.Log.Error(Tag, entry.Message);
                break;
            case LogLevel.Warning:
                Android.Util.Log.Warn(Tag, entry.Message);
                break;
            default:
                Android.Util.Log.Info(Tag, entry.Message);
                break;
        }

        try
        {
            Log?.Invoke(entry);
        }
        catch (Exception)
        {
            // a page's handler must not break the loop
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        CaptureService.Stopped -= OnCaptureStopped;
        _cts.Cancel();
        try
        {
            _pilot.Stop("app closing");
            _controls.ReleaseAll();
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            // shutting down
        }

        _analyzer.Dispose();
        _cts.Dispose();
    }
}

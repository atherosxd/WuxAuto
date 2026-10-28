using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Emgu.CV;
using Emgu.CV.CvEnum;
using WuxAuto.Automation;
using WuxAuto.Capture;
using WuxAuto.Core;
using WuxAuto.Input;
using WuxAuto.Interop;
using WuxAuto.Vision;
using LogLevel = WuxAuto.Core.LogLevel;
using Size = System.Drawing.Size;

namespace WuxAuto.Engine;

public sealed record PreviewUpdate(BitmapSource Image, string Caption);

/// <summary>
/// Owns the capture thread, the vision loop and the pilot state machine. Everything that
/// touches the game runs on the vision loop thread; the UI only ever observes events.
/// </summary>
public sealed class WuxEngine : IAsyncDisposable
{
    private const int PreviewMaxWidth = 960;

    private readonly AppSettings _settings;
    private readonly DesktopCaptureService _capture = new();
    private readonly ScreenAnalyzer _analyzer;
    private readonly WindowInput _input;
    private readonly AutomationPilot _pilot;
    private readonly AutoConfirm _autoConfirm;

    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private volatile bool _testScanRequested;
    private volatile bool _snapshotRequested;
    private List<OverlayItem> _scanOverlays = new();
    private string _scanSummary = "no scan yet";

    private long _lastPreviewTicks;    private int _framesThisSecond;
    private long _fpsWindowStart;
    private double _fps;
    private double _lastFrameMs;

    public WuxEngine(AppSettings settings)
    {
        _settings = settings;
        _capture.MaxFps = settings.Ui.CaptureFps;

        _input = new WindowInput(settings);
        _analyzer = new ScreenAnalyzer(settings, new OcrService());
        _pilot = new AutomationPilot(settings, _analyzer, _input);
        _autoConfirm = new AutoConfirm(settings, _analyzer, _input);
        _autoConfirm.Log += (level, message) => EmitLog(level, message);

        _input.Log += (level, message) => EmitLog(level, message);
        _pilot.Log += (level, message) => EmitLog(level, message);
        _pilot.StateChanged += () => StateChanged?.Invoke();
        _pilot.ModeChanged += () => ModeChanged?.Invoke();
        _pilot.BossKilled += what => BossKilled?.Invoke(what);
        _capture.StatusChanged += message => EmitLog(LogLevel.Info, message);
        _capture.Faulted += OnCaptureFaulted;

        _scanSummary = $"templates: {string.Join(", ", Templates.Names)}";
    }

    public event Action<LogEntry>? Log;
    public event Action<PreviewUpdate>? Preview;
    public event Action? StateChanged;
    public event Action? ModeChanged;
    public event Action<string>? Faulted;

    /// <summary>The pilot saw a boss die (see <see cref="AutomationPilot.BossKilled"/>); raised on the loop thread.</summary>
    public event Action<string>? BossKilled;

    public TemplateLibrary Templates => _analyzer.Library;
    public AutomationPilot Pilot => _pilot;

    /// <summary>Re-read the PNGs and clear every cached template scale.</summary>
    public void ReloadTemplates() => _analyzer.ReloadTemplates();

    public bool IsBound => _capture.IsRunning;
    public WindowInfo? BoundWindow { get; private set; }
    public double Fps => _fps;
    public double LastFrameMs => _lastFrameMs;
    public double LastOcrMs => _analyzer.Ocr.LastDurationMs;
    public string OcrStatus => _analyzer.Ocr.Status;
    public string ScanSummary => _scanSummary;
    public bool IsRunning => _pilot.IsRunning;

    public async Task BindAsync(WindowInfo window)
    {
        Unbind();

        BoundWindow = window;
        _input.Bind(window.Handle, window.ProcessId);
        _capture.Start(window.Handle, window.ProcessId);
        _analyzer.Ocr.EnsureInitialized();

        _loopCts = new CancellationTokenSource();
        _loopTask = Task.Run(() => VisionLoopAsync(_loopCts.Token));

        EmitLog(LogLevel.Success, $"Bound to {window.ShortDisplay} \"{Truncate(window.Title, 48)}\" - {window.ClientWidth}x{window.ClientHeight} client area.");
        EmitLog(LogLevel.Info, $"Detectors: {string.Join(", ", Templates.Names)}. {_analyzer.Ocr.Status}");

        if (ProcessInfo.IsProcessElevated(window.ProcessId) && !ProcessInfo.IsElevated)
        {
            EmitLog(LogLevel.Warning,
                "The game runs elevated and WuxAuto does not: Windows silently drops injected input (UIPI). " +
                "Close WuxAuto and launch it as administrator.");
        }

        if (_settings.Sequence.DryRun)
            EmitLog(LogLevel.Warning, "Dry run is on: the pilot will not send any input. Untick 'Dry run' to control the game.");

        await Task.CompletedTask;
    }

    public void Unbind()
    {
        if (!IsBound && _loopTask == null && BoundWindow == null)
            return;

        _pilot.Stop("unbound");
        _autoConfirm.Reset();
        _loopCts?.Cancel();
        _capture.Stop();

        try
        {
            _loopTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // shutting down
        }

        _loopCts?.Dispose();
        _loopCts = null;
        _loopTask = null;

        _input.Unbind();
        if (BoundWindow != null)
            EmitLog(LogLevel.Info, "Unbound.");

        BoundWindow = null;
        _scanOverlays = new List<OverlayItem>();
        StateChanged?.Invoke();
    }

    public void Start(PilotPhase startAt = PilotPhase.Focus)
    {
        if (!IsBound)
        {
            EmitLog(LogLevel.Warning, "Bind to the game window first.");
            return;
        }

        _scanOverlays = new List<OverlayItem>();
        _pilot.Start(startAt);
        StateChanged?.Invoke();
    }

    public void StopPilot(string reason = "stop pressed") => _pilot.Stop(reason);

    public void FocusGame() => _input.TryFocus();

    /// <summary>Picks up settings the user edited in the UI (capture rate, preview switch).</summary>
    public void ApplyRuntimeSettings()
    {
        _capture.MaxFps = Math.Clamp(_settings.Ui.CaptureFps, 1, 240);
    }

    public void RequestTestScan()
    {
        if (!IsBound)
        {
            EmitLog(LogLevel.Warning, "Bind to the game window first.");
            return;
        }

        _testScanRequested = true;
        EmitLog(LogLevel.Info, "Test scan queued - the next captured frame is analysed.");
    }

    /// <summary>Saves the next captured frame (with whatever overlays are active) to the debug folder.</summary>
    public void RequestSnapshot()
    {
        if (!IsBound)
        {
            EmitLog(LogLevel.Warning, "Bind to the game window first.");
            return;
        }

        _snapshotRequested = true;
        EmitLog(LogLevel.Info, "Snapshot queued - the next captured frame is written to the debug folder.");
    }

    private void OnCaptureFaulted(string message)
    {
        EmitLog(LogLevel.Error, message);
        Faulted?.Invoke(message);
    }

    private async Task VisionLoopAsync(CancellationToken token)
    {
        var timeout = TimeSpan.FromMilliseconds(250);
        while (!token.IsCancellationRequested)
        {
            CaptureFrame? frame;
            try
            {
                frame = await _capture.WaitForFrameAsync(timeout, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (frame == null)
            {
                if (!_capture.IsRunning)
                {
                    if (!WindowEnumerator.IsWindow(_capture.TargetWindow))
                        EmitLog(LogLevel.Warning, "The bound window closed; capture stopped.");
                    break;
                }

                continue;
            }

            using (frame)
            {
                var stopwatch = Stopwatch.StartNew();
                Interlocked.Increment(ref _framesThisSecond);

                // The Mat is a header over the capture buffer, which stays pinned until the Mat
                // (disposed at the end of the try block) is gone.
                var handle = GCHandle.Alloc(frame.Buffer, GCHandleType.Pinned);
                try
                {
                    using var mat = new Mat(
                        frame.Height,
                        frame.Width,
                        DepthType.Cv8U,
                        4,
                        handle.AddrOfPinnedObject(),
                        frame.Stride);

                    if (_testScanRequested)
                    {
                        _testScanRequested = false;
                        await RunTestScanAsync(mat);
                    }

                    if (_snapshotRequested)
                    {
                        _snapshotRequested = false;
                        SaveSnapshot(mat);
                    }

                    if (_pilot.IsRunning)
                        await _pilot.TickAsync(mat, token);
                    else
                        await _autoConfirm.TickAsync(mat);

                    PublishPreview(mat, frame);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    EmitLog(LogLevel.Error, $"Vision loop error: {ex.Message}");
                    _pilot.Stop("vision error");
                }
                finally
                {
                    handle.Free();
                }

                _lastFrameMs = stopwatch.Elapsed.TotalMilliseconds;
                UpdateFps();
            }
        }
    }

    private void UpdateFps()
    {
        long now = Environment.TickCount64;
        if (_fpsWindowStart == 0)
            _fpsWindowStart = now;

        long elapsed = now - _fpsWindowStart;
        if (elapsed >= 1000)
        {
            _fps = _framesThisSecond * 1000.0 / elapsed;
            _framesThisSecond = 0;
            _fpsWindowStart = now;
        }
    }

    private async Task RunTestScanAsync(Mat frame)
    {
        var report = await _analyzer.TestScanAsync(frame);
        _scanOverlays = report.Overlays;
        _scanSummary = $"{DateTime.Now:HH:mm:ss} - {report.Details.Count} finding(s)";

        EmitLog(LogLevel.Info, "=== test scan ===");
        foreach (string detail in report.Details)
            EmitLog(LogLevel.Trace, detail);
        EmitLog(LogLevel.Info, "=== end of test scan ===");
    }

    private void SaveSnapshot(Mat frame)
    {
        try
        {
            var overlays = _pilot.IsRunning ? _pilot.Overlays : _scanOverlays;
            string file = DebugArtifacts.SaveFrame(frame, overlays, "manual", null);
            if (string.IsNullOrEmpty(file))
                EmitLog(LogLevel.Warning, "Snapshot could not be written.");
            else
                EmitLog(LogLevel.Success, $"Snapshot saved: {file}");
        }
        catch (Exception ex)
        {
            EmitLog(LogLevel.Warning, $"Snapshot failed: {ex.Message}");
        }
    }

    private void PublishPreview(Mat frame, CaptureFrame capture)
    {
        if (!_settings.Ui.ShowPreview)
            return;

        int previewInterval = Math.Max(1, 1000 / Math.Clamp(_settings.Ui.PreviewFps, 1, 60));
        long now = Environment.TickCount64;
        if (now - _lastPreviewTicks < previewInterval)
            return;

        _lastPreviewTicks = now;

        try
        {
            double scale = Math.Min(1.0, PreviewMaxWidth / (double)frame.Width);
            using var small = new Mat();
            if (scale < 1.0)
            {
                CvInvoke.Resize(frame, small, new Size((int)Math.Round(frame.Width * scale), (int)Math.Round(frame.Height * scale)), 0, 0, Inter.Area);
            }
            else
            {
                frame.CopyTo(small);
                scale = 1.0;
            }

            var overlays = _pilot.IsRunning ? _pilot.Overlays
                : _autoConfirm.Overlays.Count > 0 ? _scanOverlays.Concat(_autoConfirm.Overlays).ToList()
                : _scanOverlays;
            (int X, int Y)? click = null;
            if ((_pilot.IsRunning ? _pilot.LastClick : _autoConfirm.LastClick) is { } last && now - last.Ticks < 2000)
                click = (last.X, last.Y);

            OverlayRenderer.Draw(small, overlays, click, scale);

            var bitmap = ToBitmapSource(small);
            string caption = $"{capture.Width}x{capture.Height} at ({capture.ScreenLeft},{capture.ScreenTop})  |  frame #{capture.Index}  |  {_fps:0.0} fps  |  loop {_lastFrameMs:0.0} ms";
            Preview?.Invoke(new PreviewUpdate(bitmap, caption));
        }
        catch (Exception ex)
        {
            EmitLog(LogLevel.Trace, $"Preview failed: {ex.Message}");
        }
    }

    private static BitmapSource ToBitmapSource(Mat bgra)
    {
        int stride = bgra.Width * 4;
        var bytes = new byte[stride * bgra.Height];

        // Read out of the Mat (never written into through managed code, CLAUDE.md section 10).
        if (bgra.IsContinuous)
        {
            Marshal.Copy(bgra.DataPointer, bytes, 0, bytes.Length);
        }
        else
        {
            int height = bgra.Height;
            long step = bgra.Step;
            for (int y = 0; y < height; y++)
                Marshal.Copy(bgra.DataPointer + (int)(y * step), bytes, y * stride, stride);
        }

        var bitmap = BitmapSource.Create(bgra.Width, bgra.Height, 96, 96, PixelFormats.Bgra32, null, bytes, stride);
        bitmap.Freeze();
        return bitmap;
    }

    private void EmitLog(LogLevel level, string message) => Log?.Invoke(new LogEntry(level, message));

    private static string Truncate(string value, int length)
        => value.Length <= length ? value : value[..length] + "...";

    public async ValueTask DisposeAsync()
    {
        Unbind();
        _analyzer.Dispose();
        _capture.Dispose();
        await Task.CompletedTask;
    }
}

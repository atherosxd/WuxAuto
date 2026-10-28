using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WuxAuto.Automation;
using WuxAuto.Core;
using WuxAuto.Engine;
using WuxAuto.Interop;

namespace WuxAuto.ViewModels;

public partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private const int MaxLogEntries = 600;

    /// <summary>One file per app start, so a run can be read back after the window is closed.</summary>
    private readonly string _logFile = Path.Combine(AppPaths.LogFolder, $"wuxauto_{DateTime.Now:yyyyMMdd_HHmmss}.log");

    private readonly Dispatcher _dispatcher;
    private readonly WuxEngine _engine;
    private readonly DispatcherTimer _statusTimer;
    private readonly KillAlert _killAlert = new();
    private bool _chimeWarned;
    private bool _notificationWarned;

    [ObservableProperty]
    private WindowInfo? _selectedWindow;

    [ObservableProperty]
    private BitmapSource? _previewImage;

    [ObservableProperty]
    private string _previewCaption = "Bind a window to start the live Desktop Duplication feed.";

    [ObservableProperty]
    private string _bindStatus = "No window bound.";

    [ObservableProperty]
    private string _stateText = "Idle";

    [ObservableProperty]
    private string _statText = string.Empty;

    /// <summary>The last kill alert ("14:32:05 Stage 2: Spectral down - check the loot"); empty before the first.</summary>
    [ObservableProperty]
    private string _lastKillText = string.Empty;

    [ObservableProperty]
    private bool _isBound;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _scanSummary = string.Empty;

    [ObservableProperty]
    private string _ocrStatus = string.Empty;

    public MainViewModel()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        Settings = SettingsStore.Load();
        WarBuffSwitches = WarBuffSwitch.For(Settings);
        _engine = new WuxEngine(Settings);

        _engine.Log += entry => Post(() => AppendLog(entry));
        _engine.Preview += update => Post(() =>
        {
            PreviewImage = update.Image;
            PreviewCaption = update.Caption;
        });
        _engine.StateChanged += () => Post(SyncState);
        // The mode buttons and Auto War's "Back to:" line bind through Settings: re-read them when the pilot
        // switches the mode or remembers the cave.
        _engine.ModeChanged += () => Post(() => OnPropertyChanged(nameof(Settings)));
        _engine.BossKilled += what => Post(() => OnBossKilled(what));
        _engine.Faulted += message => Post(() =>
        {
            AppendLog(new LogEntry(LogLevel.Error, $"Capture faulted: {message}"));
            SyncState();
        });

        _statusTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(750),
        };
        _statusTimer.Tick += (_, _) => TickStatus();
        _statusTimer.Start();

        RefreshWindows();
        OcrStatus = _engine.OcrStatus;
        ScanSummary = $"templates: {string.Join(", ", _engine.Templates.Names)}";

        AppendLog(new LogEntry(LogLevel.Info, "WuxAuto ready. 1) refresh + bind a window  2) Start (F9)  3) Stop (F9) / panic (F10)."));
        AppendLog(new LogEntry(LogLevel.Trace, $"Settings: {AppPaths.SettingsFile}"));
        AppendLog(new LogEntry(LogLevel.Trace, $"Debug snapshots: {AppPaths.DebugFolder}"));
        AppendLog(new LogEntry(LogLevel.Trace, $"Kill screenshots: {AppPaths.KillsFolder}"));
        AppendLog(new LogEntry(LogLevel.Trace, $"Extra templates: {AppPaths.TemplateFolder}"));
        AppendLog(new LogEntry(LogLevel.Trace, $"Log file: {_logFile}"));

        bool autostart = Environment.GetCommandLineArgs().Contains("--autostart", StringComparer.OrdinalIgnoreCase);
        if (autostart)
            _dispatcher.BeginInvoke(AutoStartAsync);

        // After the window is up; --autostart runs unattended, so it only logs a new version.
        _dispatcher.BeginInvoke(async () =>
        {
            await Task.Delay(1500);
            await CheckForUpdatesAsync(startup: true, ask: !autostart);
        }, DispatcherPriority.ApplicationIdle);
    }

    public string VersionText { get; } = $"v{UpdateService.Format(UpdateService.CurrentVersion)}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUpdateAvailable))]
    private UpdateInfo? _availableUpdate;

    public bool IsUpdateAvailable => AvailableUpdate != null;

    [ObservableProperty]
    private string _updateStatus = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckForUpdatesCommand), nameof(InstallUpdateCommand))]
    private bool _isUpdating;

    [RelayCommand(CanExecute = nameof(CanUseUpdater))]
    private Task CheckForUpdates() => CheckForUpdatesAsync(startup: false, ask: true);

    [RelayCommand(CanExecute = nameof(CanUseUpdater))]
    private Task InstallUpdate() => AvailableUpdate == null ? Task.CompletedTask : InstallUpdateAsync(AvailableUpdate);

    private bool CanUseUpdater() => !IsUpdating;

    /// <summary>
    /// Asks GitHub for the newest release. On start-up a missing connection is only a log line, and a
    /// copy that cannot update itself (a build folder) does not ask.
    /// </summary>
    private async Task CheckForUpdatesAsync(bool startup, bool ask)
    {
        if (IsUpdating)
            return;

        IsUpdating = true;
        UpdateStatus = "Checking for updates...";
        UpdateInfo? latest;
        try
        {
            latest = await UpdateService.GetLatestAsync();
        }
        catch (Exception ex)
        {
            UpdateStatus = "Update check failed (no internet?).";
            AppendLog(new LogEntry(startup ? LogLevel.Trace : LogLevel.Warning, $"Update check failed: {ex.GetBaseException().Message}"));
            return;
        }
        finally
        {
            IsUpdating = false;
        }

        if (latest == null || !UpdateService.IsNewer(latest))
        {
            AvailableUpdate = null;
            UpdateStatus = $"Up to date ({VersionText}).";
            if (!startup)
                AppendLog(new LogEntry(LogLevel.Info, latest == null ? "No release published yet." : $"WuxAuto is up to date ({VersionText})."));
            return;
        }

        AvailableUpdate = latest;
        UpdateStatus = $"Update v{latest.VersionText} available.";
        AppendLog(new LogEntry(LogLevel.Success, $"Update available: v{latest.VersionText} (this copy is {VersionText}). {latest.PageUrl}"));

        string? blocked = UpdateService.CannotInstallReason();
        if (blocked != null)
        {
            AppendLog(new LogEntry(LogLevel.Info, blocked));
            if (startup)
                return;
        }

        if (!ask || IsRunning)
            return;

        string notes = string.IsNullOrWhiteSpace(latest.Notes) ? string.Empty : $"\n\nWhat's new:\n{Shorten(latest.Notes, 700)}";
        var answer = MessageBox.Show(
            $"WuxAuto v{latest.VersionText} is available (you have {VersionText}).{notes}\n\n" +
            "Install it now? WuxAuto closes, updates and opens again by itself. Your settings are kept.",
            "WuxAuto update",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);

        if (answer == MessageBoxResult.Yes)
            await InstallUpdateAsync(latest);
    }

    private async Task InstallUpdateAsync(UpdateInfo update)
    {
        if (IsRunning)
        {
            MessageBox.Show("Stop the pilot first, then install the update.", "WuxAuto update", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string? blocked = UpdateService.CannotInstallReason();
        if (blocked != null)
        {
            MessageBox.Show($"{blocked}\n\nDownload v{update.VersionText} by hand:\n{update.PageUrl}", "WuxAuto update", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        IsUpdating = true;
        try
        {
            AppendLog(new LogEntry(LogLevel.Info, $"Downloading v{update.VersionText} ({update.ZipSize / 1048576.0:0} MB)..."));
            var progress = new Progress<double>(p => UpdateStatus = $"Downloading v{update.VersionText}: {p:P0}");
            string files = await UpdateService.DownloadAsync(update, progress);

            string log = UpdateService.StartInstaller(files, AppContext.BaseDirectory, Environment.ProcessId, restart: true);
            AppendLog(new LogEntry(LogLevel.Success, $"v{update.VersionText} downloaded - closing to install it (log: {log})."));
            UpdateStatus = "Installing - WuxAuto restarts by itself.";

            // Closing the main window disposes the engine and saves the settings first.
            Application.Current.MainWindow?.Close();
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            IsUpdating = false;
            UpdateStatus = "Update failed.";
            AppendLog(new LogEntry(LogLevel.Error, $"Update failed: {ex.GetBaseException().Message}"));
            MessageBox.Show($"The update failed:\n\n{ex.GetBaseException().Message}\n\nYou can download it by hand:\n{update.PageUrl}",
                "WuxAuto update", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..max].TrimEnd() + " ...";

    /// <summary>
    /// <c>--autostart</c>: bind the detected game window and press Start, so a run can be
    /// launched (and followed through the log file) without clicking through the UI.
    /// </summary>
    private async Task AutoStartAsync()
    {
        if (SelectedWindow?.LooksLikeGame != true)
        {
            AppendLog(new LogEntry(LogLevel.Warning, "--autostart: no game window found; bind and start by hand."));
            return;
        }

        AppendLog(new LogEntry(LogLevel.Info, $"--autostart: binding {SelectedWindow.ShortDisplay} and starting."));
        await BindAsync();
        if (!IsBound)
            return;

        // Give the capture a moment to deliver the first frames before the pilot ticks.
        await Task.Delay(1500);

        // --phase FaceNorth (or any PilotPhase) skips straight to that part of the run.
        var args = Environment.GetCommandLineArgs();
        int index = Array.FindIndex(args, a => a.Equals("--phase", StringComparison.OrdinalIgnoreCase));
        if (index >= 0 && index + 1 < args.Length && Enum.TryParse<PilotPhase>(args[index + 1], true, out var phase))
        {
            _engine.Start(phase);
            SyncState();
            return;
        }

        Start();
    }

    public AppSettings Settings { get; }

    /// <summary>Auto War's "Buffs" row: one check box per buff icon the pilot clicks (<see cref="WarBuffSwitch"/>).</summary>
    public IReadOnlyList<WarBuffSwitch> WarBuffSwitches { get; }

    /// <summary>Re-reads every settings binding (the settings objects raise no change events).</summary>
    public void RefreshSettings() => OnPropertyChanged(nameof(Settings));

    public ObservableCollection<WindowInfo> Windows { get; } = new();

    public ObservableCollection<LogEntry> LogEntries { get; } = new();

    public event Action? LogAppended;

    public AutomationPilot Pilot => _engine.Pilot;

    private void Post(Action action)
    {
        if (_dispatcher.CheckAccess())
            action();
        else
            _dispatcher.BeginInvoke(action);
    }

    private void AppendLog(LogEntry entry)
    {
        try
        {
            File.AppendAllText(_logFile, entry + Environment.NewLine);
        }
        catch
        {
            // The log file is a convenience; the log pane still has everything.
        }

        LogEntries.Add(entry);
        while (LogEntries.Count > MaxLogEntries)
            LogEntries.RemoveAt(0);

        LogAppended?.Invoke();
    }

    /// <summary>
    /// Kill alert (user, 2026-10-26: a reminder to check the loot, for friends running the pilot
    /// on a second PC): the chime and the Windows notification, each if ticked.
    /// </summary>
    private void OnBossKilled(string what)
    {
        var time = DateTime.Now;
        LastKillText = $"{time:HH:mm:ss} {what} - check the loot";
        _ = AlertAsync(what, time, test: false);
    }

    /// <summary>"Test alert": the chime and the notification as on a kill, so the sound can be checked on this PC.</summary>
    [RelayCommand]
    private Task TestKillAlert()
    {
        var sequence = Settings.Sequence;
        if (!sequence.KillChime && !sequence.KillNotification)
        {
            AppendLog(new LogEntry(LogLevel.Warning, "Kill alert test: 'Kill chime' and 'Kill notification' are both off."));
            return Task.CompletedTask;
        }

        return AlertAsync("Test", DateTime.Now, test: true);
    }

    private async Task AlertAsync(string what, DateTime time, bool test)
    {
        var sequence = Settings.Sequence;
        // A sound file can take a moment to open the first time: the notification does not wait for it.
        var chime = sequence.KillChime ? _killAlert.PlayChimeAsync(sequence.KillSound) : null;
        if (sequence.KillNotification)
            Report(_killAlert.Notify("WuxAuto: boss down", $"{what} - check the loot ({time:HH:mm})"), "notification", ref _notificationWarned, test);
        if (chime != null)
        {
            string error = await chime;
            Report(error, "chime", ref _chimeWarned, test);
        }
    }

    /// <summary><c>Settings.Sequence.KillSound</c>, the kill chime's sound file, with change notification for the UI.</summary>
    public string KillSound
    {
        get => Settings.Sequence.KillSound;
        set
        {
            if (value == Settings.Sequence.KillSound)
                return;

            Settings.Sequence.KillSound = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(KillSoundName));
            OnPropertyChanged(nameof(KillSoundPath));
            OnPropertyChanged(nameof(HasKillSound));
        }
    }

    /// <summary>What the kill chime plays, shown next to "Choose..." (checked when the window opens and on every change).</summary>
    public string KillSoundName =>
        HasKillSound ? Path.GetFileName(Settings.Sequence.KillSound) + (File.Exists(Settings.Sequence.KillSound) ? string.Empty : " (missing)")
        : File.Exists(KillAlert.CustomChimeFile) ? $"{KillAlert.CustomChimeName} (templates folder)"
        : "built-in chime";

    /// <summary>The kill chime's sound file (tooltip of <see cref="KillSoundName"/>).</summary>
    public string KillSoundPath =>
        HasKillSound ? Settings.Sequence.KillSound
        : File.Exists(KillAlert.CustomChimeFile) ? KillAlert.CustomChimeFile
        : "The built-in three-note chime.";

    /// <summary>A sound file was picked for the kill chime.</summary>
    public bool HasKillSound => !string.IsNullOrEmpty(Settings.Sequence.KillSound);

    /// <summary>
    /// "Choose...": an MP3, WAV or OGG for the kill chime (user, 2026-10-27). It is copied into the
    /// sounds folder (an OGG as a WAV), checked, saved in the settings at once and played once so it
    /// can be heard. A file that does not play leaves the current sound as it was.
    /// </summary>
    [RelayCommand]
    private async Task ChooseKillSound()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Kill chime sound",
            Filter = "Sound files (*.mp3;*.wav;*.ogg)|*.mp3;*.wav;*.ogg",
        };
        if (dialog.ShowDialog() != true)
            return;

        string picked = Path.GetFileName(dialog.FileName);
        var (file, length, error) = await KillAlert.ImportSoundAsync(dialog.FileName);
        if (error.Length > 0)
        {
            AppendLog(new LogEntry(LogLevel.Warning, $"Kill chime sound: {picked} cannot be played ({error}) - the sound stays {KillSoundName}."));
            return;
        }

        KillSound = file;
        SettingsStore.Save(Settings);
        string from = OggToWav.IsOgg(picked) ? $", converted from {picked}" : string.Empty;
        AppendLog(new LogEntry(LogLevel.Success, $"Kill chime sound: {Path.GetFileName(file)} ({length.TotalSeconds:0.0} s{from}), kept in {AppPaths.SoundsFolder}."));
        if (length > TimeSpan.FromSeconds(10))
            AppendLog(new LogEntry(LogLevel.Info, $"It plays in full on every kill ({length.TotalSeconds:0} s) - a short sound works best."));

        string played = await _killAlert.PlayChimeAsync(file);
        if (played.Length > 0)
            AppendLog(new LogEntry(LogLevel.Warning, $"Kill chime: {played}"));
    }

    /// <summary>"Default": back to the built-in chime (or chime.wav in the templates folder). The copy stays in the sounds folder.</summary>
    [RelayCommand]
    private void ResetKillSound()
    {
        KillSound = string.Empty;
        SettingsStore.Save(Settings);
        AppendLog(new LogEntry(LogLevel.Info, $"Kill chime sound: {KillSoundName}."));
    }

    /// <summary>
    /// A failed alert is logged once per app start, not on every kill; the test button always
    /// says how it went.
    /// </summary>
    private void Report(string error, string what, ref bool warned, bool test)
    {
        if (error.Length == 0)
        {
            if (test)
                AppendLog(new LogEntry(LogLevel.Success, $"Kill alert test: {what} OK."));
            return;
        }

        if (warned && !test)
            return;

        warned = true;
        AppendLog(new LogEntry(LogLevel.Warning, $"Kill {what}: {error}"));
    }

    [RelayCommand]
    private void RefreshWindows()
    {
        var windows = WindowEnumerator.Enumerate();
        Windows.Clear();
        foreach (var window in windows)
            Windows.Add(window);

        var preferred = Windows.FirstOrDefault(w => w.LooksLikeGame) ?? Windows.FirstOrDefault();
        if (preferred != null)
            SelectedWindow = preferred;

        AppendLog(new LogEntry(LogLevel.Info, $"Found {Windows.Count} window(s){(preferred?.LooksLikeGame == true ? $", defaulting to {preferred.ShortDisplay}" : string.Empty)}."));
    }

    [RelayCommand]
    private async Task BindAsync()
    {
        if (IsBound)
        {
            _engine.Unbind();
            SyncState();
            return;
        }

        if (SelectedWindow == null)
        {
            AppendLog(new LogEntry(LogLevel.Warning, "Select a window first."));
            return;
        }

        if (!WindowEnumerator.IsWindow(SelectedWindow.Handle))
        {
            AppendLog(new LogEntry(LogLevel.Error, "That window has closed. Refresh the list."));
            return;
        }

        try
        {
            await _engine.BindAsync(SelectedWindow);
        }
        catch (Exception ex)
        {
            AppendLog(new LogEntry(LogLevel.Error, $"Could not start capture: {ex.Message}"));
            AppendLog(new LogEntry(LogLevel.Info, "Desktop Duplication cannot see exclusive full-screen or protected windows. Run the game in borderless/windowed mode."));
        }

        SyncState();
    }

    [RelayCommand]
    private void Start()
    {
        _engine.Start();
        SyncState();
    }

    [RelayCommand]
    private void Stop()
    {
        _engine.StopPilot();
        SyncState();
    }

    [RelayCommand]
    private void Panic()
    {
        _engine.StopPilot("panic key");
        _engine.Unbind();
        SyncState();
    }

    [RelayCommand]
    private void ToggleStart()
    {
        if (_engine.IsRunning)
            Stop();
        else
            Start();
    }

    [RelayCommand]
    private void TestScan()
    {
        _engine.RequestTestScan();
        AppendLog(new LogEntry(LogLevel.Info, "Test scan requested."));
    }

    [RelayCommand]
    private void Snapshot() => _engine.RequestSnapshot();

    [RelayCommand]
    private void FocusGame() => _engine.FocusGame();

    [RelayCommand]
    private void SaveSettings()
    {
        SettingsStore.Save(Settings);
        SaveSettingsState = $"Settings saved {DateTime.Now:HH:mm:ss}";
        AppendLog(new LogEntry(LogLevel.Success, $"Settings written to {AppPaths.SettingsFile}"));
    }

    [ObservableProperty]
    private string _saveSettingsState = string.Empty;

    [RelayCommand]
    private void ReloadTemplates()
    {
        _engine.ReloadTemplates();
        ScanSummary = $"templates: {string.Join(", ", _engine.Templates.Names)}";
        AppendLog(new LogEntry(LogLevel.Info, $"Template references reloaded: {ScanSummary}"));
    }

    [RelayCommand]
    private void ClearLog()
    {
        LogEntries.Clear();
        LogAppended?.Invoke();
    }

    [RelayCommand]
    private void OpenDebugFolder() => OpenFolder(AppPaths.DebugFolder);

    [RelayCommand]
    private void OpenKillsFolder() => OpenFolder(AppPaths.KillsFolder);

    [RelayCommand]
    private void OpenTemplatesFolder() => OpenFolder(AppPaths.TemplateFolder);

    private static void OpenFolder(string path)
    {
        try
        {
            AppPaths.EnsureCreated();
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch
        {
            // ignored
        }
    }

    private void TickStatus()
    {
        if (_engine.IsBound)
        {
            var window = _engine.BoundWindow;
            if (window != null && !WindowEnumerator.ProcessIsAlive(window.Handle, window.ProcessId))
            {
                AppendLog(new LogEntry(LogLevel.Warning, "The bound window is gone; unbinding."));
                _engine.Unbind();
            }
            else
            {
                _engine.ApplyRuntimeSettings();
            }
        }

        StatText = $"fps {_engine.Fps:0.0}  |  loop {_engine.LastFrameMs:0.0} ms  |  ocr {_engine.LastOcrMs:0} ms";
        OcrStatus = _engine.OcrStatus;
        SyncState();
    }

    private void SyncState()
    {
        IsBound = _engine.IsBound;
        IsRunning = _engine.IsRunning;

        var window = _engine.BoundWindow;
        BindStatus = window == null
            ? "No window bound."
            : $"Bound: {window.ShortDisplay}  {window.ClientWidth}x{window.ClientHeight}\n{window.Title}";

        if (_engine.IsRunning)
        {
            string detail = _engine.Pilot.Detail;
            string phase = string.IsNullOrEmpty(detail)
                ? _engine.Pilot.PhaseText
                : $"{_engine.Pilot.PhaseText} - {detail}";
            StateText = Settings.Sequence.DryRun ? $"DRY RUN (no input sent) | {phase}" : phase;
        }
        else
        {
            StateText = _engine.Pilot.Phase switch
            {
                PilotPhase.Done => "Sequence complete.",
                PilotPhase.Failed => $"Failed - {_engine.Pilot.Detail}",
                _ => "Idle",
            };
        }

        ScanSummary = _engine.ScanSummary;
    }

    public async ValueTask DisposeAsync()
    {
        _statusTimer.Stop();
        SettingsStore.Save(Settings);
        await _engine.DisposeAsync();
        _killAlert.Dispose();
    }
}

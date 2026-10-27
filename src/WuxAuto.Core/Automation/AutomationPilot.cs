using System.Diagnostics;
using OpenCvSharp;
using WuxAuto.Core;
using WuxAuto.Input;
using WuxAuto.Vision;
using LogLevel = WuxAuto.Core.LogLevel;

namespace WuxAuto.Automation;

public enum PilotPhase
{
    Idle,
    Focus,
    CheckZone,
    AutohuntOff,
    RunSkill,
    FontSize,
    OpenInventory,
    FindScroll,
    FindChevron,
    FindDestination,
    WaitForLoad,
    WaitForGate,
    ClickMap,
    ReadStage,
    AdvanceStage,
    WaitBoss,
    SelectBoss,
    WatchBoss,
    FaceNorth,
    ZoomOut,
    AimGate,
    RunToTarget,
    ReturnToTown,
    Reconnect,
    AutoWar,
    LodRun,
    LodAttack,
    LodApproach,
    LodRespawn,
    LodLoot,
    Done,
    Failed,
}

/// <summary>
/// The teleport-to-Labyrinth script. Runs as an incremental state machine that is driven once
/// per captured frame from the vision loop, so the UI keeps updating and Stop takes effect
/// within one frame.
/// </summary>
public sealed partial class AutomationPilot
{
    private readonly AppSettings _settings;
    private readonly ScreenAnalyzer _analyzer;
    private readonly IGameInput _input;
    private readonly List<OverlayItem> _overlays = new();

    private PilotPhase _phase = PilotPhase.Idle;
    private long _waitUntil;
    private long _phaseStart;
    private long _runStart;
    private int _attempts;
    private int _runCount;
    private string _detail = string.Empty;

    private (int X, int Y)? _anchor;
    private (int X, int Y, long Ticks)? _lastClick;

    private ulong _preLoadHash;
    private ulong _lastHash;
    private int _stableSamples;

    private double _turnRate;

    private int _pages;
    private bool _chevronMissing;
    private bool _pagingBack;

    // Keys found used up this Start (by the teleport, not the item list).
    private bool _labyOut;
    private bool _lodOut;
    private long _townSeenAfterTeleport = -1; // _phaseStart of the ZoomOut that saw the town
    private readonly List<long> _restartTimes = new();
    private int _backPages;

    // ---------------------------------------------------------------- dungeon loop
    private int _stageIndex;
    private long _sweepStartedAt;
    private bool _sweepDone;
    private int _mapScrolls;
    private int _mapReopens;
    private int _mapVisibleMisses;
    private bool _noPathLogged;
    private int _rowSnapshots;
    private bool _chevronClicked;
    private int _rowNameMisses;              // AdvanceStage: ticks with the map open but the row name unread
    private int _gatePolls;
    private bool _gateWarned;
    private bool _tabPressed;
    private long _lastWaitTabAt;
    private long _nextCancelDialogWarnAt;
    private int _waitTabs;
    private bool _autohuntPressed;
    private long? _bossFirstSeenAt;
    private long? _bossMissingSince;
    private long _lastWatchTabAt;
    private int _watchTabs;
    private (int X, int Y)? _lastCoords;
    private bool _runUpProbed;
    private bool _runRightProbed;
    private RunProbe _probe = RunProbe.None;
    private int _stalledSteps;
    private int _runSteps;
    private int _waypoint;
    private bool _gateTilted;
    private bool _gateFlatChecked;
    private int _gateStillReadings;
    private bool _earlyNorth;
    private bool _arrivalConfirmed;
    private int _anchorTries;
    private bool _gateWalking;

    // Least-squares fit of "click offset (right, up in RunClickPx steps) -> world movement" over
    // every run step: S_oo = sum(o o^T), S_do = sum(d o^T). Two probes seed it; each steering step
    // refines it, so a skewed probe (measured while still walking) no longer pins the clicks.
    private double _sRR, _sRU, _sUU, _sXR, _sXU, _sYR, _sYU;

    // The two probes, kept at full weight: with only "straight up" steps afterwards the right-hand
    // information faded away (x0.9 per step) and the solve turned to garbage (bottom-left clicks
    // for a target straight north, run 19).
    private double _pRR, _pRU, _pUU, _pXR, _pXU, _pYR, _pYU;
    private int _fitSamples;
    private (double Right, double Up) _lastOffset;
    private (int X, int Y)? _jumpCandidate;
    private int _jumpAgreements;
    private bool _returning;
    private long _nextDisconnectCheckAt;
    private long _nextZoneCheckAt;

    // SimpleSteer: the last click direction (screen px from the character) and the smoothed angle
    // from screen direction to world movement (radians, + = clockwise on screen).
    private (double X, double Y)? _simpleClick;
    private double _headingError;

    // Facing west for the last stretch: the turn was asked for / is done; the heading the
    // steering expects (0 = north, -pi/2 = west).
    private bool _turnWest;
    private bool _fromWaypoint;
    private bool _runToTargetPending;

    private long? _waypointGateAt;
    private int _waypointGatePolls;
    private int _manualPresses = -1;    // -1 = not attacking by hand yet
    private bool _manualHit;
    private long? _holdUntil;                // a held skill press (stage 5's first manual attack, stages 1-4 before J) is let go at this time
    private bool _heldBeforeHunt;            // stages 1-4: the held skill press before J is done (HoldBeforeAutohunt)
    private bool _huntOn;                    // stage 5: autohunt on, clicking round the centre until a new attack line shows
    private long _huntOnAt;
    private long? _huntBaselineAt;           // stage 5: the attack log re-read AutohuntSettleMs after J (the manual attacks' late hits are gone by then)
    private int _huntClicks;                 // stage 5: centre clicks after J (J drops the target, user 2026-10-27)
    private long? _huntClickAt;
    private string? _watchAttackLog;         // stage 5 WatchBoss: last combat-log state, for the "autohunt on but not attacking" watchdog
    private long _watchAttackAt;
    private long _lastManualPressAt;
    private int _manualRounds;               // stage 5: rounds of manual attacks (hold + taps) that ended without a new attack line
    private int _stuckShots;                 // nohit_s5 / stall_s5 snapshots this Start (<= 3)
    private bool _finalHuntOffChecked;       // stage 5: the autohunt was read off in the combat log on this arrival
    private bool _westDone;
    private double _headingBase;
    private int _headingSamples;
    private long _zoneConfirmedAt;
    private int _otherZoneReads;
    private bool _zoneHoldLogged;
    private long? _zoneHoldSince;
    private long _reconnectStartedAt;
    private int _reconnectClicks;
    private bool _startGameClicked;
    private long _startGameClickedAt;
    private long _charScreenSince;      // character screen up without a readable START GAME (0 = no)
    private bool _startGameUnreadSaved;
    private long _lastOkRetryAt;

    /// <summary>A normalized OCR line that is the START GAME button: exact or 1-2 letters off.</summary>
    private static bool IsStartGame(string text)
        => text == "startgame" || (text.Length is >= 7 and <= 11 && TextMatcher.Levenshtein(text, "startgame") <= 2);
    private int _reconnectStep;
    private string _lastReconnectSeen = string.Empty;
    private bool _autohuntOff;
    private bool _positionsResetPending;
    private int _selectTries;
    private bool _selectBaselineTaken;
    private string _attackLog = string.Empty;
    private long? _selectClickedAt;
    private long _nextBossCheckAt;
    private int _watchPolls;
    private int _watchGateLines = -1;
    private bool _compassAnchored;
    private double _bestFlashlightScore;
    private int _bestFlashlightStep;
    private readonly List<int> _tapsSinceBest = new();

    // Fine search after the coarse peak: 0 = not started, 1/2 = trying RotateKey / RotateLeftKey.
    // Where the Labyrinth teleport puts the character (logged on every arrival).
    private static readonly System.Text.RegularExpressions.Regex LagPing = new(@"(^|[^\d\s])\s*ms\b");

    private const int ArrivalX = 3595;
    private const int ArrivalY = 3415;

    private bool _fontChecked;
    private bool _runSkillDue;                // RunSkill: in town this run, not checked yet
    private int _runSkillClicks;              // RunSkill: clicks on the icon this visit (<= 3)
    private bool _runKeyPressed;              // RunSkill: the fallback key went out this Start (a toggle - never twice)
    private int _huntOffPresses;              // AutohuntOff: taps of the autohunt key this visit (<= 3)
    private long _huntOffPressedAt;
    private List<string> _huntOffBaseline = [];   // autohunt log lines before the last tap
    private string? _homeTown;
    private int _fontStep;
    private int _fontClicks;
    private int _fontTabClicks;
    private bool _fontChanged;
    private int _fontGoal;
    private PixelRect _visualTab;     // the VISUAL tab's word: the wheel scrolls the list below it
    private readonly List<string> _settingsChanges = new();
    private int _tabClicks;
    private int _tabScrolls;
    private bool _tabChanged;
    private int _refineDir;
    private double _refineBest;
    private int _refineTaps;
    private int _refineGained;
    private bool _refinePending;

    public AutomationPilot(AppSettings settings, ScreenAnalyzer analyzer, IGameInput input)
    {
        _settings = settings;
        _analyzer = analyzer;
        _input = input;
    }

    public event Action<LogLevel, string>? Log;
    public event Action? StateChanged;

    /// <summary>The pilot changed the run mode itself (LOD out of entries -> Auto Laby).</summary>
    public event Action? ModeChanged;

    public bool IsRunning { get; private set; }
    public PilotPhase Phase => _phase;
    public IReadOnlyList<OverlayItem> Overlays => _overlays;
    public (int X, int Y, long Ticks)? LastClick => _lastClick;
    public string Detail => _detail;
    public int RunCount => _runCount;

    public string PhaseText => _phase switch
    {
        PilotPhase.Idle => "idle",
        PilotPhase.Focus => "focussing game window",
        PilotPhase.CheckZone => "checking where the character is",
        PilotPhase.AutohuntOff => "making sure the autohunt is off in town",
        PilotPhase.RunSkill => "making sure the Run skill is on",
        PilotPhase.FontSize => "checking the game's font size and TAB target",
        PilotPhase.OpenInventory => "opening inventory",
        PilotPhase.FindScroll => "locating teleport scroll",
        PilotPhase.FindChevron => "clicking next page chevron",
        PilotPhase.FindDestination => $"finding '{CurrentDestination}'",
        PilotPhase.WaitForLoad => "waiting for zone load",
        PilotPhase.WaitForGate => $"waiting for the gate (stage {_stageIndex + 1})",
        PilotPhase.ClickMap => "clicking the map button",
        PilotPhase.ReadStage => $"reading the map (stage {_stageIndex + 1})",
        PilotPhase.AdvanceStage => "clicking the next-stage chevron",
        PilotPhase.WaitBoss => $"waiting for the boss (stage {_stageIndex + 1})",
        PilotPhase.SelectBoss => $"selecting the boss (stage {_stageIndex + 1})",
        PilotPhase.WatchBoss => $"watching the boss (stage {_stageIndex + 1})",
        PilotPhase.FaceNorth => "lining the searchlight up",
        PilotPhase.ZoomOut => "zooming out",
        PilotPhase.AimGate => "tilting the camera, looking for the gate",
        PilotPhase.RunToTarget => "running to the staging point",
        PilotPhase.LodRun => "walking to the LOD boss, pressing Tab",
        PilotPhase.LodAttack => "spamming the skill on the LOD boss",
        PilotPhase.LodApproach => "walking up to the LOD boss (Ctrl+click on its ring)",
        PilotPhase.LodRespawn => "back at the start - settling",
        PilotPhase.LodLoot => "looting the LOD boss",
        PilotPhase.ReturnToTown => "autohunt off, returning to town",
        PilotPhase.Reconnect => "disconnected - logging back in",
        PilotPhase.AutoWar => "turning the auto war on",
        PilotPhase.Done => "sequence complete",
        PilotPhase.Failed => "failed",
        _ => "idle",
    };

    /// <param name="startAt">Debug aid: start at a later phase (e.g. FaceNorth after the last boss)
    /// instead of the teleport. Every stage is treated as done.</param>
    public void Start(PilotPhase startAt = PilotPhase.Focus)
    {
        _runCount = 0;
        _fontChecked = false;
        _runKeyPressed = false;
        _labyOut = false;
        _lodOut = false;
        _restartTimes.Clear();
        _homeTown = null;
        _lodKills = 0;
        _lodBuffAt = 0;
        _lodBuffs = 0;
        _lodApproachSnapshots = 0;
        _fontStep = 0;
        _runStart = Environment.TickCount64;
        ResetRunState();
        IsRunning = true;
        _phase = startAt;
        if (startAt != PilotPhase.Focus)
        {
            _stageIndex = _settings.Dungeon.Stages.Count;
            // The left sweep assumes the heading the labyrinth loads with; mid-run it would only
            // overshoot, so a debug start fine-tunes from wherever the camera is.
            _sweepDone = true;
            _input.TryFocus();
            Log?.Invoke(LogLevel.Warning, $"Starting at {startAt} (all stages treated as done).");
        }

        _phaseStart = Environment.TickCount64;
        _waitUntil = _phaseStart;
        _detail = string.Empty;
        Log?.Invoke(_settings.Sequence.DryRun ? LogLevel.Warning : LogLevel.Info, _settings.Sequence.DryRun
            ? "Pilot started in DRY RUN mode - detections run, NO input is sent to the game. Untick 'Dry run' to control the game."
            : "Pilot started. F10 aborts immediately.");
        RaiseState();
    }

    public void Stop(string reason)
    {
        if (!IsRunning && _phase is PilotPhase.Idle)
            return;

        IsRunning = false;
        ReleaseSweepKey();
        _phase = reason.Contains("complete", StringComparison.OrdinalIgnoreCase) ? PilotPhase.Done : PilotPhase.Idle;
        _waitUntil = 0;
        _detail = string.Empty;
        Log?.Invoke(LogLevel.Info, $"Pilot stopped: {reason}.");
        RaiseState();
    }

    private void ResetRunState()
    {
        // Positions are never carried over from an earlier run or a saved file: the game may now
        // run at another resolution. The learned template scales are dropped on the next tick
        // (the vision loop owns the analyzer), when the client size is known for the log.
        _settings.ResetPositions();
        _positionsResetPending = true;

        _attempts = 0;
        _anchor = null;
        _turnRate = 0;
        _preLoadHash = 0;
        _lastHash = 0;
        _stableSamples = 0;
        _pages = 0;
        _chevronMissing = false;
        _pagingBack = false;
        _backPages = 0;

        _stageIndex = 0;
        _mapScrolls = 0;
        _mapReopens = 0;
        _mapVisibleMisses = 0;
        _sweepDone = false;
        _chevronClicked = false;
        _sweepStartedAt = 0;
        _gatePolls = 0;
        _gateWarned = false;
        _tabPressed = false;
        _autohuntPressed = false;
        _bossFirstSeenAt = null;
        _bossMissingSince = null;
        _lastCoords = null;
        _runUpProbed = false;
        _runRightProbed = false;
        _runSteps = 0;
        _waypoint = 0;
        _gateTilted = false;
        _gateFlatChecked = false;
        _gateStillReadings = 0;
        _earlyNorth = false;
        _arrivalConfirmed = false;
        _nextZoneCheckAt = 0;
        _zoneConfirmedAt = 0;
        _otherZoneReads = 0;
        _zoneHoldLogged = false;
        _zoneHoldSince = null;
        _anchorTries = 0;
        _gateWalking = false;
        _sRR = _sRU = _sUU = _sXR = _sXU = _sYR = _sYU = 0;
        _pRR = _pRU = _pUU = _pXR = _pXU = _pYR = _pYU = 0;
        _fitSamples = 0;
        _lastOffset = default;
        _simpleClick = null;
        _headingError = 0;
        _headingSamples = 0;
        _turnWest = false;
        _faceSouth = false;
        _lodStill = 0;
        _lodWaitLogged = false;
        _fromWaypoint = false;
        _runToTargetPending = false;
        _westDone = false;
        _headingBase = 0;
        _jumpCandidate = null;
        _jumpAgreements = 0;
        _returning = false;
        _autohuntOff = false;
        _huntOffPresses = 0;
        _huntOffPressedAt = 0;
        _huntOffBaseline = [];
        _stuckShots = 0;
        _finalHuntOffChecked = false;
        _compassAnchored = false;
        _bestFlashlightScore = 0;
        _bestFlashlightStep = 0;
        _tapsSinceBest.Clear();
        _refineDir = 0;
        _refinePending = false;
        _killShotTag = null;
        ResetBossState();
    }

    private void ResetBossState()
    {
        _selectTries = 0;
        _selectBaselineTaken = false;
        _attackLog = string.Empty;
        _selectClickedAt = null;
        _manualPresses = -1;
        _manualHit = false;
        _holdUntil = null;
        _heldBeforeHunt = false;
        _manualRounds = 0;
        _huntOn = false;
        _huntOnAt = 0;
        _huntBaselineAt = null;
        _huntClicks = 0;
        _huntClickAt = null;
        _watchAttackLog = null;
        _watchAttackAt = 0;
        _nextBossCheckAt = 0;
        _lastWatchTabAt = 0;
        _watchTabs = 0;
        _watchPolls = 0;
        _watchGateLines = -1;
    }

    public async Task TickAsync(Mat frame, CancellationToken cancellationToken)
    {
        if (!IsRunning)
            return;

        _overlays.Clear();
        SetFrameSize(frame.Width, frame.Height);

        if (_positionsResetPending)
        {
            _positionsResetPending = false;
            _analyzer.ResetScaleCache();
            Log?.Invoke(LogLevel.Info,
                $"Screen positions reset to the built-in defaults for the {frame.Width}x{frame.Height} client; template scales will be re-learned.");
        }

        long now = Environment.TickCount64;

        // Checked even while a phase is waiting (boss spawn, settle delays): a disconnect can
        // happen at any time and every key/click after it would land on the login screens.
        // Not while the teleport list is open: its destinations and buttons are ours to click.
        bool cancelDialogs = !string.IsNullOrWhiteSpace(_settings.Sequence.CancelDialogText)
                             && _phase is not (PilotPhase.FindChevron or PilotPhase.FindDestination);
        if ((_settings.Sequence.AutoReconnect || cancelDialogs) && _phase != PilotPhase.Reconnect && now >= _nextDisconnectCheckAt)
        {
            _nextDisconnectCheckAt = now + 5000;
            try
            {
                var screenWords = await _analyzer.Ocr.RecognizeAsync(frame, new PixelRect(0, 0, frame.Width, frame.Height), 1.0);
                if (cancelDialogs && TryCancelDialog(frame, screenWords))
                {
                    _waitUntil = now + 800;
                    return;
                }

                if (_settings.Sequence.AutoReconnect && IsServersScreen(screenWords))
                {
                    ReleaseSweepKey();
                    Log?.Invoke(LogLevel.Warning, $"Disconnected (login screens) during {_phase} - logging back in.");
                    DebugArtifacts.SaveFrame(frame, _overlays, "disconnected");
                    _reconnectStartedAt = now;
                    _reconnectClicks = 0;
                    _startGameClicked = false;
                    _charScreenSince = 0;
                    _startGameUnreadSaved = false;
                    _reconnectStep = 0;
                    Advance(PilotPhase.Reconnect, _settings.Sequence.ReconnectStepDelayMs);
                    return;
                }
            }
            catch (Exception ex)
            {
                Log?.Invoke(LogLevel.Trace, $"Disconnect check failed: {ex.Message}");
            }
        }

        // Zone guard (user): Labyrinth phases send nothing unless the minimap title has said
        // "Labyrinth" within the last few seconds. Rotating, map clicks and Tab were sent in town
        // and in Bohai Outpost before. Stage 5 closing puts the character back in town -> next run.
        if (_settings.Dungeon.Enabled && !_returning && IsLabyrinthPhase(_phase))
        {
            if (now >= _nextZoneCheckAt)
            {
                _nextZoneCheckAt = now + 2000;
                string? title = await ReadMinimapTitleAsync(frame);
                string zone = title == null ? string.Empty : TextMatcher.Normalize(title);
                string? townSeen = TownIn(title);

                if (ZoneMatches(title, Destination))
                {
                    _zoneConfirmedAt = now;
                    _otherZoneReads = 0;
                    if (_zoneHoldLogged)
                    {
                        _zoneHoldLogged = false;
                        Log?.Invoke(LogLevel.Info, $"Back in {Destination} - {_phase} carries on.");
                    }

                    _zoneHoldSince = null;
                }
                else if (zone.Length > 0 && !_input.IsTargetForeground)
                {
                    // Another window over the game: the capture shows that window's minimap, not ours.
                    // 2026-10-27, 1440x900: a second game client (another character, in Piercing City /
                    // Dark Soul Cave) was read as "sent to town" seven times during stages 1-2, the run
                    // restarted each time and finally teleported again (a key gone). A town or other
                    // zone only counts with the game in front; behind another window the guard below
                    // just holds the phase.
                    _otherZoneReads = 0;
                }
                else if (zone.Length > 0 && (townSeen != null || ++_otherZoneReads >= 3))
                {
                    ReleaseSweepKey();
                    bool inTown = townSeen != null;

                    // Auto LOD: the boss respawns, so the map is unlimited until its timer runs out and
                    // the game sends the character to town (user) - teleport in again.
                    if (IsLod && inTown)
                    {
                        Log?.Invoke(LogLevel.Warning,
                            $"Back in {HomeTown} during {_phase} (the {Destination} timer ran out?) after {_lodKills} kill(s) - teleporting in again.");
                        ResetRunState();
                        _runStart = Environment.TickCount64;
                        Advance(PilotPhase.Focus, 1500);
                        return;
                    }

                    Log?.Invoke(LogLevel.Warning, inTown && IsFinalBoss
                        ? $"Back in {HomeTown} during {_phase} - stage 5 closed. Starting the next run."
                        : $"In '{title}' instead of the Labyrinth during {_phase} (stage {_stageIndex + 1}) - starting the next run.");
                    DebugArtifacts.SaveFrame(frame, _overlays, inTown && IsFinalBoss ? "stage5_closed" : "wrong_zone");
                    ResetRunState();
                    _runStart = Environment.TickCount64;
                    Advance(PilotPhase.Focus, 1500);
                    return;
                }
            }

            if (now - _zoneConfirmedAt > 6000)
            {
                // The 2 minutes only count while the game is in front: behind another window the
                // capture shows that window, not the minimap (this chat once covered it, run 34 LOD),
                // and no input could be sent anyway - just wait.
                if (!_input.IsTargetForeground)
                    _zoneHoldSince = now;
                _zoneHoldSince ??= now;
                if (now - _zoneHoldSince.Value > 120000)
                {
                    Fail(frame, $"The minimap has not shown {Destination} for 2 minutes with the game in front - stopped instead of sending input elsewhere.");
                    return;
                }

                _detail = "not confirmed in the Labyrinth (minimap title) - holding all input";
                if (!_zoneHoldLogged)
                {
                    _zoneHoldLogged = true;
                    ReleaseSweepKey();
                    Log?.Invoke(LogLevel.Warning, _input.IsTargetForeground
                        ? $"The minimap does not show {Destination} - holding {_phase} until it does."
                        : $"The game is behind another window - holding {_phase} until it is back in front.");
                }

                return;
            }
        }

        // After the zone guard: a kill's second screenshot must show the dungeon, not a covered or
        // loading screen.
        TickKillShot(frame, now);

        if (now < _waitUntil)
            return;

        try
        {
            switch (_phase)
            {
                case PilotPhase.Focus:
                    TickFocus();
                    break;
                case PilotPhase.CheckZone:
                    await TickCheckZoneAsync(frame);
                    break;
                case PilotPhase.AutohuntOff:
                    await TickAutohuntOffAsync(frame);
                    break;
                case PilotPhase.RunSkill:
                    TickRunSkill(frame);
                    break;
                case PilotPhase.FontSize:
                    await TickFontSizeAsync(frame);
                    break;
                case PilotPhase.OpenInventory:
                    TickOpenInventory(frame);
                    break;
                case PilotPhase.FindScroll:
                    TickFindScroll(frame);
                    break;
                case PilotPhase.FindChevron:
                    TickFindChevron(frame);
                    break;
                case PilotPhase.FindDestination:
                    await TickFindDestinationAsync(frame);
                    break;
                case PilotPhase.WaitForLoad:
                    TickWaitForLoad(frame);
                    break;
                case PilotPhase.FaceNorth:
                    await TickFaceNorthAsync(frame);
                    break;
                case PilotPhase.WaitForGate:
                    await TickWaitForGateAsync(frame);
                    break;
                case PilotPhase.ClickMap:
                    await TickClickMapAsync(frame);
                    break;
                case PilotPhase.ReadStage:
                    await TickReadStageAsync(frame);
                    break;
                case PilotPhase.AdvanceStage:
                    await TickAdvanceStageAsync(frame);
                    break;
                case PilotPhase.WaitBoss:
                    await TickWaitBossAsync(frame);
                    break;
                case PilotPhase.SelectBoss:
                    await TickSelectBossAsync(frame);
                    break;
                case PilotPhase.WatchBoss:
                    await TickWatchBossAsync(frame);
                    break;
                case PilotPhase.LodRun:
                    await TickLodRunAsync(frame);
                    break;
                case PilotPhase.LodAttack:
                    await TickLodAttackAsync(frame);
                    break;
                case PilotPhase.LodApproach:
                    await TickLodApproachAsync(frame);
                    break;
                case PilotPhase.LodRespawn:
                    await TickLodRespawnAsync(frame);
                    break;
                case PilotPhase.LodLoot:
                    TickLodLoot(frame);
                    break;
                case PilotPhase.RunToTarget:
                    await TickRunToTargetAsync(frame);
                    break;
                case PilotPhase.ZoomOut:
                    await TickZoomOutAsync(frame);
                    break;
                case PilotPhase.AimGate:
                    TickAimGate(frame);
                    break;
                case PilotPhase.ReturnToTown:
                    TickReturnToTown();
                    break;
                case PilotPhase.Reconnect:
                    await TickReconnectAsync(frame);
                    break;
                case PilotPhase.AutoWar:
                    await TickAutoWarAsync(frame);
                    break;
                case PilotPhase.Done:
                    TickDone();
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            Stop("cancelled");
        }
        catch (Exception ex)
        {
            Fail(frame, $"unexpected error: {ex.Message}");
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    // ---------------------------------------------------------------- phases

    private void TickFocus()
    {
        if (_settings.Input.Mode == InputMode.ForegroundSendInput && _settings.Input.FocusOnStart)
        {
            if (_input.TryFocus())
            {
                Log?.Invoke(LogLevel.Info, "Game window is in the foreground; input will only go there.");
                // The game needs a moment after activation before it takes keys reliably.
                Advance(PilotPhase.CheckZone, _settings.Input.FocusSettleMs);
                return;
            }

            _attempts++;
            if (_attempts > 6)
            {
                Fail(null, "Could not activate the bound window. Click the game once and press Start again, " +
                            "or clear 'Focus on start' and use background message mode.");
                return;
            }

            _waitUntil = Environment.TickCount64 + 600;
            return;
        }

        Advance(PilotPhase.CheckZone, 120);
    }

    /// <summary>
    /// Before the teleport: where is the character? A re-login puts it back where it was - in the
    /// Labyrinth mid-fight - and Start after an Alt-Tab did too; the teleport then ran inside the
    /// dungeon (user, run 28). Labyrinth on the minimap -> resume at the stage whose boss name shows
    /// (Tab first), never teleport; anywhere else -> the teleport as usual.
    /// </summary>
    private async Task TickCheckZoneAsync(Mat frame)
    {
        var dungeon = _settings.Dungeon;
        string? title = await ReadMinimapTitleAsync(frame);
        bool inLabyrinth = ZoneMatches(title, Destination);
        TownIn(title);

        // Auto LOD already on the map: the boss targeted -> attack, otherwise face south and walk.
        if (IsLod && inLabyrinth)
        {
            _zoneConfirmedAt = Environment.TickCount64;
            if (await FindLodBossAsync(frame) != null)
            {
                // Resume (user): rebuff first - a fight may be running, so stop attacking for it
                // (Shura, else Space) - then attack.
                Log?.Invoke(LogLevel.Warning, $"Already at {Destination} with '{_settings.Lod.BossName}' in view - rebuffing, then attacking.");
                TryLodBuff(frame, Environment.TickCount64, force: true, attacking: true);
                BeginLodFight();
                _lodHeadingUnknown = true;
                Advance(PilotPhase.LodAttack, 700);
                return;
            }

            // Not targeted yet: Tab a few times before deciding there is no boss near.
            if (_attempts < 3)
            {
                if (TapLodTab(Environment.TickCount64, 0))
                    _attempts++;
                _detail = $"at {Destination} - Tab {_attempts}/3 for '{_settings.Lod.BossName}'";
                _waitUntil = Environment.TickCount64 + Math.Max(1000, _settings.Lod.TabEveryMs);
                return;
            }

            Log?.Invoke(LogLevel.Warning, $"Already at {Destination} - carrying on from the walk to the boss.");
            BeginFaceSouth();
            return;
        }

        if ((!dungeon.Enabled && !IsLod) || !inLabyrinth)
        {
            // An unreadable title gets a few more frames (just after a load) before teleporting.
            if (title == null && ++_attempts < 6)
            {
                _detail = $"reading the minimap title ({_attempts}/6)";
                _waitUntil = Environment.TickCount64 + 500;
                return;
            }

            // In town: are there keys left? (item list, bottom right - user)
            if (TownIn(title) != null && !_returning && await CheckKeysAsync(frame))
                return;

            // In town, every run (user, 2026-10-25): the autohunt must be off before the teleport -
            // a restart, a closed stage 5 or a re-login can leave it running.
            bool townCheck = TownIn(title) != null && !_returning;
            _runSkillDue = townCheck && _settings.Sequence.RunSkillCheck;
            _runSkillClicks = 0;
            if (townCheck && _settings.Sequence.AutohuntOffInTown && !string.IsNullOrWhiteSpace(dungeon.AutohuntKey))
            {
                _huntOffPresses = 0;
                _huntOffBaseline = [];
                Advance(PilotPhase.AutohuntOff, 120);
                return;
            }

            Advance(AfterTownChecks(), 120);
            return;
        }

        // The open map lists every boss name - wait a moment for it to be closed. Strict chevron
        // score: the 0.50 of MapWindowVisible may match the boss's patterned body, which filled the
        // screen when this was first tried (run 29, stage 5) and the check never got further.
        var listRoi = _settings.Regions.MapBossList.ToPixels(frame.Width, frame.Height);
        bool mapOpen = _analyzer.FindIcons(frame, "rightchevron", listRoi, 0.85, 6).Count >= 2;
        if (mapOpen && Environment.TickCount64 - _phaseStart < 3000)
        {
            if (_detail != "the map is open - close it so the boss name can be read")
                Log?.Invoke(LogLevel.Info, "In the Labyrinth with the map open - waiting for it to close before reading the boss name.");
            _detail = "the map is open - close it so the boss name can be read";
            _waitUntil = Environment.TickCount64 + 1000;
            return;
        }

        // Which stage? The highest stage whose boss name is on screen (the target bar shows it).
        int found = -1;
        string text = string.Empty;
        var textRoi = _settings.Regions.BossName.ToPixels(frame.Width, frame.Height);
        for (int i = dungeon.Stages.Count; i >= 0; i--)
        {
            string name = StageName(i);
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var (hit, _, _) = await _analyzer.FindTextAsync(frame, textRoi, name);
            if (hit != null && IsFullName(hit, name))
            {
                found = i;
                text = hit.Text;
                break;
            }
        }

        if (found < 0)
        {
            // Standing where the teleport drops the character (3595, 3415 every time so far): a
            // fresh arrival - the teleport went through late (run 31, server lag). Carry on as if
            // it had just loaded.
            var at = await _analyzer.ReadCoordinatesAsync(frame);
            if (at != null && at.DistanceTo(ArrivalX, ArrivalY) <= 400)
            {
                _arrivalConfirmed = true;
                _zoneConfirmedAt = Environment.TickCount64;
                Log?.Invoke(LogLevel.Warning, $"Already in the Labyrinth at its entrance ({at}) - carrying on from the arrival.");
                Advance(PilotPhase.ZoomOut, 200);
                return;
            }

            // Tab shows the boss name and marker; give it a few tries.
            if (_attempts < 5)
            {
                if (_input.KeyTap(dungeon.TabKey))
                    _attempts++;
                _detail = $"in the Labyrinth - looking for a boss name (Tab {_attempts}/5)";
                _waitUntil = Environment.TickCount64 + Math.Max(1200, dungeon.TabSettleMs);
                return;
            }

            Fail(frame, "Already in the Labyrinth, but no boss name could be read to tell the stage - not teleporting. " +
                        "Target the boss (Tab) and press Start again, or leave the Labyrinth first.");
            return;
        }

        _stageIndex = found;
        _autohuntPressed = true;   // it is already fighting; J is a toggle
        _zoneConfirmedAt = Environment.TickCount64;
        _sweepDone = true;
        Log?.Invoke(LogLevel.Warning,
            $"Already in the Labyrinth - resuming at stage {found + 1} ('{text}') instead of teleporting.");
        Advance(PilotPhase.SelectBoss, 200);
    }

    /// <summary>
    /// In town: the Run skill (every run), then once per Start the game's font size (user: 110 - 120 %)
    /// and TAB target (Default); else the teleport.
    /// </summary>
    private PilotPhase AfterTownChecks()
    {
        if (_runSkillDue)
        {
            _runSkillDue = false;
            return PilotPhase.RunSkill;
        }

        bool settingsCheck = _settings.Sequence.FontSizePercent > 0 || _settings.Sequence.TabTargetDefault;
        return !_fontChecked && settingsCheck ? PilotPhase.FontSize : PilotPhase.OpenInventory;
    }

    /// <summary>
    /// In town, before the teleport (user, 2026-10-25: "when the character goes to town - retrying,
    /// an error - check whether the autohunt is on; press J, the log on the left shows 'Auto hunt
    /// started' or 'Auto hunt ended'; if on, turn it off"). The key is a toggle, so: baseline of
    /// the log's autohunt lines -> tap -> a new "ended" line = it was on, off now; a new "started"
    /// line = it was off, tap again (expect "ended"). Nothing new within 2 s of the first tap ->
    /// one more tap (the first key after focusing is sometimes eaten; two unread taps leave the
    /// state as it was); still nothing -> warning + <c>autohunt_unread</c> snapshot, carry on.
    /// At most 3 taps. A dry run sends nothing, so it skips the check.
    /// </summary>
    private async Task TickAutohuntOffAsync(Mat frame)
    {
        if (await EnsureAutohuntOffAsync(frame, $"in {HomeTown}", trustVisibleLine: false))
            Advance(AfterTownChecks(), 120);
    }

    /// <summary>
    /// The autohunt-off check shared by the town visit (<see cref="TickAutohuntOffAsync"/>) and the
    /// arrival at the stage 5 boss (<see cref="TickWaitBossAsync"/>, user 2026-10-26). Ticked until it
    /// returns true (off, or given up); false = still working, <c>_waitUntil</c> is set. Reset
    /// <c>_huntOffPresses</c> / <c>_huntOffBaseline</c> before the first tick.
    /// </summary>
    /// <param name="where">For the log: "in Piercing City", "at the final boss".</param>
    /// <param name="trustVisibleLine">
    /// Before the first tap, read the newest autohunt line already in the combat log: "ended" = off,
    /// nothing to press; "started" = on, one tap should end it. Right after the stage 4 kill the log
    /// still shows the line of the J that turned the autohunt off for the run, so the arrival check
    /// usually presses nothing (a tap on an autohunt that is off would turn it on for a second). In
    /// town the log may be hours old, so the town check always taps.
    /// </param>
    private async Task<bool> EnsureAutohuntOffAsync(Mat frame, string where, bool trustVisibleLine)
    {
        var seq = _settings.Sequence;
        string key = _settings.Dungeon.AutohuntKey;
        long now = Environment.TickCount64;

        if (_input.IsDryRun)
        {
            Log?.Invoke(LogLevel.Info, $"Dry run - not checking whether the autohunt is on {where}.");
            return true;
        }

        var roi = _settings.Regions.AttackMessage.ToPixels(frame.Width, frame.Height);
        var lines = await ReadAutohuntLinesAsync(frame, roi);

        bool Tap()
        {
            if (!_input.KeyTap(key))
            {
                _detail = "autohunt key blocked - waiting for the game to be in front";
                _waitUntil = now + 300;
                return false;
            }

            _huntOffBaseline = lines;
            _huntOffPresses++;
            _huntOffPressedAt = now;
            _detail = $"pressed '{key}' ({_huntOffPresses}/3) - reading the combat log";
            _waitUntil = now + 600;
            return true;
        }

        if (_huntOffPresses == 0)
        {
            if (trustVisibleLine)
            {
                bool? visible = AutohuntLog.NewState([], lines, seq.AutohuntStartedText, seq.AutohuntEndedText, out string? last, seq.AutohuntStoppedText);
                if (visible == false)
                {
                    Log?.Invoke(LogLevel.Success, $"Autohunt is off {where} (the combat log's newest autohunt line reads '{last}').");
                    return true;
                }

                if (visible == true)
                {
                    if (Tap())
                        Log?.Invoke(LogLevel.Warning, $"The combat log's newest autohunt line reads '{last}' - the autohunt is on {where}; pressed '{key}' to turn it off.");
                    return false;
                }
            }

            if (Tap())
                Log?.Invoke(LogLevel.Info, $"Pressing '{key}' {where} to see whether the autohunt is on ('{seq.AutohuntStartedText}' / '{seq.AutohuntEndedText}' in the combat log).");
            return false;
        }

        bool? on = AutohuntLog.NewState(_huntOffBaseline, lines, seq.AutohuntStartedText, seq.AutohuntEndedText, out string? line, seq.AutohuntStoppedText);
        if (on == false)
        {
            Log?.Invoke(LogLevel.Success, $"Autohunt is off {where} ('{line}' after '{key}' x{_huntOffPresses}).");
            return true;
        }

        if (on == true)
        {
            if (_huntOffPresses < 3)
            {
                if (Tap())
                    Log?.Invoke(LogLevel.Warning, $"The autohunt was off - '{key}' turned it on ('{line}'); pressed it again to turn it off.");
                return false;
            }

            Log?.Invoke(LogLevel.Warning, $"The combat log still reads '{line}' after '{key}' x3 - carrying on.");
            DebugArtifacts.SaveFrame(frame, _overlays, "autohunt_on");
            return true;
        }

        if (now - _huntOffPressedAt < 2000)
        {
            _detail = $"waiting for an autohunt line in the combat log ('{key}' x{_huntOffPresses})";
            _waitUntil = now + 400;
            return false;
        }

        if (_huntOffPresses == 1)
        {
            if (Tap())
                Log?.Invoke(LogLevel.Warning, $"No '{seq.AutohuntStartedText}' / '{seq.AutohuntEndedText}' line after '{key}' - pressed it once more (the first key after focusing is sometimes eaten).");
            return false;
        }

        Log?.Invoke(LogLevel.Warning, $"The combat log showed no autohunt line after '{key}' x{_huntOffPresses} - cannot tell whether the autohunt is on; carrying on.");
        DebugArtifacts.SaveFrame(frame, _overlays, "autohunt_unread");
        return true;
    }

    /// <summary>
    /// In town, before the teleport (user, 2026-10-25: "before running LOD or Laby add a check if the
    /// Run skill is activated, it will have a ring on it"). The icon
    /// (<see cref="SequenceSettings.RunSkillTemplate"/>, cropped inside the ring) is looked for on the
    /// skill bar; the gold ring round it (<see cref="ScreenAnalyzer.RingFraction"/>) = on. No ring ->
    /// click the icon and look again on the next frame, at most 3 clicks (then a warning and
    /// <c>runskill_off</c>). Icon not on the bar -> tap <see cref="SequenceSettings.RunSkillKey"/>
    /// once per Start: the key is a toggle and without the icon nothing says whether it was on, so a
    /// second press could turn it off again (<c>runskill_unread</c>). A dry run only reports.
    /// </summary>
    private void TickRunSkill(Mat frame)
    {
        var seq = _settings.Sequence;
        long now = Environment.TickCount64;

        void Done() => Advance(AfterTownChecks(), 120);

        var icon = FindRunSkill(frame);
        if (icon == null)
        {
            string missing = string.IsNullOrWhiteSpace(seq.RunSkillTemplate)
                ? "The Run skill has no icon template"
                : $"The Run skill icon ('{seq.RunSkillTemplate}') is not on the skill bar";
            if (string.IsNullOrWhiteSpace(seq.RunSkillKey))
            {
                Log?.Invoke(LogLevel.Warning, $"{missing} and no Run key is set - carrying on.");
                DebugArtifacts.SaveFrame(frame, _overlays, "runskill_unread");
                Done();
                return;
            }

            if (_input.IsDryRun)
            {
                Log?.Invoke(LogLevel.Info, $"Dry run - {missing.ToLowerInvariant()}; would press '{seq.RunSkillKey}' once.");
                Done();
                return;
            }

            if (_runKeyPressed)
            {
                Log?.Invoke(LogLevel.Info, $"{missing}; '{seq.RunSkillKey}' was already pressed this session (it is a toggle) - carrying on.");
                Done();
                return;
            }

            if (!_input.KeyTap(seq.RunSkillKey))
            {
                _detail = "Run key blocked - waiting for the game to be in front";
                _waitUntil = now + 300;
                return;
            }

            _runKeyPressed = true;
            Log?.Invoke(LogLevel.Warning, $"{missing} - pressed '{seq.RunSkillKey}' once (cannot tell whether it was already on; not again this session).");
            DebugArtifacts.SaveFrame(frame, _overlays, "runskill_unread");
            Done();
            return;
        }

        double ring = _analyzer.RingFraction(frame, icon.Bounds);
        bool on = ring >= seq.RunSkillRingMin;
        _overlays.Add(new OverlayItem(icon.Bounds.Inflate(icon.Bounds.Width / 3), $"ring {ring:0.00}", on ? OverlayColor.Green : OverlayColor.Red));
        int x = icon.Bounds.CenterX, y = icon.Bounds.CenterY;
        if (on)
        {
            string after = _runSkillClicks > 0 ? $" after {_runSkillClicks} click(s)" : "";
            Log?.Invoke(LogLevel.Success, $"The Run skill is on{after} (ring {ring:0.00} round its icon at {x},{y}, score {icon.Score:0.00}).");
            Done();
            return;
        }

        if (_input.IsDryRun)
        {
            Log?.Invoke(LogLevel.Info, $"Dry run - the Run skill is off (ring {ring:0.00}); would click its icon at {x},{y} (score {icon.Score:0.00}).");
            Done();
            return;
        }

        if (_runSkillClicks >= 3)
        {
            Log?.Invoke(LogLevel.Warning, $"Still no ring round the Run skill icon at {x},{y} after {_runSkillClicks} clicks (ring {ring:0.00}) - carrying on.");
            DebugArtifacts.SaveFrame(frame, _overlays, "runskill_off");
            Done();
            return;
        }

        if (!_input.Click(x, y))
        {
            _detail = "Run icon click blocked - waiting for the game to be in front";
            _waitUntil = now + 300;
            return;
        }

        _runSkillClicks++;
        Log?.Invoke(LogLevel.Info, $"The Run skill is off (ring {ring:0.00}) - clicked its icon at {x},{y} ({_runSkillClicks}/3, score {icon.Score:0.00}).");
        _detail = $"clicked the Run skill icon ({_runSkillClicks}/3) - looking for its ring";
        _waitUntil = now + 900;
    }

    /// <summary>
    /// The Run skill icon on the skill bar (<see cref="RegionSettings.Hotbar"/>), drawn on the
    /// preview; the best score wins, equal scores -> the leftmost (as <c>FindShura</c>).
    /// </summary>
    private TemplateMatch? FindRunSkill(Mat frame)
    {
        var seq = _settings.Sequence;
        if (string.IsNullOrWhiteSpace(seq.RunSkillTemplate))
            return null;

        var roi = _settings.Regions.Hotbar.ToPixels(frame.Width, frame.Height);
        var hits = _analyzer.FindIcons(frame, seq.RunSkillTemplate, roi, seq.RunSkillMatchScore, maxResults: 4);
        TemplateMatch? best = null;
        foreach (var hit in hits)
        {
            _overlays.Add(new OverlayItem(hit.Bounds, $"run {hit.Score:0.00}", OverlayColor.Green));
            if (best == null || hit.Score > best.Score + 0.01 ||
                (Math.Abs(hit.Score - best.Score) <= 0.01 && hit.Bounds.CenterX < best.Bounds.CenterX))
                best = hit;
        }

        return best;
    }

    /// <summary>The combat log's autohunt lines ("autohuntstarted" / "autohuntended" keys), top to bottom.</summary>
    private async Task<List<string>> ReadAutohuntLinesAsync(Mat frame, PixelRect roi)
    {
        var seq = _settings.Sequence;
        string? prefix = AutohuntLog.Prefix(seq.AutohuntStartedText, seq.AutohuntEndedText);
        if (prefix == null)
            return [];

        var (_, _, words) = await _analyzer.FindTextAsync(frame, roi, seq.AutohuntStartedText);
        var keys = new List<string>();
        foreach (var line in words.GroupBy(w => w.LineBounds).OrderBy(g => g.Key.Y))
        {
            string? key = AutohuntLog.Key(line.First().LineText, prefix);
            if (key == null)
                continue;
            keys.Add(key);
            _overlays.Add(new OverlayItem(line.Key, line.First().LineText, OverlayColor.Green));
        }

        return keys;
    }

    /// <summary>
    /// One visit to the game's Settings window per Start, in town:
    /// Escape -> VISUAL -> list scrolled to the top -> "Font size" kept within
    /// <see cref="SequenceSettings.FontSizePercent"/> .. <see cref="SequenceSettings.FontSizeMaxPercent"/>
    /// (outside it, the chevrons beside the value move it to the nearer end) -> list scrolled down
    /// to "TAB target", set to Default with its left chevron (<see cref="SequenceSettings.TabTargetDefault"/>)
    /// -> SAVE once if anything changed -> Escape. Only buttons found by their own text are clicked
    /// (the window also has CHARACTER SELECT and EXIT). Anything unexpected: close the window and
    /// carry on with the run - neither setting is worth a failure.
    /// </summary>
    private async Task TickFontSizeAsync(Mat frame)
    {
        const int OpenStep = 0, VisualStep = 1, TopStep = 2, FontStep = 3, TabStep = 4, SaveStep = 5, CloseStep = 9;
        const int ListNotches = 10;   // one 10-notch scroll ran the 17-row VISUAL list end to end (1920x1080)

        int low = _settings.Sequence.FontSizePercent;
        int high = Math.Max(low, _settings.Sequence.FontSizeMaxPercent);
        var full = new PixelRect(0, 0, frame.Width, frame.Height);
        long now = Environment.TickCount64;

        async Task<TextHit?> Find(string text)
        {
            var (hit, _, _) = await _analyzer.FindTextAsync(frame, full, text);
            return hit != null && TextMatcher.CoversMost(hit.Text, text) ? hit : null;
        }

        void Close()
        {
            _fontChecked = true;
            _fontStep = CloseStep;
            _attempts = 0;
        }

        // Font size done (or skipped): the TAB target next, then SAVE if anything changed.
        void AfterFont()
        {
            if (_settings.Sequence.TabTargetDefault)
            {
                _fontStep = TabStep;
                return;
            }

            AfterTab();
        }

        void AfterTab()
        {
            if (_fontChanged || _tabChanged)
                _fontStep = SaveStep;
            else
                Close();
        }

        // Scrolls the VISUAL list: the wheel over a point well below the VISUAL tab is over the
        // list at every resolution seen (1920x1080: 10 text heights below the tab = the Font size row).
        bool ScrollList(int notches)
        {
            int x = _visualTab.CenterX;
            int y = Math.Min(frame.Height - 1, _visualTab.Bottom + _visualTab.Height * 10);
            if (_input.Wheel(x, y, notches))
                return true;

            _detail = "scroll blocked - waiting for the game to be in front";
            _waitUntil = now + 300;
            return false;
        }

        switch (_fontStep)
        {
            case OpenStep:
                _fontClicks = 0;
                _fontTabClicks = 0;
                _fontChanged = false;
                _tabClicks = 0;
                _tabScrolls = 0;
                _tabChanged = false;
                _settingsChanges.Clear();
                if (!_input.KeyTap("Escape"))
                {
                    _waitUntil = now + 300;
                    return;
                }

                _fontStep = VisualStep;
                _waitUntil = now + 900;
                return;

            case VisualStep: // Settings open? then the VISUAL tab
            {
                if (await Find("SETTINGS") == null)
                {
                    if (++_attempts < 3)
                    {
                        _fontStep = OpenStep;
                        return;
                    }

                    _fontChecked = true;
                    _fontStep = OpenStep;
                    Log?.Invoke(LogLevel.Warning, "The Settings window did not open - leaving the font size and TAB target alone.");
                    Advance(PilotPhase.OpenInventory, 300);
                    return;
                }

                var visual = await Find("VISUAL");
                if (visual == null)
                {
                    Log?.Invoke(LogLevel.Warning, "Settings opened but no VISUAL tab was read - leaving the font size and TAB target alone.");
                    Close();
                    return;
                }

                if (!Click(visual.Bounds))
                    return;
                _visualTab = visual.Bounds;
                _fontStep = TopStep;
                _waitUntil = now + 900;
                return;
            }

            case TopStep: // the list may still be scrolled down from the last visit: back to the top
                if (!ScrollList(ListNotches))
                    return;
                _fontStep = FontStep;
                _waitUntil = now + 700;
                return;

            case FontStep: // read the row, step towards the target
            {
                var row = await _analyzer.ReadSettingRowAsync(frame, "Font size");

                // Still on the SOUND tab: the VISUAL click right after the window opened did not
                // take (run 32). Click it again, up to 3 times.
                if (row == null && ++_fontTabClicks < 3)
                {
                    _fontStep = VisualStep;
                    _attempts = 0;
                    _waitUntil = now + 500;
                    return;
                }

                if (low <= 0) // font size check off
                {
                    AfterFont();
                    return;
                }

                if (row?.Percent is not int percent)
                {
                    DebugArtifacts.SaveFrame(frame, _overlays, "fontsize_unread");
                    Log?.Invoke(LogLevel.Warning, "No 'Font size' value was read in the Settings window - leaving it alone.");
                    AfterFont();
                    return;
                }

                if (percent >= low && percent <= high)
                {
                    if (_fontChanged)
                        _settingsChanges.Add($"font size {percent}%");
                    else
                        Log?.Invoke(LogLevel.Info, low == high
                            ? $"Font size is already {percent}%."
                            : $"Font size is {percent}% (within {low}-{high}%).");
                    AfterFont();
                    return;
                }

                // Outside the range: move to the nearer end (below -> low, above -> high).
                if (!_fontChanged)
                    _fontGoal = percent < low ? low : high;
                var chevron = percent < _fontGoal ? row.Right : row.Left;
                if (chevron == null || ++_fontClicks > 12)
                {
                    Log?.Invoke(LogLevel.Warning, $"Font size is {percent}% and could not be set to {_fontGoal}% - leaving it.");
                    if (_fontChanged)
                        _settingsChanges.Add($"font size {percent}%");
                    AfterFont();
                    return;
                }

                if (!Click(chevron.Value))
                    return;
                _fontChanged = true;
                _detail = $"font size {percent}% -> {_fontGoal}%";
                _waitUntil = now + 500;
                return;
            }

            case TabStep: // "TAB target" -> Default; the row is at the bottom of the list
            {
                var row = await _analyzer.ReadSettingRowAsync(frame, "TAB target", numeric: false);
                if (row?.ValueText is not string value)
                {
                    if (_tabScrolls < 3)
                    {
                        if (!ScrollList(-ListNotches))
                            return;
                        _tabScrolls++;
                        _detail = $"scrolling the VISUAL list to 'TAB target' ({_tabScrolls}/3)";
                        _waitUntil = now + 700;
                        return;
                    }

                    DebugArtifacts.SaveFrame(frame, _overlays, "tabtarget_unread");
                    Log?.Invoke(LogLevel.Warning, "No 'TAB target' value was read in the Settings window - leaving it alone.");
                    if (_tabChanged)
                        _settingsChanges.Add("TAB target (changed, then unread)");
                    AfterTab();
                    return;
                }

                // Default is the first option: every click on the left chevron moves one step towards it.
                if (IsDefaultTabTarget(value))
                {
                    if (_tabChanged)
                        _settingsChanges.Add("TAB target Default");
                    else
                        Log?.Invoke(LogLevel.Info, "TAB target is already Default.");
                    AfterTab();
                    return;
                }

                if (row.Left == null || _tabClicks >= 5)
                {
                    Log?.Invoke(LogLevel.Warning, $"TAB target is '{value}' and could not be set to Default - leaving it.");
                    if (_tabChanged)
                        _settingsChanges.Add($"TAB target {value}");
                    AfterTab();
                    return;
                }

                if (!Click(row.Left.Value))
                    return;
                _tabClicks++;
                _tabChanged = true;
                _detail = $"TAB target '{value}' -> Default";
                _waitUntil = now + 500;
                return;
            }

            case SaveStep: // SAVE - the exact word only
            {
                string changes = _settingsChanges.Count > 0 ? string.Join(", ", _settingsChanges) : "settings changed";
                var save = await Find("SAVE");
                if (save == null || TextMatcher.Normalize(save.Text) != "save")
                {
                    Log?.Invoke(LogLevel.Warning, $"Set {changes}, but no SAVE button was read - it may not stick.");
                    Close();
                    return;
                }

                if (!Click(save.Bounds))
                    return;
                Log?.Invoke(LogLevel.Success, $"Set {changes} and saved.");
                Close();
                _waitUntil = now + 800;
                return;
            }

            default: // close the window (Escape toggles it), then the teleport
            {
                if (_attempts < 3 && await Find("SETTINGS") != null)
                {
                    _attempts++;
                    _input.KeyTap("Escape");
                    _waitUntil = now + 700;
                    return;
                }

                _fontStep = OpenStep;
                Advance(PilotPhase.OpenInventory, 300);
                return;
            }
        }
    }

    /// <summary>
    /// The TAB target value reads as "Default". The other options (Nearest enemy, Enemy worth CP,
    /// Nearest monster) score below 0.2 against it.
    /// </summary>
    public static bool IsDefaultTabTarget(string value) => TextMatcher.Similarity(value, "Default") >= 0.75;

    private void TickOpenInventory(Mat frame)
    {
        var scrollRoi = _settings.Regions.Scroll.ToPixels(frame.Width, frame.Height);
        var probe = _analyzer.FindIcon(frame, "teleportscroll", scrollRoi, ScrollThreshold);

        if (probe != null)
        {
            _detail = $"scroll visible already ({probe.Score:0.00})";
            Advance(PilotPhase.FindScroll, 0);
            return;
        }

        if (!_input.KeyTap(_settings.Input.InventoryKey))
        {
            _waitUntil = Environment.TickCount64 + 250;
            _detail = "input blocked - waiting for the game to be in front";
            return;
        }

        Log?.Invoke(LogLevel.Info, $"Opening inventory with '{_settings.Input.InventoryKey}'.");
        Advance(PilotPhase.FindScroll, _settings.Sequence.InventoryOpenMs);
    }

    private void TickFindScroll(Mat frame)
    {
        var roi = _settings.Regions.Scroll.ToPixels(frame.Width, frame.Height);
        var (match, ms) = _analyzer.FindIconTimed(frame, "teleportscroll", roi, ScrollThreshold);

        if (match == null)
        {
            _detail = "no teleport scroll on screen";

            if (_attempts + 1 > _settings.Sequence.MaxRetries)
            {
                // Final attempt: report how close the best candidate got, so the fix is obvious.
                var loose = _analyzer.FindIcons(frame, "teleportscroll", roi, 0.30, 3);
                string hint = loose.Count == 0
                    ? "nothing similar is on screen - is the inventory open, and on the page that holds the scroll?"
                    : $"the closest look-alike scored {loose[0].Score:0.00} at ({loose[0].Bounds.X},{loose[0].Bounds.Y}) - lower Detection > Template score or re-crop teleportscroll.png from your screen";
                Fail(frame, $"teleportscroll.png not found after {_settings.Sequence.MaxRetries} attempts: {hint}.");
                return;
            }

            Retry(frame, "teleportscroll.png");
            return;
        }

        _overlays.Add(new OverlayItem(match.Bounds, $"scroll {match.Score:0.00}", OverlayColor.Green));
        _detail = $"scroll {match.Score:0.00} in {ms:0}ms";

        // Right click: in Wuxen2 a left click only picks the item up (it sticks to the cursor) and a
        // double click does nothing - the right click is what opens the "TELEPORT SCROLL" panel.
        if (!RightClick(match.Bounds))
            return;

        Log?.Invoke(LogLevel.Success, $"Right-clicked the teleport scroll (score {match.Score:0.00}, match {ms:0.0}ms).");

        // Look at the destination list first; the pilot only pages with the chevron if the wanted
        // entry is not on the page that just opened.
        Advance(PilotPhase.FindDestination, _settings.Input.AfterClickMs + _settings.Sequence.RetryDelayMs);
    }

    private void TickFindChevron(Mat frame)
    {
        var roi = _settings.Regions.TeleportList.ToPixels(frame.Width, frame.Height);
        string arrow = _pagingBack ? "leftchevron" : "rightchevron";
        var matches = _analyzer.FindIcons(frame, arrow, roi, _settings.Detection.TemplateThreshold, 10);

        foreach (var match in matches)
            _overlays.Add(new OverlayItem(match.Bounds, $">{match.Score:0.00}", OverlayColor.Orange));

        if (matches.Count == 0)
        {
            _chevronMissing = true;
            Fail(frame, $"rightchevron.png was not found in the destination list region, so '{CurrentDestination}' cannot be paged to. Widen Sequence ▸ Regions ▸ Teleport list, or re-crop rightchevron.png.");
            return;
        }

        var pick = _anchor.HasValue && !_pagingBack
            ? TemplateMatcher.Closest(matches, _anchor.Value.X, _anchor.Value.Y) ?? matches[0]
            : matches[0];

        _overlays.Add(new OverlayItem(pick.Bounds, "click chevron", OverlayColor.Green));
        _detail = $"chevron {pick.Score:0.00}";
        if (!_pagingBack)
            _anchor = (pick.Bounds.CenterX, pick.Bounds.CenterY);

        if (!Click(pick.Bounds))
            return;

        _pages++;
        Log?.Invoke(LogLevel.Info, _pagingBack
            ? $"Paged the destination list back looking for '{CurrentDestination}'."
            : $"Paged the destination list (page {_pages}) looking for '{CurrentDestination}'.");
        Advance(PilotPhase.FindDestination, _settings.Input.AfterClickMs + _settings.Sequence.RetryDelayMs);
    }

    private async Task TickFindDestinationAsync(Mat frame)
    {
        var roi = _settings.Regions.Ocr.ToPixels(frame.Width, frame.Height);
        var (hit, ms, words) = await _analyzer.FindTextAsync(
            frame,
            roi,
            CurrentDestination,
            _anchor?.X,
            _anchor?.Y);

        // The fuzzy match takes pieces: "[Den of Rebirth]" must not pass for "Ultimate Den of
        // Rebirth" (Auto LOD) - the start of the name has to be there.
        if (hit != null && !ZoneMatches(hit.LineText, CurrentDestination) && !ZoneMatches(hit.Text, CurrentDestination))
            hit = null;

        if (hit == null)
        {
            if (_pages <= 1)
            {
                string seen = words.Count == 0
                    ? "no text recognised"
                    : string.Join(" | ", words.Select(w => w.LineText).Distinct().Take(8));
                Log?.Invoke(LogLevel.Trace, $"OCR saw: {seen}");
            }

            _detail = $"'{CurrentDestination}' not found by OCR";

            // Auto LOD lives on page 2 of the list (user): looked at that page without it = out of
            // keys/entries - switch to Auto Laby; the Labyrinth is on the same page.
            if (IsLod && !_returning && _pages + 1 >= _settings.Lod.ListPage)
            {
                DebugArtifacts.SaveFrame(frame, _overlays, "lod_no_entries");
                if (OutOfKeys(labyrinth: false, $"'{Destination}' is not on page {_settings.Lod.ListPage} of the teleport list - out of Den of Rebirth entries"))
                    _waitUntil = Environment.TickCount64 + 200; // the Labyrinth is on this page
                return;
            }

            // The list's arrows say where we are: the last page has no ">" (a click there did nothing,
            // "paged" 12 times on page 3/3, run 8), the first page no "<". The page counter itself is
            // not read by OCR.
            var (hasNext, hasPrev) = TeleportListArrows(frame);
            if (hasNext || hasPrev)
            {
                bool lastPage = !hasNext;
                bool firstPage = !hasPrev;
                if (lastPage && !_pagingBack)
                {
                    // Auto LOD out of entries: the map is not in the list any more (user: switch to
                    // Auto Laby then) - look for the Labyrinth, paging back.
                    if (IsLod && !_returning)
                    {
                        DebugArtifacts.SaveFrame(frame, _overlays, "lod_no_entries");
                        if (!OutOfKeys(labyrinth: false, $"'{Destination}' is not in the teleport list (last page reached) - out of Den of Rebirth entries"))
                            return;
                    }

                    _pagingBack = true;
                    _backPages = 0;
                }
                else if (firstPage && _pagingBack)
                {
                    Fail(frame, $"'{CurrentDestination}' is on none of the teleport list pages.");
                    return;
                }

                if (_pagingBack)
                {
                    if (_backPages++ > _settings.Sequence.MaxRetries)
                    {
                        Fail(frame, $"'{CurrentDestination}' not found paging back through the teleport list.");
                        return;
                    }

                    Advance(PilotPhase.FindChevron, _settings.Sequence.RetryDelayMs);
                    return;
                }
            }

            // The destination is usually a few pages down the list: click the chevron and look again.
            if (!_chevronMissing && _pages < _settings.Sequence.MaxRetries)
            {
                Advance(PilotPhase.FindChevron, _settings.Sequence.RetryDelayMs);
                return;
            }

            Fail(frame, $"OCR could not find '{CurrentDestination}' after paging {_pages} time(s). Check the OCR region, the spelling of the destination, and that rightchevron.png is up to date.");
            return;
        }

        _overlays.Add(new OverlayItem(hit.Bounds, $"{hit.Text} {hit.Similarity:0.00}", OverlayColor.Green));
        _detail = $"OCR '{hit.Text}' {hit.Similarity:0.00} in {ms:0}ms";

        if (!Click(hit.Bounds))
            return;

        Log?.Invoke(LogLevel.Success,
            $"Clicked '{hit.Text}' (similarity {hit.Similarity:0.00}, OCR {_analyzer.Ocr.LastDurationMs:0}ms, total {ms:0}ms).");

        _preLoadHash = FrameSignature.Compute(frame, new PixelRect(0, 0, frame.Width, frame.Height));
        Advance(PilotPhase.WaitForLoad, _settings.Input.AfterClickMs);
    }

    private void TickWaitForLoad(Mat frame)
    {
        long elapsed = Environment.TickCount64 - _phaseStart;
        var full = new PixelRect(0, 0, frame.Width, frame.Height);
        ulong hash = FrameSignature.Compute(frame, full);

        if (_lastHash != 0)
        {
            int difference = FrameSignature.Distance(hash, _lastHash);
            if (difference <= 3)
                _stableSamples++;
            else
                _stableSamples = 0;
        }

        _lastHash = hash;
        bool changed = _preLoadHash == 0 || FrameSignature.Distance(hash, _preLoadHash) > 5;
        _detail = $"loading {elapsed}ms, stable {_stableSamples}, changed {changed}";

        if (elapsed > _settings.Sequence.LoadingTimeoutMs)
        {
            Log?.Invoke(LogLevel.Warning, "Load did not settle in time; carrying on anyway.");
            AdvanceAfterLoad();
            return;
        }

        if (elapsed >= _settings.Sequence.LoadingMinMs && changed && _stableSamples >= 2)
        {
            Log?.Invoke(LogLevel.Success, $"Zone loaded after {elapsed}ms.");
            AdvanceAfterLoad();
            return;
        }

        _waitUntil = Environment.TickCount64 + 120;
    }

    /// <summary>
    /// "Rotate until the flashlight matches": the searchlight drawn on the minimap is compared
    /// with flashlight.png and the pilot hill-climbs on that score (turn, look, keep turning while
    /// it improves, back off and halve the step when it gets worse). That is literally what the
    /// user checks by eye, and it needs no pivot calibration like an angle estimate would.
    /// </summary>
    private async Task TickFaceNorthAsync(Mat frame)
    {
        var dungeon = _settings.Dungeon;

        // Optional blind turn first (RotateLeftSweepMs, off by default): the searchlight is now
        // lined up with short taps that check the score after each one, below.
        if (!_sweepDone && dungeon.RotateLeftSweepMs > 0 &&
            _settings.Input.Mode == InputMode.ForegroundSendInput && SweepLeft(dungeon))
            return;

        // The compass box is placed relative to the minimap's "x , y" line, so read it first. Right
        // after the zoom-out one read failed (frame mid-redraw): retry for ~3 s before falling back.
        if (!_compassAnchored)
        {
            var coords = await _analyzer.ReadCoordinatesAsync(frame);
            if (coords == null && ++_anchorTries < 10)
            {
                _detail = $"reading the minimap coordinates ({_anchorTries}/10)";
                _waitUntil = Environment.TickCount64 + 300;
                return;
            }

            _compassAnchored = true;
            _anchorTries = 0;
            Log?.Invoke(coords == null ? LogLevel.Warning : LogLevel.Info, coords == null
                ? "Could not read the minimap coordinates; using Regions.Compass for the searchlight."
                : $"Minimap found at {coords.Bounds} ({coords}); searchlight box {_analyzer.CompassRegion(frame.Width, frame.Height)}.");
        }

        // Auto LOD: the boss in view while turning - stop the turn and use the skill (user).
        if (_faceSouth && IsLod && await LodBossInViewWhileTurningAsync(frame))
            return;

        var roi = _analyzer.CompassRegion(frame.Width, frame.Height);
        string template = _analyzer.Library.TryGet(dungeon.FlashlightTemplate, out _) ? dungeon.FlashlightTemplate : "flashlight";
        // Auto LOD: the user's flashlightLOD crop already faces SOUTH - used as is for the south turn
        // (it was turned 180 degrees by mistake, and the pilot lined up facing north).
        bool lodSouthCrop = IsLod && _faceSouth && _analyzer.Library.TryGet(_settings.Lod.FlashlightTemplate, out _);
        if (lodSouthCrop)
            template = _settings.Lod.FlashlightTemplate;
        if (_turnWest && !_westDone && _analyzer.Library.TryGet(template + "west", out _))
            template += "west";
        if (_faceSouth && !lodSouthCrop && _analyzer.Library.TryGet(template + "south", out _))
            template += "south";
        var (match, ms) = _analyzer.FindIconAnyScale(frame, template, roi, 0.30);
        double score = match?.Score ?? 0;
        double need = IsLod ? _settings.Lod.FlashlightMatchScore : dungeon.FlashlightMatchScore;

        _overlays.Add(new OverlayItem(roi, $"{template} {score:0.00}", score >= need ? OverlayColor.Green : OverlayColor.Yellow));

        if (_refineDir > 0)
        {
            TickRefineNorth(score, template);
            return;
        }

        if (score > _bestFlashlightScore + 0.002)
        {
            _bestFlashlightScore = score;
            _bestFlashlightStep = _attempts;
            _tapsSinceBest.Clear();
        }

        // Stopping at the first tap above the threshold left the beam north-north-east: it was still
        // swinging towards north. Keep tapping past the threshold until the score falls (or stops
        // rising), then undo the taps made since the best heading.
        bool pastPeak = _bestFlashlightScore >= need &&
                        (score < _bestFlashlightScore - 0.02 || _tapsSinceBest.Count >= 4);
        if (pastPeak)
        {
            if (!TapBack())
                return;

            Log?.Invoke(LogLevel.Success,
                $"Searchlight lined up at its best score {_bestFlashlightScore:0.00} ({template}, {_attempts} tap(s), {_tapsSinceBest.Count} undone).");
            _tapsSinceBest.Clear();
            if (dungeon.NorthRefineTapMs > 0)
            {
                // Score the restored heading first (next frame), then try fine taps both ways.
                _refineDir = 1;
                _refineBest = 0;
                _refineTaps = 0;
                _refineGained = 0;
                _refinePending = false;
                _waitUntil = Environment.TickCount64 + _settings.Sequence.RotateSettleMs;
                return;
            }

            Advance(AfterNorth(), _settings.Sequence.RotateSettleMs);
            return;
        }

        int maxSteps = _settings.Sequence.AlignMaxAttempts;
        if (_attempts >= maxSteps)
        {
            // A full scan without a clean match: tap back to the best heading seen, if it was close.
            if (_bestFlashlightScore >= 0.60)
            {
                if (!TapBack())
                    return;

                Log?.Invoke(LogLevel.Warning,
                    $"Searchlight never reached {need:0.00}; turned back {_tapsSinceBest.Count} tap(s) to the best heading (score {_bestFlashlightScore:0.00}).");
                _tapsSinceBest.Clear();
                Advance(AfterNorth(), _settings.Sequence.RotateSettleMs);
                return;
            }

            // Auto LOD: walk anyway - the heading is learned from the steps.
            if (_faceSouth)
            {
                Log?.Invoke(LogLevel.Warning, $"Could not line the searchlight up facing south (best {_bestFlashlightScore:0.00}) - walking anyway.");
                Advance(AfterNorth(), 300);
                return;
            }

            // The early turn is only a head start: carry on with the stages, the check after stage 4
            // turns again.
            if (_earlyNorth)
            {
                Log?.Invoke(LogLevel.Warning,
                    $"Could not line the searchlight up on arrival (best {_bestFlashlightScore:0.00}) - carrying on, north is checked again after stage 4.");
                DebugArtifacts.SaveFrame(frame, _overlays, "north_arrival");
                Advance(AfterNorth(), 300);
                return;
            }

            Fail(frame, $"Could not line the searchlight up in {maxSteps} taps " +
                        $"(best score {_bestFlashlightScore:0.00}). Check the compass region and {template}.png in Test scan.");
            return;
        }

        // Short tap, then look again; half taps close to the peak for a finer stop.
        bool fine = _bestFlashlightScore >= need - 0.05;
        int tapMs = fine ? Math.Max(20, dungeon.RotateStepMs / 2) : dungeon.RotateStepMs;
        if (!HoldKey(dungeon.RotateLeftKey, tapMs))
        {
            _waitUntil = Environment.TickCount64 + 300;
            return;
        }

        _tapsSinceBest.Add(tapMs);
        _attempts++;
        _detail = $"searchlight {score:0.00} (best {_bestFlashlightScore:0.00}), tap {_attempts}/{maxSteps}{(fine ? " fine" : string.Empty)}";
        _waitUntil = Environment.TickCount64 + _settings.Sequence.RotateSettleMs;
    }

    /// <summary>
    /// Fine search around the coarse peak: tap <see cref="DungeonSettings.NorthRefineTapMs"/> one
    /// way while the score rises; a tap that does not raise it is undone, and if the first
    /// direction never helped, the other one is tried. At most 8 taps per direction.
    /// </summary>
    private void TickRefineNorth(double score, string template)
    {
        var dungeon = _settings.Dungeon;
        int tapMs = dungeon.NorthRefineTapMs;
        string key = _refineDir == 1 ? _settings.Input.RotateKey : dungeon.RotateLeftKey;
        string back = _refineDir == 1 ? dungeon.RotateLeftKey : _settings.Input.RotateKey;
        const double Gain = 0.004;

        if (!_refinePending)
        {
            // Baseline (restored heading, or the heading after an undo).
            _refineBest = Math.Max(_refineBest, score);
        }
        else if (score > _refineBest + Gain)
        {
            _refineBest = score;
            _refineGained++;
        }
        else
        {
            // No better: undo that tap, then either try the other way or stop.
            if (!HoldKey(back, tapMs))
            {
                _waitUntil = Environment.TickCount64 + 300;
                return;
            }

            _refinePending = false;
            if (_refineDir == 1 && _refineGained == 0)
            {
                _refineDir = 2;
                _refineTaps = 0;
                _waitUntil = Environment.TickCount64 + _settings.Sequence.RotateSettleMs;
                return;
            }

            FinishRefine(template);
            return;
        }

        if (_refineTaps >= 8)
        {
            FinishRefine(template);
            return;
        }

        if (!HoldKey(key, tapMs))
        {
            _waitUntil = Environment.TickCount64 + 300;
            return;
        }

        _refinePending = true;
        _refineTaps++;
        _detail = $"fine-tuning north: {score:0.000} (best {_refineBest:0.000}), '{key}' tap {_refineTaps}";
        _waitUntil = Environment.TickCount64 + _settings.Sequence.RotateSettleMs;
    }

    private void FinishRefine(string template)
    {
        string way = _refineGained == 0 ? "no fine tap helped"
            : $"{_refineGained} fine '{(_refineDir == 1 ? _settings.Input.RotateKey : _settings.Dungeon.RotateLeftKey)}' tap(s)";
        Log?.Invoke(LogLevel.Success, $"Searchlight fine-tuned: {way}, score {_refineBest:0.000} ({template}).");
        _refineDir = 0;
        _refinePending = false;
        Advance(AfterNorth(), _settings.Sequence.RotateSettleMs);
    }

    /// <summary>
    /// Undoes the taps made since the best heading, newest first, with the opposite key and the
    /// same tap lengths (one long hold does not turn as far as several taps).
    /// </summary>
    private bool TapBack()
    {
        string reverse = _settings.Input.RotateKey;
        for (int i = _tapsSinceBest.Count - 1; i >= 0; i--)
        {
            if (!HoldKey(reverse, _tapsSinceBest[i]))
            {
                _waitUntil = Environment.TickCount64 + 300;
                return false;
            }

            Thread.Sleep(Math.Max(80, _settings.Sequence.RotateSettleMs / 2));
        }

        return true;
    }

    private PilotPhase AfterNorth()
    {
        if (!_settings.Dungeon.Enabled)
            return PilotPhase.WaitForGate;

        if (_faceSouth)
            return AfterFaceSouth();

        // The west turn at the last waypoint: back to the run, screen-up is now world-west.
        if (_turnWest && !_westDone)
        {
            _westDone = true;
            _headingBase = -Math.PI / 2;
            _headingError = _headingBase;
            _headingSamples = 0;
            _simpleClick = null;
            _lastCoords = null;
            _stalledSteps = 0;
            return PilotPhase.RunToTarget;
        }

        // The early turn right after loading: back to the stage loop; the check after stage 4 then
        // only needs a tap or two.
        if (_earlyNorth)
        {
            _earlyNorth = false;
            _compassAnchored = false;
            _bestFlashlightScore = 0;
            _bestFlashlightStep = 0;
            _tapsSinceBest.Clear();
            return PilotPhase.WaitForGate;
        }

        return _settings.Dungeon.AimGate ? PilotPhase.AimGate : PilotPhase.RunToTarget;
    }

    /// <summary>
    /// Tilts the camera up (right-drag upwards from the centre) so the next stage's gate shows as a
    /// red/orange glow ahead, then clicks it: one click walks most of the way. Not found -> the
    /// coordinate run as before. A gate_view snapshot is saved either way to tune the detection.
    /// </summary>
    private void TickAimGate(Mat frame)
    {
        var dungeon = _settings.Dungeon;
        int cx = frame.Width / 2;
        int cy = frame.Height / 2;

        if (!_gateTilted)
        {
            // The camera is no longer tilted back down (user), so the tilt stays from the last run
            // and a second 60 px moved the gate to y ~190 and the run clicks into the unwalkable
            // doorway (run 34). Look first; tilt only when the gate is not already in view.
            if (!_gateFlatChecked)
            {
                _gateFlatChecked = true;
                int area = Math.Max(150, frame.Width * frame.Height / 4000);
                var seen = _analyzer.FindRedOrangeGlow(frame, new NormRect(0.25, 0.07, 0.31, 0.15).ToPixels(frame.Width, frame.Height), area)
                           ?? _analyzer.FindRedOrangeGlow(frame, new NormRect(0.25, 0.22, 0.50, 0.20).ToPixels(frame.Width, frame.Height), area);
                if (seen is { } already && Math.Abs(already.CenterX - cx) <= frame.Width * 0.15)
                {
                    _gateTilted = true;
                    Log?.Invoke(LogLevel.Info, $"The gate is already in view at {already} - no tilt needed.");
                    return;
                }
            }

            if (!_input.Drag(cx, cy, cx, cy - dungeon.CameraTiltPx, 300))
            {
                _waitUntil = Environment.TickCount64 + 300;
                return;
            }

            _gateTilted = true;
            Log?.Invoke(LogLevel.Info, $"Tilted the camera up ({dungeon.CameraTiltPx}px right-drag) to look for the gate.");
            _waitUntil = Environment.TickCount64 + 600;
            return;
        }

        // Straight ahead, below every HUD element (target HP bar, stat panel icons, minimap - all
        // red/orange and all matched a wider band) and above the character: a torch beside the
        // character once matched when it already stood in the gate doorway (band ended at 50 %).
        // With the 60 px tilt (user) the gate shows high up, between the top HUD and the old band
        // (388,89 44x20 at 1024x768, run 29) - that band stays left of the stat panel (x ~0.57),
        // whose icons are orange. The old band is for steeper tilts.
        var upper = new NormRect(0.25, 0.07, 0.31, 0.15).ToPixels(frame.Width, frame.Height);
        var roi = new NormRect(0.25, 0.22, 0.50, 0.20).ToPixels(frame.Width, frame.Height);
        int minArea = Math.Max(150, frame.Width * frame.Height / 4000);
        var glow = _analyzer.FindRedOrangeGlow(frame, upper, minArea) ?? _analyzer.FindRedOrangeGlow(frame, roi, minArea);
        _overlays.Add(new OverlayItem(upper, "gate search", OverlayColor.Blue));

        // Facing north the gate is (almost) straight ahead - a glow off to the side is something else.
        // The run-29 gate sat ~100 px left of the centre of a 1024 px client, so 15 %.
        if (glow is { } off && Math.Abs(off.CenterX - frame.Width / 2) > frame.Width * 0.15)
        {
            Log?.Invoke(LogLevel.Info, $"Red/orange patch at {off} is off the centre line - not the gate.");
            glow = null;
        }

        _overlays.Add(new OverlayItem(roi, "gate search", OverlayColor.Blue));
        if (glow is { } g)
            _overlays.Add(new OverlayItem(g, "gate", OverlayColor.Green));
        DebugArtifacts.SaveFrame(frame, _overlays, "gate_view", glow is { } c ? (c.CenterX, c.CenterY) : null);

        if (glow is not { } gate)
        {
            UntiltCamera(frame);
            Log?.Invoke(LogLevel.Info, "No red/orange gate glow found - steering by coordinates.");
            Advance(PilotPhase.RunToTarget, 400);
            return;
        }

        // The red/orange is the stage's background; click a little below it, on the brown floor in
        // front of the gate (user), and stay above the character.
        int x = gate.CenterX;
        int y = Math.Min(gate.Bottom + Math.Max(12, (int)(frame.Height * 0.03)), (int)(frame.Height * 0.50));
        if (!_input.Click(x, y))
        {
            _waitUntil = Environment.TickCount64 + 300;
            return;
        }

        _lastClick = (x, y, Environment.TickCount64);
        _gateWalking = true;
        Log?.Invoke(LogLevel.Success, $"Clicked the gate glow at {x},{y} ({gate}) - walking there.");
        UntiltCamera(frame);
        Advance(PilotPhase.RunToTarget, 1200);
    }

    /// <summary>Undoes the tilt when <see cref="DungeonSettings.UntiltAfterGate"/> is on (off: user).</summary>
    private void UntiltCamera(Mat frame)
    {
        if (!_gateTilted || !_settings.Dungeon.UntiltAfterGate)
            return;

        int cx = frame.Width / 2;
        int cy = frame.Height / 2;
        if (_input.Drag(cx, cy, cx, cy + _settings.Dungeon.CameraTiltPx, 300))
            _gateTilted = false;
    }

    /// <summary>Holds a key for <paramref name="ms"/> (only into the bound window).</summary>
    private bool HoldKey(string key, int ms)
    {
        if (!_input.KeyDown(key))
            return false;

        Thread.Sleep(ms);
        _input.KeyUp(key);
        Log?.Invoke(LogLevel.Trace, $"Held '{key}' for {ms}ms.");
        return true;
    }

    /// <summary>
    /// Holds the "turn left" key for <see cref="DungeonSettings.RotateLeftSweepMs"/>, a little at a
    /// time so the tick loop stays responsive. Returns true while the sweep is still in charge.
    /// </summary>
    private bool SweepLeft(DungeonSettings dungeon)
    {
        long now = Environment.TickCount64;

        if (_sweepStartedAt == 0)
        {
            if (!_input.KeyDown(dungeon.RotateLeftKey))
            {
                _detail = "waiting for the game window to turn left";
                _waitUntil = now + 400;
                return true;
            }

            _sweepStartedAt = now;
            Log?.Invoke(LogLevel.Info,
                $"Turning left ('{dungeon.RotateLeftKey}') for {dungeon.RotateLeftSweepMs / 1000.0:0.#}s to put the searchlight on north.");
            _detail = "turning left to north";
            _waitUntil = now + 200;
            return true;
        }

        long elapsed = now - _sweepStartedAt;
        if (elapsed < dungeon.RotateLeftSweepMs)
        {
            _detail = $"turning left {elapsed / 1000.0:0.0}s / {dungeon.RotateLeftSweepMs / 1000.0:0.#}s";
            _waitUntil = now + 200;
            return true;
        }

        _input.KeyUp(dungeon.RotateLeftKey);
        _sweepStartedAt = 0;
        _sweepDone = true;
        Log?.Invoke(LogLevel.Info, $"Finished the left sweep ({elapsed}ms) - checking the searchlight.");
        _waitUntil = now + Math.Max(250, _settings.Sequence.RotateSettleMs);
        return true;
    }

    /// <summary>Releases a key that may still be held if the run is stopped mid-sweep.</summary>
    private void ReleaseSweepKey()
    {
        ReleaseHeldSkill();
        if (_sweepStartedAt == 0)
            return;

        _sweepStartedAt = 0;
        _input.KeyUp(_settings.Dungeon.RotateLeftKey);
    }

    /// <summary>Turns by a signed number of degrees along the forward key / rightward drag axis.</summary>
    private int ApplyTurn(double signedDegrees)
    {
        if (_settings.Sequence.RotationMode == RotationMode.MouseDrag)
        {
            int pixels = (int)Math.Clamp(signedDegrees * _turnRate, -600, 600);
            if (Math.Abs(pixels) < 8)
                pixels = signedDegrees >= 0 ? 8 : -8;

            var cursor = _analyzer.CompassRegion(_lastFrameWidth, _lastFrameHeight);
            int duration = Math.Clamp(Math.Abs(pixels) * 3, 120, 700);
            if (!_input.Drag(cursor.CenterX, cursor.CenterY, cursor.CenterX + pixels, cursor.CenterY, duration))
                return 0;

            Log?.Invoke(LogLevel.Trace, $"Turn drag {pixels}px over {duration}ms.");
            return duration;
        }

        string key = signedDegrees >= 0 ? _settings.Input.RotateKey : _settings.Input.RotateKeyReverse;
        int holdMs = (int)Math.Clamp(Math.Abs(signedDegrees) / Math.Max(5, _turnRate) * 1000.0, 80, 1800);

        if (!_input.KeyDown(key))
            return 0;

        Thread.Sleep(holdMs);
        _input.KeyUp(key);
        Log?.Invoke(LogLevel.Trace, $"Turn key '{key}' held for {holdMs}ms ({signedDegrees:0}deg).");
        return holdMs;
    }

    // ---------------------------------------------------------------- dungeon flow

    /// <summary>
    /// The real scroll scores ~0.83 at 1024x768, but with the inventory closed something on the
    /// town screen matched at 0.70 (the general icon threshold): the pilot then thought the
    /// inventory was open, skipped 'I' and right-clicked the top-right corner.
    /// </summary>
    private const double ScrollThreshold = 0.78;

    /// <summary>
    /// The loading screen itself is still and passes the "settled" check, so the zoom-out waits
    /// ArrivalDelayMs to land on the labyrinth, not on the loading screen (user).
    /// </summary>
    private void AdvanceAfterLoad()
    {
        var next = NextAfterLoad();
        Advance(next, next == PilotPhase.ZoomOut ? _settings.Dungeon.ArrivalDelayMs : 200);
    }

    private PilotPhase NextAfterLoad()
        => _returning ? PilotPhase.Done
            : !_settings.Dungeon.Enabled ? PilotPhase.FaceNorth
            : _settings.Dungeon.ZoomOutNotches > 0 || _settings.Dungeon.NorthOnArrival ? PilotPhase.ZoomOut
            : PilotPhase.WaitForGate;

    /// <summary>
    /// Just after the labyrinth has loaded: zoom out with the mouse wheel, then face north at once
    /// (user) - the camera keeps that heading, so after stage 4 it already looks at the gate.
    /// </summary>
    private async Task TickZoomOutAsync(Mat frame)
    {
        var dungeon = _settings.Dungeon;

        // The load check passes on the still loading screen, and loads are sometimes slower: wait
        // for the zone name on the minimap (user) before zooming - never zoom without it.
        if (!_arrivalConfirmed && !await MinimapTitleContainsAsync(frame, Destination))
        {
            long waited = Environment.TickCount64 - _phaseStart;

            // The teleport did nothing (no Labyrinth key, user): the town is still on the minimap.
            // Not after 5 s: with the server lagging (ping "--ms" in the top bar) the teleport only
            // went through later and the run had already failed (run 31) - 20 s, 45 s while lagging.
            long limit = await IsLaggingAsync(frame) ? 45000 : 20000;
            bool inTown = waited > limit && await IsInTownAsync(frame);
            if (inTown)
                _townSeenAfterTeleport = _phaseStart;

            // Once the town was seen after this teleport, one missed read must not zoom in town.
            if (inTown || (_townSeenAfterTeleport == _phaseStart && !IsLod))
            {
                // The teleport is what tells whether a key is left (user; the item list misread it):
                // Labyrinth - still in town after Sequence.LabyKeyWaitSeconds = out of Temple of
                // Fiends Keys -> Auto LOD. Waiting covers a lagging server (user: 2 min is enough).
                long townLimit = IsLod ? limit : Math.Max(limit, _settings.Sequence.LabyKeyWaitSeconds * 1000L);
                if (waited <= townLimit)
                {
                    _detail = $"still in {HomeTown} after the teleport - waiting ({waited / 1000}s of {townLimit / 1000}s)";
                    _waitUntil = Environment.TickCount64 + 1000;
                    return;
                }

                if (!IsLod)
                {
                    DebugArtifacts.SaveFrame(frame, _overlays, "out_of_keys");
                    OutOfKeys(labyrinth: true, $"still in {HomeTown} {waited / 1000}s after the teleport - out of Temple of Fiends Keys");
                    return;
                }

                Fail(frame, $"Still in {HomeTown} after the teleport - the destination is closed?");
                return;
            }

            // Zoom only once the Labyrinth / LOD has loaded (user): no "zoom anyway". Not there and
            // not in town for this long -> fail (the restart's zone check sorts out where we are).
            long arriveLimit = Math.Max(60000, _settings.Sequence.LabyKeyWaitSeconds * 1000L);
            if (waited < arriveLimit)
            {
                _detail = $"waiting for '{Destination}' on the minimap ({waited / 1000.0:0.0}s)";
                _waitUntil = Environment.TickCount64 + 500;
                return;
            }

            Fail(frame, $"'{Destination}' never showed on the minimap after the teleport ({waited / 1000}s).");
            return;
        }

        // Once confirmed, a blocked zoom below must not re-check and re-log every tick.
        _arrivalConfirmed = true;
        _zoneConfirmedAt = Environment.TickCount64;

        if (dungeon.ZoomOutNotches > 0)
        {
            // The game zooms with Ctrl + wheel (user); wheel down = out.
            int x = frame.Width / 2;
            int y = (int)(frame.Height * 0.40);
            if (!_input.CtrlWheel(x, y, -dungeon.ZoomOutNotches))
            {
                _waitUntil = Environment.TickCount64 + 300;
                return;
            }

            Log?.Invoke(LogLevel.Info, $"Zoomed out (Ctrl + {dungeon.ZoomOutNotches} wheel notches down).");
        }

        if (IsLod)
        {
            BeginFaceSouth();
            return;
        }

        if (dungeon.NorthOnArrival)
        {
            _earlyNorth = true;
            _compassAnchored = false;
            _bestFlashlightScore = 0;
            _bestFlashlightStep = 0;
            _tapsSinceBest.Clear();
            Advance(PilotPhase.FaceNorth, 400);
            return;
        }

        Advance(PilotPhase.WaitForGate, 300);
    }

    /// <summary>The teleport flow goes to the dungeon, or back to town after the final boss.</summary>
    private string CurrentDestination => _returning ? HomeTown : Destination;

    /// <summary>The character's home town: the one last seen on the minimap, else the setting.</summary>
    private string HomeTown => _homeTown
                               ?? (string.IsNullOrWhiteSpace(_settings.Dungeon.ReturnDestination) ? null : _settings.Dungeon.ReturnDestination)
                               ?? _settings.Dungeon.HomeTowns.FirstOrDefault()
                               ?? "Piercing City";

    /// <summary>Which home town <paramref name="text"/> names (any clan's), remembering it; null for none.</summary>
    private string? TownIn(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var towns = _settings.Dungeon.HomeTowns.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(_settings.Dungeon.ReturnDestination))
            towns = towns.Append(_settings.Dungeon.ReturnDestination);

        string? found = towns.FirstOrDefault(t => ZoneMatches(text, t));
        if (found != null)
            _homeTown = found;
        return found;
    }

    /// <summary>Stage index past the map stages = the 5th boss reached on foot.</summary>
    private bool IsFinalBoss => _stageIndex >= _settings.Dungeon.Stages.Count;

    /// <summary>
    /// Stages 1-4: one held skill press on the boss before J (user, 2026-10-27: "on other stages,
    /// before going autohunt, try to skill hold first") - the switch, a skill key and a hold length.
    /// </summary>
    private bool HoldBeforeAutohunt =>
        _settings.Dungeon.HoldBeforeAutohunt
        && !string.IsNullOrWhiteSpace(_settings.Dungeon.SkillKey)
        && _settings.Dungeon.HoldSkillMs > 0;

    private string StageName(int index)
        => index < _settings.Dungeon.Stages.Count ? _settings.Dungeon.Stages[index] : _settings.Dungeon.FinalBoss;

    /// <summary>
    /// Waits for the "DUNGEON GATE OPENED!" banner. The template the user cropped is tried on
    /// every poll (fast); OCR of the same banner text runs every few polls as a fallback.
    /// </summary>
    private async Task TickWaitForGateAsync(Mat frame)
    {
        var dungeon = _settings.Dungeon;
        _gatePolls++;

        string? banner = await FindGateBannerAsync(frame, _gatePolls % Math.Max(1, dungeon.GateOcrEveryNPolls) == 0);
        if (banner != null)
        {
            GateOpened(banner);
            return;
        }

        long elapsed = Environment.TickCount64 - _phaseStart;
        if (_stageIndex > 0 && dungeon.GateFallbackMs > 0 && elapsed > dungeon.GateFallbackMs)
        {
            // Opening the map while the last boss still fights would skip it: look first.
            var previous = await FindBossAsync(frame, _stageIndex - 1);
            if (previous.Found)
            {
                _stageIndex--;
                Log?.Invoke(LogLevel.Warning,
                    $"No gate banner, but the stage {_stageIndex + 1} boss is still up ({previous.Text}) - watching it again.");
                ResetBossState();
                Advance(PilotPhase.WatchBoss, 0);
                return;
            }

            Log?.Invoke(LogLevel.Warning,
                $"No gate banner seen {elapsed / 1000.0:0}s after the stage {_stageIndex} boss died - opening the map anyway.");
            DebugArtifacts.SaveFrame(frame, _overlays, $"nogate_s{_stageIndex + 1}");
            _mapReopens = 0;
            _mapVisibleMisses = 0;
            Advance(PilotPhase.ClickMap, 0);
            return;
        }

        if (elapsed > dungeon.GateGiveUpMs)
        {
            Fail(frame, $"No gate banner after {elapsed / 1000.0:0}s (stage {_stageIndex + 1}). " +
                        "Check the boss actually died, or widen Regions.GateBanner - press F11 while the banner is up.");
            return;
        }

        if (elapsed > dungeon.GateTimeoutMs && !_gateWarned)
        {
            _gateWarned = true;
            Log?.Invoke(LogLevel.Warning,
                $"No gate banner after {elapsed / 1000.0:0}s - still waiting (F10 stops the run).");
        }

        _detail = $"waiting for the gate ({elapsed / 1000.0:0.0}s)";
        _waitUntil = Environment.TickCount64 + dungeon.GatePollMs;
    }

    /// <summary>
    /// Looks for the "DUNGEON GATE OPENED!" banner: template on every call, OCR only when
    /// <paramref name="useOcr"/>. With <paramref name="newTextOnly"/> an OCR hit only counts when
    /// there are more matching lines than on the first call (<see cref="_watchGateLines"/>), so an
    /// old line in the chat log cannot end a boss fight that has only just started.
    /// </summary>
    /// <returns>How the banner was found, or <c>null</c>.</returns>
    private async Task<string?> FindGateBannerAsync(Mat frame, bool useOcr, bool newTextOnly = false)
    {
        var dungeon = _settings.Dungeon;
        var roi = _settings.Regions.GateBanner.ToPixels(frame.Width, frame.Height);

        if (!string.IsNullOrWhiteSpace(dungeon.GateTemplate) &&
            _analyzer.Library.TryGet(dungeon.GateTemplate, out _))
        {
            var (match, ms) = _analyzer.FindIconTimed(frame, dungeon.GateTemplate, roi);
            if (match != null)
            {
                _overlays.Add(new OverlayItem(match.Bounds, $"gate {match.Score:0.00}", OverlayColor.Green));
                return $"banner template {match.Score:0.00} in {ms:0}ms";
            }
        }

        if (!useOcr || string.IsNullOrWhiteSpace(dungeon.GateText))
            return null;

        if (!newTextOnly)
        {
            var (hit, _, _) = await _analyzer.FindTextAsync(frame, roi, dungeon.GateText);
            if (hit == null || !IsFullName(hit, dungeon.GateText))
                return null;

            _overlays.Add(new OverlayItem(hit.Bounds, $"'{hit.Text}'", OverlayColor.Green));
            return $"banner text '{hit.Text}' ({hit.Similarity:0.00})";
        }

        int lines = await CountTextLinesAsync(frame, roi, dungeon.GateText);
        if (_watchGateLines < 0)
        {
            _watchGateLines = lines;
            return null;
        }

        if (lines > _watchGateLines)
            return $"banner text '{dungeon.GateText}' (new line)";

        // A line scrolling out of the chat log must not hide the next new one.
        _watchGateLines = Math.Min(_watchGateLines, lines);
        return null;
    }

    private void GateOpened(string how)
    {
        int total = Math.Max(1, _settings.Dungeon.Stages.Count);
        Log?.Invoke(LogLevel.Success, $"Gate open - stage {_stageIndex + 1}/{total} ({how}).");

        // Let the kill animation / walk to the gate finish before opening the map: clicking too
        // early leaves the character with "no walkable path" and the row click does nothing.
        int delay = _settings.Dungeon.GateToMapDelayMs;
        if (delay > 0)
            Log?.Invoke(LogLevel.Info, $"Waiting {delay / 1000.0:0.#}s before opening the map.");

        _mapReopens = 0;
        _mapVisibleMisses = 0;
        Advance(PilotPhase.ClickMap, delay);
    }

    /// <summary>
    /// True when the map window is on screen: the list's "Spawns" header reads in
    /// <c>Regions.MapScroll</c> (the paging anchor; similarity &gt;= 0.90) and at least one row '>'
    /// matches in <c>Regions.MapBossList</c>. Until 2026-10-26 two chevron hits at 0.50 were enough,
    /// and the 'x' of "Drop rate x 2 Event", the ')' of the item list's "(time(s))" lines and the
    /// boss's patterned body passed for them: the pilot then clicked the list positions in the game
    /// world (user: their own mouse move / click while the map opened had left it shut). Over the
    /// 429 saved frames: all 219 with the map open read "Spawns" (0.93-1.00, 12 ms median), none of
    /// the others; three or more chevrons in one column alone had 38 false columns (--mapopen probe).
    /// </summary>
    private async Task<bool> MapWindowVisibleAsync(Mat frame)
    {
        var scrollRoi = _settings.Regions.MapScroll.ToPixels(frame.Width, frame.Height);
        var (spawns, _, _) = await _analyzer.FindTextAsync(frame, scrollRoi, "Spawns");
        if (spawns == null || spawns.Similarity < 0.90)
            return false;

        var listRoi = _settings.Regions.MapBossList.ToPixels(frame.Width, frame.Height);
        return _analyzer.FindIcons(frame, "rightchevron", listRoi, 0.55, 3).Count >= 1;
    }

    private async Task TickClickMapAsync(Mat frame)
    {
        var dungeon = _settings.Dungeon;

        // Pure diagnostics: if the game is complaining that the character has nowhere to walk,
        // the stage row click will do nothing, so it is worth seeing in the log.
        if (!string.IsNullOrWhiteSpace(dungeon.NoPathText) && !_noPathLogged)
        {
            var msgRoi = _settings.Regions.SystemMessage.ToPixels(frame.Width, frame.Height);
            var (msg, _, _) = await _analyzer.FindTextAsync(frame, msgRoi, dungeon.NoPathText);
            if (msg != null)
            {
                _noPathLogged = true;
                _overlays.Add(new OverlayItem(msg.Bounds, $"'{msg.Text}'", OverlayColor.Yellow));
                Log?.Invoke(LogLevel.Warning,
                    $"Game notice: '{msg.Text}' - the character was sent somewhere it cannot walk to.");
            }
        }

        var roi = _settings.Regions.MapIcon.ToPixels(frame.Width, frame.Height);

        // The button sits in the very corner of the panel and is often partly clipped by the
        // client edge, which costs a lot of correlation - so it gets a softer floor than icons
        // that are fully on screen. The region keeps it from matching anything else.
        double threshold = Math.Max(0.50, _settings.Detection.TemplateThreshold - 0.10);
        var (match, ms) = _analyzer.FindIconTimed(frame, "mapicon", roi, threshold);

        if (match == null)
        {
            _detail = "map button not found";
            Retry(frame, "mapicon.png");
            return;
        }

        _overlays.Add(new OverlayItem(match.Bounds, $"map {match.Score:0.00}", OverlayColor.Green));
        _detail = $"map icon {match.Score:0.00} in {ms:0}ms";

        if (!Click(match.Bounds))
            return;

        Log?.Invoke(LogLevel.Success, $"Clicked the map button (score {match.Score:0.00}, match {ms:0.0}ms).");
        _mapScrolls = 0;
        Advance(_settings.Dungeon.Enabled ? PilotPhase.ReadStage : PilotPhase.Done,
            _settings.Dungeon.Enabled ? _settings.Dungeon.MapOpenDelayMs : _settings.Input.AfterClickMs);
    }

    /// <summary>
    /// Reads the boss name of the current stage from the map's monster list, scrolling the list
    /// with the wheel when the name is not on the visible page, then clicks that row's '>'.
    /// </summary>
    private async Task TickReadStageAsync(Mat frame)
    {
        var dungeon = _settings.Dungeon;
        if (dungeon.Stages.Count == 0)
        {
            Advance(PilotPhase.AdvanceStage, 0);
            return;
        }

        _stageIndex = Math.Clamp(_stageIndex, 0, dungeon.Stages.Count - 1);
        string expected = dungeon.Stages[_stageIndex];
        var roi = _settings.Regions.MapBossList.ToPixels(frame.Width, frame.Height);

        // The map button click can be swallowed (the game is still busy after the kill). If the
        // window is not there at all, click the button again instead of scrolling a dead frame.
        if (!await MapWindowVisibleAsync(frame))
        {
            // One confirmation poll first: clicking the button twice would toggle the map shut.
            if (++_mapVisibleMisses < 2)
            {
                _detail = "waiting for the map window to appear";
                _waitUntil = Environment.TickCount64 + 700;
                return;
            }

            if (_mapReopens < dungeon.MapReopenRetries)
            {
                _mapReopens++;
                _mapVisibleMisses = 0;
                Log?.Invoke(LogLevel.Warning,
                    $"The map window is not open - clicking the map button again ({_mapReopens}/{dungeon.MapReopenRetries}).");
                Advance(PilotPhase.ClickMap, 400);
                return;
            }

            // No clicking without the map (user, 2026-10-26): a boss name read here with the map
            // shut is the name tag in the game world, and a click on it walks the character.
            Fail(frame, "The map window never opened. Widen Regions.MapBossList or raise Dungeon.MapReopenRetries " +
                        "- press F11 with the map open to check the region.");
            return;
        }

        _mapVisibleMisses = 0;

        var (hit, _, _) = await _analyzer.FindTextAsync(frame, roi, expected);

        // Only a (nearly) whole boss name counts: the fuzzy matcher accepts fragments, and the
        // '[Rage' of a [Rage Elixir] item text once passed for 'Ragesoul Heathen' (run 23, stage 4).
        if (hit != null && !IsFullName(hit, expected))
            hit = null;

        // Different resolutions move the map window around, so before paging the list try one
        // generous region. If the name turns up there, say where - that is the correction for
        // Regions.MapBossList.
        if (hit == null && _attempts == 0 && _mapScrolls == 0)
        {
            var wide = new NormRect(0.32, 0.08, 0.68, 0.86).ToPixels(frame.Width, frame.Height);
            var (wideHit, _, _) = await _analyzer.FindTextAsync(frame, wide, expected);
            if (wideHit != null && IsFullName(wideHit, expected))
            {
                _overlays.Add(new OverlayItem(wideHit.Bounds, $"'{wideHit.Text}' outside the list region", OverlayColor.Yellow));
                Log?.Invoke(LogLevel.Warning,
                    $"Found '{wideHit.Text}' at {wideHit.Bounds.X},{wideHit.Bounds.Y} " +
                    $"({wideHit.Bounds.Width}x{wideHit.Bounds.Height}) - outside Regions.MapBossList. " +
                    "Widen that region to cover it.");
                hit = wideHit;
            }
        }

        if (hit != null)
        {
            _overlays.Add(new OverlayItem(hit.Bounds, $"stage {_stageIndex + 1}: {hit.Text}", OverlayColor.Green));
            Log?.Invoke(LogLevel.Success, $"Map shows stage {_stageIndex + 1}: '{hit.Text}' ({hit.Similarity:0.00}).");
            ClickStageRow(frame, hit.Bounds, hit.Text);
            return;
        }

        // Not on the visible part of the list: page it with the up/down arrows next to "Spawns"
        // (the wheel does the same thing if the arrows cannot be matched).
        if (_mapScrolls < dungeon.MapScrollMaxSteps && dungeon.MapScrollMaxSteps > 0)
        {
            // The map always opens with the list at the top, so only ever page down - clicking an
            // up arrow that is not even drawn (it is hidden at the top) would just be a stray click.
            bool back = false;
            string glyph = back ? "upchevron" : "downchevron";
            var scrollRoi = _settings.Regions.MapScroll.ToPixels(frame.Width, frame.Height);

            // The arrows are tiny and the map window is laid out differently at every resolution,
            // so the light glyphs just above the "Spawns" header are the most reliable anchor.
            var (upArrow, downArrow) = await _analyzer.FindMapScrollArrowsAsync(frame, scrollRoi);
            PixelRect? arrowRect = back ? upArrow : downArrow;
            string how = arrowRect != null ? "above 'Spawns'" : string.Empty;

            // Second choice: the rotated-chevron template inside the scroll band.
            if (arrowRect == null)
            {
                // Real arrows score 0.85+; a 0.53 look-alike far right of the 1920x1080 map was clicked 30 times.
                var arrow = _analyzer.FindIcon(frame, glyph, scrollRoi, 0.75);
                if (arrow != null)
                {
                    arrowRect = arrow.Bounds;
                    how = $"{glyph} {arrow.Score:0.00}";
                }
            }

            if (arrowRect != null)
            {
                _overlays.Add(new OverlayItem(arrowRect.Value, $"{(back ? "up" : "down")} ({how})", OverlayColor.Yellow));
                if (!Click(arrowRect.Value))
                    return;

                Log?.Invoke(LogLevel.Info,
                    $"'{expected}' is not visible - clicked the {(back ? "up" : "down")} arrow ({how}, {_mapScrolls + 1}/{dungeon.MapScrollMaxSteps}).");
            }
            else
            {
                int wheelX = (int)Math.Round(frame.Width * dungeon.MapWheelX);
                int wheelY = (int)Math.Round(frame.Height * dungeon.MapWheelY);
                if (!_input.Wheel(wheelX, wheelY, back ? dungeon.MapScrollNotches : -dungeon.MapScrollNotches))
                {
                    _waitUntil = Environment.TickCount64 + 400;
                    return;
                }

                Log?.Invoke(LogLevel.Info,
                    $"'{expected}' is not visible - scrolled {(back ? "up" : "down")} ({_mapScrolls + 1}/{dungeon.MapScrollMaxSteps}).");
            }

            _mapScrolls++;
            _waitUntil = Environment.TickCount64 + dungeon.MapScrollSettleMs;
            return;
        }

        // A different stage name means the game is ahead/behind us - say so instead of guessing.
        foreach (var (name, index) in dungeon.Stages.Select((n, i) => (n, i)))
        {
            if (index == _stageIndex)
                continue;

            var (other, _, _) = await _analyzer.FindTextAsync(frame, roi, name);
            if (other == null)
                continue;

            Log?.Invoke(LogLevel.Warning,
                $"Map shows '{other.Text}' (stage {index + 1}) but '{expected}' (stage {_stageIndex + 1}) was expected.");
            break;
        }

        if (++_attempts > _settings.Sequence.MaxRetries)
        {
            // One more full cycle (map icon -> list) before giving up: the list scroll position
            // and the boss names are reset when the map is reopened.
            if (_mapReopens < dungeon.MapReopenRetries)
            {
                _mapReopens++;
                Log?.Invoke(LogLevel.Warning,
                    $"'{expected}' was not in the list - reopening the map ({_mapReopens}/{dungeon.MapReopenRetries}).");
                Advance(PilotPhase.ClickMap, 600);
                return;
            }

            Fail(frame, $"Could not find '{expected}' in the map's monster list after scrolling " +
                        $"{dungeon.MapScrollMaxSteps} time(s). Check Regions.MapBossList - press F11 with the map open.");
            return;
        }

        _mapScrolls = 0;
        _detail = $"reading the map for '{expected}' (retry {_attempts}/{_settings.Sequence.MaxRetries})";
        _waitUntil = Environment.TickCount64 + 700;
    }


    /// <summary>Clicks the '>' at the right edge of the row the boss name is on.</summary>
    private void ClickStageRow(Mat frame, PixelRect nameBounds, string text)
    {
        // Selecting the row (a click on the boss name) makes the game highlight it, which is what
        // rightchevronfoc.png is cropped from - that is what the next step clicks.
        if (_settings.Dungeon.ClickBossName)
        {
            int x = nameBounds.CenterX;
            int y = nameBounds.CenterY;
            SaveRowClickSnapshot(frame, x, y, $"select_{text}");

            if (!_input.Click(x, y))
            {
                _detail = "row click blocked - waiting for the game to be in front";
                _waitUntil = Environment.TickCount64 + 300;
                return;
            }

            _lastClick = (x, y, Environment.TickCount64);
            _chevronClicked = false;
            _rowNameMisses = 0;
            Log?.Invoke(LogLevel.Success, $"Selected the '{text}' row at {x},{y} - clicking its highlighted '>' next.");
            Advance(PilotPhase.AdvanceStage, _settings.Dungeon.BossNameClickDelayMs);
            return;
        }

        // Fallback when the name must not be clicked: the old chevron hunt.
        ClickRowChevronFallback(frame, nameBounds, text);
    }

    /// <summary>
    /// Finds the '>' of the selected row with rightchevronfoc.png. The selected row is the only
    /// one drawn with the highlighted background, so this is unique on the map; the row that was
    /// just clicked is preferred when several match.
    /// </summary>
    private TemplateMatch? FindFocusedChevron(Mat frame, PixelRect rowBounds)
    {
        var dungeon = _settings.Dungeon;
        string name = string.IsNullOrWhiteSpace(dungeon.RowChevronTemplate)
            ? "rightchevronfoc"
            : dungeon.RowChevronTemplate;

        if (!_analyzer.Library.TryGet(name, out _))
            return null;

        var listRoi = _settings.Regions.MapBossList.ToPixels(frame.Width, frame.Height);
        var hits = _analyzer.FindIcons(frame, name, listRoi, dungeon.RowChevronThreshold, 8);

        if (hits.Count == 0)
        {
            // The list region may not cover the map window at this resolution - widen once.
            var wide = new NormRect(0.45, 0.10, 0.55, 0.85).ToPixels(frame.Width, frame.Height);
            hits = _analyzer.FindIcons(frame, name, wide, dungeon.RowChevronThreshold, 8);
        }

        if (hits.Count == 0)
            return null;

        // Guard against matching world text just outside the map panel: the selected row's '>'
        // must sit in the same column as the other rows' chevrons.
        var columnHits = _analyzer.FindIcons(frame, "rightchevron", listRoi, 0.55, 24);
        int? columnX = null;
        if (columnHits.Count >= 3)
        {
            var xs = columnHits.Select(m => m.Bounds.CenterX).OrderBy(v => v).ToArray();
            columnX = xs[xs.Length / 2];
            int slack = Math.Max(12, (int)(columnX.Value * 0.02));
            var nearColumn = hits.Where(m => Math.Abs(m.Bounds.CenterX - columnX.Value) <= slack).ToList();
            if (nearColumn.Count > 0)
                hits = nearColumn;
        }

        // Row band: half the spacing between the list's chevrons, so the neighbouring rows can never
        // fall inside it. The closest '>' wins, not the best score - the highlighted row's '>'
        // scores ~0.99 while a plain neighbour scores 1.00.
        int band = Math.Max(12, rowBounds.Height * 2);
        var ys = columnHits.Select(m => m.Bounds.CenterY).OrderBy(v => v).ToArray();
        var gaps = ys.Zip(ys.Skip(1), (a, b) => b - a).Where(d => d > 5).OrderBy(d => d).ToArray();
        if (gaps.Length > 0)
            band = Math.Max(6, gaps[gaps.Length / 2] * 45 / 100);

        var onRow = hits.Where(m => Math.Abs(m.Bounds.CenterY - rowBounds.CenterY) <= band).ToList();
        if (onRow.Count > 0)
            return onRow.OrderBy(m => Math.Abs(m.Bounds.CenterY - rowBounds.CenterY)).ThenByDescending(m => m.Score).First();

        // Never take a '>' from another row: every row has one and they all score ~0.99, so the
        // best hit elsewhere is the neighbour (Spectral once got Sundersword's '>' one row lower).
        // The highlighted row's '>' can fail the template; its position is known anyway - the
        // chevron column at the OCR'd row's height.
        if (columnX == null)
            return null;

        var like = hits[0].Bounds;
        var bounds = new PixelRect(columnX.Value - like.Width / 2, rowBounds.CenterY - like.Height / 2, like.Width, like.Height);
        Log?.Invoke(LogLevel.Info, $"No '>' matched on the selected row; using the chevron column at {bounds.CenterX},{bounds.CenterY}.");
        return new TemplateMatch(name, 0, bounds, hits[0].Scale);
    }

    /// <summary>Old behaviour: colour, then template column, then the name itself.</summary>
    private void ClickRowChevronFallback(Mat frame, PixelRect nameBounds, string text)
    {
        var dungeon = _settings.Dungeon;
        int rowY = nameBounds.CenterY;

        int clickX;
        int clickY = rowY;

        // 1) Colour: the '>' is a light glyph on a dark panel and is the right-most glyph on the
        //    row. This needs no template, so it works at every resolution and UI scale.
        PixelRect? glyph = FindRowGlyph(frame, nameBounds);

        // 2) Template: the whole list gives a column of '>', so use their median x with the OCR
        //    row y (the map window does not have to fill the client).
        var listRoi = _settings.Regions.MapBossList.ToPixels(frame.Width, frame.Height);
        var hits = glyph == null
            ? _analyzer.FindIcons(frame, "rightchevron", listRoi, 0.50, 24)
                       .Where(m => m.Bounds.CenterX > nameBounds.CenterX + 4)
                       .ToList()
            : new List<TemplateMatch>();

        if (glyph != null)
        {
            clickX = glyph.Value.CenterX;
            clickY = glyph.Value.CenterY;
            _overlays.Add(new OverlayItem(glyph.Value, "row > (colour)", OverlayColor.Green));
            Log?.Invoke(LogLevel.Success,
                $"Row '>' for '{text}' found by colour at {clickX},{clickY} ({glyph.Value.Width}x{glyph.Value.Height}).");
        }
        else if (hits.Count > 0)
        {
            var xs = hits.Select(m => m.Bounds.CenterX).OrderBy(x => x).ToArray();
            int columnX = xs[xs.Length / 2];
            clickX = columnX;
            clickY = rowY;

            // Draw the column so a wrong spot is obvious in the F11 snapshot.
            foreach (var m in hits)
                _overlays.Add(new OverlayItem(m.Bounds, $"{m.Score:0.00}", OverlayColor.Yellow));

            var nearest = hits.OrderBy(m => Math.Abs(m.Bounds.CenterY - rowY)).First();
            _overlays.Add(new OverlayItem(
                new PixelRect(columnX - 14, rowY - 14, 28, 28), $"row > col {columnX}", OverlayColor.Green));

            Log?.Invoke(LogLevel.Success,
                $"{hits.Count} row chevron(s) found, column x={columnX} (nearest {nearest.Bounds.CenterX},{nearest.Bounds.CenterY} {nearest.Score:0.00}); " +
                $"clicking the row of '{text}' at {clickX},{clickY}.");
        }
        else
        {
            clickX = nameBounds.CenterX;
            Log?.Invoke(LogLevel.Info,
                $"No row chevron matched in the map list for '{text}'; clicking the name itself at {clickX},{clickY}.");
        }

        SaveRowClickSnapshot(frame, clickX, clickY, text);

        if (!_input.Click(clickX, clickY))
        {
            _detail = "row click blocked - waiting for the game to be in front";
            _waitUntil = Environment.TickCount64 + 300;
            return;
        }

        _lastClick = (clickX, clickY, Environment.TickCount64);
        // A '>' click (glyph or column) is expected to close the map; a click on the name is not.
        _chevronClicked = glyph != null || hits.Count > 0;
        _rowNameMisses = 0;
        Advance(PilotPhase.AdvanceStage, dungeon.BossNameClickDelayMs);
    }

    /// <summary>
    /// The row's '>' found by brightness instead of a template: on the row's band, to the right
    /// of the name, the right-most small light glyph is the chevron. Text height (from OCR) is
    /// the only size reference, so this scales with whatever resolution the game runs at.
    /// </summary>
    private PixelRect? FindRowGlyph(Mat frame, PixelRect nameBounds)
    {
        int textH = Math.Max(10, nameBounds.Height);
        int left = Math.Min(frame.Width - 4, Math.Max(0, nameBounds.Right + 2));
        int top = Math.Max(0, nameBounds.CenterY - textH);
        int bottom = Math.Min(frame.Height, nameBounds.CenterY + textH);
        var roi = new PixelRect(left, top, Math.Max(4, frame.Width - left), Math.Max(4, bottom - top));

        PixelRect? best = null;
        foreach (var blob in _analyzer.FindBrightBlobs(frame, roi))
        {
            // Same order of height as a line of text, and not a long run of text itself.
            if (blob.Height < textH * 0.5 || blob.Height > textH * 2.2)
                continue;

            if (blob.Width > blob.Height * 1.8)
                continue;

            // A panel border or scrollbar sits on the strip's right edge - never the row's '>'.
            if (blob.Right > roi.Right - 3)
                continue;

            _overlays.Add(new OverlayItem(blob, $"{blob.Width}x{blob.Height}", OverlayColor.Yellow));

            if (best == null || blob.Right > best.Value.Right)
                best = blob;
        }

        return best;
    }

    /// <summary>Saves the frame with the click position drawn, so a misplaced click can be seen.</summary>
    private void SaveRowClickSnapshot(Mat frame, int x, int y, string text)
    {
        if (_rowSnapshots >= 6)
            return;

        try
        {
            _rowSnapshots++;
            string file = DebugArtifacts.SaveFrame(frame, _overlays, $"rowclick_s{_stageIndex + 1}_{Sanitize(text)}", (x, y));
            if (!string.IsNullOrEmpty(file))
                Log?.Invoke(LogLevel.Info, $"Saved a snapshot of the row click: {file}");
        }
        catch
        {
            // diagnostics only
        }
    }

    private static string Sanitize(string value)
    {
        var chars = value.Where(char.IsLetterOrDigit).Take(16).ToArray();
        return chars.Length == 0 ? "boss" : new string(chars);
    }

    /// <summary>
    /// After the row is selected: clicks its highlighted '>' (rightchevronfoc.png). The map closes
    /// when that click registers; if it did not, the chevron is clicked again before giving up.
    /// </summary>
    private async Task TickAdvanceStageAsync(Mat frame)
    {
        var dungeon = _settings.Dungeon;
        string? expected = _stageIndex < dungeon.Stages.Count ? dungeon.Stages[_stageIndex] : null;
        if (expected == null)
        {
            Advance(PilotPhase.WaitBoss, 300);
            return;
        }

        var roi = _settings.Regions.MapBossList.ToPixels(frame.Width, frame.Height);
        var (stillThere, _, _) = await _analyzer.FindTextAsync(frame, roi, expected);
        bool mapOpen = await MapWindowVisibleAsync(frame);
        // The list band also covers the item list on the right ("[Rage Elixir] 25 (min(s))"
        // reads as a partial "Ragesoul Heathen"), so only trust a full name with the map open.
        if (stillThere != null && (!IsFullName(stillThere, expected) || !mapOpen))
            stillThere = null;

        if (stillThere == null)
        {
            if (_chevronClicked)
            {
                Log?.Invoke(LogLevel.Info, "The map closed after the '>' click; waiting for the spawn.");
                Advance(PilotPhase.WaitBoss, 300);
                return;
            }

            // The name click only highlights the row - the '>' is what closes the map. The map gone
            // before that click means something else shut it (user, 2026-10-26: a mouse move / click
            // of their own while the pilot opened it); the old flow then waited for a boss that was
            // never picked. Map still open but the name unread -> a few re-reads, then the list again.
            if (mapOpen)
            {
                if (++_rowNameMisses <= 3)
                {
                    _detail = $"map open, '{expected}' not read ({_rowNameMisses}/3)";
                    _waitUntil = Environment.TickCount64 + 500;
                    return;
                }

                Log?.Invoke(LogLevel.Warning, $"'{expected}' is no longer read on the open map after the row click - reading the list again.");
                _mapScrolls = 0;
                Advance(PilotPhase.ReadStage, 300);
                return;
            }

            if (_mapReopens < dungeon.MapReopenRetries)
            {
                _mapReopens++;
                _mapVisibleMisses = 0;
                Log?.Invoke(LogLevel.Warning,
                    $"The map is gone before the '>' click (a stray click or mouse move?) - opening it again ({_mapReopens}/{dungeon.MapReopenRetries}).");
                Advance(PilotPhase.ClickMap, 500);
                return;
            }

            Log?.Invoke(LogLevel.Warning, $"The map is gone before the '>' click and was reopened {dungeon.MapReopenRetries}x already; waiting for the spawn anyway.");
            Advance(PilotPhase.WaitBoss, 300);
            return;
        }

        if (!_chevronClicked)
        {
            var chevron = FindFocusedChevron(frame, stillThere.Bounds);
            if (chevron != null)
            {
                _overlays.Add(new OverlayItem(chevron.Bounds, $"focus > {chevron.Score:0.00}", OverlayColor.Green));
                if (!Click(chevron.Bounds))
                    return;

                _chevronClicked = true;
                Log?.Invoke(LogLevel.Success,
                    $"Clicked the highlighted '>' of '{stillThere.Text}' at {chevron.Bounds.CenterX},{chevron.Bounds.CenterY} ({chevron.Score:0.00}).");

                if (_rowSnapshots < 6)
                {
                    _rowSnapshots++;
                    string file = DebugArtifacts.SaveFrame(frame, _overlays,
                        $"chevron_s{_stageIndex + 1}_{Sanitize(stillThere.Text)}",
                        (chevron.Bounds.CenterX, chevron.Bounds.CenterY));
                    if (!string.IsNullOrEmpty(file))
                        Log?.Invoke(LogLevel.Info, $"Saved a snapshot of the chevron click: {file}");
                }

                _waitUntil = Environment.TickCount64 + dungeon.BossNameClickDelayMs;
                return;
            }

            if (++_attempts > 3)
            {
                Log?.Invoke(LogLevel.Warning,
                    $"No highlighted '>' matched ({dungeon.RowChevronTemplate}.png, score {dungeon.RowChevronThreshold:0.00}) - " +
                    "falling back to the chevron hunt.");
                _attempts = 0;
                ClickRowChevronFallback(frame, stillThere.Bounds, stillThere.Text);
                return;
            }

            _detail = $"looking for the highlighted '>' ({_attempts}/3)";
            _waitUntil = Environment.TickCount64 + 500;
            return;
        }

        if (++_attempts > 3)
        {
            Log?.Invoke(LogLevel.Warning, "The map is still open after the chevron click; carrying on anyway.");
            Advance(PilotPhase.WaitBoss, 300);
            return;
        }

        Log?.Invoke(LogLevel.Info, $"Map still open - clicking the highlighted '>' again ({_attempts}/3).");
        _chevronClicked = false;
        _waitUntil = Environment.TickCount64 + 400;
    }


    /// <summary>
    /// Waits out the spawn timer, turns the autohunt on (J - stages 1-4 only, and only without a held
    /// skill press first), presses Tab for the monster marker, and once the boss has been visible
    /// for a moment hands over to the select step.
    /// </summary>
    private async Task TickWaitBossAsync(Mat frame)
    {
        var dungeon = _settings.Dungeon;
        long elapsed = Environment.TickCount64 - _phaseStart;
        // The final boss is already standing there when the run arrives.
        long spawnWait = IsFinalBoss ? 0 : dungeon.BossSpawnWaitMs;

        // Stage 5, first thing at the waypoint (user, 2026-10-26: the boss "still manages to get
        // stuck on autohunt but not attacking it" - "when in Waypoint 1, disable auto hunt"): the J
        // after stage 4 is a blind toggle, so the combat log decides whether the autohunt is really
        // off before the Tab / skill selection starts.
        if (IsFinalBoss && !_finalHuntOffChecked)
        {
            if (dungeon.FinalBossPressAutohunt && !string.IsNullOrWhiteSpace(dungeon.AutohuntKey))
            {
                if (!await EnsureAutohuntOffAsync(frame, "at the final boss", trustVisibleLine: true))
                    return;
                _autohuntPressed = false;
            }

            _finalHuntOffChecked = true;
            _phaseStart = Environment.TickCount64;
            elapsed = 0;
        }

        // Stages 1-4: autohunt on after the spawn wait, as it always was (user: that works) - unless
        // a held skill press comes first (HoldBeforeAutohunt, user 2026-10-27: "on other stages,
        // before going autohunt, try to skill hold first"); J then follows the hold in SelectBoss.
        // Stage 5 only after the boss is attacked by hand (SelectBoss -> EngageAutohunt) - it
        // engaged there before the character was at the boss (user).
        if (!IsFinalBoss && !_autohuntPressed && !HoldBeforeAutohunt && elapsed >= spawnWait)
        {
            if (!string.IsNullOrWhiteSpace(dungeon.AutohuntKey) && _input.KeyTap(dungeon.AutohuntKey))
            {
                _autohuntPressed = true;
                Log?.Invoke(LogLevel.Success, $"Pressed '{dungeon.AutohuntKey}' to turn the autohunt on (stage {_stageIndex + 1}).");
                _waitUntil = Environment.TickCount64 + 600;
                return;
            }
        }

        if (elapsed < spawnWait)
        {
            _detail = $"waiting for the boss {dungeon.BossSpawnWaitMs / 1000}s ({elapsed / 1000:0}s, stage {_stageIndex + 1})";
            _waitUntil = Environment.TickCount64 + 1000;
            return;
        }

        if (!_tabPressed)
        {
            if (_input.KeyTap(dungeon.TabKey))
            {
                _tabPressed = true;
                _lastWaitTabAt = Environment.TickCount64;
                _waitTabs = 1;
                Log?.Invoke(LogLevel.Info, $"Pressed '{dungeon.TabKey}' to show the monster marker/name.");
                _waitUntil = Environment.TickCount64 + dungeon.TabSettleMs;
                return;
            }
        }

        var seen = await FindBossAsync(frame);
        if (seen.Found)
        {
            _bossFirstSeenAt ??= Environment.TickCount64;
            long visibleMs = Environment.TickCount64 - _bossFirstSeenAt.Value;

            if (visibleMs < dungeon.BossSelectDelayMs)
            {
                _detail = $"boss up, letting it settle ({visibleMs}ms): {seen.Text}";
                _waitUntil = Environment.TickCount64 + 250;
                return;
            }

            Log?.Invoke(LogLevel.Success, $"Boss for stage {_stageIndex + 1} is up: {seen.Text}.");
            // Reference picture of the final boss (user): cropped into finalboss.png it becomes the
            // first select click below.
            if (IsFinalBoss)
                DebugArtifacts.SaveFrame(frame, _overlays, "stage5_boss");
            Advance(PilotPhase.SelectBoss, 0);
            return;
        }

        // At the waypoint the boss only spawns with "DUNGEON GATE OPENED" (user): the 15 s before the
        // fallback count from that banner (new lines only), or 60 s without one.
        if (IsFinalBoss && _fromWaypoint && !_runToTargetPending && _waypointGateAt == null)
        {
            string? banner = await FindGateBannerAsync(frame, useOcr: _waypointGatePolls++ % 2 == 0, newTextOnly: true);
            if (banner != null)
            {
                _waypointGateAt = Environment.TickCount64;
                Log?.Invoke(LogLevel.Info, $"Gate banner at the waypoint ({banner}) - the boss spawns now, still tabbing.");
            }
        }

        bool spawnWaitOver = _waypointGateAt is long gateAt ? Environment.TickCount64 - gateAt > 15000 : elapsed > 60000;

        // Tabbing from the waypoint found nothing (boss out of range): autohunt off again and walk
        // to the boss coordinates; arriving there starts this wait again.
        if (IsFinalBoss && _fromWaypoint && _runToTargetPending == false && spawnWaitOver && dungeon.WaypointWalkFallback)
        {
            if (_autohuntPressed && !string.IsNullOrWhiteSpace(dungeon.AutohuntKey) && !_input.KeyTap(dungeon.AutohuntKey))
            {
                _waitUntil = Environment.TickCount64 + 300;
                return;
            }

            _autohuntPressed = false;
            _runToTargetPending = true;
            Log?.Invoke(LogLevel.Warning, $"No '{dungeon.FinalBoss}' after {elapsed / 1000:0}s of Tab at the waypoint - walking to {dungeon.TargetX},{dungeon.TargetY}.");
            _stalledSteps = 0;
            _lastCoords = null;
            Advance(PilotPhase.RunToTarget, 300);
            return;
        }

        // Stage 5 never turns the autohunt on without a hit by hand first (user, 2026-10-26): it keeps
        // tabbing until the boss shows or the game closes the stage (zone guard -> next run).
        if (!IsFinalBoss && elapsed > spawnWait + 90000)
        {
            if (!EngageAutohunt())
            {
                _waitUntil = Environment.TickCount64 + 300;
                return;
            }

            Log?.Invoke(LogLevel.Warning,
                $"No monster marker or boss name after {elapsed / 1000:0}s; autohunt on, watching anyway.");
            _bossMissingSince = null;
            Advance(PilotPhase.WatchBoss, 0);
            return;
        }

        // One Tab is not always enough: on arrival at the final boss the character was still
        // walking and the name only showed 25 s later (user, run 23). Keep pressing it.
        long now = Environment.TickCount64;
        // Every second (user; was 2.5 s).
        if (now - _lastWaitTabAt >= 1000 && _input.KeyTap(dungeon.TabKey))
        {
            _lastWaitTabAt = now;
            _tabPressed = true;
            if (++_waitTabs == 2)
                Log?.Invoke(LogLevel.Info,
                    $"No boss name/marker yet - pressing '{dungeon.TabKey}' again every second (stage {_stageIndex + 1}).");
            _waitUntil = now + Math.Min(dungeon.TabSettleMs, 500);
            return;
        }

        _detail = $"waiting for the monster marker (stage {_stageIndex + 1}, {_waitTabs} Tab)";
        _waitUntil = now + 300;
    }

    /// <summary>
    /// The game's autohunt does not actually attack the boss until it has been selected, so the
    /// pilot left clicks around the middle of the window - the centre first, then rings around
    /// it - until a new "Attack succeeded!" line shows in the combat log on the left. WatchBoss then just waits for the
    /// name/marker to disappear.
    /// </summary>
    private async Task TickSelectBossAsync(Mat frame)
    {
        var dungeon = _settings.Dungeon;
        bool confirm = !string.IsNullOrWhiteSpace(dungeon.AttackConfirmText);
        var msgRoi = _settings.Regions.AttackMessage.ToPixels(frame.Width, frame.Height);
        _overlays.Add(new OverlayItem(msgRoi, "attack message", OverlayColor.Blue));

        // The combat log on the left keeps the last ~8 "Attack succeeded!... damage(+N)" lines and
        // is usually full, so counting lines cannot see a new hit. Instead the visible lines
        // (text + damage number, top to bottom) are remembered, and any change after a click
        // means a new hit scrolled in.
        if (confirm && !_selectBaselineTaken)
        {
            _attackLog = await ReadAttackLogAsync(frame, msgRoi, dungeon.AttackConfirmText);
            _selectBaselineTaken = true;
        }

        if (IsFinalBoss && _huntOn)
        {
            await TickAutohuntConfirmAsync(frame, msgRoi);
            return;
        }

        if (_manualPresses >= 0)
        {
            await TickManualAttackAsync(frame, msgRoi, confirm);
            return;
        }

        // Stage 5 (user, 2026-10-27): the boss is in view (WaitBoss saw its name) - attack by hand
        // straight away: hold the skill key, tap it ManualAttacks times, and only a new attack line
        // lets the autohunt on. The selection ring is not read any more (it was reported missing
        // with the ring plainly on screen), and nothing is clicked while the autohunt is off.
        if (IsFinalBoss && !string.IsNullOrWhiteSpace(dungeon.SkillKey))
        {
            StartManualAttack($"'{dungeon.FinalBoss}' in view");
            return;
        }

        // Stages 1-4 (user, 2026-10-27: "on other stages, before going autohunt, try to skill hold
        // first"): the boss in view -> one held skill press (the character runs at the boss and
        // attacks it), then J, then the centre clicks as before. The hold's last hits still print
        // for a moment after J, so the log baseline for the clicks is taken again after a settle.
        if (!IsFinalBoss && !_autohuntPressed && HoldBeforeAutohunt)
        {
            if (!_heldBeforeHunt)
            {
                if (_holdUntil == null)
                {
                    if (!StartHeldSkill())
                        return;

                    Log?.Invoke(LogLevel.Info,
                        $"Holding '{dungeon.SkillKey}' for {dungeon.HoldSkillMs} ms on the stage {_stageIndex + 1} boss before the autohunt.");
                    return;
                }

                if (!TickHeldSkill())
                    return;

                _heldBeforeHunt = true;
            }

            if (!EngageAutohunt())
            {
                _waitUntil = Environment.TickCount64 + 300;
                return;
            }

            _selectBaselineTaken = false;
            _detail = "autohunt on - letting the held attack's hits settle before the select clicks";
            _waitUntil = Environment.TickCount64 + AutohuntSettleMs;
            return;
        }

        if (_selectClickedAt.HasValue)
        {
            string log = await ReadAttackLogAsync(frame, msgRoi, dungeon.AttackConfirmText);
            if (log.Length > 0 && log != _attackLog && !IsFinalBoss)
            {
                // Stages 1-4: the old flow (user) - the autohunt is on already (WaitBoss, or J after
                // the held press), just watch. A no-op then; J only when the hold switch was turned
                // off between WaitBoss and here.
                if (!EngageAutohunt())
                {
                    _waitUntil = Environment.TickCount64 + 300;
                    return;
                }

                Log?.Invoke(LogLevel.Success,
                    $"'{dungeon.AttackConfirmText}' - boss selected on click {_selectTries} at {_lastClick?.X},{_lastClick?.Y}.");
                Advance(PilotPhase.WatchBoss, 500);
                return;
            }

            if (log.Length > 0 && log != _attackLog)
            {
                // Stage 5 without a skill key: the click hit, autohunt on and the clicks again.
                _attackLog = log;
                StartManualAttack($"'{dungeon.AttackConfirmText}' on click {_selectTries} at {_lastClick?.X},{_lastClick?.Y}");
                return;
            }

            if (Environment.TickCount64 - _selectClickedAt.Value < dungeon.AttackConfirmMs)
            {
                _detail = $"waiting for '{dungeon.AttackConfirmText}' (click {_selectTries}/{dungeon.BossSelectMaxTries})";
                _waitUntil = Environment.TickCount64 + 250;
                return;
            }

            _selectClickedAt = null;
        }

        if (_selectTries >= dungeon.BossSelectMaxTries)
        {
            // Stage 5 before the autohunt: no hit from the clicks = not selected, and the autohunt
            // must not go on without the selection ring (user, 2026-10-26) - Tab for the boss again.
            if (IsFinalBoss)
            {
                Log?.Invoke(LogLevel.Warning,
                    $"No '{dungeon.AttackConfirmText}' after {_selectTries} click(s) around the centre - autohunt stays off; Tab for '{dungeon.FinalBoss}' again.");
                DebugArtifacts.SaveFrame(frame, _overlays, $"attack_s{_stageIndex + 1}", _lastClick is { } c5 ? (c5.X, c5.Y) : null);
                ArriveAtFinalBoss();
                return;
            }

            Log?.Invoke(LogLevel.Warning,
                $"No '{dungeon.AttackConfirmText}' after {_selectTries} click(s) around the centre; watching the boss anyway.");
            if (!EngageAutohunt())
            {
                _waitUntil = Environment.TickCount64 + 300;
                return;
            }

            DebugArtifacts.SaveFrame(frame, _overlays, $"attack_s{_stageIndex + 1}", _lastClick is { } c ? (c.X, c.Y) : null);
            Advance(PilotPhase.WatchBoss, 500);
            return;
        }

        var (x, y) = SelectClickPoint(frame.Width, frame.Height, _selectTries);

        // A cropped picture of the final boss (finalboss.png, from a stage5_boss snapshot) is
        // clicked first; the ring around the centre stays the fallback.
        if (IsFinalBoss && _selectTries < 3 && _analyzer.Library.TryGet("finalboss", out _))
        {
            var body = _analyzer.FindIconAnyScale(frame, "finalboss", new PixelRect(0, 0, frame.Width, frame.Height), 0.60).Match;
            if (body != null)
            {
                x = Math.Clamp(body.Bounds.CenterX, (int)(frame.Width * 0.25), (int)(frame.Width * 0.75));
                y = Math.Clamp(body.Bounds.CenterY, (int)(frame.Height * 0.15), (int)(frame.Height * 0.78));
                _overlays.Add(new OverlayItem(body.Bounds, $"finalboss {body.Score:0.00}", OverlayColor.Green));
            }
        }

        if (!_input.Click(x, y))
        {
            _detail = "boss select click blocked - waiting for the game to be in front";
            _waitUntil = Environment.TickCount64 + 300;
            return;
        }

        _selectTries++;
        _lastClick = (x, y, Environment.TickCount64);
        _overlays.Add(new OverlayItem(new PixelRect(x - 6, y - 6, 12, 12), $"select {_selectTries}", OverlayColor.Yellow));

        if (!confirm)
        {
            Log?.Invoke(LogLevel.Success, $"Left clicked the middle of the window ({x},{y}) to select the boss.");
            Advance(PilotPhase.WatchBoss, 500);
            return;
        }

        Log?.Invoke(LogLevel.Info, $"Select click {_selectTries}/{dungeon.BossSelectMaxTries} at {x},{y}.");
        _selectClickedAt = Environment.TickCount64;
        _waitUntil = _selectClickedAt.Value + 300;
    }

    private void StartManualAttack(string why)
    {
        var dungeon = _settings.Dungeon;
        _manualPresses = 0;
        _manualHit = false;
        _holdUntil = null;
        Log?.Invoke(LogLevel.Success,
            $"Attacking '{dungeon.FinalBoss}' by hand ({why}): '{dungeon.SkillKey}' held {dungeon.HoldSkillMs} ms, then {SkillText(dungeon.SkillKey)} x{dungeon.ManualAttacks} ({dungeon.ManualAttackIntervalMs} ms apart); the autohunt goes on after a new '{dungeon.AttackConfirmText}'.");
        _waitUntil = Environment.TickCount64 + 200;
    }

    /// <summary>
    /// One manual attack press: the skill key, then Ctrl + skill key with
    /// <see cref="DungeonSettings.CtrlSkill"/> (user, 2026-10-26: "when manually attacking couple it
    /// with ctrl + skill key"). False only when the plain key was blocked; a blocked Ctrl tap after it
    /// still counts as the press.
    /// </summary>
    private bool TapSkill(string key)
    {
        if (!_input.KeyTap(key))
            return false;

        if (_settings.Dungeon.CtrlSkill)
        {
            // Let the game see the plain key go up before the same key goes down with Ctrl.
            Thread.Sleep(CtrlSkillGapMs);
            _input.CtrlKeyTap(key);
        }

        return true;
    }

    private const int CtrlSkillGapMs = 60;

    /// <summary>A skill press for the log: '1', or '1' + Ctrl+1 with CtrlSkill.</summary>
    private string SkillText(string key) => _settings.Dungeon.CtrlSkill ? $"'{key}' + Ctrl+{key}" : $"'{key}'";

    /// <summary>
    /// Starts a held skill press: the key goes down now and stays down until <c>HoldSkillMs</c> is
    /// over (<see cref="TickHeldSkill"/>). Between ticks <see cref="ReleaseHeldSkill"/> lets go of it
    /// on Stop / a phase change, like the LOD hold. False while the key is blocked (retried next tick).
    /// </summary>
    private bool StartHeldSkill()
    {
        var dungeon = _settings.Dungeon;
        long now = Environment.TickCount64;
        if (!_input.KeyDown(dungeon.SkillKey))
        {
            _detail = "skill key blocked - waiting for the game to be in front";
            _waitUntil = now + 300;
            return false;
        }

        _heldSkill = dungeon.SkillKey;
        _holdUntil = now + dungeon.HoldSkillMs;
        _detail = $"holding '{dungeon.SkillKey}' (0/{dungeon.HoldSkillMs} ms)";
        _waitUntil = now + 250;
        return true;
    }

    /// <summary>
    /// Ticks a held skill press: false while the key is still down (waits), true once it has been
    /// let go - followed by Ctrl + skill key with <see cref="DungeonSettings.CtrlSkill"/>.
    /// </summary>
    private bool TickHeldSkill()
    {
        var dungeon = _settings.Dungeon;
        long now = Environment.TickCount64;
        if (_holdUntil is not long holdUntil)
            return true;

        if (now < holdUntil)
        {
            _detail = $"holding '{dungeon.SkillKey}' ({now - (holdUntil - dungeon.HoldSkillMs)}/{dungeon.HoldSkillMs} ms)";
            _waitUntil = Math.Min(holdUntil, now + 250);
            return false;
        }

        ReleaseHeldSkill();
        _holdUntil = null;
        if (dungeon.CtrlSkill)
        {
            Thread.Sleep(CtrlSkillGapMs);
            _input.CtrlKeyTap(dungeon.SkillKey);
        }

        return true;
    }

    /// <summary>
    /// Stage 5, the boss in view (user, 2026-10-27: "Attack Hold skill, then do 10x attack (total
    /// 11); check if has attack succeeded, then auto hunt then click on the boss"): press 1 holds
    /// the skill key down for <c>HoldSkillMs</c> (the character runs at the target and keeps
    /// attacking - it stood off at a distance with taps alone), then <c>ManualAttacks</c> taps at
    /// least <c>ManualAttackIntervalMs</c> apart. Every press ends with Ctrl + skill key
    /// (<see cref="DungeonSettings.CtrlSkill"/>). Only a new "Attack succeeded" line after the last
    /// press counts (user, 2026-10-23); none <c>ManualAttackWaitMs</c> later -> more taps, up to 3x as
    /// many in all; still none -> Tab and another round (hold + taps, <see cref="MaxManualRounds"/>),
    /// then back to the Tab wait with the autohunt still off (<c>nohit_s5</c> snapshot). A hit -> J,
    /// then the centre clicks (<see cref="TickAutohuntConfirmAsync"/>). The selection ring and the
    /// target bar are not read any more (user, same day: "it says it didn't see the pink ring, but it
    /// is there").
    /// </summary>
    private async Task TickManualAttackAsync(Mat frame, PixelRect msgRoi, bool confirm)
    {
        var dungeon = _settings.Dungeon;
        long now = Environment.TickCount64;
        int taps = string.IsNullOrWhiteSpace(dungeon.SkillKey) ? 0 : dungeon.ManualAttacks;
        bool hold = taps > 0 && dungeon.HoldSkillMs > 0;
        int wanted = taps + (hold ? 1 : 0);
        const int maxRounds = MaxManualRounds;

        // Press 1 = the hold: the key is down between two ticks, so Stop / a phase change can let
        // go of it (ReleaseHeldSkill) and the preview keeps running.
        if (_holdUntil != null)
        {
            if (!TickHeldSkill())
                return;

            _manualPresses = 1;
            _lastManualPressAt = Environment.TickCount64;
            _detail = $"attacking by hand: '{dungeon.SkillKey}' held {dungeon.HoldSkillMs} ms (1/{wanted})";
            _waitUntil = _lastManualPressAt + dungeon.ManualAttackIntervalMs;
            return;
        }

        // Read every tick, so the baseline is current when the last press goes out.
        if (confirm && _manualPresses > 0)
        {
            string log = await ReadAttackLogAsync(frame, msgRoi, dungeon.AttackConfirmText);
            if (log.Length > 0 && log != _attackLog)
            {
                _attackLog = log;
                _manualHit = true;
            }
        }

        // Only a hit after the last press counts (user): before it, forget earlier hits. Then wait
        // ManualAttackWaitMs (500, user) and look for a new "Attack succeeded"; none -> tap again,
        // up to 3x as many.
        bool needPress = _manualPresses < wanted;
        if (!needPress && wanted > 0 && now - _lastManualPressAt < dungeon.ManualAttackWaitMs)
        {
            _detail = $"waiting {dungeon.ManualAttackWaitMs}ms after press {_manualPresses}";
            _waitUntil = _lastManualPressAt + dungeon.ManualAttackWaitMs;
            return;
        }

        if (!needPress && confirm && !_manualHit && _manualPresses < wanted * 3)
            needPress = true;

        if (needPress)
        {
            // At least ManualAttackIntervalMs between two presses (user: 300 ms or more), measured
            // from the press itself - the log OCR above takes a varying part of each tick.
            if (_manualPresses > 0 && now - _lastManualPressAt < dungeon.ManualAttackIntervalMs)
            {
                _waitUntil = _lastManualPressAt + dungeon.ManualAttackIntervalMs;
                return;
            }

            if (_manualPresses >= wanted - 1)
                _manualHit = false;

            if (_manualPresses == 0 && hold)
            {
                StartHeldSkill();
                return;
            }

            if (!TapSkill(dungeon.SkillKey))
            {
                _detail = "skill key blocked - waiting for the game to be in front";
                _waitUntil = now + 300;
                return;
            }

            _manualPresses++;
            _lastManualPressAt = Environment.TickCount64;
            _detail = $"attacking by hand: '{dungeon.SkillKey}' {_manualPresses}/{wanted}" + (_manualRounds > 0 ? $" (round {_manualRounds + 1}/{maxRounds})" : "");
            _waitUntil = _lastManualPressAt + dungeon.ManualAttackIntervalMs;
            return;
        }

        // Stage 5: a new "Attack succeeded" line after the last press, or no check (user, 2026-10-27:
        // "check if has attack succeeded, then auto hunt"). None -> Tab and another round (no centre
        // clicks while the autohunt is off - those run after J, in TickAutohuntConfirmAsync). After
        // 3 rounds back to the Tab wait, autohunt still off.
        if (IsFinalBoss && wanted > 0 && confirm && !_manualHit)
        {
            if (++_manualRounds < maxRounds)
            {
                if (!_input.KeyTap(dungeon.TabKey))
                {
                    _manualRounds--;
                    _detail = "tab key blocked - waiting for the game to be in front";
                    _waitUntil = now + 300;
                    return;
                }

                Log?.Invoke(LogLevel.Warning,
                    $"No new '{dungeon.AttackConfirmText}' after {_manualPresses} press(es) of '{dungeon.SkillKey}' - '{dungeon.TabKey}' and another round ({_manualRounds + 1}/{maxRounds}: hold + {taps} taps) before the autohunt.");
                _manualPresses = 0;
                _manualHit = false;
                _waitUntil = now + dungeon.TabSettleMs;
                return;
            }

            Log?.Invoke(LogLevel.Warning,
                $"No new '{dungeon.AttackConfirmText}' after {maxRounds} rounds of '{dungeon.SkillKey}' (hold + {taps} taps) - autohunt stays off; Tab for '{dungeon.FinalBoss}' again.");
            if (_stuckShots++ < 3)
                DebugArtifacts.SaveFrame(frame, _overlays, "nohit_s5");
            ArriveAtFinalBoss();
            return;
        }

        if (!EngageAutohunt())
        {
            _waitUntil = now + 300;
            return;
        }

        Log?.Invoke(_manualHit || !confirm ? LogLevel.Success : LogLevel.Warning,
            $"Attacked by hand {_manualPresses}x ({(_manualHit ? "hit" : confirm ? "no hit seen" : "no check")}) - autohunt on.");

        // Stage 5: J drops the target (user, 2026-10-27) - click on the boss next, until a new attack line.
        if (IsFinalBoss && confirm)
        {
            _huntOn = true;
            _huntOnAt = Environment.TickCount64;
            _huntBaselineAt = null;
            _huntClicks = 0;
            _huntClickAt = null;
            _manualPresses = -1;
            _attackLog = await ReadAttackLogAsync(frame, msgRoi, dungeon.AttackConfirmText);
            _detail = "autohunt on - letting the manual attacks' hits settle before the clicks";
            _waitUntil = now + 250;
            return;
        }

        Advance(PilotPhase.WatchBoss, 500);
    }

    /// <summary>Stage 5: the autohunt may select the boss and attack it by hand this many times in a row before the pilot goes back to the Tab wait.</summary>
    private const int MaxManualRounds = 3;

    /// <summary>After J: attack lines this old are the held press' / manual attacks' own late hits, not the autohunt's (stage 5, and stages 1-4 with HoldBeforeAutohunt).</summary>
    private const int AutohuntSettleMs = 1000;

    /// <summary>
    /// Stage 5, autohunt just turned on (user, 2026-10-27: "then auto hunt then click on the boss";
    /// earlier that day: "after enabling J, it unselected the boss, add the click the middle to try
    /// to select the boss"). After <c>AutohuntSettleMs</c> of silence (the manual attacks' late hits)
    /// the combat log is the baseline, then the stage 1-4 centre clicks (<see cref="SelectClickPoint"/>,
    /// finalboss.png first) go out at once, one every <c>AttackConfirmMs</c>; a new attack line after
    /// any of them -> WatchBoss. All <c>BossSelectMaxTries</c> clicks without one -> J (off), another
    /// round of manual attacks (hold + taps, then J and the clicks again); after
    /// <see cref="MaxManualRounds"/> rounds back to the Tab wait with the autohunt off.
    /// </summary>
    private async Task TickAutohuntConfirmAsync(Mat frame, PixelRect msgRoi)
    {
        var dungeon = _settings.Dungeon;
        long now = Environment.TickCount64;

        // The manual attacks' last skill animations still print "Attack succeeded" for a moment
        // after J, so a line right after J proves nothing (user, 2026-10-27: v0.9.3 still ended
        // under the boss with the autohunt on and no attack). The log is read again AutohuntSettleMs
        // after J and only lines newer than that count.
        if (now - _huntOnAt < AutohuntSettleMs)
        {
            _detail = $"autohunt on - letting the manual attacks' hits settle ({now - _huntOnAt}/{AutohuntSettleMs} ms)";
            _waitUntil = _huntOnAt + AutohuntSettleMs;
            return;
        }

        string log = await ReadAttackLogAsync(frame, msgRoi, dungeon.AttackConfirmText);
        if (_huntBaselineAt == null)
        {
            _attackLog = log;
            _huntBaselineAt = now;
        }
        else if (log.Length > 0 && log != _attackLog)
        {
            Log?.Invoke(LogLevel.Success,
                $"'{dungeon.AttackConfirmText}' after centre click {_huntClicks} at {_lastClick?.X},{_lastClick?.Y} - the boss is selected, autohunt on, watching '{dungeon.FinalBoss}'.");
            _huntOn = false;
            _huntBaselineAt = null;
            Advance(PilotPhase.WatchBoss, 300);
            return;
        }

        // One click per AttackConfirmMs, the log is read every tick above.
        if (_huntClickAt is long clickedAt && now - clickedAt < dungeon.AttackConfirmMs)
        {
            _detail = $"autohunt on, no '{dungeon.AttackConfirmText}' yet - centre click {_huntClicks}/{dungeon.BossSelectMaxTries} sent, waiting";
            _waitUntil = now + 250;
            return;
        }

        if (_huntClicks < dungeon.BossSelectMaxTries)
        {
            var (x, y) = SelectClickPoint(frame.Width, frame.Height, _huntClicks);
            if (_huntClicks < 3 && _analyzer.Library.TryGet("finalboss", out _))
            {
                var body = _analyzer.FindIconAnyScale(frame, "finalboss", new PixelRect(0, 0, frame.Width, frame.Height), 0.60).Match;
                if (body != null)
                {
                    x = Math.Clamp(body.Bounds.CenterX, (int)(frame.Width * 0.25), (int)(frame.Width * 0.75));
                    y = Math.Clamp(body.Bounds.CenterY, (int)(frame.Height * 0.15), (int)(frame.Height * 0.78));
                    _overlays.Add(new OverlayItem(body.Bounds, $"finalboss {body.Score:0.00}", OverlayColor.Green));
                }
            }

            if (!_input.Click(x, y))
            {
                _detail = "boss select click blocked - waiting for the game to be in front";
                _waitUntil = now + 300;
                return;
            }

            _huntClicks++;
            _huntClickAt = Environment.TickCount64;
            _lastClick = (x, y, _huntClickAt.Value);
            _overlays.Add(new OverlayItem(new PixelRect(x - 6, y - 6, 12, 12), $"select {_huntClicks}", OverlayColor.Yellow));
            if (_huntClicks == 1)
                Log?.Invoke(LogLevel.Info,
                    $"Autohunt on - clicking on '{dungeon.FinalBoss}' round the centre ({x},{y}) until a new '{dungeon.AttackConfirmText}' (up to {dungeon.BossSelectMaxTries} clicks, '{dungeon.AutohuntKey}' drops the target).");
            else
                Log?.Invoke(LogLevel.Info, $"Select click {_huntClicks}/{dungeon.BossSelectMaxTries} at {x},{y} (autohunt on).");
            _waitUntil = _huntClickAt.Value + 300;
            return;
        }

        // Autohunt off again (J is a toggle).
        if (_autohuntPressed && !string.IsNullOrWhiteSpace(dungeon.AutohuntKey) && !_input.KeyTap(dungeon.AutohuntKey))
        {
            _detail = "autohunt key blocked - waiting for the game to be in front";
            _waitUntil = now + 300;
            return;
        }

        _autohuntPressed = false;
        _huntOn = false;
        _huntBaselineAt = null;
        _huntClicks = 0;
        _huntClickAt = null;

        if (++_manualRounds < MaxManualRounds)
        {
            Log?.Invoke(LogLevel.Warning,
                $"No new '{dungeon.AttackConfirmText}' after {dungeon.BossSelectMaxTries} centre click(s) with the autohunt on - '{dungeon.AutohuntKey}' off, attacking by hand again (round {_manualRounds + 1}/{MaxManualRounds}).");
            StartManualAttack($"autohunt did not attack after {dungeon.BossSelectMaxTries} select clicks");
            return;
        }

        Log?.Invoke(LogLevel.Warning,
            $"No new '{dungeon.AttackConfirmText}' with the autohunt on after {MaxManualRounds} rounds - autohunt off; Tab for '{dungeon.FinalBoss}' again.");
        if (_stuckShots++ < 3)
            DebugArtifacts.SaveFrame(frame, _overlays, "nohit_s5");
        ArriveAtFinalBoss();
    }

    /// <summary>Autohunt on (J is a toggle) unless it already is. False while the key is blocked.</summary>
    private bool EngageAutohunt()
    {
        var dungeon = _settings.Dungeon;
        if (_autohuntPressed || string.IsNullOrWhiteSpace(dungeon.AutohuntKey))
            return true;
        if (!_input.KeyTap(dungeon.AutohuntKey))
            return false;

        _autohuntPressed = true;
        Log?.Invoke(LogLevel.Success, $"Pressed '{dungeon.AutohuntKey}' to turn the autohunt on (stage {_stageIndex + 1}).");
        return true;
    }

    /// <summary>
    /// Attempt 0 is <c>BossSelectX/Y</c>; then 8 points one step out (up first, clockwise), then 8
    /// points two steps out, repeating. Kept inside the middle of the client so a miss never lands
    /// on the HUD.
    /// </summary>
    private (int X, int Y) SelectClickPoint(int width, int height, int attempt)
    {
        var dungeon = _settings.Dungeon;
        double cx = width * dungeon.BossSelectX;
        double cy = height * dungeon.BossSelectY;

        int slot = attempt % 17;
        if (slot > 0)
        {
            int ring = (slot - 1) / 8 + 1;
            double angle = (slot - 1) % 8 * Math.PI / 4;
            double r = height * dungeon.BossSelectStep * ring;
            cx += Math.Sin(angle) * r;
            cy -= Math.Cos(angle) * r;
        }

        int x = (int)Math.Round(Math.Clamp(cx, width * 0.25, width * 0.75));
        int y = (int)Math.Round(Math.Clamp(cy, height * 0.15, height * 0.78));
        return (x, y);
    }

    /// <summary>Distinct OCR lines in the region that read like <paramref name="text"/>, top to bottom (drawn as overlays).</summary>
    private async Task<List<(PixelRect Bounds, string Text)>> ReadTextLinesAsync(Mat frame, PixelRect roi, string text)
    {
        var (_, _, words) = await _analyzer.FindTextAsync(frame, roi, text);
        var hits = words
            .GroupBy(w => w.LineBounds)
            .Where(line => TextMatcher.Similarity(line.First().LineText, text) >= _settings.Detection.OcrMinSimilarity
                           && TextMatcher.CoversMost(line.First().LineText, text))
            .Select(line => (Bounds: line.Key, Text: line.First().LineText))
            .OrderBy(line => line.Bounds.Y)
            .ToList();

        foreach (var (bounds, _) in hits)
            _overlays.Add(new OverlayItem(bounds, text, OverlayColor.Green));

        return hits;
    }

    private async Task<int> CountTextLinesAsync(Mat frame, PixelRect roi, string text)
        => (await ReadTextLinesAsync(frame, roi, text)).Count;

    /// <summary>The visible combat-log lines matching <paramref name="text"/>, normalised and joined.</summary>
    private async Task<string> ReadAttackLogAsync(Mat frame, PixelRect roi, string text)
    {
        var lines = await ReadTextLinesAsync(frame, roi, text);
        return string.Join("|", lines.Select(line => AttackLineKey(line.Text, text)));
    }

    /// <summary>
    /// One combat-log line as "attacksucceeded...damage(N)": the semi-transparent log sits over the
    /// moving game world, so stray characters before it and the cut-off "Attribu..." tail OCR
    /// differently from frame to frame and would look like a new hit.
    /// </summary>
    private static string AttackLineKey(string line, string text)
    {
        string start = TextMatcher.Normalize(text);
        string value = TextMatcher.Normalize(line);
        int from = value.IndexOf(start, StringComparison.Ordinal);
        if (from > 0)
            value = value[from..];
        int tail = value.IndexOf("attrib", StringComparison.Ordinal);
        return tail > 0 ? value[..tail] : value;
    }

    /// <summary>
    /// The boss is dead when the gate banner shows (checked every <c>GatePollMs</c>, because it is
    /// only on screen for a few seconds) or when the boss name/marker is gone on two checks in a
    /// row (every <c>BossPollMs</c>).
    /// </summary>
    private async Task TickWatchBossAsync(Mat frame)
    {
        var dungeon = _settings.Dungeon;
        long now = Environment.TickCount64;
        long elapsed = now - _phaseStart;
        _watchPolls++;

        bool useOcr = _watchGateLines < 0 || _watchPolls % Math.Max(1, dungeon.GateOcrEveryNPolls) == 0;
        string? banner = await FindGateBannerAsync(frame, useOcr, newTextOnly: true);
        if (banner != null)
        {
            Log?.Invoke(LogLevel.Success, $"Boss for stage {_stageIndex + 1} is dead - gate open ({banner}).");
            OnLabyBossDead(frame);
            NextStageOrFinish(banner);
            return;
        }

        if (now < _nextBossCheckAt)
        {
            _waitUntil = now + dungeon.GatePollMs;
            return;
        }

        var seen = await FindBossAsync(frame);

        if (seen.Found)
        {
            if (_bossMissingSince.HasValue && _watchTabs > 0)
                Log?.Invoke(LogLevel.Info, $"Boss for stage {_stageIndex + 1} is still up after Tab ({seen.Text}).");
            _bossMissingSince = null;
            _watchTabs = 0;

            // Stage 5 watchdog (user, 2026-10-27: "goes directly below the boss with autohunt on but
            // doesn't target or attack it"): the boss up, the autohunt on, but no new attack line for
            // AutohuntStallMs -> J off, Tab / select / attack by hand again (ArriveAtFinalBoss).
            if (IsFinalBoss && _autohuntPressed && !string.IsNullOrWhiteSpace(dungeon.AttackConfirmText))
            {
                var msgRoi = _settings.Regions.AttackMessage.ToPixels(frame.Width, frame.Height);
                string log = await ReadAttackLogAsync(frame, msgRoi, dungeon.AttackConfirmText);
                if (_watchAttackLog == null || (log.Length > 0 && log != _watchAttackLog))
                {
                    _watchAttackLog = log;
                    _watchAttackAt = now;
                }
                else if (now - _watchAttackAt >= dungeon.AutohuntStallMs)
                {
                    if (!string.IsNullOrWhiteSpace(dungeon.AutohuntKey) && !_input.KeyTap(dungeon.AutohuntKey))
                    {
                        _detail = "autohunt key blocked - waiting for the game to be in front";
                        _waitUntil = now + 300;
                        return;
                    }

                    _autohuntPressed = false;
                    Log?.Invoke(LogLevel.Warning,
                        $"Autohunt on but no new '{dungeon.AttackConfirmText}' for {(now - _watchAttackAt) / 1000:0}s with {seen.Text} up - '{dungeon.AutohuntKey}' off, selecting and attacking by hand again.");
                    if (_stuckShots++ < 3)
                        DebugArtifacts.SaveFrame(frame, _overlays, "stall_s5");
                    ArriveAtFinalBoss();
                    return;
                }
            }
            // No time limit: a slow kill is fine, the dungeon stays open until the boss dies (user,
            // 2026-10-24 - a 15 min limit moved on with the stage 1 boss at 1 %).
            _detail = $"boss up: {seen.Text} ({elapsed / 1000:0}s)";
            _nextBossCheckAt = now + dungeon.BossPollMs;
            _waitUntil = now + dungeon.GatePollMs;
            return;
        }

        _bossMissingSince ??= now;
        long missingMs = now - _bossMissingSince.Value;
        _detail = $"boss not visible for {missingMs / 1000.0:0.0}s (dead after {dungeon.BossGoneMs / 1000}s or the gate banner)";

        // Gone only counts once a Tab actually reached the game during this stretch: with the game
        // in the background every Tab is blocked and the boss may just have lost focus.
        if (missingMs >= dungeon.BossGoneMs && _watchTabs > 0)
        {
            Log?.Invoke(LogLevel.Success,
                $"Boss for stage {_stageIndex + 1} is gone ({missingMs / 1000:0}s without its name/marker, Tab pressed {_watchTabs}x).");
            OnLabyBossDead(frame);
            NextStageOrFinish();
            return;
        }

        // A missing name usually means the boss lost focus, not that it died: press Tab again and
        // look once the marker has had time to show.
        if (now - _lastWatchTabAt >= Math.Max(2000, dungeon.TabSettleMs * 2) && _input.KeyTap(dungeon.TabKey))
        {
            _lastWatchTabAt = now;
            _watchTabs++;
            if (_watchTabs == 1)
                Log?.Invoke(LogLevel.Info, $"Boss for stage {_stageIndex + 1} not visible - pressed '{dungeon.TabKey}' to check it is really gone.");
            _nextBossCheckAt = now + dungeon.TabSettleMs;
            _waitUntil = now + dungeon.GatePollMs;
            return;
        }

        _nextBossCheckAt = now + dungeon.BossPollMs;
        _waitUntil = now + dungeon.GatePollMs;
    }

    /// <param name="gateSeen">How the gate banner was seen while watching the boss, if it was; the
    /// next stage then skips WaitForGate.</param>
    private void NextStageOrFinish(string? gateSeen = null)
    {
        var dungeon = _settings.Dungeon;
        _stageIndex++;
        _mapScrolls = 0;
        _mapReopens = 0;
        _mapVisibleMisses = 0;
        _tabPressed = false;
        _autohuntPressed = false;
        _bossFirstSeenAt = null;
        _gatePolls = 0;
        _gateWarned = false;
        _bossMissingSince = null;
        ResetBossState();

        if (_stageIndex > dungeon.Stages.Count)
        {
            Log?.Invoke(LogLevel.Success, $"Final boss '{dungeon.FinalBoss}' cleared.");
            Advance(PilotPhase.ReturnToTown, 500);
            return;
        }

        if (_stageIndex == dungeon.Stages.Count)
        {
            // Autohunt (still on from stage 4) keeps buffing and fighting and stopped the gate walk
            // (user): off for the run, on again at the final boss (ArriveAtFinalBoss -> WaitBoss).
            if (dungeon.FinalBossPressAutohunt && !string.IsNullOrWhiteSpace(dungeon.AutohuntKey))
            {
                if (!_input.KeyTap(dungeon.AutohuntKey))
                {
                    _waitUntil = Environment.TickCount64 + 300;
                    return;
                }

                Log?.Invoke(LogLevel.Info, $"Pressed '{dungeon.AutohuntKey}' - autohunt off for the run to the final boss.");
            }

            Log?.Invoke(LogLevel.Success, $"All {dungeon.Stages.Count} stages cleared - lining up the searchlight for the run.");
            Advance(PilotPhase.FaceNorth, 800);
            return;
        }

        if (gateSeen != null)
        {
            GateOpened(gateSeen);
            return;
        }

        Log?.Invoke(LogLevel.Info, $"Waiting for the stage {_stageIndex + 1} gate.");
        Advance(PilotPhase.WaitForGate, 0);
    }

    /// <summary>Boss alive = the Tab marker is on screen, or the boss name is readable anywhere.</summary>
    private async Task<(bool Found, string Text)> FindBossAsync(Mat frame, int? stageIndex = null)
    {
        var dungeon = _settings.Dungeon;
        int index = stageIndex ?? _stageIndex;

        var iconRoi = _settings.Regions.MonsterIcon.ToPixels(frame.Width, frame.Height);
        var icon = _analyzer.FindIcon(frame, "monstericon", iconRoi);
        if (icon != null)
        {
            _overlays.Add(new OverlayItem(icon.Bounds, $"monster {icon.Score:0.00}", OverlayColor.Green));
            return (true, $"monster icon {icon.Score:0.00}");
        }

        string name = index >= 0 ? StageName(index) : string.Empty;
        if (!string.IsNullOrWhiteSpace(name))
        {
            string expected = name;
            var textRoi = _settings.Regions.BossName.ToPixels(frame.Width, frame.Height);
            var (hit, _, _) = await _analyzer.FindTextAsync(frame, textRoi, expected);
            if (hit != null && IsFullName(hit, expected))
            {
                _overlays.Add(new OverlayItem(hit.Bounds, hit.Text, OverlayColor.Green));
                return (true, $"'{hit.Text}' ({hit.Similarity:0.00})");
            }
        }

        return (false, string.Empty);
    }

    /// <summary>
    /// Fuzzy matching accepts any 4+ letter piece of a name, which made the "[Rage Elixir]" buff
    /// label (always on screen, bottom right) count as "Ragesoul Heathen" - the stage 4 boss then
    /// never died. A boss name only counts when most of it was read.
    /// </summary>
    private static bool IsFullName(TextHit hit, string expected)
        => hit.Similarity >= 0.90 || TextMatcher.CoversMost(hit.Text, expected);

    /// <summary>
    /// Run clicks stay below every top bar and panel and left of the minimap: with 15 % / 75 % the
    /// top-right corner (768,115 / 768,215) hit the stat panel's pet (bee) icon instead of the ground.
    /// </summary>
    private const double RunClickTop = 0.25;
    private const double RunClickRight = 0.74;

    private enum RunProbe
    {
        None,
        Up,
        UpRight,
        Steering,
    }

    /// <summary>
    /// Final approach: read the "x , y" readout, learn how a screen click maps to world
    /// coordinates (one probe straight up, one probe up-right), then steer with clicks in the
    /// upper part of the window - the camera faces north by now, so "up" is forward - until the
    /// staging point is reached.
    /// </summary>
    private async Task TickRunToTargetAsync(Mat frame)
    {
        var dungeon = _settings.Dungeon;
        var coords = await _analyzer.ReadCoordinatesAsync(frame);

        if (coords == null)
        {
            // The label is sometimes unreadable for a few frames (beam behind it, effects): the
            // character keeps walking meanwhile, so be patient before giving up.
            if (++_attempts > 40)
            {
                Fail(frame, "Could not read the coordinate display under the minimap. " +
                            "Adjust Regions.Coordinates (Test scan shows the OCR box).");
                return;
            }

            _detail = "coordinate readout not found";
            _waitUntil = Environment.TickCount64 + 700;
            return;
        }

        // A misread digit must not steer: one step moves ~255 units, so a reading more than 500 away
        // from the last one is held back ("432" for 3432 and "9014" for 8014 were both seen). If a
        // misread did get through, the real readings all look like jumps - so two new readings that
        // agree with each other re-anchor the position instead of being refused forever.
        if (_lastCoords is (int lastX, int lastY) && Math.Abs(coords.X - lastX) + Math.Abs(coords.Y - lastY) > 500)
        {
            bool agrees = _jumpCandidate is (int candX, int candY) && Math.Abs(coords.X - candX) + Math.Abs(coords.Y - candY) <= 200;
            _jumpAgreements = agrees ? _jumpAgreements + 1 : 0;

            // OCR drops a digit the same way several frames in a row ("306" for 3306 twice), so a
            // reading with a different number of digits than the last good one needs 5 agreeing
            // readings, a normal jump 2. The 306 misread once steered the character into a wall.
            bool digitsChanged = Digits(coords.X) != Digits(lastX) || Digits(coords.Y) != Digits(lastY);
            int needed = digitsChanged ? 4 : 1;
            if (agrees && _jumpAgreements >= needed)
            {
                Log?.Invoke(LogLevel.Warning, $"Coordinates re-anchored at {coords} (last {lastX},{lastY} looked wrong).");
                _jumpCandidate = null;
                _jumpAgreements = 0;
                _lastCoords = null;
                _probe = RunProbe.None;
            }
            else
            {
                Log?.Invoke(LogLevel.Trace, $"Holding back coordinate reading {coords} (last {lastX},{lastY}).");
                _jumpCandidate = (coords.X, coords.Y);
                if (++_attempts > 40)
                {
                    Fail(frame, $"Coordinates jumped from {lastX},{lastY} to {coords} and never settled.");
                    return;
                }

                _waitUntil = Environment.TickCount64 + 300;
                return;
            }
        }
        else
        {
            _jumpCandidate = null;
            _jumpAgreements = 0;
        }

        var route = dungeon.Route;

        // A waypoint is only a way round a wall: once the character is closer to the boss than the
        // waypoint itself is, it took a shortcut - skip it (it once stood 76 from the boss and kept
        // walking back towards the waypoint behind it into a wall).
        while (_waypoint < route.Count)
        {
            double toFinal = coords.DistanceTo(dungeon.TargetX, dungeon.TargetY);
            var wp = route[_waypoint];
            double wpToFinal = Math.Sqrt(Math.Pow(wp.X - dungeon.TargetX, 2.0) + Math.Pow(wp.Y - dungeon.TargetY, 2.0));
            if (toFinal >= wpToFinal)
                break;

            Log?.Invoke(LogLevel.Info, $"At {coords}: closer to {dungeon.TargetX},{dungeon.TargetY} than waypoint {_waypoint + 1} ({wp}) - skipping it.");
            _waypoint++;
            _stalledSteps = 0;

            // Skipping the last waypoint still means "Tab there" (user): it used to walk on to the
            // boss coordinates (west) instead.
            if (_waypoint >= route.Count && dungeon.TabAtWaypoint && !_fromWaypoint && !string.IsNullOrWhiteSpace(dungeon.FinalBoss))
            {
                _fromWaypoint = true;
                Log?.Invoke(LogLevel.Info, $"Past the last waypoint - Tab for '{dungeon.FinalBoss}', then skill '{dungeon.SkillKey}'.");
                ArriveAtFinalBoss();
                return;
            }
        }

        bool final = _waypoint >= route.Count;
        int targetX = final ? dungeon.TargetX : route[_waypoint].X;
        int targetY = final ? dungeon.TargetY : route[_waypoint].Y;
        int tolerance = final ? dungeon.TargetTolerance : dungeon.WaypointTolerance;
        string targetName = final ? $"{targetX},{targetY}" : $"waypoint {_waypoint + 1} ({targetX},{targetY})";

        double distance = coords.DistanceTo(targetX, targetY);
        bool close = distance <= tolerance;
        _overlays.Add(new OverlayItem(coords.Bounds, $"{coords} d={distance:0}", close ? OverlayColor.Green : OverlayColor.Yellow));
        _detail = $"{coords} -> {targetName} ({distance:0} left, step {_runSteps})";

        if (close && !final)
        {
            // The learned click-to-world mapping stays valid (same camera), so just retarget.
            Log?.Invoke(LogLevel.Success, $"At {coords}: reached {targetName} after {_runSteps} step(s).");
            _waypoint++;
            _stalledSteps = 0;

            // At the last waypoint: Tab until the boss name shows, then the skill key walks there
            // and attacks (user). The coordinate walk is only a fallback with WaypointWalkFallback.
            if (_waypoint >= route.Count && dungeon.TabAtWaypoint && !_fromWaypoint && !string.IsNullOrWhiteSpace(dungeon.FinalBoss))
            {
                _fromWaypoint = true;
                Log?.Invoke(LogLevel.Info, $"At the last waypoint - Tab for '{dungeon.FinalBoss}', then skill '{dungeon.SkillKey}'.");
                ArriveAtFinalBoss();
                return;
            }

            // The boss stands west of the last waypoint: face it so it is in view (user).
            if (_waypoint >= route.Count && dungeon.FaceWestAtWaypoint && !_turnWest)
            {
                _turnWest = true;
                _compassAnchored = false;
                _bestFlashlightScore = 0;
                _bestFlashlightStep = 0;
                _tapsSinceBest.Clear();
                _refineDir = 0;
                Log?.Invoke(LogLevel.Info, "Turning west to face the final boss.");
                Advance(PilotPhase.FaceNorth, 300);
                return;
            }

            _waitUntil = Environment.TickCount64 + 100;
            return;
        }

        if (close)
        {
            Log?.Invoke(LogLevel.Success, $"At {coords}: within {tolerance} of {targetName} after {_runSteps} step(s).");
            if (string.IsNullOrWhiteSpace(dungeon.FinalBoss))
            {
                Advance(PilotPhase.Done, 400);
                return;
            }

            ArriveAtFinalBoss();
            return;
        }

        // Walking to the clicked gate: no clicks while the character still moves; steer once it stops.
        if (_gateWalking)
        {
            // Stopped = still for 3 readings (~3 s) and at least 5 s after the gate click: steering
            // after one still reading clicked into the wall while the character had only paused (user).
            bool still = _lastCoords is (int wx, int wy) && Math.Abs(coords.X - wx) + Math.Abs(coords.Y - wy) < 20;
            _gateStillReadings = still ? _gateStillReadings + 1 : 0;
            bool settled = _lastClick is { } gc && Environment.TickCount64 - gc.Ticks >= 5000;
            if (_gateStillReadings >= 3 && settled)
            {
                _gateWalking = false;
                _gateStillReadings = 0;
                _lastCoords = null;
                Log?.Invoke(LogLevel.Info, $"Gate walk stopped at {coords} ({distance:0} from {targetName}) - steering from here.");
            }
            else
            {
                _lastCoords = (coords.X, coords.Y);
                _attempts = 0;
                Log?.Invoke(LogLevel.Info, $"Run: walking to the gate, at {coords} ({distance:0} from {targetName}).");
                _waitUntil = Environment.TickCount64 + 1000;
                return;
            }
        }

        // Use the movement we just made to learn the screen-to-world mapping.
        if (_lastCoords is (int previousX, int previousY) && _probe != RunProbe.None)
        {
            var delta = ((double)(coords.X - previousX), (double)(coords.Y - previousY));
            if (_probe == RunProbe.Up)
                _runUpProbed = true;
            else if (_probe == RunProbe.UpRight)
                _runRightProbed = true;

            bool moved = Math.Abs(delta.Item1) + Math.Abs(delta.Item2) >= 20;
            if (moved && _probe == RunProbe.Steering && _simpleClick is (double cx, double cy))
                LearnHeadingError((cx, cy), delta);
            else if (moved)
                AddRunSample(_lastOffset, delta, permanent: _probe is RunProbe.Up or RunProbe.UpRight);

            // Walking into a wall still shuffles a few units ((2,-7) was seen), so tiny moves count.
            if (!moved)
            {
                _stalledSteps++;
                // Blocked right next to the boss (it stands on the spot): close enough.
                if (_waypoint >= dungeon.Route.Count && coords.DistanceTo(dungeon.TargetX, dungeon.TargetY) <= 120 && _stalledSteps >= 2)
                {
                    Log?.Invoke(LogLevel.Success, $"At {coords}: blocked {coords.DistanceTo(dungeon.TargetX, dungeon.TargetY):0} from {dungeon.TargetX},{dungeon.TargetY} - close enough.");
                    ArriveAtFinalBoss();
                    return;
                }

                // The final boss came to us: autohunt engaged it ~270 short of its spot and its body
                // blocked the walk, which then failed as "not moving" (run 29 loop 2). Its name on
                // screen (target bar) while stalled = arrived.
                if (_stalledSteps >= 2 && !string.IsNullOrWhiteSpace(dungeon.FinalBoss))
                {
                    var bossRoi = _settings.Regions.BossName.ToPixels(frame.Width, frame.Height);
                    var (bossHit, _, _) = await _analyzer.FindTextAsync(frame, bossRoi, dungeon.FinalBoss);
                    if (bossHit != null && IsFullName(bossHit, dungeon.FinalBoss))
                    {
                        Log?.Invoke(LogLevel.Success, $"At {coords}: '{bossHit.Text}' is already fighting us - arrived.");
                        ArriveAtFinalBoss();
                        return;
                    }
                }

                if (_stalledSteps >= 6)
                {
                    Fail(frame, $"The character is not moving (still at {coords}). Something is blocking the run.");
                    return;
                }
            }
            else
            {
                _stalledSteps = 0;
            }

            _probe = RunProbe.None;
        }

        _attempts = 0;
        Log?.Invoke(LogLevel.Info, Math.Abs(_headingError) < 0.01
            ? $"Run: at {coords} ({distance:0} from {targetName})."
            : $"Run: at {coords} ({distance:0} from {targetName}), heading fix {_headingError * 180 / Math.PI:+0;-0} deg.");
        _lastCoords = (coords.X, coords.Y);
        int centerX = frame.Width / 2;
        int centerY = frame.Height / 2;
        int step = dungeon.RunClickPx;

        // Simple steering (user): we face north, so screen-up is world-north (y down) and
        // screen-right is world-east. Click from the character straight towards the target; the
        // coordinates are only read to correct the direction and to notice arrival.
        if (dungeon.SimpleSteer && _probe == RunProbe.None)
        {
            double needX = targetX - coords.X;
            double needY = targetY - coords.Y;
            double length = Math.Sqrt(needX * needX + needY * needY);
            double reach = step * Math.Clamp(length / 300.0, 0.45, 1.0);

            double ox = needX / length * reach;
            double oy = needY / length * reach;

            // A wall ahead of the diagonal: after two steps that barely moved, try straight ahead.
            if (_stalledSteps >= 2 && needY < -100)
            {
                ox = 0;
                oy = -reach;
            }

            // Still stuck: click closer. With the camera tilted further a full-length click landed
            // in the gate doorway, which is not walkable, six times in a row (run 34).
            if (_stalledSteps >= 2)
            {
                double shorter = _stalledSteps >= 4 ? 0.3 : 0.5;
                ox *= shorter;
                oy *= shorter;
            }

            // "North" is only as good as the searchlight match: a few degrees off made the
            // character drift ~16 units west per step and walk into the west wall after 13 steps
            // (run 24). Turn the click by the angle measured between clicks and actual movement.
            if (_headingError != 0)
            {
                double c = Math.Cos(_headingError), sn = Math.Sin(_headingError);
                (ox, oy) = (ox * c + oy * sn, -ox * sn + oy * c);
            }

            _simpleClick = (ox, oy);

            double scale = 1;
            double roomLeft = centerX - frame.Width * 0.25, roomRight = frame.Width * RunClickRight - centerX;
            double roomUp = centerY - frame.Height * RunClickTop, roomDown = frame.Height * 0.78 - centerY;
            if (ox < 0) scale = Math.Min(scale, roomLeft / -ox);
            if (ox > 0) scale = Math.Min(scale, roomRight / ox);
            if (oy < 0) scale = Math.Min(scale, roomUp / -oy);
            if (oy > 0) scale = Math.Min(scale, roomDown / oy);

            int clickX = (int)Math.Round(centerX + ox * scale);
            int clickY = (int)Math.Round(centerY + oy * scale);
            _probe = RunProbe.Steering;
            MoveTo(clickX, clickY, $"towards {targetName}");
            if (_waitUntil > Environment.TickCount64)
                _waitUntil = Environment.TickCount64 + dungeon.RunDashWaitMs;
            return;
        }

        if (_probe == RunProbe.None)
        {
            if (!_runUpProbed)
            {
                _probe = RunProbe.Up;
                _lastOffset = (0, 1);
                MoveTo(centerX, Math.Max(centerY - step, (int)(frame.Height * RunClickTop)), "up");
                return;
            }

            if (!_runRightProbed)
            {
                _probe = RunProbe.UpRight;
                _lastOffset = (1, 1);
                MoveTo(Math.Min(centerX + step, (int)(frame.Width * RunClickRight)), Math.Max(centerY - step, (int)(frame.Height * RunClickTop)), "up-right");
                return;
            }

            double needX = targetX - coords.X;
            double needY = targetY - coords.Y;
            var (rightSteps, upSteps) = SolveRunOffset(needX, needY);

            // Facing north, screen-up is world-north (y goes down): a target clearly north or south
            // that the solve wants to reach the other way means the fit is broken - start over.
            if (Math.Abs(needY) > 100 && !double.IsNaN(upSteps) && Math.Sign(upSteps) != Math.Sign(-needY))
            {
                Log?.Invoke(LogLevel.Warning,
                    $"Steering fit looks wrong at {coords} (target {(needY < 0 ? "north" : "south")}, solve says {(upSteps < 0 ? "down" : "up")}) - re-probing.");
                _sRR = _sRU = _sUU = _sXR = _sXU = _sYR = _sYU = 0;
                _pRR = _pRU = _pUU = _pXR = _pXU = _pYR = _pYU = 0;
                _fitSamples = 0;
                _runUpProbed = false;
                _runRightProbed = false;
                _waitUntil = Environment.TickCount64 + 100;
                return;
            }

            // Screen offset in pixels (y grows downwards). The direction is what matters: shrink the
            // whole vector to fit, never clamp the axes separately - that bent "mostly up, a little
            // left" into a 45-degree diagonal and run 6 kept clicking the top-left corner.
            double ox = double.IsNaN(rightSteps) ? 0 : rightSteps * step;
            double oy = double.IsNaN(upSteps) ? -step : -upSteps * step;
            double length = Math.Sqrt(ox * ox + oy * oy);
            double scale = length > step * 2 ? step * 2 / length : 1;
            double roomLeft = centerX - frame.Width * 0.25, roomRight = frame.Width * RunClickRight - centerX;
            double roomUp = centerY - frame.Height * RunClickTop, roomDown = frame.Height * 0.78 - centerY;
            if (ox < 0) scale = Math.Min(scale, roomLeft / -ox);
            if (ox > 0) scale = Math.Min(scale, roomRight / ox);
            if (oy < 0) scale = Math.Min(scale, roomUp / -oy);
            if (oy > 0) scale = Math.Min(scale, roomDown / oy);

            // Never click on (or right next to) the character itself.
            if (length > 0 && length * scale < step * 0.35)
                scale = step * 0.35 / length;

            ox *= scale;
            oy *= scale;

            // A wall ahead of the diagonal: after two steps that barely moved, try straight up.
            if (_stalledSteps >= 2 && upSteps > 0.15)
            {
                ox = 0;
                oy = -Math.Min(roomUp, Math.Max(Math.Abs(oy), step * 0.35));
            }

            int clickX = (int)Math.Round(Math.Clamp(centerX + ox, frame.Width * 0.25, frame.Width * RunClickRight));
            int clickY = (int)Math.Round(Math.Clamp(centerY + oy, frame.Height * RunClickTop, frame.Height * 0.78));

            _probe = RunProbe.Steering;
            _lastOffset = ((clickX - centerX) / (double)step, (centerY - clickY) / (double)step);
            MoveTo(clickX, clickY, $"towards {targetName}");
            return;
        }

        // A click is still in flight; the next tick reads the new position.
        _waitUntil = Environment.TickCount64 + dungeon.RunStepWaitMs;
    }

    private static int Digits(int value) => Math.Abs(value).ToString().Length;

    /// <summary>Stage 5: same select/watch steps as the map stages, without the spawn wait.</summary>
    private void ArriveAtFinalBoss()
    {
        var dungeon = _settings.Dungeon;
        if (string.IsNullOrWhiteSpace(dungeon.FinalBoss))
        {
            Advance(PilotPhase.Done, 400);
            return;
        }

        _tabPressed = false;
        _autohuntPressed = !dungeon.FinalBossPressAutohunt;
        _waypointGateAt = null;
        _waypointGatePolls = 0;

        // WaitBoss first reads the autohunt off in the combat log (user, 2026-10-26).
        _finalHuntOffChecked = false;
        _huntOffPresses = 0;
        _huntOffPressedAt = 0;
        _huntOffBaseline = [];

        // Autohunt only once the boss has been hit by hand (SelectBoss, user).
        _bossFirstSeenAt = null;
        ResetBossState();
        Log?.Invoke(LogLevel.Info, $"At the final boss - looking for '{dungeon.FinalBoss}'.");
        Advance(PilotPhase.WaitBoss, 300);
    }

    /// <summary>Adds one (offset, movement) pair to the fit; older steps fade (factor 0.9).</summary>
    private void AddRunSample((double Right, double Up) o, (double X, double Y) d, bool permanent = false)
    {
        if (permanent)
        {
            _pRR += o.Right * o.Right;
            _pRU += o.Right * o.Up;
            _pUU += o.Up * o.Up;
            _pXR += d.X * o.Right;
            _pXU += d.X * o.Up;
            _pYR += d.Y * o.Right;
            _pYU += d.Y * o.Up;
            _fitSamples++;
            return;
        }

        const double Keep = 0.9;
        _sRR = _sRR * Keep + o.Right * o.Right;
        _sRU = _sRU * Keep + o.Right * o.Up;
        _sUU = _sUU * Keep + o.Up * o.Up;
        _sXR = _sXR * Keep + d.X * o.Right;
        _sXU = _sXU * Keep + d.X * o.Up;
        _sYR = _sYR * Keep + d.Y * o.Right;
        _sYU = _sYU * Keep + d.Y * o.Up;
        _fitSamples++;
    }

    /// <summary>
    /// Click offset (in steps) that should cover <paramref name="needX"/>/<paramref name="needY"/>:
    /// fit M = S_do * S_oo^-1 (world movement per step right / up), then o = M^-1 * need.
    /// Falls back to "straight ahead" while the fit is degenerate.
    /// </summary>
    private (double Right, double Up) SolveRunOffset(double needX, double needY)
    {
        double rr = _sRR + _pRR, ru = _sRU + _pRU, uu = _sUU + _pUU;
        double xr = _sXR + _pXR, xu = _sXU + _pXU, yr = _sYR + _pYR, yu = _sYU + _pYU;

        double d = rr * uu - ru * ru;
        if (_fitSamples < 2 || Math.Abs(d) < 1e-6)
            return (0, 1);

        double mxr = (xr * uu - xu * ru) / d;
        double mxu = (xu * rr - xr * ru) / d;
        double myr = (yr * uu - yu * ru) / d;
        double myu = (yu * rr - yr * ru) / d;

        double det = mxr * myu - mxu * myr;
        if (Math.Abs(det) < 1e-6)
            return (0, 1);

        double right = (needX * myu - needY * mxu) / det;
        double up = (mxr * needY - myr * needX) / det;
        return (right, up);
    }

    /// <summary>
    /// SimpleSteer: the angle between where we clicked (screen, from the character) and where the
    /// character actually went (world). Screen and world axes point the same way when facing
    /// exactly north, so this is the heading error; smoothed, at most 35 degrees.
    /// </summary>
    private void LearnHeadingError((double X, double Y) click, (double X, double Y) moved)
    {
        if (Math.Sqrt(moved.X * moved.X + moved.Y * moved.Y) < 60 || Math.Sqrt(click.X * click.X + click.Y * click.Y) < 1)
            return;

        double cross = click.X * moved.Y - click.Y * moved.X;
        double dot = click.X * moved.X + click.Y * moved.Y;
        double error = Math.Atan2(cross, dot);

        // Relative to the heading we turned to (north, or west after the last waypoint).
        double relative = Math.IEEERemainder(error - _headingBase, 2 * Math.PI);
        if (Math.Abs(relative) > 1.2)
            return; // pushed sideways by a wall or a mob, not a heading error

        // The stored click is the corrected one, so this is the whole screen-to-world angle, not a
        // remainder: smooth towards it.
        double current = Math.IEEERemainder(_headingError - _headingBase, 2 * Math.PI);
        double smoothed = _headingSamples == 0 ? relative : 0.5 * current + 0.5 * relative;
        _headingError = _headingBase + Math.Clamp(smoothed, -0.6, 0.6);
        _headingSamples++;
    }

    private void MoveTo(int x, int y, string what)
    {
        _runSteps++;
        if (_runSteps > _settings.Dungeon.RunMaxSteps)
        {
            Fail(null, $"Gave up after {_settings.Dungeon.RunMaxSteps} run steps.");
            return;
        }

        if (!_input.Click(x, y))
        {
            _detail = "run click blocked - waiting for the game to be in front";
            _probe = RunProbe.None;
            _waitUntil = Environment.TickCount64 + 300;
            return;
        }

        _lastClick = (x, y, Environment.TickCount64);
        Log?.Invoke(LogLevel.Trace, $"Run step {_runSteps}: click {what} at {x},{y}.");
        _waitUntil = Environment.TickCount64 + _settings.Dungeon.RunStepWaitMs;
    }

    /// <summary>After the final boss: autohunt off (it is a toggle), then the teleport scroll back to town.</summary>
    private void TickReturnToTown()
    {
        var dungeon = _settings.Dungeon;
        if (!_autohuntOff)
        {
            if (!string.IsNullOrWhiteSpace(dungeon.AutohuntKey) && !_input.KeyTap(dungeon.AutohuntKey))
            {
                _detail = "autohunt-off key blocked - waiting for the game to be in front";
                _waitUntil = Environment.TickCount64 + 300;
                return;
            }

            _autohuntOff = true;
            Log?.Invoke(LogLevel.Success, $"Pressed '{dungeon.AutohuntKey}' to turn the autohunt off.");
            _waitUntil = Environment.TickCount64 + 800;
            return;
        }

        if (string.IsNullOrWhiteSpace(HomeTown))
        {
            Advance(PilotPhase.Done, 300);
            return;
        }

        _returning = true;
        _pages = 0;
        _anchor = null;
        _chevronMissing = false;
        Log?.Invoke(LogLevel.Info, $"Teleporting back to '{HomeTown}'.");
        Advance(PilotPhase.OpenInventory, 300);
    }

    /// <summary>
    /// Logged out: the server list ("SERVERS"), the login form ("ACCOUNT NAME" / "Remember me")
    /// or the character screen ("START GAME" / "CHARACTER SELECT"). The game was once found on
    /// the login form, which a SERVERS-only check missed.
    /// </summary>
    // "characterselect" is not a sign: the in-game Settings window has a CHARACTER SELECT button
    // and was taken for a disconnect (run 32). The character screen also shows START GAME.
    private static bool IsServersScreen(IReadOnlyList<OcrWord> words)
    {
        var lines = words.Select(w => TextMatcher.Normalize(w.LineText)).ToList();
        return !lines.Contains("settings")
               && lines.Any(line => line is "servers" or "accountname" or "rememberme" or "startgame");
    }

    /// <summary>
    /// A dialog showing <see cref="SequenceSettings.CancelDialogText"/> ("Bohai Outpost", user) is
    /// closed with the "Cancel" word nearest to that text. The minimap title is ignored - in that
    /// town it reads "Bohai Outpost" too - and nothing is clicked without a readable "Cancel".
    /// </summary>
    private bool TryCancelDialog(Mat frame, IReadOnlyList<OcrWord> words)
    {
        string wanted = TextMatcher.Normalize(_settings.Sequence.CancelDialogText);
        var minimap = _settings.Regions.Minimap.ToPixels(frame.Width, frame.Height);
        // The "[Bohai Outpost (Adept)] [10] minute(s) to opening of the entrance" notices in the
        // combat log are not the dialog (run 25 warned on one).
        var text = words.FirstOrDefault(w =>
            TextMatcher.Normalize(w.LineText) is var line &&
            line.Contains(wanted, StringComparison.Ordinal) &&
            !line.Contains("minute", StringComparison.Ordinal) && !line.Contains("entrance", StringComparison.Ordinal) &&
            !minimap.Contains(w.LineBounds.CenterX, w.LineBounds.CenterY));
        if (text == null)
            return false;

        // Close to the text only: the "[Bohai Outpost (Adept)] [10] minute(s) to opening" notice
        // line has no buttons, and a Cancel elsewhere belongs to something else.
        int reach = frame.Height / 3;
        var cancel = words
            .Where(w => TextMatcher.Normalize(w.Text) is "cancel" or "cance1" or "cancei")
            .Where(w => Math.Abs(w.Bounds.CenterX - text.LineBounds.CenterX) <= reach && Math.Abs(w.Bounds.CenterY - text.LineBounds.CenterY) <= reach)
            .OrderBy(w => Math.Abs(w.Bounds.CenterX - text.LineBounds.CenterX) + Math.Abs(w.Bounds.CenterY - text.LineBounds.CenterY))
            .FirstOrDefault();
        if (cancel == null)
        {
            if (Environment.TickCount64 >= _nextCancelDialogWarnAt)
            {
                _nextCancelDialogWarnAt = Environment.TickCount64 + 60000;
                Log?.Invoke(LogLevel.Warning, $"'{text.LineText}' is showing but no 'Cancel' button could be read - press F11 so it can be checked.");
                DebugArtifacts.SaveFrame(frame, _overlays, "canceldialog_unread");
            }

            return false;
        }

        _overlays.Add(new OverlayItem(cancel.Bounds, "Cancel", OverlayColor.Yellow));
        if (!Click(cancel.Bounds))
            return false;

        Log?.Invoke(LogLevel.Warning,
            $"'{text.LineText}' dialog during {_phase} - clicked 'Cancel' at {cancel.Bounds.CenterX},{cancel.Bounds.CenterY}.");
        DebugArtifacts.SaveFrame(frame, _overlays, "canceldialog", (cancel.Bounds.CenterX, cancel.Bounds.CenterY));
        return true;
    }

    private static bool IsLabyrinthPhase(PilotPhase phase)
        => phase is PilotPhase.WaitForGate or PilotPhase.ClickMap or PilotPhase.ReadStage or PilotPhase.AdvanceStage
            or PilotPhase.WaitBoss or PilotPhase.SelectBoss or PilotPhase.WatchBoss or PilotPhase.FaceNorth
            or PilotPhase.AimGate or PilotPhase.RunToTarget
            or PilotPhase.LodRun or PilotPhase.LodAttack or PilotPhase.LodApproach or PilotPhase.LodRespawn or PilotPhase.LodLoot;

    /// <summary>
    /// The zone name at the top of the minimap panel, e.g. "[The Labyrinth (Adep", "[Piercing City)",
    /// "[Bohai Outpost (Adep": the first line there that starts with '[' and has no digits (the
    /// coordinates line below it has digits). Null when it cannot be read.
    /// </summary>
    private async Task<string?> ReadMinimapTitleAsync(Mat frame)
    {
        var band = _settings.Regions.Minimap.ToPixels(frame.Width, frame.Height);
        var words = await _analyzer.Ocr.RecognizeAsync(frame, band, 2.0);
        var lines = words.Select(w => w.LineText.Trim()).Distinct().ToList();
        return lines.FirstOrDefault(l => ZoneMatches(l, Destination))
               ?? lines.FirstOrDefault(l => l.StartsWith('[') && !l.Any(char.IsDigit) && TextMatcher.Normalize(l).Length >= 4);
    }

    /// <summary>
    /// One kind of key used up: switch to the other mode, or - both used up this Start - J in town
    /// (the game's built-in auto war, user) and stop. True when it switched (the run goes on).
    /// </summary>
    private bool OutOfKeys(bool labyrinth, string why)
    {
        if (labyrinth)
            _labyOut = true;
        else
            _lodOut = true;

        if (!(_labyOut && _lodOut))
        {
            var next = labyrinth ? RunMode.AutoLod : RunMode.AutoLaby;
            Log?.Invoke(LogLevel.Warning, $"{why} - switching to {next}.");
            _settings.Sequence.Mode = next;
            SettingsStore.Save(_settings);
            ModeChanged?.Invoke();
            if (labyrinth)
            {
                // From town: the LOD run starts with a fresh teleport.
                ResetRunState();
                _runStart = Environment.TickCount64;
                Advance(PilotPhase.Focus, 1500);
            }

            return true;
        }

        BeginAutoWar($"{why}, and no Den of Rebirth entries / Temple of Fiends Keys either");
        return false;
    }

    /// <summary>
    /// The teleport list's page arrows: "&gt;" next page, "&lt;" previous page (the right chevron
    /// mirrored). The list arrows score ~0.98; 0.80 keeps fainter look-alikes out.
    /// </summary>
    private (bool HasNext, bool HasPrev) TeleportListArrows(Mat frame)
    {
        var roi = _settings.Regions.TeleportList.ToPixels(frame.Width, frame.Height);
        bool next = _analyzer.FindIcons(frame, "rightchevron", roi, 0.80, 3).Count > 0;
        bool prev = _analyzer.FindIcons(frame, "leftchevron", roi, 0.80, 3).Count > 0;
        return (next, prev);
    }

    /// <summary>The top bar shows "--ms" instead of a ping while the connection is stuck.</summary>
    private async Task<bool> IsLaggingAsync(Mat frame)
    {
        var bar = new NormRect(0.50, 0, 0.45, 0.06).ToPixels(frame.Width, frame.Height);
        var words = await _analyzer.Ocr.RecognizeAsync(frame, bar, 2.0);
        // "93ms" normally; "--ms" while stuck, which OCR may read as "—ms" - "ms" without a digit before it.
        return words.Any(w => LagPing.IsMatch(w.LineText));
    }

    /// <summary>Any clan's home town (<see cref="DungeonSettings.HomeTowns"/>) as the minimap title.</summary>
    private async Task<bool> IsInTownAsync(Mat frame) => TownIn(await ReadMinimapTitleAsync(frame)) != null;

    /// <summary>True when the minimap panel (top right) shows <paramref name="text"/> - the zone title.</summary>
    private async Task<bool> MinimapTitleContainsAsync(Mat frame, string text)
    {
        if (TextMatcher.Normalize(text).Length == 0)
            return false;

        var band = _settings.Regions.Minimap.ToPixels(frame.Width, frame.Height);
        var words = await _analyzer.Ocr.RecognizeAsync(frame, band, 2.0);
        return words.Any(w => ZoneMatches(w.LineText, text));
    }

    /// <summary>
    /// Logs back in after a disconnect. Each tick reads the whole screen and clicks the button of
    /// whichever step is showing (the order the screens appear in is not assumed), then waits
    /// ReconnectStepDelayMs. After START GAME it waits for the town name on the minimap and
    /// restarts the run.
    /// </summary>
    private async Task TickReconnectAsync(Mat frame)
    {
        var sequence = _settings.Sequence;
        long now = Environment.TickCount64;
        int delay = sequence.ReconnectStepDelayMs;

        if (now - _reconnectStartedAt > 180000 || _reconnectClicks > 30)
        {
            Fail(frame, $"Could not log back in ({_reconnectClicks} click(s) in {(now - _reconnectStartedAt) / 1000}s). " +
                        "Check that the game remembers the account.");
            return;
        }

        // x1.5 returned garbage on the login screens while x2 and x1 read them fine.
        var full = new PixelRect(0, 0, frame.Width, frame.Height);
        var words = await _analyzer.Ocr.RecognizeAsync(frame, full, 2.0);
        if (words.Count < 3)
            words = await _analyzer.Ocr.RecognizeAsync(frame, full, 1.0);
        var lines = words
            .GroupBy(w => w.LineBounds)
            .Select(g => (Bounds: g.Key, Text: TextMatcher.Normalize(g.First().LineText)))
            .ToList();

        // A button is a line that reads exactly the label, or a single word of it (OCR may merge
        // the server row "[CH-01] - Wuxen2 ... Online" into one line). The lowest one wins: buttons
        // sit below titles/messages with the same word ("LOGIN" title, "Login OK" message).
        PixelRect? Line(string text) => lines.Where(l => l.Text == text).Select(l => l.Bounds)
            .Concat(words.Where(w => TextMatcher.Normalize(w.Text) == text).Select(w => w.Bounds))
            .Select(b => (PixelRect?)b)
            .OrderByDescending(b => b!.Value.Y)
            .FirstOrDefault();

        // Back in town after START GAME: done, start the labyrinth run again.
        if (_startGameClicked)
        {
            // Only the minimap title counts: the character screen shows the location too.
            var minimap = _settings.Regions.Minimap.ToPixels(frame.Width, frame.Height);
            // The game puts the character back where it was: town, or the Labyrinth mid-run (run 28).
            // Focus -> CheckZone then either teleports or resumes the stage.
            string? where = lines.Any(l => IsStartGame(l.Text)) ? null : lines
                .Where(l => l.Bounds.CenterX >= minimap.X && l.Bounds.CenterX <= minimap.Right &&
                            l.Bounds.CenterY >= minimap.Y && l.Bounds.CenterY <= minimap.Bottom)
                .Select(l => l.Text)
                .Select(t => TownIn(t) is { } townName ? townName
                    : ZoneMatches(t, Destination) ? Destination : null)
                .FirstOrDefault(w => w != null);
            if (where != null)
            {
                Log?.Invoke(LogLevel.Success, $"Logged back in - in {where}. Restarting the run.");
                ResetRunState();
                _runStart = Environment.TickCount64;
                Advance(PilotPhase.Focus, 1500);
                return;
            }
        }

        // The user's order: Online -> CONNECT -> LOGIN -> OK -> START GAME. The expected step is
        // clicked when its button shows; a later step's button jumps ahead (e.g. the login screen
        // came up directly); the server list showing again starts over.
        bool servers = lines.Any(l => l.Text == "servers");
        bool loginForm = lines.Any(l => l.Text is "accountname" or "rememberme");
        var gold = _analyzer.FindGoldButtons(frame);
        if (servers && _reconnectStep > 1)
            _reconnectStep = 0;

        // The character screen. START GAME sometimes did not read for 15-60 s while "Login OK" was
        // still on screen (logs 2026-10-20 19:51 / 20:01): the OK click had not closed that dialog,
        // which may cover the button. And a START GAME click that did not take was never repeated.
        var startGame = StartGameButton();
        bool charScreen = lines.Any(l => l.Text is "characterselect" or "characterinfo" or "newcharacter"
                                              or "deletecharacter" or "selectworld");
        if (_reconnectStep >= 4 && startGame == null && charScreen)
        {
            // "Login OK" still up: click its OK again - only a read "OK", never a guessed button here.
            var message = lines.FirstOrDefault(l => l.Text.Contains("loginok", StringComparison.Ordinal) ||
                                                    l.Text.Contains("login0k", StringComparison.Ordinal));
            if (message.Text != null && now - _lastOkRetryAt > 3000)
            {
                var ok = lines.Where(l => l.Text is "ok" or "0k" && l.Bounds.Y > message.Bounds.Bottom)
                    .Select(l => (PixelRect?)l.Bounds).OrderByDescending(b => b!.Value.Y).FirstOrDefault();
                if (ok is { } okBox && Click(okBox))
                {
                    _lastOkRetryAt = now;
                    _reconnectClicks++;
                    Log?.Invoke(LogLevel.Info, $"Reconnect: 'Login OK' still up - clicked OK again at {okBox.CenterX},{okBox.CenterY}.");
                    _waitUntil = Environment.TickCount64 + delay;
                    return;
                }
            }

            if (_charScreenSince == 0)
                _charScreenSince = now;
            else if (!_startGameUnreadSaved && now - _charScreenSince > 5000)
            {
                _startGameUnreadSaved = true;
                Log?.Invoke(LogLevel.Warning, "Reconnect: on the character screen but START GAME does not read - snapshot 'startgame_unread'.");
                DebugArtifacts.SaveFrame(frame, _overlays, "startgame_unread");
            }
        }
        else
            _charScreenSince = 0;

        // START GAME clicked but still there after a few seconds: click it again.
        if (_startGameClicked && startGame != null && now - _startGameClickedAt > 4000 && _reconnectStep > 4)
            _reconnectStep = 4;

        (string Label, PixelRect? Target)[] steps =
        {
            ("Online", servers ? Line("online") : null),
            ("CONNECT", Line("connect")),
            ("LOG IN", LogInButton()),
            ("OK", OkButton()),
            ("START GAME", startGame),
        };

        // Exactly "START GAME", or a near miss of it (one or two letters off) as a line of its own.
        PixelRect? StartGameButton()
            => lines.Where(l => IsStartGame(l.Text)).Select(l => (PixelRect?)l.Bounds)
                .OrderByDescending(b => b!.Value.Y)
                .FirstOrDefault() ?? Line("startgame");

        PixelRect? OkButton()
        {
            // Never the "OK" inside the message "Login OK" (it was clicked once): only a line that
            // is exactly "OK"/"0K" (OCR reads O as zero), below the message when it is visible.
            var message = lines.FirstOrDefault(l => l.Text.Contains("loginok", StringComparison.Ordinal) ||
                                                    l.Text.Contains("login0k", StringComparison.Ordinal));
            int below = message.Text == null ? 0 : message.Bounds.Bottom;
            var read = lines.Where(l => l.Text is "ok" or "0k" && l.Bounds.Y > below)
                .Select(l => (PixelRect?)l.Bounds)
                .OrderByDescending(b => b!.Value.Y)
                .FirstOrDefault();
            if (read != null)
                return read;

            // The dialog's gold button: below the message, not the LOG IN button of the form behind.
            if (_reconnectStep != 3 || gold.Count == 0)
                return null;

            var logIn = loginForm ? LogInButton() : null;
            var candidates = gold
                .Where(b => b.Y > below)
                .Where(b => logIn is not { } l || Math.Abs(b.CenterX - l.CenterX) + Math.Abs(b.CenterY - l.CenterY) > 20)
                .ToList();
            return candidates.Count == 0
                ? null
                : candidates.OrderBy(b => Math.Abs(b.CenterX - frame.Width / 2) + Math.Abs(b.CenterY - frame.Height / 2)).First();
        }

        // "LOG IN" is dark text on a gold button and reads as "OG N" or not at all: OCR variants,
        // then the gold colour, then the gap between "Remember me" and the BACK button.
        PixelRect? LogInButton()
        {
            var read = lines.Where(l => l.Text is "login" or "ogn" or "logn" or "ogin" or "lgin")
                .Where(l => lines.Any(t => t.Text == "accountname" && t.Bounds.Y < l.Bounds.Y))
                .Select(l => (PixelRect?)l.Bounds).FirstOrDefault();
            if (read != null)
                return read;

            // Dark text on gold: the colour finds it when OCR does not.
            if (loginForm && gold.Count > 0)
                return gold.OrderBy(b => Math.Abs(b.CenterX - frame.Width / 2) + Math.Abs(b.CenterY - frame.Height / 2)).First();

            var remember = lines.FirstOrDefault(l => l.Text == "rememberme");
            var back = lines.FirstOrDefault(l => l.Text == "back");
            if (remember.Text == null || back.Text == null || back.Bounds.Y <= remember.Bounds.Y)
                return null;

            int y = (remember.Bounds.CenterY + back.Bounds.CenterY) / 2;
            return new PixelRect(back.Bounds.CenterX - 20, y - 6, 40, 12);
        }

        int pick = -1;
        // On the server list CONNECT only works once a server is selected: while "Online" has not
        // read yet (the list was still loading once), wait instead of jumping ahead.
        bool waitForOnline = servers && _reconnectStep == 0 && steps[0].Target == null;
        for (int i = _reconnectStep; i < steps.Length && !waitForOnline; i++)
        {
            if (steps[i].Target != null)
            {
                pick = i;
                break;
            }
        }

        if (pick >= 0)
        {
            var (label, target) = steps[pick];
            var box = target!.Value;
            if (!Click(box))
            {
                _input.TryFocus();
                return;
            }

            _reconnectClicks++;
            _reconnectStep = pick + 1;
            if (label == "START GAME")
            {
                _startGameClicked = true;
                _startGameClickedAt = Environment.TickCount64;
            }
            Log?.Invoke(LogLevel.Info, $"Reconnect: clicked {label} at {box.CenterX},{box.CenterY}.");
            _overlays.Add(new OverlayItem(box, label, OverlayColor.Green));
            _waitUntil = Environment.TickCount64 + delay;
            return;
        }

        _detail = _startGameClicked ? "loading into town" : "waiting for a login screen button";
        string seen = string.Join(" | ", lines.Select(l => l.Text).Take(20));
        if (seen != _lastReconnectSeen)
        {
            _lastReconnectSeen = seen;
            Log?.Invoke(LogLevel.Trace, $"Reconnect (step {_reconnectStep + 1}) sees: {seen}");
        }
        _waitUntil = Environment.TickCount64 + delay;
    }

    private void TickDone()
    {
        _runCount++;
        double seconds = (Environment.TickCount64 - _runStart) / 1000.0;
        Log?.Invoke(LogLevel.Success, $"Sequence #{_runCount} complete in {seconds:0.0}s.");

        // In dungeon mode the run ends at the last boss, which still has to be fought: looping
        // would teleport away in the middle of it (it did once).
        if (_settings.Sequence.Loop && _settings.Dungeon.Enabled && !_returning)
            Log?.Invoke(LogLevel.Info, "Stopped at the last boss - not looping. Finish it, then press F9 for the next run.");
        else if (_settings.Sequence.Loop)
        {
            ResetRunState();
            _runStart = Environment.TickCount64;
            Log?.Invoke(LogLevel.Info, $"Looping: next run in {_settings.Sequence.LoopDelayMs}ms.");
            _phase = PilotPhase.Focus;
            _phaseStart = Environment.TickCount64;
            _waitUntil = Environment.TickCount64 + _settings.Sequence.LoopDelayMs;
            RaiseState();
            return;
        }

        IsRunning = false;
        _waitUntil = 0;
        _detail = string.Empty;
        RaiseState();
    }

    // ---------------------------------------------------------------- helpers

    private bool Click(PixelRect target)
    {
        if (!_input.Click(target.CenterX, target.CenterY))
        {
            _detail = "click blocked - waiting for the game to be in front";
            _waitUntil = Environment.TickCount64 + 300;
            return false;
        }

        _lastClick = (target.CenterX, target.CenterY, Environment.TickCount64);
        return true;
    }

    private bool RightClick(PixelRect target)
    {
        if (!_input.Click(target.CenterX, target.CenterY, MouseButton.Right))
        {
            _detail = "click blocked - waiting for the game to be in front";
            _waitUntil = Environment.TickCount64 + 300;
            return false;
        }

        _lastClick = (target.CenterX, target.CenterY, Environment.TickCount64);
        return true;
    }

    private void Advance(PilotPhase next, int delayMs)
    {
        if (next != PilotPhase.LodAttack)
            ReleaseHeldSkill();
        _phase = next;
        _phaseStart = Environment.TickCount64;
        _attempts = 0;
        _lastHash = 0;
        _stableSamples = 0;
        _noPathLogged = false;
        _waitUntil = _phaseStart + Math.Max(0, delayMs);
        RaiseState();
    }

    private void Retry(Mat? frame, string what)
    {
        _attempts++;
        if (_attempts > _settings.Sequence.MaxRetries)
        {
            Fail(frame, $"{what} not found after {_settings.Sequence.MaxRetries} attempts.");
            return;
        }

        _detail = $"{_detail} (retry {_attempts}/{_settings.Sequence.MaxRetries})";
        _waitUntil = Environment.TickCount64 + _settings.Sequence.RetryDelayMs;
    }

    private void Fail(Mat? frame, string reason)
    {
        Log?.Invoke(LogLevel.Error, reason);

        if (frame != null)
        {
            string file = DebugArtifacts.SaveFrame(frame, _overlays, $"fail_{_phase}", _lastClick.HasValue ? (_lastClick.Value.X, _lastClick.Value.Y) : null);
            if (!string.IsNullOrEmpty(file))
                Log?.Invoke(LogLevel.Info, $"Saved a debug snapshot: {file}");
        }

        ReleaseSweepKey();

        // Start over from the top instead of stopping (user), a limited number of times.
        long now = Environment.TickCount64;
        _restartTimes.RemoveAll(t => now - t > 30 * 60 * 1000);
        if (_settings.Sequence.RestartOnError && _restartTimes.Count < _settings.Sequence.MaxRestarts)
        {
            _restartTimes.Add(now);
            Log?.Invoke(LogLevel.Warning,
                $"Restarting from the top in {_settings.Sequence.RestartDelayMs / 1000}s ({_restartTimes.Count}/{_settings.Sequence.MaxRestarts} in 30 min).");
            ResetRunState();
            _runStart = now;
            Advance(PilotPhase.Focus, _settings.Sequence.RestartDelayMs);
            return;
        }

        if (_settings.Sequence.RestartOnError)
            Log?.Invoke(LogLevel.Error, $"{_settings.Sequence.MaxRestarts} restarts in 30 minutes - stopping.");

        IsRunning = false;
        _phase = PilotPhase.Failed;
        _waitUntil = 0;
        _detail = reason;
        RaiseState();
    }

    private void RaiseState() => StateChanged?.Invoke();

    private int _lastFrameWidth = 1920;
    private int _lastFrameHeight = 1080;

    /// <summary>The vision loop calls this before each tick so helpers know the client size.</summary>
    public void SetFrameSize(int width, int height)
    {
        _lastFrameWidth = Math.Max(1, width);
        _lastFrameHeight = Math.Max(1, height);
    }
}

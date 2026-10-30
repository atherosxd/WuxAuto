using Emgu.CV;
using WuxAuto.Core;
using WuxAuto.Vision;
using LogLevel = WuxAuto.Core.LogLevel;

namespace WuxAuto.Automation;

/// <summary>
/// Auto Blossom (user, 2026-10-29; docs/superpowers/specs/2026-10-29-auto-blossom-design.md): teleport to Blossom Garden
/// (page 1), zoom out, tap D until the camera looks at the middle between the two pillars, then walk the waypoints - 1 to 5
/// once, then 6 to 17 over and over - pressing Tab. A targeted "[S]Jaso Practitioner" (or the target bar's chest) is
/// attacked until it is gone, then looted; the walk goes on to the waypoint it was heading for. The map's AFK checker kicks
/// an idle character to town (user), so a reached waypoint is left after at most WaypointWaitMs; the zone guard teleports
/// back in from town.
/// </summary>
public sealed partial class AutomationPilot
{
    /// <summary>The short fine-tune tap once both pillars are in view but off centre.</summary>
    private const int BlossomFineTapMs = 40;
    private const int BlossomMaxFineTaps = 12;
    private const int BlossomTurnSettleMs = 350;
    private const int BlossomMaxStalls = 5;   // ~5-6 s against a wall: the AFK checker kicks idle characters (user)
    private const int BlossomMaxUnread = 40;
    private const int BlossomRebuffOverdueMs = 30000;
    private const int BlossomGiveUpIgnoreMs = 10000;
    private const int BlossomChaseTargetMs = 1000;   // a big move with the target seen this recently = a chase, not a death
    private const int BlossomStuckMoveUnits = 40;    // fight: moved less than this between two checks = still
    private const int BlossomStepWalkMs = 800;       // fight: the side step's walk before the skill is held again
    private const int BlossomStepRecheckMs = 1500;   // fight: the next "still?" look after a step

    /// <summary>The ways a stuck character tries in turn, screen-relative (x right, y down): right, left, back, then the diagonals.</summary>
    private static readonly (double X, double Y, string Name)[] BlossomStepWays =
    [
        (1, 0, "right"), (-1, 0, "left"), (0, 1, "back"),
        (0.71, 0.71, "back-right"), (-0.71, 0.71, "back-left"), (0.71, -0.71, "ahead-right"), (-0.71, -0.71, "ahead-left"),
    ];
    private const int BlossomArrivalX = 19148, BlossomArrivalY = 19062;   // where the teleport lands (every live arrival)
    private const int BlossomArrivalRadius = 500;    // a respawn closer than this lines up on the pillars again
    private const int BlossomOffRouteDistance = 600; // after a fight: this far from the waypoint it was heading for ...
    private const int BlossomRejoinDistance = 300;   // ... and this close to another = walk on from that one
    private const int BlossomSkipReach = 1000;       // a random skip lands only on waypoints this close to the fight's end
    private const double BlossomHeadingMissRad = 0.6;
    private const int BlossomHeadingMissesToRelearn = 3;
    private const int BlossomRelearnWindow = 6;   // steps after the heading was set in which it may be re-learnt
    private const int BlossomNoProgressSteps = 15;
    private const int BlossomTargetCheckMs = 700;
    private const double BlossomNearDistance = 300;   // from here the walk homes in on the waypoint
    private const int BlossomNearReadMs = 450;
    private const double BlossomCloseEnough = 150;    // blocked this close to a waypoint = reached
    private const double BlossomLookAhead = 250;      // world units ahead on the leg's line the walk aims at
    private const double BlossomPxPerUnit = 1.8;      // screen px per world unit of a walk click (LOD: 200 px walked 80-140)
    private const double BlossomMinNearClickPx = 80;  // a click closer to the feet lands on the character's own body

    private IReadOnlyList<(int X, int Y)> _blossomRoute = [];
    private int _blossomWaypoint = -1;
    private long _blossomTabAt;
    private long _blossomPauseSince;
    private int _blossomStalls;
    private int _blossomUnread;
    private bool _blossomHeadingKnown;
    private bool _blossomBuffThenTurn;
    private bool _blossomHuntOffChecked;
    private int _blossomHeadingMisses;
    private int _blossomHeadingAge;   // steps (>= 60 units) since the heading was set
    private long _blossomIgnoreTargetsUntil;
    private int _blossomWaypointSteps;          // reads in a row without getting closer to the waypoint
    private double _blossomBestDistance = double.MaxValue;
    private bool _blossomFocusTold;
    private (double X, double Y)? _blossomDir;   // the walk click (screen offset) until the next coordinate read
    private bool _blossomNear;                   // within BlossomNearDistance of the waypoint: one click per read
    private bool _blossomForward = true;         // BackAndForth: walking towards the last waypoint (false: back)
    private long _blossomStuckCheckAt;           // fight: next "no move, no hit" check (FightStuckMs)
    private (int X, int Y)? _blossomStuckCoords; // fight: the spot and the combat log at the last check
    private string? _blossomStuckLog;
    private int? _blossomStuckHp;
    private bool _blossomRecheckRoute;           // a fight happened: the next walk read checks it is still near its waypoint
    private int _blossomUnblockWay;              // walk: the next way of BlossomStepWays to try; back to 0 on progress
    private int _blossomSteps;                   // fight: side steps so far (the direction turns each time)
    private (int Cur, int Max)? _blossomTargetHp; // the target bar's numbers at the latest target read
    private long _blossomNotFullToldAt;
    private long _blossomFightLootAt;            // LootWhileFighting: the last loot tap
    private int _blossomAfkSteps;
    private (int X, int Y)? _blossomLegFrom;     // where the current leg starts: the waypoint just left
    private (int X, int Y)? _blossomJumpCandidate;
    private long _blossomClickAt;
    private long _blossomReadAt;
    private long _blossomTargetAt;
    private int _blossomCamTaps;                 // the camera turn when leaving a waypoint (WaypointTurns), taps left
    private TurnDirection _blossomCamKey;
    private IReadOnlyDictionary<int, (TurnDirection Direction, int Taps)> _blossomTurns = new Dictionary<int, (TurnDirection, int)>();
    private readonly Dictionary<string, int> _blossomShots = [];

    private int _blossomTurnTaps;
    private int _blossomFineTaps;
    private TurnDirection _blossomFineDir = TurnDirection.Right;
    private double? _blossomLastOffset;
    private bool _blossomTurnTold;

    private string _blossomTarget = string.Empty;
    private long _blossomTargetSeenAt;
    private long _blossomFightStart;
    private long _blossomSkillAt;
    private long _blossomCoordsAt;
    private (int X, int Y)? _blossomAttackCoords;
    private long _blossomLootStart;
    private long _blossomLootAt;
    private int _blossomKills;

    private bool IsBlossom => _settings.Sequence.Mode == RunMode.AutoBlossom;

    /// <summary>The Blossom phases (the zone guard covers them).</summary>
    private static bool IsBlossomPhase(PilotPhase phase)
        => phase is PilotPhase.BlossomTurn or PilotPhase.BlossomWalk or PilotPhase.BlossomAttack or PilotPhase.BlossomLoot
            or PilotPhase.BlossomRespawn;

    /// <summary>Per run (Start, each restart): where the route stands and what the walk has learnt.</summary>
    private void ResetBlossomRunState()
    {
        _blossomRoute = BlossomRoute.Parse(_settings.Blossom.Waypoints);
        _blossomTurns = BlossomRoute.ParseTurns(_settings.Blossom.WaypointTurns);
        _blossomCamTaps = 0;
        _blossomForward = true;
        _blossomTargetHp = null;
        _blossomNotFullToldAt = 0;
        _blossomAfkSteps = 0;
        _blossomLegFrom = null;
        _blossomJumpCandidate = null;
        _blossomDir = null;
        _blossomClickAt = 0;
        _blossomReadAt = 0;
        _blossomTargetAt = 0;
        _blossomWaypoint = -1;
        _blossomTabAt = 0;
        _blossomPauseSince = 0;
        _blossomStalls = 0;
        _blossomUnread = 0;
        _blossomHeadingKnown = false;
        _blossomHeadingMisses = 0;
        _blossomHuntOffChecked = false;
        _blossomIgnoreTargetsUntil = 0;
    }

    /// <summary>In Blossom Garden after the teleport, a restart or a death: the buffs, then the turn to the pillars.</summary>
    private void BeginBlossomArrival() => BeginBlossomBuff("arrived", thenTurn: true);

    /// <summary>
    /// The buffs the Auto War way (user: "same rebuff logic as auto war"): autobuff known? (the item list, else the autohunt
    /// settings) -> the buff key and the item buffs, or the ticked buff icons. <see cref="FinishWarBuffAsync"/> comes back to
    /// <see cref="AfterBlossomBuff"/>: the turn (arrival, death) or the walk.
    /// </summary>
    private void BeginBlossomBuff(string why, bool thenTurn)
    {
        _blossomBuffThenTurn = thenTurn;
        _warBuffStep = _warHasAutoBuff == null ? 0 : 3;
        _warBuffRound = null;
        _warBuffIcon = 0;
        _warBuffClicks = 0;
        _warHuntSettingsPresses = 0;
        Log?.Invoke(LogLevel.Info, $"Buffing ({why}).");
        Advance(PilotPhase.WarBuff, 300);
    }

    /// <summary>The rebuff timer (War.BuffEveryMs since the last buff) has run out.</summary>
    private bool BlossomRebuffDue(long now) => now - _warBuffedAt >= _settings.War.BuffEveryMs;

    private void AfterBlossomBuff()
    {
        if (_blossomBuffThenTurn)
            BeginBlossomTurn();
        else
            ResumeBlossomWalk();
    }

    /// <summary>After a loot or at a reached waypoint: rebuff when due, else on with the walk.</summary>
    private void ResumeBlossomWalkOrRebuff(string where)
    {
        long now = Environment.TickCount64;
        if (BlossomRebuffDue(now))
        {
            BeginBlossomBuff(_warBuffedAt == 0 ? $"no buff yet this Start, {where}" : $"{Clock(now - _warBuffedAt)} since the last buff, {where}", thenTurn: false);
            return;
        }

        ResumeBlossomWalk();
    }

    /// <summary>Tap D until both pillars are in view with their midpoint at the centre (user).</summary>
    private void BeginBlossomTurn()
    {
        _blossomTurnTaps = 0;
        _blossomFineTaps = 0;
        _blossomFineDir = TurnDirection.Right;
        _blossomLastOffset = null;
        _blossomTurnTold = false;
        Log?.Invoke(LogLevel.Info, $"Turning ('{_settings.Input.RotateKey}' {_settings.Blossom.TurnTapMs} ms taps) until the two pillars are centred.");
        Advance(PilotPhase.BlossomTurn, 400);
    }

    /// <summary>After the turn: the walk starts at the nearest waypoint, with the heading learnt from its first step.</summary>
    private void StartBlossomWalk()
    {
        _blossomHeadingKnown = false;
        _blossomWaypoint = -1;
        ResumeBlossomWalk();
    }

    /// <summary>Back to the walk towards the current waypoint (after a fight, a loot, a buff).</summary>
    private void ResumeBlossomWalk()
    {
        _lastCoords = null;
        _simpleClick = null;
        _blossomDir = null;
        _blossomClickAt = 0;
        _blossomReadAt = 0;
        _blossomTargetAt = 0;
        _blossomStalls = 0;
        _blossomPauseSince = 0;
        Advance(PilotPhase.BlossomWalk, 0);
    }

    private void TickBlossomTurn(Mat frame)
    {
        var b = _settings.Blossom;
        long now = Environment.TickCount64;
        var pair = _analyzer.FindPillarPair(frame, b.PillarTemplate, b.PillarMatchScore);
        if (pair != null)
        {
            _overlays.Add(new OverlayItem(pair.Left.Bounds, $"pillar {pair.Left.Score:0.00}", OverlayColor.Green));
            _overlays.Add(new OverlayItem(pair.Right.Bounds, $"pillar {pair.Right.Score:0.00}", OverlayColor.Green));
            if (Math.Abs(pair.OffsetFraction) <= b.PillarCenterTolerance || _blossomFineTaps >= BlossomMaxFineTaps)
            {
                Log?.Invoke(LogLevel.Success, Math.Abs(pair.OffsetFraction) <= b.PillarCenterTolerance
                    ? $"Lined up on the pillars (offset {pair.OffsetFraction:+0.000;-0.000}, after {_blossomTurnTaps} tap(s) + {_blossomFineTaps} fine)."
                    : $"Pillars in view, still {pair.OffsetFraction:+0.000;-0.000} off centre after {_blossomFineTaps} fine taps - walking from here.");
                StartBlossomWalk();
                return;
            }

            // Off centre: short taps; a tap that took the midpoint further out turns the other way next (which way D
            // moves the view is not assumed).
            if (_blossomLastOffset is { } last && Math.Abs(pair.OffsetFraction) > Math.Abs(last))
                _blossomFineDir = _blossomFineDir == TurnDirection.Right ? TurnDirection.Left : TurnDirection.Right;
            if (!_controls.Turn(_blossomFineDir, BlossomFineTapMs))
            {
                _detail = "turn blocked - waiting for the game to be in front";
                _waitUntil = now + 300;
                return;
            }

            _blossomLastOffset = pair.OffsetFraction;
            _blossomFineTaps++;
            _detail = $"pillars {pair.OffsetFraction:+0.000;-0.000} off centre - fine tap {_blossomFineTaps}";
            _waitUntil = Environment.TickCount64 + BlossomTurnSettleMs;
            return;
        }

        _blossomLastOffset = null;
        if (_blossomTurnTaps >= b.TurnMaxTaps || now - _phaseStart >= b.TurnMaxMs)
        {
            Log?.Invoke(LogLevel.Warning, $"Turned {_blossomTurnTaps} taps ({(now - _phaseStart) / 1000.0:0.#}s) and no pillars lined up - walking anyway; the heading is learnt from the walk.");
            BlossomShot(frame, "blossom_no_pillars");
            StartBlossomWalk();
            return;
        }

        if (_blossomTurnTaps == b.TurnTaps && !_blossomTurnTold)
        {
            _blossomTurnTold = true;
            Log?.Invoke(LogLevel.Info, $"Pillars not lined up after {b.TurnTaps} taps - turning on (up to {b.TurnMaxTaps}).");
        }

        if (!_controls.Turn(TurnDirection.Right, b.TurnTapMs))
        {
            _detail = "turn blocked - waiting for the game to be in front";
            _waitUntil = now + 300;
            return;
        }

        _blossomTurnTaps++;
        _detail = $"turning to the pillars (tap {_blossomTurnTaps})";
        _waitUntil = Environment.TickCount64 + BlossomTurnSettleMs;
    }

    private bool TapBlossomTab(long now)
    {
        if (now - _blossomTabAt < _settings.Blossom.TabEveryMs || !_controls.Has(GameAction.TargetNearest))
            return false;
        if (!_controls.Press(GameAction.TargetNearest))
            return false;
        _blossomTabAt = now;
        return true;
    }

    /// <summary>
    /// What the target bar shows, when it is ours: a short line in the top-centre band (y &lt; 13 % h, |x - 50 % w| &lt; 22 %
    /// w, as LOD measured it) reading the Jaso's name - the "[S]" may read "[5]" or drop, so the name after it counts - or
    /// the target bar's chest (user: the Jaso shows it). Null = no target, or another one (a monster, a player).
    /// </summary>
    private async Task<string?> FindBlossomTargetAsync(Mat frame)
    {
        var b = _settings.Blossom;
        string name = b.TargetName;
        string core = TextMatcher.Normalize(name.StartsWith('[') && name.IndexOf(']') is int close and > 0 ? name[(close + 1)..] : name);
        var roi = _settings.Regions.BossName.ToPixels(frame.Width, frame.Height);
        var (_, _, words) = await _analyzer.FindTextAsync(frame, roi, name);
        _blossomTargetHp = BlossomText.TargetHp(ScreenAnalyzer.ToLines(words),
            new NormRect(0.25, 0, 0.50, 0.16).ToPixels(frame.Width, frame.Height));
        foreach (var line in words.GroupBy(w => w.LineBounds).Select(g => (Bounds: g.Key, Text: g.First().LineText)))
        {
            if (line.Bounds.CenterY > frame.Height * 0.13 || Math.Abs(line.Bounds.CenterX - frame.Width / 2.0) > frame.Width * 0.22)
                continue;
            string text = TextMatcher.Normalize(line.Text);
            if (text.Length > core.Length + 10)
                continue;
            if ((core.Length > 0 && text.Contains(core, StringComparison.Ordinal)) || TextMatcher.Similarity(line.Text, name) >= 0.85)
            {
                _overlays.Add(new OverlayItem(line.Bounds, "target", OverlayColor.Green));
                return line.Text;
            }
        }

        var bar = new NormRect(0.25, 0, 0.50, 0.16).ToPixels(frame.Width, frame.Height);
        var chest = _analyzer.FindIcon(frame, "monstericon", bar, 0.80);
        if (chest != null)
        {
            _overlays.Add(new OverlayItem(chest.Bounds, $"chest {chest.Score:0.00}", OverlayColor.Green));
            return $"chest {chest.Score:0.00}";
        }

        return null;
    }

    /// <summary>The AFK Checker watch is on: Auto Blossom, in the map (its phases or the buff round), the game in front.</summary>
    private bool BlossomAfkWatch => IsBlossom && !string.IsNullOrWhiteSpace(_settings.Blossom.AfkCheckerText)
                                    && (IsBlossomPhase(_phase) || _phase == PilotPhase.WarBuff) && _controls.IsGameInFront;

    /// <summary>
    /// The chat's AFK Checker whisper (user, 2026-10-30: "(Whisper) [AFK Checker] : Start 1/5" ... "add a AFK Checker, if 1+/5,
    /// try to move a little bit to other directions"): the newest such line at 1/5 or more -> the held skill let go and a short
    /// Ctrl+click (half <see cref="BlossomSettings.WalkClickPx"/> from the feet) a different way each time; the phase carries on.
    /// Read from the shared whole-screen check (every 5 s), so at most one step per check. True when it stepped.
    /// </summary>
    private bool TryBlossomAfkStep(Mat frame, IReadOnlyList<OcrWord> words, long now)
    {
        var b = _settings.Blossom;
        if (BlossomText.AfkCheckerCount(ScreenAnalyzer.ToLines(words), b.AfkCheckerText, frame.Width, frame.Height) is not int count
            || count < 1)
            return false;

        var (ux, uy, side) = BlossomStepWays[_blossomAfkSteps % BlossomStepWays.Length];
        double px = b.WalkClickPx * 0.5;
        int feetX = frame.Width / 2, feetY = (int)(frame.Height * 0.55);
        int clickX = (int)Math.Round(Math.Clamp(feetX + ux * px, frame.Width * 0.25, frame.Width * RunClickRight));
        int clickY = (int)Math.Round(Math.Clamp(feetY + uy * px, frame.Height * RunClickTop, frame.Height * 0.78));
        ReleaseHeldSkill();
        if (!_controls.WalkTo(clickX, clickY, forceWalk: true))
            return false;

        _blossomAfkSteps++;
        Log?.Invoke(LogLevel.Warning, $"AFK Checker at {count}/5 - stepping {side} a little.");
        BlossomShot(frame, "blossom_afk");
        if (_phase == PilotPhase.BlossomAttack)
            _blossomTargetSeenAt = Math.Max(_blossomTargetSeenAt, now + BlossomStepWalkMs);   // the step is not the kill
        _waitUntil = now + BlossomStepWalkMs;
        return true;
    }

    /// <summary>
    /// Only a target at full health is fought (user, 2026-10-30: "Only Attack monster if full health" - a Jaso another player
    /// is hitting is theirs); the walk goes on and Tab picks another. Unread numbers do not stop a fight. Said at most every
    /// 5 s.
    /// </summary>
    private bool BlossomTargetAtFullHealth(string target, long now)
    {
        if (_blossomTargetHp is not (int cur, int max) || cur >= max)
            return true;

        if (now - _blossomNotFullToldAt >= 5000)
        {
            _blossomNotFullToldAt = now;
            Log?.Invoke(LogLevel.Info, $"'{target}' is not at full health ({cur}/{max}) - leaving it, tabbing on.");
        }

        return false;
    }

    private void BeginBlossomFight(string target)
    {
        long now = Environment.TickCount64;
        // Met while standing at a reached waypoint: that one is done, the walk goes on to the next after the fight.
        if (_blossomPauseSince > 0 && _blossomWaypoint >= 0)
        {
            bool wasForward = _blossomForward;
            _blossomWaypoint = BlossomRoute.Next(_blossomWaypoint, _blossomRoute.Count, _settings.Blossom.LoopFrom,
                _settings.Blossom.BackAndForth, ref _blossomForward);
            LogBlossomTurnaround(wasForward);
        }
        _blossomPauseSince = 0;
        _blossomTarget = target;
        _blossomTargetSeenAt = now;
        _blossomFightStart = now;
        _blossomSkillAt = 0;
        _blossomCoordsAt = 0;
        _blossomAttackCoords = null;
        _blossomJumpCandidate = null;
        _blossomStuckCheckAt = 0;   // the first attack tick takes the baseline
        _blossomStuckCoords = null;
        _blossomStuckLog = null;
        _blossomStuckHp = null;
        _blossomSteps = 0;
        _blossomFightLootAt = 0;
        _blossomRecheckRoute = true;
        Log?.Invoke(LogLevel.Success, $"Jaso Practitioner targeted ('{target}') - {(_settings.Blossom.HoldSkill ? "holding" : "attacking with")} {SkillText(_settings.Dungeon.SkillKey)} ({_blossomKills} kill(s) so far).");
        Advance(PilotPhase.BlossomAttack, 0);
    }

    /// <summary>
    /// Walk the route (SimpleSteer, as LOD), Tab every TabEveryMs, and fight whatever Jaso the target bar shows. The first
    /// step after a turn clicks straight up: where the character went sets the heading (which world direction the pillars'
    /// view looks is not known). A reached waypoint: stand and Tab for at most WaypointWaitMs (the AFK checker), then the next.
    /// </summary>
    private async Task TickBlossomWalkAsync(Mat frame)
    {
        var b = _settings.Blossom;
        long now = Environment.TickCount64;
        if (_blossomRoute.Count == 0)
        {
            Fail(frame, "Auto Blossom has no waypoints (Blossom.Waypoints in settings.json).");
            return;
        }

        // The game behind another window refuses the clicks (live 2026-10-29): a character standing still then is not
        // blocked - nothing is clicked or counted until it is back in front.
        if (!_controls.IsGameInFront)
        {
            _lastCoords = null;
            _blossomDir = null;
            _detail = "waiting for the game window to be in front";
            if (!_blossomFocusTold)
            {
                _blossomFocusTold = true;
                Log?.Invoke(LogLevel.Warning, "The game is behind another window - the walk waits until it is back in front (no clicks).");
            }

            _waitUntil = now + 500;
            return;
        }

        if (_blossomFocusTold)
        {
            _blossomFocusTold = false;
            Log?.Invoke(LogLevel.Info, "The game is back in front - walking on.");
        }

        // The camera turn when leaving a waypoint (user), one tap per tick.
        if (_blossomCamTaps > 0)
        {
            if (!_controls.Turn(_blossomCamKey, b.TurnTapMs))
            {
                _waitUntil = now + 300;
                return;
            }

            if (--_blossomCamTaps == 0)
            {
                _blossomHeadingKnown = false;
                _blossomHeadingMisses = 0;
                _lastCoords = null;
                _simpleClick = null;
                _blossomDir = null;
                Log?.Invoke(LogLevel.Info, "Done - turned the camera; the next step learns the heading again.");
            }

            _waitUntil = now + 250;
            return;
        }

        TapBlossomTab(now);

        // The target bar every BlossomTargetCheckMs (a whole-screen read), not on every click. After a fight given up,
        // targets are ignored for a while: the same one would only start the same fight again.
        if (now >= _blossomTargetAt)
        {
            _blossomTargetAt = now + BlossomTargetCheckMs;
            if (now >= _blossomIgnoreTargetsUntil && await FindBlossomTargetAsync(frame) is { } target && BlossomTargetAtFullHealth(target, now))
            {
                BeginBlossomFight(target);
                return;
            }
        }

        // A rebuff waits for a loot or a waypoint pause; 30 s past due it goes out on the walk.
        if (now - (_warBuffedAt + _settings.War.BuffEveryMs) >= BlossomRebuffOverdueMs)
        {
            ResumeBlossomWalkOrRebuff("on the walk");
            return;
        }

        // At a reached waypoint: no coordinate read needed, so an unreadable minimap cannot stretch the pause past the AFK
        // checker's limit. A rebuff that falls due here goes now, and the walk then heads for the next waypoint.
        if (_blossomPauseSince > 0)
        {
            if (BlossomRebuffDue(now))
            {
                AdvanceBlossomWaypoint($"Waypoint {_blossomWaypoint + 1} done");
                ResumeBlossomWalkOrRebuff("at a waypoint");
                return;
            }

            if (now - _blossomPauseSince < b.WaypointWaitMs)
            {
                _detail = $"at waypoint {_blossomWaypoint + 1}, tabbing ({(now - _blossomPauseSince) / 1000.0:0.0}s of {b.WaypointWaitMs / 1000.0:0.#}s)";
                _waitUntil = Math.Min(now + 300, _blossomPauseSince + b.WaypointWaitMs);
                return;
            }

            AdvanceBlossomWaypoint($"Waypoint {_blossomWaypoint + 1} done");
            return;
        }

        // The coordinates every WalkReadEveryMs steer the clicks; between reads the same click goes out every
        // WalkClickEveryMs, so the character keeps running (user: "continuously click to run instead of staggered").
        if ((now >= _blossomReadAt || _blossomDir == null) && !await BlossomReadStepAsync(frame, now))
            return;

        if (_blossomDir is (double ox, double oy) && now >= _blossomClickAt)
        {
            int centerX = frame.Width / 2;
            // Near a waypoint the click is measured from the character's feet (0.55 h), elsewhere from the middle.
            int centerY = _blossomNear ? (int)(frame.Height * 0.55) : frame.Height / 2;
            double scale = 1;
            double roomLeft = centerX - frame.Width * 0.25, roomRight = frame.Width * RunClickRight - centerX;
            double roomUp = centerY - frame.Height * RunClickTop, roomDown = frame.Height * 0.78 - centerY;
            if (ox < 0) scale = Math.Min(scale, roomLeft / -ox);
            if (ox > 0) scale = Math.Min(scale, roomRight / ox);
            if (oy < 0) scale = Math.Min(scale, roomUp / -oy);
            if (oy > 0) scale = Math.Min(scale, roomDown / oy);

            int clickX = (int)Math.Round(centerX + ox * scale);
            int clickY = (int)Math.Round(centerY + oy * scale);
            if (!_controls.WalkTo(clickX, clickY, forceWalk: false))
            {
                _detail = "walk click blocked - waiting for the game to be in front";
                _waitUntil = now + 300;
                return;
            }

            _simpleClick = (ox, oy);
            _lastClick = (clickX, clickY, now);
            // Near a waypoint one click per read (the read sets the next one); else the same click again shortly.
            _blossomClickAt = _blossomNear ? long.MaxValue : now + b.WalkClickEveryMs;
        }

        long next = Math.Min(Math.Min(_blossomClickAt, _blossomReadAt), Math.Min(_blossomTargetAt, _blossomTabAt + b.TabEveryMs));
        _waitUntil = Math.Max(now + 50, next);
    }

    /// <summary>
    /// One step of the walk: read the coordinates, learn the heading from the movement since the last read (the clicks in
    /// between all went the same way), and decide the click for the next WalkReadEveryMs (<c>_blossomDir</c>). False when
    /// this tick is done: the waypoint was reached (the pause), skipped, or the walk waits.
    /// </summary>
    private async Task<bool> BlossomReadStepAsync(Mat frame, long now)
    {
        var b = _settings.Blossom;
        var coords = await _analyzer.ReadCoordinatesAsync(frame);
        if (coords == null)
        {
            if (++_blossomUnread > BlossomMaxUnread)
            {
                Fail(frame, $"The minimap coordinates did not read {BlossomMaxUnread} times in a row.");
                return false;
            }

            // The clicks go on the way they went; the next read soon.
            _detail = "reading the minimap coordinates";
            _blossomReadAt = now + 300;
            return _blossomDir != null;
        }

        _blossomUnread = 0;
        _blossomReadAt = now + b.WalkReadEveryMs;   // near the waypoint: see _blossomNear below
        if (_blossomWaypoint < 0)
        {
            _blossomWaypointSteps = 0;
            _blossomBestDistance = double.MaxValue;
            _blossomWaypoint = BlossomRoute.Nearest(_blossomRoute, coords.X, coords.Y);
            _blossomLegFrom = _blossomWaypoint > 0 ? _blossomRoute[_blossomWaypoint - 1] : (coords.X, coords.Y);
            var start = _blossomRoute[_blossomWaypoint];
            Log?.Invoke(LogLevel.Info, $"Walking the route from waypoint {_blossomWaypoint + 1} ({start.X},{start.Y}), at {coords}.");
        }
        else if (_blossomRecheckRoute)
        {
            // The first read after a fight (live 2026-10-30 11:20: a chase ended 111 units from waypoint 20, and the walk
            // then tried to reach waypoint 7, 2750 units south, straight through the terrain): far from the waypoint it was
            // heading for, but next to another one -> on from that one, along the route as before.
            var heading = _blossomRoute[_blossomWaypoint];
            int nearest = BlossomRoute.Nearest(_blossomRoute, coords.X, coords.Y);
            var near = _blossomRoute[nearest];
            if (nearest != _blossomWaypoint && coords.DistanceTo(heading.X, heading.Y) > BlossomOffRouteDistance + b.RunAheadDistance
                && coords.DistanceTo(near.X, near.Y) <= BlossomRejoinDistance)
            {
                Log?.Invoke(LogLevel.Info, $"Off the route after the fight ({coords.DistanceTo(heading.X, heading.Y):0} from waypoint " +
                                           $"{_blossomWaypoint + 1}) - walking on from waypoint {nearest + 1} ({near.X},{near.Y}), the nearest.");
                _blossomWaypoint = nearest;
                _blossomLegFrom = (coords.X, coords.Y);
                _blossomWaypointSteps = 0;
                _blossomBestDistance = double.MaxValue;
                _blossomStalls = 0;
            }
        }

        _blossomRecheckRoute = false;

        // Run, don't stagger (user, 2026-10-30: "dont walk, run far away instead of staggered"): waypoints closer than
        // RunAheadDistance are run past - the walk aims at the first one at least that far along the route, so a recorded
        // route (a waypoint every 120 units) is not homed in on and paused at every 120 units. One with a camera turn is
        // still reached, so its turn is made there.
        if (b.RunAheadDistance > 0)
        {
            for (int n = 0; n < _blossomRoute.Count; n++)
            {
                var wp = _blossomRoute[_blossomWaypoint];
                if (coords.DistanceTo(wp.X, wp.Y) >= b.RunAheadDistance || _blossomTurns.ContainsKey(_blossomWaypoint + 1))
                    break;
                bool wasForward = _blossomForward;
                _blossomLegFrom = wp;
                _blossomWaypoint = BlossomRoute.Next(_blossomWaypoint, _blossomRoute.Count, b.LoopFrom, b.BackAndForth, ref _blossomForward);
                LogBlossomTurnaround(wasForward);
                _blossomWaypointSteps = 0;
                _blossomBestDistance = double.MaxValue;
            }
        }

        var goal = _blossomRoute[_blossomWaypoint];
        double needX = goal.X - coords.X, needY = goal.Y - coords.Y;
        double distance = Math.Sqrt(needX * needX + needY * needY);
        _overlays.Add(new OverlayItem(coords.Bounds, $"{coords} -> wp {_blossomWaypoint + 1} d={distance:0}", OverlayColor.Yellow));

        // Near the waypoint (live run 5, user: it stopped north of waypoint 1 and did not correct it - the clicks between two
        // reads ran on blind and overshot): one shorter click per read, every BlossomNearReadMs.
        _blossomNear = distance < BlossomNearDistance;
        if (_blossomNear)
            _blossomReadAt = now + BlossomNearReadMs;

        if (_lastCoords is (int px, int py) && _simpleClick is (double cx, double cy))
        {
            var moved = ((double)(coords.X - px), (double)(coords.Y - py));
            double step = Math.Sqrt(moved.Item1 * moved.Item1 + moved.Item2 * moved.Item2);
            if (step >= 60)
            {
                // The angle from the (corrected) click to the movement: the whole screen-to-world heading.
                double cross = cx * moved.Item2 - cy * moved.Item1;
                double dot = cx * moved.Item1 + cy * moved.Item2;
                double angle = Math.Atan2(cross, dot);
                if (!_blossomHeadingKnown)
                {
                    SetBlossomHeading(angle, "learnt from the first step");
                }
                else if (Math.Abs(Math.IEEERemainder(angle - _headingError, 2 * Math.PI)) > BlossomHeadingMissRad)
                {
                    // Far from what the heading says: a wall or a mob pushed one step - or the first step was such a push
                    // and the heading is wrong. Several in a row soon after the heading was set = the heading
                    // (LearnHeadingError never moves it that far). Later, misses are a wall to slide along, not the camera
                    // (live run 10: sliding along the bridge's railing re-learnt +90 and the walk went east, off the route).
                    if (++_blossomHeadingAge <= BlossomRelearnWindow && ++_blossomHeadingMisses >= BlossomHeadingMissesToRelearn)
                        SetBlossomHeading(angle, $"re-learnt after {_blossomHeadingMisses} steps that went elsewhere");
                }
                else
                {
                    _blossomHeadingAge++;
                    _blossomHeadingMisses = 0;
                    LearnHeadingError((cx, cy), moved);
                }
            }

            // Near a waypoint the clicks are short, so a smaller step still counts as moving.
            _blossomStalls = step < (_blossomNear ? 8 : 20) ? _blossomStalls + 1 : 0;
        }

        _lastCoords = (coords.X, coords.Y);

        // Reached: within WaypointTolerance - or blocked close to it (a railing, a pond edge), which is as near as it gets.
        bool closeEnough = distance > b.WaypointTolerance && _blossomStalls >= 2 && distance <= BlossomCloseEnough;
        if (distance <= b.WaypointTolerance || closeEnough)
        {
            _simpleClick = null;
            _blossomDir = null;
            Log?.Invoke(LogLevel.Info, closeEnough
                ? $"Waypoint {_blossomWaypoint + 1} reached at {coords} (close enough - blocked {distance:0} away) - tabbing up to {b.WaypointWaitMs / 1000.0:0.#}s."
                : $"Waypoint {_blossomWaypoint + 1} reached at {coords} - tabbing up to {b.WaypointWaitMs / 1000.0:0.#}s.");
            if (b.WaypointWaitMs <= 0)
            {
                AdvanceBlossomWaypoint($"Waypoint {_blossomWaypoint + 1} done");
                return false;
            }

            _blossomPauseSince = now;
            _waitUntil = now + 300;
            return false;
        }

        // No skip (user, 2026-10-30: "If the character didn't move for 5s, try to zoom out and move in every direction until it
        // moves"): one note and a snapshot, and the steps go on (BlossomUnblock); the no-progress rule below still skips.
        if (_blossomStalls == BlossomMaxStalls)
        {
            Log?.Invoke(LogLevel.Warning, $"Not moving for {_blossomStalls} steps at {coords} on the way to waypoint {_blossomWaypoint + 1} - trying every way until it moves.");
            BlossomShot(frame, "blossom_stuck");
        }

        // Moving, but no closer (round an obstacle and back): skipped after BlossomNoProgressSteps reads without getting 30
        // units closer. Not a fixed step count - live 2026-10-29 the 3200-unit leg to waypoint 2 was skipped after 40 steps
        // while it was still closing in.
        if (distance < _blossomBestDistance - 30)
        {
            if (_blossomBestDistance < double.MaxValue)
                _blossomUnblockWay = 0;   // closer: the next block starts with the first way again
            _blossomBestDistance = distance;
            _blossomWaypointSteps = 0;
        }
        else if (++_blossomWaypointSteps >= BlossomNoProgressSteps)
        {
            Log?.Invoke(LogLevel.Warning, $"Waypoint {_blossomWaypoint + 1} is taking too long (no closer than {_blossomBestDistance:0} in {BlossomNoProgressSteps} steps, at {coords}) - skipping it.");
            BlossomShot(frame, "blossom_too_long");
            AdvanceBlossomWaypoint($"Waypoint {_blossomWaypoint + 1} skipped");
            return false;
        }

        if (!_blossomHeadingKnown)
        {
            // The first step: straight up the screen (see the summary).
            _blossomDir = (0, -b.WalkClickPx);
        }
        else
        {
            double ox, oy;
            if (_blossomNear)
            {
                // Homing in: click the waypoint's own spot, measured from the character's feet (the click base is the feet
                // then, see the walk tick) - never a short click just under them, which lands on the character's body and
                // does nothing (live run 7: "close enough" 52 off, the click back south never walked).
                double clickPx = Math.Clamp(distance * BlossomPxPerUnit, BlossomMinNearClickPx, b.WalkClickPx);
                (ox, oy) = (needX / distance * clickPx, needY / distance * clickPx);
            }
            else
            {
                // Along the leg: aim BlossomLookAhead units ahead on the line from the waypoint just left to this one (user:
                // "not aligned by the dirt road ... goes straight to the water beside the bridge") - drift is pulled back
                // onto the line, which the user laid along the road.
                var (tx, ty) = BlossomRoute.LookAhead(_blossomLegFrom ?? (coords.X, coords.Y), goal, (coords.X, coords.Y), BlossomLookAhead);
                double ax = tx - coords.X, ay = ty - coords.Y, len = Math.Max(1, Math.Sqrt(ax * ax + ay * ay));
                (ox, oy) = (ax / len * b.WalkClickPx, ay / len * b.WalkClickPx);
            }

            double c = Math.Cos(_headingError), sn = Math.Sin(_headingError);
            (ox, oy) = (ox * c + oy * sn, -ox * sn + oy * c);
            if (_blossomStalls >= 2)
                (ox, oy) = BlossomUnblock(frame, coords, ox, oy);
            _blossomDir = (ox, oy);
        }

        _blossomClickAt = Math.Min(_blossomClickAt, now);   // a new way: click it now
        _detail = $"{coords} -> waypoint {_blossomWaypoint + 1} ({distance:0} left)";
        return true;
    }

    /// <summary>
    /// Blocked (live 2026-10-29: against the side of the bridge, where the camera had zoomed in - a 200 px click is a much
    /// shorter walk then), from the 2nd still step: the ways of <see cref="BlossomStepWays"/> in turn, relative to the way
    /// (right of it, left, back, then the diagonals), a full-length click each; the camera is zoomed out again at the start
    /// of each round (user, 2026-10-30: "try to zoom out and move in every direction until it moves").
    /// </summary>
    private (double X, double Y) BlossomUnblock(Mat frame, GameCoordinates coords, double ox, double oy)
    {
        double len = Math.Max(1, Math.Sqrt(ox * ox + oy * oy));
        double fx = ox / len * _settings.Blossom.WalkClickPx, fy = oy / len * _settings.Blossom.WalkClickPx;   // ahead
        // The way to try goes on round the list until the walk gets closer to its waypoint (live 2026-10-30 11:20: each
        // sidestep moved the character 20+ units, which reset the still-step count, so it stepped right, left, right ...).
        int index = _blossomUnblockWay++ % BlossomStepWays.Length;
        if (index == 0 && _settings.Dungeon.ZoomOutNotches > 0
            && _controls.ZoomOut(frame.Width / 2, (int)(frame.Height * 0.40), _settings.Dungeon.ZoomOutNotches))
            Log?.Invoke(LogLevel.Info, $"Blocked at {coords} - zoomed out again.");

        var (x, y, name) = BlossomStepWays[index];   // x: right of the way, y: back
        Log?.Invoke(LogLevel.Info, $"Blocked at {coords} ({_blossomStalls} still steps towards waypoint {_blossomWaypoint + 1}) - stepping {name}.");
        return (-x * fy - y * fx, x * fx - y * fy);
    }

    /// <summary>The view's world direction (screen-up turned <paramref name="angle"/> from world north); the heading fix restarts there.</summary>
    private void SetBlossomHeading(double angle, string how)
    {
        _headingBase = angle;
        _headingError = angle;
        _headingSamples = 1;
        _blossomHeadingKnown = true;
        _blossomHeadingMisses = 0;
        _blossomHeadingAge = 0;
        Log?.Invoke(LogLevel.Info, $"Heading {how}: the view is turned {angle * 180 / Math.PI:0} deg from north.");
    }

    /// <summary>A debug snapshot, at most 3 per tag per Start (a wall on the loop would save one every lap).</summary>
    private void BlossomShot(Mat frame, string tag)
    {
        int n = _blossomShots.GetValueOrDefault(tag);
        if (n >= 3)
            return;
        _blossomShots[tag] = n + 1;
        DebugArtifacts.SaveFrame(frame, _overlays, tag);
    }

    private void AdvanceBlossomWaypoint(string what)
    {
        int left = _blossomWaypoint + 1;
        bool wasForward = _blossomForward;
        _blossomLegFrom = _blossomRoute[_blossomWaypoint];   // the next leg runs from the waypoint just left
        _blossomWaypoint = BlossomRoute.Next(_blossomWaypoint, _blossomRoute.Count, _settings.Blossom.LoopFrom,
            _settings.Blossom.BackAndForth, ref _blossomForward);
        var next = _blossomRoute[_blossomWaypoint];
        bool turnaround = LogBlossomTurnaround(wasForward);
        Log?.Invoke(LogLevel.Info, $"{what} - on to waypoint {_blossomWaypoint + 1} ({next.X},{next.Y}).");
        _blossomPauseSince = 0;
        _blossomStalls = 0;
        _blossomWaypointSteps = 0;
        _blossomBestDistance = double.MaxValue;
        _simpleClick = null;
        _lastCoords = null;
        _blossomDir = null;
        _waitUntil = Environment.TickCount64;

        // The camera turn the user set for the waypoint just left (WaypointTurns), done by the walk one tap per tick. Walking
        // back (BackAndForth) it is undone at the same waypoint, so each stretch keeps the camera it had on the way out; a
        // turnaround taps nothing (the last waypoint's own turn is never made, and at the loop start undo + redo cancel out).
        if (!turnaround && _blossomTurns.TryGetValue(left, out var turn))
        {
            if (!_blossomForward)
                turn = (turn.Direction == TurnDirection.Left ? TurnDirection.Right : TurnDirection.Left, turn.Taps);
            _blossomCamKey = turn.Direction;
            _blossomCamTaps = turn.Taps;
            string key = turn.Direction == TurnDirection.Left ? _settings.Dungeon.RotateLeftKey : _settings.Input.RotateKey;
            Log?.Invoke(LogLevel.Info, $"Leaving waypoint {left}: turning the camera {(turn.Direction == TurnDirection.Left ? "left" : "right")} ('{key}' x{turn.Taps}).");
        }
    }

    /// <summary>
    /// After a kill, 0 to RandomSkipMax waypoints ahead at random (user, 2026-10-30: "randomly try to skip multiple waypoints so
    /// that it doesn't look like a bot"), only onto waypoints within BlossomSkipReach of where the fight ended.
    /// </summary>
    private void BlossomRandomSkip()
    {
        var b = _settings.Blossom;
        if (b.RandomSkipMax <= 0 || _blossomWaypoint < 0 || _blossomAttackCoords is not (int x, int y))
            return;

        int n = Random.Shared.Next(0, b.RandomSkipMax + 1);
        bool wasForward = _blossomForward;
        int from = _blossomWaypoint;
        _blossomWaypoint = BlossomRoute.SkipWithin(_blossomRoute, _blossomWaypoint, b.LoopFrom, b.BackAndForth, ref _blossomForward,
            n, (x, y), BlossomSkipReach);
        if (_blossomWaypoint == from)
            return;

        LogBlossomTurnaround(wasForward);
        _blossomLegFrom = null;
        _blossomWaypointSteps = 0;
        _blossomBestDistance = double.MaxValue;
        Log?.Invoke(LogLevel.Info, $"Skipping ahead at random - on to waypoint {_blossomWaypoint + 1} instead of {from + 1}.");
    }

    /// <summary>Walk-back (BackAndForth): one log line when the walk's direction just turned. True when it did.</summary>
    private bool LogBlossomTurnaround(bool wasForward)
    {
        if (wasForward == _blossomForward)
            return false;
        Log?.Invoke(LogLevel.Info, _blossomForward
            ? "Back at the start of the route - walking it forward again."
            : "End of the route - walking it back the same way.");
        return true;
    }

    /// <summary>
    /// The skill on the Jaso (held with Blossom.HoldSkill, on by default, else tapped every Lod.SkillEveryMs; Ctrl + skill
    /// with Dungeon.CtrlSkill) until the target is gone TargetGoneMs = dead -> loot. A coordinate jump with no target in view =
    /// died (with the target in view it is a chase); a target that never goes is given up after FightMaxMs (one out of reach
    /// would hold the character still).
    /// </summary>
    private async Task TickBlossomAttackAsync(Mat frame)
    {
        var b = _settings.Blossom;
        var lod = _settings.Lod;
        string skill = _settings.Dungeon.SkillKey;
        long now = Environment.TickCount64;

        if (!string.IsNullOrWhiteSpace(skill))
        {
            if (b.HoldSkill)
            {
                if (_heldSkill != null && _settings.Dungeon.CtrlSkill && now - _blossomSkillAt >= lod.SkillEveryMs)
                {
                    ReleaseHeldSkill();
                    Thread.Sleep(CtrlSkillGapMs);
                    _controls.SkillCtrlTap(skill);
                    Thread.Sleep(CtrlSkillGapMs);
                }

                if (_heldSkill == null && _controls.SkillDown(skill))
                {
                    _heldSkill = skill;
                    _blossomSkillAt = now;
                }
            }
            else if (now - _blossomSkillAt >= lod.SkillEveryMs && _controls.SkillTap(skill))
            {
                _blossomSkillAt = now;
            }
        }

        if (await FindBlossomTargetAsync(frame) != null)
            _blossomTargetSeenAt = now;

        if (now - _blossomCoordsAt >= 1500)
        {
            _blossomCoordsAt = now;
            var coords = await _analyzer.ReadCoordinatesAsync(frame);
            if (coords != null)
            {
                // A jump counts only when the next read agrees with it (live run 7: "12757, 1352" was 13521 with a digit
                // dropped - the pilot took it for a death mid-fight and turned back to the pillars).
                bool jumped = _blossomAttackCoords is (int ax0, int ay0) && coords.DistanceTo(ax0, ay0) > lod.TeleportJump;
                bool confirmed = jumped && _blossomJumpCandidate is (int jx, int jy) && coords.DistanceTo(jx, jy) <= 200;
                if (jumped && !confirmed)
                {
                    _blossomJumpCandidate = (coords.X, coords.Y);
                    _blossomCoordsAt = now - 1000;   // check again in 0.5 s
                }
                else if (!jumped)
                {
                    _blossomJumpCandidate = null;
                }

                // The target still in the target bar = alive and chasing it (live 2026-10-30: with TAB target "Nearest
                // monster" and the Run skill the character covers ~500 units between two reads): the new spot is the baseline.
                if (confirmed && now - _blossomTargetSeenAt <= BlossomChaseTargetMs)
                {
                    _blossomJumpCandidate = null;
                    _blossomAttackCoords = (coords.X, coords.Y);
                    Log?.Invoke(LogLevel.Trace, $"Moved to {coords} while '{b.TargetName}' is targeted - chasing it.");
                }
                else if (confirmed && _blossomAttackCoords is (int ax, int ay))
                {
                    _blossomJumpCandidate = null;
                    ReleaseHeldSkill();
                    Log?.Invoke(LogLevel.Warning, $"Moved to {coords} (from {ax},{ay}) during the fight - killed? Lining up again.");
                    BlossomShot(frame, "blossom_died");
                    _lastCoords = null;
                    Advance(PilotPhase.BlossomRespawn, lod.RespawnSettleMs);
                    return;
                }

                if (!jumped)
                    _blossomAttackCoords = (coords.X, coords.Y);
            }
        }

        // Looting through the fight (LootWhileFighting): the drops are picked up during the TargetGoneMs that tells the kill.
        if (b.LootWhileFighting && now - _blossomFightLootAt >= b.LootEveryMs && _controls.Press(GameAction.Loot))
            _blossomFightLootAt = now;

        if (now - _blossomTargetSeenAt > b.TargetGoneMs)
        {
            ReleaseHeldSkill();
            _blossomKills++;
            if (b.LootWhileFighting)
            {
                Log?.Invoke(LogLevel.Success, $"Jaso Practitioner down (kill {_blossomKills}) - looted while fighting, walking on.");
                BlossomRandomSkip();
                ResumeBlossomWalkOrRebuff("after the kill");
                return;
            }

            Log?.Invoke(LogLevel.Success, $"Jaso Practitioner down (kill {_blossomKills}) - looting with '{lod.LootKey}' for {b.LootMs / 1000.0:0.#}s in {b.LootDelayMs / 1000.0:0.#}s.");
            _blossomLootStart = 0;
            _blossomLootAt = 0;
            Advance(PilotPhase.BlossomLoot, b.LootDelayMs);
            return;
        }

        if (now - _blossomFightStart > b.FightMaxMs)
        {
            ReleaseHeldSkill();
            Log?.Invoke(LogLevel.Warning, $"'{_blossomTarget}' still targeted after {b.FightMaxMs / 1000}s - giving it up, walking on (targets ignored for {BlossomGiveUpIgnoreMs / 1000}s).");
            BlossomShot(frame, "blossom_fight_long");
            _controls.Press(GameAction.TargetNearest);
            _blossomTabAt = now;
            _blossomIgnoreTargetsUntil = now + BlossomGiveUpIgnoreMs;
            ResumeBlossomWalk();
            return;
        }

        if (b.FightStuckMs > 0 && now >= _blossomStuckCheckAt && await BlossomFightStuckAsync(frame, now))
            return;

        _detail = $"attacking '{_blossomTarget}' (seen {(now - _blossomTargetSeenAt) / 1000.0:0.0}s ago, fight {(now - _blossomFightStart) / 1000}s)";
        _waitUntil = now + Math.Min(lod.SkillEveryMs, 300);
    }

    /// <summary>
    /// Every FightStuckMs of a fight (user, 2026-10-30: "if a monster is tab, but there is 5s that it didn't move and no attack
    /// succeeded log, try to click on other direction to reposition"): the spot (the fight's latest coordinate read) and the
    /// combat log's "Attack succeeded" lines against the last check. Neither changed -> the skill let go and a Ctrl+click
    /// (walk, the target stays) WalkClickPx from the feet: right, then left, back, ahead in turn; the hold comes back on the
    /// next tick. An unread spot is not taken for a still one. True when it stepped.
    /// </summary>
    private async Task<bool> BlossomFightStuckAsync(Mat frame, long now)
    {
        var b = _settings.Blossom;
        var roi = _settings.Regions.AttackMessage.ToPixels(frame.Width, frame.Height);
        string log = await ReadAttackLogAsync(frame, roi, _settings.Dungeon.AttackConfirmText);
        var at = _blossomAttackCoords;
        bool first = _blossomStuckCheckAt == 0;
        bool still = !first && at is (int x, int y) && _blossomStuckCoords is (int sx, int sy)
                     && Math.Sqrt((double)(x - sx) * (x - sx) + (double)(y - sy) * (y - sy)) < BlossomStuckMoveUnits;
        // A hit = a new "Attack succeeded" line, or the target bar's HP lower than at the last look (live 2026-10-30 11:19:
        // looting while fighting fills the log with "No items or [Silver] to obtain", so the attack lines scroll away).
        int? hp = _blossomTargetHp?.Cur;
        bool hpFell = hp is int now0 && _blossomStuckHp is int then0 && now0 < then0;
        bool noHit = !first && log == _blossomStuckLog && !hpFell;
        _blossomStuckCoords = at;
        _blossomStuckLog = log;
        _blossomStuckHp = hp;
        _blossomStuckCheckAt = now + b.FightStuckMs;
        if (!still || !noHit)
        {
            _blossomSteps = 0;   // moving or hitting: the next stuck spell starts with a zoom-out and the first way again
            return false;
        }

        // User, 2026-10-30: "If the character didn't move for 5s, try to zoom out and move in every direction until it moves":
        // a zoom-out at the start of each round of ways, and the next look comes soon after a step, not FightStuckMs later.
        var (ux, uy, side) = BlossomStepWays[_blossomSteps % BlossomStepWays.Length];
        if (_blossomSteps % BlossomStepWays.Length == 0 && _settings.Dungeon.ZoomOutNotches > 0
            && _controls.ZoomOut(frame.Width / 2, (int)(frame.Height * 0.40), _settings.Dungeon.ZoomOutNotches))
            Log?.Invoke(LogLevel.Info, $"Stuck on '{_blossomTarget}' - zoomed out again.");
        _blossomSteps++;
        int feetX = frame.Width / 2, feetY = (int)(frame.Height * 0.55);
        int clickX = (int)Math.Round(Math.Clamp(feetX + ux * b.WalkClickPx, frame.Width * 0.25, frame.Width * RunClickRight));
        int clickY = (int)Math.Round(Math.Clamp(feetY + uy * b.WalkClickPx, frame.Height * RunClickTop, frame.Height * 0.78));
        ReleaseHeldSkill();
        if (!_controls.WalkTo(clickX, clickY, forceWalk: true))
        {
            _blossomStuckCheckAt = now;   // input refused (the game behind another window): look again next tick
            return false;
        }

        Log?.Invoke(LogLevel.Info, $"No move and no new '{_settings.Dungeon.AttackConfirmText}' line for {b.FightStuckMs / 1000.0:0.#}s on " +
                                   $"'{_blossomTarget}' - stepping {side} to get round it (step {_blossomSteps}).");
        BlossomShot(frame, "blossom_fight_stuck");
        _blossomTargetSeenAt = now + BlossomStepWalkMs;   // the step's walk is not the target gone
        _blossomStuckCheckAt = now + BlossomStepWalkMs + BlossomStepRecheckMs;
        _detail = $"stepping {side} to reach '{_blossomTarget}'";
        _waitUntil = now + BlossomStepWalkMs;
        return true;
    }

    /// <summary>The loot key every LootEveryMs for LootMs (user: 0.2 s for 5 s), no Tab; then on with the walk.</summary>
    private void TickBlossomLoot()
    {
        var b = _settings.Blossom;
        long now = Environment.TickCount64;
        if (_blossomLootStart == 0)
            _blossomLootStart = now;

        if (now - _blossomLootStart >= b.LootMs || !_controls.Has(GameAction.Loot))
        {
            Log?.Invoke(LogLevel.Info, $"Looted for {b.LootMs / 1000.0:0.#}s - " +
                                       (_blossomWaypoint >= 0 ? $"on to waypoint {_blossomWaypoint + 1}." : "back to the route."));
            BlossomRandomSkip();
            ResumeBlossomWalkOrRebuff("after the loot");
            return;
        }

        if (now - _blossomLootAt >= b.LootEveryMs && _controls.Press(GameAction.Loot))
            _blossomLootAt = now;
        _detail = $"looting ('{_settings.Lod.LootKey}') {(now - _blossomLootStart) / 1000.0:0.0}s";
        _waitUntil = now + 50;
    }

    /// <summary>After a death: two equal coordinate readings (or 20 s), then line up again and walk from the nearest waypoint.</summary>
    private async Task TickBlossomRespawnAsync(Mat frame)
    {
        long now = Environment.TickCount64;
        var coords = await _analyzer.ReadCoordinatesAsync(frame);
        bool still = coords != null && _lastCoords is (int lx, int ly) && coords.DistanceTo(lx, ly) < 20;
        _lastCoords = coords == null ? null : (coords.X, coords.Y);
        if (!still && now - _phaseStart < 20000)
        {
            _detail = "waiting for the character to settle";
            _waitUntil = now + 700;
            return;
        }

        // The pillars are only in view from the arrival spot, where a death puts the character back. Elsewhere the "death"
        // was a chase that lost its target while the minimap did not read (live 2026-10-30 10:20, alive at full HP 778 units
        // on): buffs and the walk, the heading learnt from it.
        if (coords != null && coords.DistanceTo(BlossomArrivalX, BlossomArrivalY) > BlossomArrivalRadius)
        {
            Log?.Invoke(LogLevel.Info, $"At {coords}, away from the arrival spot - no pillar turn, walking from the nearest waypoint.");
            _blossomHeadingKnown = false;
            _blossomWaypoint = -1;
            BeginBlossomBuff("after the jump", thenTurn: false);
            return;
        }

        BeginBlossomArrival();
    }

    /// <summary>
    /// Start (or a restart) inside Blossom Garden: a Jaso already targeted -> fight it first; else the buffs. Then the walk
    /// from the nearest waypoint, with no pillar turn.
    /// </summary>
    private async Task TickBlossomCheckZoneAsync(Mat frame)
    {
        _zoneConfirmedAt = Environment.TickCount64;

        // Start inside with the autohunt running: it would fight everything beside the walk. The newest autohunt line in
        // the combat log decides (J is a toggle), as at the Laby's final boss; ticked until it is off or given up.
        if (!_blossomHuntOffChecked && !_controls.IsDryRun && _controls.Has(GameAction.ToggleAutohunt))
        {
            if (!await EnsureAutohuntOffAsync(frame, $"in {Destination}", trustVisibleLine: true))
                return;
            _blossomHuntOffChecked = true;
        }

        // No pillar turn here (user, 2026-10-30: "it is possible to start it already in the map and no need to look for the
        // pillar"): away from the arrival spot they are not in view; the walk learns the heading from its first step.
        if (await FindBlossomTargetAsync(frame) is { } target && BlossomTargetAtFullHealth(target, Environment.TickCount64))
        {
            Log?.Invoke(LogLevel.Warning, $"Already in {Destination} with '{target}' targeted - fighting it, then walking from the nearest waypoint.");
            BeginBlossomFight(target);
            return;
        }

        Log?.Invoke(LogLevel.Info, $"Already in {Destination} - buffing, then walking from the nearest waypoint (no pillar turn).");
        BeginBlossomBuff("already in the map", thenTurn: false);
    }
}

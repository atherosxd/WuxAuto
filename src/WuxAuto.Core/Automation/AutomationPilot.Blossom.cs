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
    private const double BlossomHeadingMissRad = 0.6;
    private const int BlossomHeadingMissesToRelearn = 3;
    private const int BlossomNoProgressSteps = 15;
    private const int BlossomTargetCheckMs = 700;
    private const double BlossomNearDistance = 300;   // from here the walk homes in on the waypoint
    private const int BlossomNearReadMs = 450;
    private const double BlossomCloseEnough = 150;    // blocked this close to a waypoint = reached

    private IReadOnlyList<(int X, int Y)> _blossomRoute = [];
    private int _blossomWaypoint = -1;
    private long _blossomTabAt;
    private long _blossomPauseSince;
    private int _blossomStalls;
    private int _blossomUnread;
    private bool _blossomHeadingKnown;
    private bool _blossomHeadingUnknown;   // a fight began before any turn (Start inside the map): turn after it
    private bool _blossomBuffThenTurn;
    private bool _blossomHuntOffChecked;
    private int _blossomHeadingMisses;
    private long _blossomIgnoreTargetsUntil;
    private int _blossomWaypointSteps;          // reads in a row without getting closer to the waypoint
    private double _blossomBestDistance = double.MaxValue;
    private bool _blossomFocusTold;
    private (double X, double Y)? _blossomDir;   // the walk click (screen offset) until the next coordinate read
    private bool _blossomNear;                   // within BlossomNearDistance of the waypoint: one click per read
    private long _blossomClickAt;
    private long _blossomReadAt;
    private long _blossomTargetAt;
    private int _blossomCamTaps;                 // the camera turn when leaving a waypoint (WaypointTurns), taps left
    private TurnDirection _blossomCamKey;
    private IReadOnlyDictionary<int, (TurnDirection Direction, int Taps)> _blossomTurns = new Dictionary<int, (TurnDirection, int)>();
    private bool _blossomZoomedOutAgain;
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
        _blossomHeadingUnknown = false;
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
        _blossomHeadingUnknown = false;
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
        if (_blossomHeadingUnknown)
        {
            BeginBlossomTurn();
            return;
        }

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
        if (_blossomTurnTaps >= b.TurnMaxTaps)
        {
            Log?.Invoke(LogLevel.Warning, $"Turned {_blossomTurnTaps} taps and no pillars lined up - walking anyway; the heading is learnt from the walk.");
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

    private void BeginBlossomFight(string target)
    {
        long now = Environment.TickCount64;
        // Met while standing at a reached waypoint: that one is done, the walk goes on to the next after the fight.
        if (_blossomPauseSince > 0 && _blossomWaypoint >= 0)
            _blossomWaypoint = BlossomRoute.Next(_blossomWaypoint, _blossomRoute.Count, _settings.Blossom.LoopFrom);
        _blossomPauseSince = 0;
        _blossomTarget = target;
        _blossomTargetSeenAt = now;
        _blossomFightStart = now;
        _blossomSkillAt = 0;
        _blossomCoordsAt = 0;
        _blossomAttackCoords = null;
        Log?.Invoke(LogLevel.Success, $"Jaso Practitioner targeted ('{target}') - attacking with {SkillText(_settings.Dungeon.SkillKey)} ({_blossomKills} kill(s) so far).");
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
            if (now >= _blossomIgnoreTargetsUntil && await FindBlossomTargetAsync(frame) is { } target)
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
            int centerY = frame.Height / 2;
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
            _blossomZoomedOutAgain = false;
            _blossomWaypoint = BlossomRoute.Nearest(_blossomRoute, coords.X, coords.Y);
            var start = _blossomRoute[_blossomWaypoint];
            Log?.Invoke(LogLevel.Info, $"Walking the route from waypoint {_blossomWaypoint + 1} ({start.X},{start.Y}), at {coords}.");
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
                    // and the heading is wrong. Several in a row = the heading (LearnHeadingError never moves it that far).
                    if (++_blossomHeadingMisses >= BlossomHeadingMissesToRelearn)
                        SetBlossomHeading(angle, $"re-learnt after {_blossomHeadingMisses} steps that went elsewhere");
                }
                else
                {
                    _blossomHeadingMisses = 0;
                    LearnHeadingError((cx, cy), moved);
                }
            }

            _blossomStalls = step < 20 ? _blossomStalls + 1 : 0;
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

        if (_blossomStalls >= BlossomMaxStalls)
        {
            Log?.Invoke(LogLevel.Warning, $"Not moving for {_blossomStalls} steps at {coords} on the way to waypoint {_blossomWaypoint + 1} - skipping it.");
            BlossomShot(frame, "blossom_stuck");
            AdvanceBlossomWaypoint($"Waypoint {_blossomWaypoint + 1} skipped");
            return false;
        }

        // Moving, but no closer (round an obstacle and back): skipped after BlossomNoProgressSteps reads without getting 30
        // units closer. Not a fixed step count - live 2026-10-29 the 3200-unit leg to waypoint 2 was skipped after 40 steps
        // while it was still closing in.
        if (distance < _blossomBestDistance - 30)
        {
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
            double reach = b.WalkClickPx * Math.Clamp(distance / BlossomNearDistance, 0.3, 1.0);
            double ox = needX / distance * reach, oy = needY / distance * reach;
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
    /// shorter walk then): on the 2nd still step the camera is zoomed out again (once per waypoint) and the click goes to the
    /// right of the way, on the 3rd to the left, on the 4th back; the 5th skips the waypoint. A full-length click each time.
    /// </summary>
    private (double X, double Y) BlossomUnblock(Mat frame, GameCoordinates coords, double ox, double oy)
    {
        double len = Math.Max(1, Math.Sqrt(ox * ox + oy * oy));
        double ux = ox / len * _settings.Blossom.WalkClickPx, uy = oy / len * _settings.Blossom.WalkClickPx;
        if (!_blossomZoomedOutAgain && _settings.Dungeon.ZoomOutNotches > 0
            && _controls.ZoomOut(frame.Width / 2, (int)(frame.Height * 0.40), _settings.Dungeon.ZoomOutNotches))
        {
            _blossomZoomedOutAgain = true;
            Log?.Invoke(LogLevel.Info, $"Blocked at {coords} - zoomed out again.");
        }

        var (x, y, how) = _blossomStalls switch
        {
            2 => (-uy, ux, "sidestepping right"),
            3 => (uy, -ux, "sidestepping left"),
            _ => (-ux, -uy, "stepping back"),
        };
        Log?.Invoke(LogLevel.Info, $"Blocked at {coords} ({_blossomStalls} still steps towards waypoint {_blossomWaypoint + 1}) - {how}.");
        return (x, y);
    }

    /// <summary>The view's world direction (screen-up turned <paramref name="angle"/> from world north); the heading fix restarts there.</summary>
    private void SetBlossomHeading(double angle, string how)
    {
        _headingBase = angle;
        _headingError = angle;
        _headingSamples = 1;
        _blossomHeadingKnown = true;
        _blossomHeadingMisses = 0;
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
        _blossomWaypoint = BlossomRoute.Next(_blossomWaypoint, _blossomRoute.Count, _settings.Blossom.LoopFrom);
        var next = _blossomRoute[_blossomWaypoint];
        Log?.Invoke(LogLevel.Info, $"{what} - on to waypoint {_blossomWaypoint + 1} ({next.X},{next.Y}).");
        _blossomPauseSince = 0;
        _blossomStalls = 0;
        _blossomWaypointSteps = 0;
        _blossomBestDistance = double.MaxValue;
        _blossomZoomedOutAgain = false;
        _simpleClick = null;
        _lastCoords = null;
        _blossomDir = null;
        _waitUntil = Environment.TickCount64;

        // The camera turn the user set for the waypoint just left (WaypointTurns), done by the walk one tap per tick.
        if (_blossomTurns.TryGetValue(left, out var turn))
        {
            _blossomCamKey = turn.Direction;
            _blossomCamTaps = turn.Taps;
            string key = turn.Direction == TurnDirection.Left ? _settings.Dungeon.RotateLeftKey : _settings.Input.RotateKey;
            Log?.Invoke(LogLevel.Info, $"Leaving waypoint {left}: turning the camera {(turn.Direction == TurnDirection.Left ? "left" : "right")} ('{key}' x{turn.Taps}).");
        }
    }

    /// <summary>
    /// The skill on the Jaso (as LOD: tapped every Lod.SkillEveryMs, or held with Lod.HoldSkill; Ctrl + skill with
    /// Dungeon.CtrlSkill) until the target is gone TargetGoneMs = dead -> loot. A coordinate jump = died; a target that never
    /// goes is given up after FightMaxMs (one out of reach would hold the character still).
    /// </summary>
    private async Task TickBlossomAttackAsync(Mat frame)
    {
        var b = _settings.Blossom;
        var lod = _settings.Lod;
        string skill = _settings.Dungeon.SkillKey;
        long now = Environment.TickCount64;

        if (!string.IsNullOrWhiteSpace(skill))
        {
            if (lod.HoldSkill)
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
                if (_blossomAttackCoords is (int ax, int ay) && coords.DistanceTo(ax, ay) > lod.TeleportJump)
                {
                    ReleaseHeldSkill();
                    Log?.Invoke(LogLevel.Warning, $"Moved to {coords} (from {ax},{ay}) during the fight - killed? Lining up again.");
                    BlossomShot(frame, "blossom_died");
                    _lastCoords = null;
                    Advance(PilotPhase.BlossomRespawn, lod.RespawnSettleMs);
                    return;
                }

                _blossomAttackCoords = (coords.X, coords.Y);
            }
        }

        if (now - _blossomTargetSeenAt > b.TargetGoneMs)
        {
            ReleaseHeldSkill();
            _blossomKills++;
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

        _detail = $"attacking '{_blossomTarget}' (seen {(now - _blossomTargetSeenAt) / 1000.0:0.0}s ago, fight {(now - _blossomFightStart) / 1000}s)";
        _waitUntil = now + Math.Min(lod.SkillEveryMs, 300);
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
            Log?.Invoke(LogLevel.Info, $"Looted for {b.LootMs / 1000.0:0.#}s - on to waypoint {_blossomWaypoint + 1}.");
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

        BeginBlossomArrival();
    }

    /// <summary>
    /// Start (or a restart) inside Blossom Garden: a Jaso already targeted -> fight it first (the heading is unknown, so the
    /// turn follows); else the arrival (buffs, the turn).
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

        if (await FindBlossomTargetAsync(frame) is { } target)
        {
            _blossomHeadingUnknown = true;
            Log?.Invoke(LogLevel.Warning, $"Already in {Destination} with '{target}' targeted - fighting it, then lining up on the pillars.");
            BeginBlossomFight(target);
            return;
        }

        Log?.Invoke(LogLevel.Info, $"Already in {Destination} - lining up on the pillars, then walking from the nearest waypoint.");
        BeginBlossomArrival();
    }
}

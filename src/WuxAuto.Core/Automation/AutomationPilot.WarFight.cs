using System.Globalization;
using Emgu.CV;
using WuxAuto.Core;
using WuxAuto.Vision;
using LogLevel = WuxAuto.Core.LogLevel;

namespace WuxAuto.Automation;

/// <summary>
/// Auto War inside Bohai Outpost (design §4.4-§4.7): the zone after the war dialog's OK, TAB target "Enemy worth CP" (the
/// Settings visit), the buffs - the buff key with an autobuff scroll, else the buff and weapon buff icons one by one;
/// then the item buffs on the skill bar either way, every icon only when ticked on (<see cref="WarBuffClicks"/>) - then the fight
/// cycle: Tab until a target (or "Gate is Open"), the X buff, Shura held (when its key is set), the skill until the
/// target is gone. The HP
/// numbers are watched all the while; after a death it waits for the revive, rebuffs and fights on. The game sending the
/// character to town ends the war: the trip back to the cave. Reads and keys only with the game in front - alts often
/// run beside a second game client, whose screen would otherwise be read - and the waits stand still meanwhile.
/// </summary>
public sealed partial class AutomationPilot
{
    // One stay in Bohai (BeginWarEnter).
    private int _warEnterStep;             // WarEnter: 0 wait for the zone, 1 close the war windows, then the Settings visit
    private long _warEnterSince;           // WarEnter: war-clock time the wait for the zone began
    private bool _warStarted;              // a target or "Gate is Open" was seen in this war
    private bool? _warHasAutoBuff;         // the character has an autobuff scroll; null = not known
    private bool _warBuffKeyWarned;        // autobuff without a buff key, warned this war
    private readonly HashSet<string> _warMissingIcons = new(StringComparer.OrdinalIgnoreCase);   // buff icons warned about this Start (Start clears it)
    private bool _warShuraSkipTold;        // no Shura key: the skipped hold was said this Start (Start clears it)
    private long _warInsideZoneAt;         // next minimap title read (WarZoneCheckAsync)
    private int _warOtherZoneReads;        // reads of another zone since the last Bohai read
    private bool _warZoneHeld;             // the last title read was another zone: the phase holds

    // Per Start (Start clears it): what the HP reads say - the maximum seen alive and the reads in a row (WarHpNext).
    private WarHpWatch _warHp;

    // WarBuff.
    private int _warBuffStep;              // 0 autobuff known?, 1 ask the autohunt settings, 2 close them, 3 buff
    private IReadOnlyList<string>? _warBuffRound;   // step 3: the icons this round clicks (WarBuffClicks); null = the round has not begun
    private bool _warBuffByKey;            // step 3: this round pressed the buff key (autobuff)
    private int _warBuffIcon;              // step 3: the next of _warBuffRound
    private int _warBuffClicks;            // step 3: icons clicked this round
    private int _warHuntSettingsPresses;   // step 1: presses of the autohunt settings key
    private int _warHuntSettingsCloses;    // step 2: presses to close them
    private long _warHuntSettingsAt;       // steps 1 / 2: the last press
    private long _warBuffedAt;             // the last buff (TickCount64: buffs wear off in game time, in front or not)
    private long? _warBuffsDroppedAt;      // the HP maximum fell (buffs gone) since the last buff: rebuff wanted from then

    // WarFight.
    private int _warFightStep;             // 0 Tab, 1 a target?, 2 Shura down, 3 Shura up, 4 attack, 5 walk toward a player
    private long _warStepAt;               // step 3: the Shura key goes up; step 4: the next skill press
    private long _warTargetReadAt;         // step 4: the next target read
    private long _warTargetSeenAt;         // war clock: the target was last seen
    private string _warTarget = string.Empty;   // the target, for the log
    private int _warTabPolls;              // step 1 reads without a target (the gate text is read every 2nd)

    // WarFight: the walk out of the respawn point (step 5), one "no target" spell at a time.
    private int _warNoTargetTabs;          // Tabs in a row without a target
    private int _warWalkSteps;             // walk steps in this spell
    private long _warWalkRestUntil;        // war clock: Tab only until then (after WarWalkMaxSteps steps)
    private bool _warNoPlayersTold;        // "no players in view" said this spell
    private bool _warTitleUnreadTold;      // "the minimap does not read" said this spell
    private bool _warTitleIsWarMap;        // the last minimap title read was the war map (WarZoneCheckAsync): a walk may click

    // The HP watch (WarFight, WarDead).
    private long _warHpReadAt;             // next HP read (TickCount64)
    private long _warHpUnreadSince;        // war clock: HP unread since (0 = it reads)
    private bool _warHpUnreadWarned;
    private long _warDeadSince;            // WarDead: war clock at the death
    private bool _warDeadWarned;

    // Time with the game in front: the waits inside Bohai stand still while it is behind another window.
    private readonly FrontClock _warClock = new();

    /// <summary>HP unread this long (war clock) = a warning and <c>war_hp_unread</c>; the fight carries on as alive.</summary>
    internal const int WarHpUnreadMs = 10000;

    /// <summary>
    /// The least time after any buff (the buff key, an icon click, the fight's X buff) before the next input: the buff's
    /// animation must finish first (user, 2026-10-28: "allow at least 0.8s for buff animation before going next").
    /// </summary>
    internal const int WarBuffAnimationMs = 800;

    /// <summary>The wait after a buff in a buff round: <see cref="WarSettings.BuffClickGapMs"/>, never under <see cref="WarBuffAnimationMs"/>.</summary>
    internal static int WarBuffWaitMs(int buffClickGapMs) => Math.Max(buffClickGapMs, WarBuffAnimationMs);

    /// <summary>HP reads in a row behind every verdict (dead, back on our feet, buffs gone): one alone may be a misread.</summary>
    internal const int WarHpReadsInARow = 2;

    /// <summary>A rebuff this overdue is not left waiting for the Tab point: it interrupts the attack (step 4).</summary>
    internal const int WarRebuffOverdueMs = 30000;

    /// <summary>
    /// Presses of the autohunt settings key while nothing reads: right after the Settings visit an unreadable open window
    /// is likelier than an eaten press, and an even count of the toggle leaves it as it was.
    /// </summary>
    private const int WarHuntSettingsPresses = 2;

    /// <summary>
    /// A clock that only runs while the game is in front: behind another window nothing is read and nothing is sent, so
    /// that time must not count towards "the target is gone", "dead for 30 s" or the wait for the war dialog. It stops and
    /// goes on; it never jumps.
    /// </summary>
    internal sealed class FrontClock
    {
        private long _hidden;          // ms spent behind other windows so far
        private long? _hiddenSince;    // the game went behind another window then; null = in front

        public bool IsHidden => _hiddenSince != null;

        /// <summary>The time on this clock at TickCount64 <paramref name="now"/>.</summary>
        public long Now(long now) => (_hiddenSince ?? now) - _hidden;

        /// <summary>The game went behind another window (already behind: no change).</summary>
        public void Hide(long now) => _hiddenSince ??= now;

        /// <summary>The game is in front: the clock goes on from where it stopped. Returns how long it stood still.</summary>
        public long Show(long now)
        {
            if (_hiddenSince is not long since)
                return 0;

            long stood = Math.Max(0, now - since);
            _hidden += stood;
            _hiddenSince = null;
            return stood;
        }
    }

    /// <summary>What the item status list says about an autobuff scroll.</summary>
    internal enum WarItemList
    {
        AutoBuff,
        NoAutoBuff,
        Unread,
    }

    /// <summary>
    /// The item status list's OCR lines (one or more reads): an "[Auto buff scroll]" line = autobuff; three or more other
    /// lines (letters and digits, the same line read twice counted once) = the list shows, without the scroll; else unread.
    /// </summary>
    internal static WarItemList AutoBuffInItemList(IReadOnlyCollection<string> lines)
    {
        if (lines.Any(WarText.IsAutoBuffScrollLine))
            return WarItemList.AutoBuff;

        int shown = lines.Select(TextMatcher.Normalize).Where(l => l.Length >= 3).Distinct().Count();
        return shown >= 3 ? WarItemList.NoAutoBuff : WarItemList.Unread;
    }

    /// <summary>
    /// The icons one buff round clicks on the skill bar, in order (user, 2026-10-28). With <paramref name="byBuffKey"/> -
    /// the autobuff key pressed - only the item buffs (<see cref="WarSettings.ItemBuffIcons"/>: the key buffs the skills,
    /// it uses no items, and the user wants the items clicked with autobuff too); otherwise the buffs
    /// (<see cref="WarSettings.BuffIcons"/>), the weapon buffs (<see cref="WarSettings.WeaponBuffIcons"/>, skill buffs too),
    /// then the item buffs. Only the ones ticked on (<see cref="WarSettings.IsBuffOn"/>), blanks left out, each once (an icon
    /// in two lists is clicked once; with autobuff, as an item).
    /// </summary>
    internal static IReadOnlyList<string> WarBuffClicks(WarSettings war, bool byBuffKey)
        => (byBuffKey ? Enumerable.Empty<string>() : war.BuffIcons.Concat(war.WeaponBuffIcons)).Concat(war.ItemBuffIcons)
            .Where(i => !string.IsNullOrWhiteSpace(i) && war.IsBuffOn(i))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// When a rebuff is wanted: <paramref name="buffEveryMs"/> after the last buff, or from the moment the buffs were seen
    /// gone (<paramref name="droppedAt"/>), whichever comes first.
    /// </summary>
    internal static long WarRebuffWantedAt(long buffedAt, int buffEveryMs, long? droppedAt)
        => Math.Min(buffedAt + buffEveryMs, droppedAt ?? long.MaxValue);

    /// <summary>
    /// Rebuff now: at the fight cycle's Tab (step 0) once it is wanted; in the attack (step 4) only once it is
    /// <see cref="WarRebuffOverdueMs"/> overdue - a target that keeps reading (one out of reach) would keep the attack going
    /// and the buffs would never come; never between Tab and the attack (steps 1-3, the Shura hold).
    /// </summary>
    internal static bool WarRebuffDue(long now, long wantedAt, int fightStep)
        => now >= wantedAt && (fightStep == 0 || (fightStep == 4 && now - wantedAt >= WarRebuffOverdueMs));

    /// <summary>
    /// What the HP reads so far say. <c>AliveMax</c>: the player's HP maximum (<see cref="WarLearnAliveMax"/>);
    /// <c>PendingMax</c>: the last read that may set it, waiting for a second; the reads in a row behind each verdict;
    /// whether a fall of the maximum (buffs gone) may be reported - armed by a read at the maximum, spent by a report; and
    /// whether a raise too big to be a buff was reported this Start.
    /// </summary>
    internal readonly record struct WarHpWatch(
        int? AliveMax, int? PendingMax, int DeadReads, int ReviveReads, int LowReads, bool DropArmed, bool RaiseReported);

    /// <summary>
    /// What one HP read decides: nothing, dead, back on our feet (while dead), the buffs gone (alive), or - once per Start - a
    /// raise of the maximum refused as too big for a buff.
    /// </summary>
    internal enum WarHpVerdict
    {
        None,
        Dead,
        Revived,
        BuffsDropped,
        RaiseIgnored,
    }

    /// <summary>
    /// The player's HP maximum after one more read. A read can set it when the character is alive (HP above 0), its maximum
    /// is 1000 or more and - with a maximum known - not below it (buffs raise it; it is never lowered: buffs gone or the MP
    /// line read in place of the HP line both read lower). Two such reads in a row that agree within 1 % set it (the later
    /// one); anything else read in between starts the pair again, an unread does not. A raise to more than 1.5 times the
    /// known maximum is refused (<c>RaiseIgnored</c>): the user's buff raises it about 1.24 times (34493 -> 42833), and the
    /// MP line of a character with a higher MP maximum, read twice in place of the HP line, must not hide every death after.
    /// </summary>
    internal static (int? AliveMax, int? PendingMax, bool RaiseIgnored) WarLearnAliveMax(int? aliveMax, int? pendingMax, (int Cur, int Max) read)
    {
        bool candidate = read.Cur > 0 && read.Max >= 1000 && (aliveMax is not int known || read.Max >= known);
        if (!candidate)
            return (aliveMax, null, false);

        bool agree = pendingMax is int pending && Math.Abs((long)pending - read.Max) * 100 <= Math.Max(pending, read.Max);
        if (!agree)
            return (aliveMax, read.Max, false);
        if (aliveMax is int current && 2L * read.Max > 3L * current)
            return (aliveMax, read.Max, true);
        return (read.Max, read.Max, false);
    }

    /// <summary>
    /// One HP read through the watch. The maximum is learnt first (<see cref="WarLearnAliveMax"/>), then the read is
    /// judged against it - "the player's maximum" is <see cref="WarText.IsPlayerHpMax"/>, half of it to 5 % over:
    /// <list type="bullet">
    /// <item>dead: <see cref="WarText.IsDeadRead"/> <see cref="WarHpReadsInARow"/> times in a row;</item>
    /// <item>back on our feet (only while <paramref name="dead"/>): HP above 0 out of the player's maximum, as many times;</item>
    /// <item>buffs gone (alive): HP above 0 out of the player's maximum but under 9/10 of it, as many times - once per fall,
    /// armed again by a read at 9/10 or more. It also catches a quick respawn that was never seen as a death.</item>
    /// </list>
    /// <c>Told</c>: the read said something about the player's HP. No read, or - with the maximum known - a read of another
    /// "N / M" line (outside the player's maximum: the MP line "8000/11467", the "0/3000" bar) tells nothing: it neither
    /// counts nor breaks a row, and it runs the HP-unread time (<see cref="WarHpUnreadSince"/>). A death read that
    /// alternates with the MP line is still two death reads in a row.
    /// </summary>
    internal static (WarHpWatch Watch, WarHpVerdict Verdict, bool Told) WarHpNext(WarHpWatch watch, (int Cur, int Max)? hp, bool dead)
    {
        if (hp is not { } read)
            return (watch, WarHpVerdict.None, false);

        var (aliveMax, pendingMax, raiseIgnored) = WarLearnAliveMax(watch.AliveMax, watch.PendingMax, read);
        if (aliveMax != null && !WarText.IsPlayerHpMax(read.Max, aliveMax))
        {
            bool report = raiseIgnored && !watch.RaiseReported;
            var learnt = watch with { AliveMax = aliveMax, PendingMax = pendingMax, RaiseReported = watch.RaiseReported || raiseIgnored };
            return (learnt, report ? WarHpVerdict.RaiseIgnored : WarHpVerdict.None, false);
        }

        bool alive = read.Cur > 0 && WarText.IsPlayerHpMax(read.Max, aliveMax);
        bool low = alive && aliveMax is int max && 10L * read.Max < 9L * max;
        var next = watch with
        {
            AliveMax = aliveMax,
            PendingMax = pendingMax,
            DeadReads = WarText.IsDeadRead(read, aliveMax) ? watch.DeadReads + 1 : 0,
            ReviveReads = alive ? watch.ReviveReads + 1 : 0,
            LowReads = low ? watch.LowReads + 1 : 0,
            DropArmed = watch.DropArmed || (alive && aliveMax != null && !low),
        };

        if (dead)
            return (next, next.ReviveReads >= WarHpReadsInARow ? WarHpVerdict.Revived : WarHpVerdict.None, true);
        if (next.DeadReads >= WarHpReadsInARow)
            return (next, WarHpVerdict.Dead, true);
        if (next.LowReads >= WarHpReadsInARow && next.DropArmed)
            return (next with { DropArmed = false }, WarHpVerdict.BuffsDropped, true);
        return (next, WarHpVerdict.None, true);
    }

    /// <summary>A key Auto War presses: the setting's name (as the settings panel shows it), its value, and whether it must be set.</summary>
    internal readonly record struct WarKey(string Setting, string Key, bool Required);

    /// <summary>
    /// What is wrong with the keys, null when nothing: a required key left empty, or a key name the platform cannot press
    /// (<paramref name="isKnown"/>) - such a key is refused on every press, which reads like the game being behind another
    /// window, so an unmappable Shura key would mean no attack in the whole war.
    /// </summary>
    internal static string? WarKeyProblem(IEnumerable<WarKey> keys, Func<string, bool> isKnown)
    {
        foreach (var (setting, key, required) in keys)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                if (required)
                    return $"No key is set for '{setting}' - Auto War presses it; set it and press Start again";
                continue;
            }

            if (!isKnown(key))
                return $"'{key}' ('{setting}') is not a key the pilot can press - fix it and press Start again";
        }

        return null;
    }

    // ---------------------------------------------------------------- the walk out of the respawn point (pure parts)

    /// <summary>Tabs in a row without a target before the fight walks toward a player (user, 2026-10-28; about 5 s).</summary>
    internal const int WarWalkAfterTabs = 5;

    /// <summary>Walk steps in one "no target" spell; then Tab only for <see cref="WarWalkRestMs"/>.</summary>
    internal const int WarWalkMaxSteps = 6;

    /// <summary>Tab only this long (war clock) after <see cref="WarWalkMaxSteps"/> walk steps, then a new spell.</summary>
    internal const int WarWalkRestMs = 20000;

    /// <summary>The wait after a walk click before the next Tab.</summary>
    internal const int WarWalkWaitMs = 1500;

    /// <summary>
    /// A name tag this close to the character's spot (x height) is our own: on the saved frames our "[Premium]" / name tags
    /// sit up to 0.176 h away (1024x768 and 2048x1152; the brief's 0.12 h let them through as other players).
    /// </summary>
    internal const double WarOwnTagRadius = 0.22;

    /// <summary>
    /// Walk now: <see cref="WarWalkAfterTabs"/> or more Tabs in a row found no target, fewer than
    /// <see cref="WarWalkMaxSteps"/> steps in this spell, and not resting (<paramref name="restUntil"/>, war clock).
    /// </summary>
    internal static bool WarWalkDue(int noTargetTabs, int walkSteps, long clockNow, long restUntil)
        => noTargetTabs >= WarWalkAfterTabs && walkSteps < WarWalkMaxSteps && clockNow >= restUntil;

    /// <summary>
    /// The UI bands no name tag is looked for in: the top 13 % (the target box and the top bar), the minimap, the combat
    /// log, the skill bar, the item list - and the column between the minimap and the item list, where the server's event
    /// notices sit right-aligned ("Pet Exp x 2%", "Mount Exp x 0.050": below Regions.Minimap on a saved 1024x768 frame).
    /// </summary>
    internal static IReadOnlyList<PixelRect> WarWorldBands(RegionSettings regions, int width, int height)
    {
        var minimap = regions.Minimap.ToPixels(width, height);
        var items = regions.ItemList.ToPixels(width, height);
        var bands = new List<PixelRect>
        {
            new(0, 0, width, (int)(0.13 * height)),
            minimap,
            regions.AttackMessage.ToPixels(width, height),
            regions.Hotbar.ToPixels(width, height),
            items,
        };
        if (items.Y > minimap.Bottom)
            bands.Add(new PixelRect(minimap.X, minimap.Bottom, width - minimap.X, items.Y - minimap.Bottom));
        return bands;
    }

    /// <summary>The character's spot on the screen (0.5 w, 0.54 h): the LOD ring check's spot.</summary>
    private static (double X, double Y) WarCharacterSpot(int width, int height) => (0.5 * width, 0.54 * height);

    /// <summary>
    /// The name tag to walk toward, from whole-screen OCR lines: a tag (<see cref="WarText.IsPlayerTagText"/>) whose centre
    /// lies in none of <paramref name="bands"/> and more than <see cref="WarOwnTagRadius"/> x height from the character's spot
    /// (closer is our own name tag); the nearest to the spot. Null when none.
    /// </summary>
    internal static (string Text, PixelRect Bounds)? WarWalkGoal(IReadOnlyList<(string Text, PixelRect Bounds)> lines, int width, int height,
        IReadOnlyList<PixelRect> bands)
    {
        var (spotX, spotY) = WarCharacterSpot(width, height);
        double own = WarOwnTagRadius * height;
        (string Text, PixelRect Bounds)? best = null;
        double bestAway = double.MaxValue;
        foreach (var (text, bounds) in lines)
        {
            if (!WarText.IsPlayerTagText(text))
                continue;

            int cx = bounds.CenterX, cy = bounds.CenterY;
            if (bands.Any(b => cx >= b.X && cx < b.Right && cy >= b.Y && cy < b.Bottom))
                continue;

            double away = Math.Sqrt(Math.Pow(cx - spotX, 2) + Math.Pow(cy - spotY, 2));
            if (away <= own || away >= bestAway)
                continue;

            best = (text.Trim(), bounds);
            bestAway = away;
        }

        return best;
    }

    /// <summary>
    /// Where a walk step Ctrl+clicks for a name tag: the floor 4 % of the height below the tag's centre, pulled toward the
    /// character's spot to at most 0.25 x height from it, then kept inside the walk-click area the LOD walk uses
    /// (0.25-0.74 w, 0.25-0.78 h: clear of the chat box, the panels and the skill bar).
    /// </summary>
    internal static (int X, int Y) WarWalkClick(PixelRect tag, int width, int height)
    {
        var (spotX, spotY) = WarCharacterSpot(width, height);
        double dx = tag.CenterX - spotX;
        double dy = tag.CenterY + Math.Round(0.04 * height) - spotY;
        double length = Math.Sqrt(dx * dx + dy * dy);
        double reach = 0.25 * height;
        if (length > reach)
        {
            dx *= reach / length;
            dy *= reach / length;
        }

        int x = (int)Math.Round(Math.Clamp(spotX + dx, width * 0.25, width * RunClickRight));
        int y = (int)Math.Round(Math.Clamp(spotY + dy, height * RunClickTop, height * 0.78));
        return (x, y);
    }

    /// <summary>The target counts as gone after <paramref name="targetGoneMs"/> without a sight, on the war clock.</summary>
    internal static bool WarTargetGone(long clockNow, long seenAt, int targetGoneMs) => clockNow - seenAt >= targetGoneMs;

    /// <summary>
    /// Since when (war clock) no read has told anything about the player's HP, after one more read (<paramref name="told"/>,
    /// from <see cref="WarHpNext"/>): 0 again after one that told, else the start of the spell.
    /// </summary>
    internal static long WarHpUnreadSince(long unreadSince, bool told, long clockNow)
        => told ? 0 : unreadSince == 0 ? clockNow : unreadSince;

    /// <summary>The HP has not read for <see cref="WarHpUnreadMs"/> on the war clock (<paramref name="unreadSince"/> 0 = it reads).</summary>
    internal static bool WarHpUnreadTooLong(long clockNow, long unreadSince)
        => unreadSince > 0 && clockNow - unreadSince >= WarHpUnreadMs;

    // ---------------------------------------------------------------- in front, in Bohai

    /// <summary>
    /// Inside the war - and in the war steps outside it that read the screen (the timer read, the join, the autohunt at the
    /// monster) - nothing is read or sent with the game behind another window: a held key is let go, the war clock stands
    /// still, the tick waits. True when the game is in front.
    /// </summary>
    private bool WarInFront(long now)
    {
        if (_input.IsTargetForeground)
        {
            long stood = _warClock.Show(now);
            if (stood > 0)
                Log?.Invoke(LogLevel.Info, $"The game is back in front after {stood / 1000.0:0.#}s - {_phase} goes on.");
            return true;
        }

        if (!_warClock.IsHidden)
        {
            ReleaseHeldSkill();
            Log?.Invoke(LogLevel.Info, $"The game is behind another window - {_phase} holds (no reads, no keys) until it is back in front.");
        }

        _warClock.Hide(now);
        _detail = "waiting for the game window to be in front";
        _waitUntil = now + 500;
        return false;
    }

    /// <summary>
    /// Where the character is (design §4.7), the minimap title every 2 s, game in front only (<see cref="WarInFront"/>):
    /// Bohai or no title read - the phase goes on; a home town - the war is over, the trip back; the saved cave (the game
    /// took the character back by itself) - on from where the trip back confirms the cave; another zone - the phase holds
    /// until the next read, and 3 such reads without a Bohai read between them are <c>wrong_zone</c> and the trip back. True
    /// when the phase may act this tick.
    /// </summary>
    private async Task<bool> WarZoneCheckAsync(Mat frame)
    {
        long now = Environment.TickCount64;
        var war = _settings.War;
        if (!WarInFront(now))
            return false;

        string? title = null;
        bool read = now >= _warInsideZoneAt;
        if (read)
        {
            _warInsideZoneAt = now + 2000;
            title = await ReadMinimapTitleAsync(frame);
        }

        if (read)
            _warTitleIsWarMap = title != null && ZoneMatches(title, war.Destination);

        if (title == null)
        {
            // No title read goes on (a loading screen, the panel covered); no read this tick: the last one decides.
            if (read)
                _warZoneHeld = false;
            if (!_warZoneHeld)
                return true;

            _waitUntil = now + 500;
            return false;
        }

        if (ZoneMatches(title, war.Destination))
        {
            _warOtherZoneReads = 0;
            _warZoneHeld = false;
            return true;
        }

        if (TownIn(title) != null)
        {
            BeginWarReturn(frame, "the war is over");
            return false;
        }

        if (!_warRemembering && HasWarSpot && WarText.SameZone(title, war.HomeMap))
        {
            Log?.Invoke(LogLevel.Info, $"The minimap reads '{title}' - the game took the character back to {war.HomeMap}; the war is over.");
            await WarBackInCaveAsync(frame, now);
            return false;
        }

        if (++_warOtherZoneReads < 3)
        {
            _warZoneHeld = true;
            _detail = $"the minimap reads '{title}', not {war.Destination} - holding ({_warOtherZoneReads}/3)";
            _waitUntil = now + 500;
            return false;
        }

        Log?.Invoke(LogLevel.Warning, $"The minimap reads '{title}' 3 times - neither {war.Destination} nor the cave.");
        WarShot(frame, "wrong_zone");
        BeginWarReturn(frame, $"in '{title}' instead of {war.Destination}");
        return false;
    }

    // ---------------------------------------------------------------- enter

    /// <summary>
    /// Into the war (<see cref="PilotPhase.WarEnter"/>): after OK on the war dialog, or found in Bohai already. A fresh stay -
    /// the war not started, autobuff not known, the trips back since the last war counted from zero again
    /// (<see cref="WarReturnAllowed"/>); the HP maximum seen alive is kept for this Start.
    /// </summary>
    private void BeginWarEnter(string why)
    {
        long now = Environment.TickCount64;
        var war = _settings.War;
        _warReturnTrips.Clear();
        _warStarted = false;
        _warHasAutoBuff = null;
        _warBuffKeyWarned = false;
        _warBuffStep = 0;
        _warBuffRound = null;
        _warBuffIcon = 0;
        _warBuffClicks = 0;
        _warHuntSettingsPresses = 0;
        _warHuntSettingsCloses = 0;
        _warHuntSettingsAt = 0;
        _warEnterStep = 0;
        if (_input.IsTargetForeground)
            _warClock.Show(now);    // a hide left over from an earlier stay (stopped behind another window) is not this one's
        _warEnterSince = _warClock.Now(now);
        _warInsideZoneAt = 0;
        _warOtherZoneReads = 0;
        _warZoneHeld = false;
        _warTitleIsWarMap = false;
        Log?.Invoke(LogLevel.Info, $"Entering {war.Destination} ({why}): TAB target '{war.TabTargetOption}', buffs, then the fight.");
        Advance(PilotPhase.WarEnter, 500);
    }

    /// <summary>
    /// Step 0: <see cref="WarSettings.Destination"/> on the minimap (60 s on the war clock, else Fail - the restart's dispatch
    /// sorts it out). Step 1: the MENU / TRIBE STATUS a join in the middle of a timer read left open, closed with the war
    /// timer's close step. Then one HP read (the alive maximum before the fight) and the Settings visit for the TAB target,
    /// which goes on to the buffs.
    /// </summary>
    private async Task TickWarEnterAsync(Mat frame)
    {
        long now = Environment.TickCount64;
        var war = _settings.War;
        if (!WarInFront(now))
            return;

        if (_warEnterStep == 0)
        {
            string? title = await ReadMinimapTitleAsync(frame);
            if (ZoneMatches(title, war.Destination))
            {
                Log?.Invoke(LogLevel.Success, $"In {war.Destination} ('{title}').");
                _warEnterStep = 1;
                _warInsideZoneAt = now + 2000;
                _waitUntil = now + 300;
                return;
            }

            long waited = _warClock.Now(now) - _warEnterSince;
            if (waited >= 60000)
            {
                Fail(frame, $"'{war.Destination}' never showed on the minimap after joining the war ({waited / 1000}s; it reads '{title}').");
                return;
            }

            _detail = $"waiting for '{war.Destination}' on the minimap ({waited / 1000}s of 60s)";
            _waitUntil = now + 500;
            return;
        }

        if (!await WarZoneCheckAsync(frame))
            return;

        if (_warTimerStep != 4)
            BeginWarClose();
        await TickWarCloseAsync(frame);
        if (_warTimerStep == 4)
            return;     // clicked an X (or the click waits for the game): look again on a fresh frame

        var (_, hp, _) = await ReadWarHpAsync(frame);
        Log?.Invoke(LogLevel.Info, hp is { } read
            ? $"HP {read.Cur} / {read.Max}; setting TAB target to '{war.TabTargetOption}'."
            : $"HP not read; setting TAB target to '{war.TabTargetOption}'.");
        BeginSettingsVisit(war.TabTargetOption, skipFont: true, next: PilotPhase.WarBuff);
    }

    // ---------------------------------------------------------------- buffs

    /// <summary>
    /// The buffs (design §4.4), one step per tick, each on its own frame: 0 autobuff known? - the item list; 1 ask the
    /// autohunt settings (S) for the AUTO BUFF dialog; 2 close them; 3 buff. Then the fight.
    /// </summary>
    private async Task TickWarBuffAsync(Mat frame)
    {
        if (!await WarZoneCheckAsync(frame))
            return;

        switch (_warBuffStep)
        {
            case 0:
                await TickWarItemListAsync(frame);
                return;
            case 1:
                await TickWarAskAutoBuffAsync(frame);
                return;
            case 2:
                await TickWarCloseHuntSettingsAsync(frame);
                return;
            default:
                await TickWarBuffUpAsync(frame);
                return;
        }
    }

    /// <summary>
    /// Step 0: known already - buff; else the item status list (OCR x1.5, then x2): an "[Auto buff scroll]" line - autobuff;
    /// otherwise the autohunt settings are asked (shown without the scroll or unread alike - the list may be folded away).
    /// </summary>
    private async Task TickWarItemListAsync(Mat frame)
    {
        long now = Environment.TickCount64;
        if (_warHasAutoBuff != null)
        {
            _warBuffStep = 3;
            return;
        }

        var roi = _settings.Regions.ItemList.ToPixels(frame.Width, frame.Height);
        var lines = new List<string>();
        foreach (double scale in new[] { 1.5, 2.0 })
        {
            lines.AddRange(ScreenAnalyzer.ToLines(await _analyzer.Ocr.RecognizeAsync(frame, roi, scale)).Select(l => l.Text));
            if (lines.Any(WarText.IsAutoBuffScrollLine))
                break;
        }

        string key = _settings.Sequence.AutohuntSettingsKey;
        switch (AutoBuffInItemList(lines))
        {
            case WarItemList.AutoBuff:
                _warHasAutoBuff = true;
                Log?.Invoke(LogLevel.Info, $"The item list shows '{lines.First(WarText.IsAutoBuffScrollLine)}' - autobuff, buffing with '{_settings.Lod.BuffKey}'.");
                _warBuffStep = 3;
                break;
            case WarItemList.NoAutoBuff:
                Log?.Invoke(LogLevel.Info, $"The item list shows no '[Auto buff scroll]' - asking the autohunt settings ('{key}').");
                _warBuffStep = 1;
                break;
            default:
                Log?.Invoke(LogLevel.Info, $"The item list did not read - asking the autohunt settings ('{key}') whether there is autobuff.");
                _warBuffStep = 1;
                break;
        }

        _waitUntil = now + 200;
    }

    /// <summary>
    /// The autohunt settings window on this frame, read the way the keys-out Regular War step reads it (whole client, OCR
    /// x2, x1 when that reads almost nothing): the AUTO BUFF dialog's title (null when it is not up) and whether the
    /// "Regular War" box's label shows (the settings window itself).
    /// </summary>
    private async Task<(PixelRect? AutoBuffTitle, bool RegularWar)> ReadHuntSettingsAsync(Mat frame)
    {
        var full = new PixelRect(0, 0, frame.Width, frame.Height);
        var words = await _analyzer.Ocr.RecognizeAsync(frame, full, 2.0);
        if (words.Count < 3)
            words = await _analyzer.Ocr.RecognizeAsync(frame, full, 1.0);

        var lines = ScreenAnalyzer.ToLines(words);
        var title = lines.Where(l => WarText.IsAutoBuffTitle(l.Text)).Select(l => (PixelRect?)l.Bounds).FirstOrDefault();
        return (title, lines.Any(l => WarText.IsRegularWarLine(l.Text)));
    }

    /// <summary>
    /// Step 1: the autohunt settings key (S, at most <see cref="WarHuntSettingsPresses"/> presses, 2.5 s apart): the AUTO
    /// BUFF dialog shows - autobuff; its X (a bright blob right of the title, never a guessed point) is clicked, or with no X
    /// the settings are closed with S alone. The settings without the dialog - no autobuff. Nothing read after the last
    /// press - no autobuff, a warning and <c>war_autobuff_unread</c>. A dry run presses nothing and buffs by the icons.
    /// </summary>
    private async Task TickWarAskAutoBuffAsync(Mat frame)
    {
        long now = Environment.TickCount64;
        string key = _settings.Sequence.AutohuntSettingsKey;
        if (_input.IsDryRun || string.IsNullOrWhiteSpace(key))
        {
            Log?.Invoke(_input.IsDryRun ? LogLevel.Info : LogLevel.Warning, _input.IsDryRun
                ? $"Dry run - not opening the autohunt settings ('{key}'); buffing by the icons."
                : "No autohunt settings key is set - cannot tell whether the character has autobuff; buffing by the icons.");
            _warHasAutoBuff = false;
            _warBuffStep = 3;
            return;
        }

        var (title, regular) = await ReadHuntSettingsAsync(frame);
        if (title is { } autoBuff)
        {
            _warHasAutoBuff = true;
            _overlays.Add(new OverlayItem(autoBuff, "AUTO BUFF", OverlayColor.Blue));
            var close = _analyzer.FindCloseBlobRightOf(frame, autoBuff);
            if (close is { } x)
            {
                _overlays.Add(new OverlayItem(x, "close", OverlayColor.Green));
                if (!WarClick(x, "the X of the AUTO BUFF dialog"))
                    return;     // the game is not in front: read again

                Log?.Invoke(LogLevel.Info, $"The AUTO BUFF dialog is up - autobuff, buffing with '{_settings.Lod.BuffKey}'.");
            }
            else
            {
                Log?.Invoke(LogLevel.Warning, $"The AUTO BUFF dialog is up (autobuff), but no close button shows right of its title - not clicking; closing the autohunt settings with '{key}'.");
                WarShot(frame, "war_autobuff_open");
            }

            BeginCloseHuntSettings(now, 900);
            return;
        }

        if (regular)
        {
            _warHasAutoBuff = false;
            Log?.Invoke(LogLevel.Info, "The autohunt settings show no AUTO BUFF dialog - no autobuff, buffing by the icons.");
            BeginCloseHuntSettings(now, 0);
            return;
        }

        if (now - _warHuntSettingsAt < 2500)
        {
            _detail = $"waiting for the autohunt settings ('{key}' x{_warHuntSettingsPresses})";
            _waitUntil = now + 500;
            return;
        }

        if (_warHuntSettingsPresses >= WarHuntSettingsPresses)
        {
            _warHasAutoBuff = false;
            Log?.Invoke(LogLevel.Warning, $"Neither the AUTO BUFF dialog nor the autohunt settings read after '{key}' x{_warHuntSettingsPresses} - buffing by the icons.");
            WarShot(frame, "war_autobuff_unread");
            _warBuffStep = 3;
            return;
        }

        if (!_input.KeyTap(key))
        {
            _detail = "autohunt settings key blocked - waiting for the game to be in front";
            _waitUntil = now + 300;
            return;
        }

        _warHuntSettingsPresses++;
        _warHuntSettingsAt = now;
        Log?.Invoke(LogLevel.Info, $"Pressed '{key}' for the autohunt settings - does the AUTO BUFF dialog show? ({_warHuntSettingsPresses}/{WarHuntSettingsPresses})");
        _waitUntil = now + 1200;
    }

    /// <summary>Step 2 next: nothing pressed to close the settings yet.</summary>
    private void BeginCloseHuntSettings(long now, int delayMs)
    {
        _warBuffStep = 2;
        _warHuntSettingsCloses = 0;
        _waitUntil = now + delayMs;
    }

    /// <summary>
    /// Step 2: step 1 has seen the autohunt settings open (the AUTO BUFF dialog or the "Regular War" label), so the first
    /// press of the key goes out without a read - one OCR miss of a label never seen live must not leave the window open.
    /// After it the label decides, on a fresh frame 1.2 s later: still there - one more press (at most 2), then a warning;
    /// gone - the buffs. The AUTO BUFF dialog alone does not get a second press: the key is a toggle, and a press for a
    /// dialog left on its own would open the settings again.
    /// </summary>
    private async Task TickWarCloseHuntSettingsAsync(Mat frame)
    {
        long now = Environment.TickCount64;
        string key = _settings.Sequence.AutohuntSettingsKey;
        if (_warHuntSettingsCloses > 0)
        {
            var (title, regular) = await ReadHuntSettingsAsync(frame);
            if (!regular)
            {
                if (title != null)
                {
                    Log?.Invoke(LogLevel.Warning, $"The AUTO BUFF dialog is still up after '{key}' - leaving it.");
                    WarShot(frame, "war_autohunt_open");
                }
                else
                {
                    Log?.Invoke(LogLevel.Info, "The autohunt settings are closed.");
                }

                _warBuffStep = 3;
                return;
            }

            if (_warHuntSettingsCloses >= 2)
            {
                Log?.Invoke(LogLevel.Warning, $"The autohunt settings are still open after '{key}' x2 - leaving them.");
                WarShot(frame, "war_autohunt_open");
                _warBuffStep = 3;
                return;
            }
        }

        if (now - _warHuntSettingsAt < 1200)
        {
            _waitUntil = _warHuntSettingsAt + 1200;
            return;
        }

        if (!_input.KeyTap(key))
        {
            _detail = "autohunt settings key blocked - waiting for the game to be in front";
            _waitUntil = now + 300;
            return;
        }

        _warHuntSettingsCloses++;
        _warHuntSettingsAt = now;
        Log?.Invoke(LogLevel.Info, $"Pressed '{key}' to close the autohunt settings ({_warHuntSettingsCloses}/2).");
        _waitUntil = now + 1200;
    }

    /// <summary>
    /// Step 3, one round: autobuff - the buff key (<see cref="LodSettings.BuffKey"/>, O) first; then the icons of
    /// <see cref="WarBuffClicks"/> - with the key only the item buffs, otherwise the buffs, the weapon buffs, then the item
    /// buffs, each only when ticked on - one click per tick <see cref="WarSettings.BuffClickGapMs"/> apart, each found on that tick's
    /// frame; one not on the bar is skipped, with a warning once per Start. Then the fight.
    /// </summary>
    private async Task TickWarBuffUpAsync(Mat frame)
    {
        long now = Environment.TickCount64;
        var war = _settings.War;
        string buffKey = _settings.Lod.BuffKey;
        if (_warBuffRound == null)
        {
            bool byKey = _warHasAutoBuff == true && !string.IsNullOrWhiteSpace(buffKey);
            if (_warHasAutoBuff == true && !byKey && !_warBuffKeyWarned)
            {
                _warBuffKeyWarned = true;
                Log?.Invoke(LogLevel.Warning, "Autobuff, but no buff key is set (the 'Buff key (LOD)' setting) - clicking the buff icons instead.");
            }

            if (byKey && !_input.KeyTap(buffKey))
            {
                _detail = "buff key blocked - waiting for the game to be in front";
                _waitUntil = now + 300;
                return;
            }

            // The round is fixed here: a box ticked in the settings panel meanwhile counts from the next round.
            _warBuffRound = WarBuffClicks(war, byKey);
            _warBuffByKey = byKey;
            if (byKey)
            {
                // The key's animation runs before the item buffs - or, with none ticked, before the fight's first Tab.
                _detail = _warBuffRound.Count > 0
                    ? $"buffing - pressed '{buffKey}' (autobuff), then the item buffs"
                    : $"buffing - pressed '{buffKey}' (autobuff)";
                _waitUntil = now + WarBuffWaitMs(war.BuffClickGapMs);
                return;
            }
        }

        var icons = _warBuffRound;
        while (_warBuffIcon < icons.Count)
        {
            string icon = icons[_warBuffIcon];
            var hit = _analyzer.FindSkillBarIcons(frame, [icon], war.IconMatchScore)[0].Match;
            if (hit == null)
            {
                if (_warMissingIcons.Add(icon))
                    Log?.Invoke(LogLevel.Warning, _analyzer.Library.Get(icon) == null
                        ? $"The buff icon '{icon}' cannot be looked for ({NoTemplateText(icon)}) - skipping it (said once per Start)."
                        : $"The buff icon '{icon}' is not on the skill bar - skipping it (said once per Start; untick it in Settings > Auto War > Buffs when this character does not use it).");
                _warBuffIcon++;
                continue;
            }

            _overlays.Add(new OverlayItem(hit.Bounds, $"{icon} {hit.Score:0.00}", OverlayColor.Green));
            if (!WarClick(hit.Bounds, $"the buff icon '{icon}' ({hit.Score:0.00})", LogLevel.Trace))
                return;     // the game is not in front: find it again on the next frame

            _warBuffIcon++;
            _warBuffClicks++;
            _detail = $"buffing - clicked '{icon}' ({_warBuffIcon}/{icons.Count})";
            _waitUntil = now + WarBuffWaitMs(war.BuffClickGapMs);
            return;
        }

        string again = $"again in {Clock(war.BuffEveryMs)}";
        if (_warBuffByKey)
            Log?.Invoke(LogLevel.Success, icons.Count == 0
                ? $"Buffed with '{buffKey}' (autobuff); {again}."
                : $"Buffed with '{buffKey}' (autobuff) and {_warBuffClicks} of {icons.Count} item buff(s) clicked; {again}.");
        else if (icons.Count == 0)
            Log?.Invoke(LogLevel.Info, $"No buff is ticked on (Settings > Auto War > Buffs) - nothing to click; {again}.");
        else
            Log?.Invoke(_warBuffClicks > 0 ? LogLevel.Success : LogLevel.Warning,
                $"Buffed: {_warBuffClicks} of {icons.Count} icon(s) clicked; {again}.");
        await FinishWarBuffAsync(frame, now);
    }

    /// <summary>The buffs are done: the rebuff timer starts again, one HP read (the alive maximum), then the fight from Tab.</summary>
    private async Task FinishWarBuffAsync(Mat frame, long now)
    {
        _warBuffedAt = now;
        _warBuffsDroppedAt = null;
        _warBuffStep = 0;
        _warBuffRound = null;
        _warBuffIcon = 0;
        _warBuffClicks = 0;
        await ReadWarHpAsync(frame);
        BeginWarFight();
    }

    /// <summary>Every buff again (the rebuff timer, a revive); whether the character has autobuff is kept.</summary>
    private void BeginWarRebuff(string why, int delayMs)
    {
        _warBuffStep = 3;
        _warBuffRound = null;
        _warBuffIcon = 0;
        _warBuffClicks = 0;
        Log?.Invoke(LogLevel.Info, $"Rebuffing ({why}).");
        Advance(PilotPhase.WarBuff, delayMs);
    }

    // ---------------------------------------------------------------- fight

    /// <summary>
    /// The fight cycle from Tab, with fresh rows of HP reads (the maximum learnt so far is kept). The buffs-gone check waits
    /// for a read at the maximum: buffs that did not take must not bring a rebuff after every rebuff.
    /// </summary>
    private void BeginWarFight()
    {
        _warFightStep = 0;
        _warTabPolls = 0;
        _warNoTargetTabs = 0;
        _warWalkSteps = 0;
        _warWalkRestUntil = 0;
        _warNoPlayersTold = false;
        _warTitleUnreadTold = false;
        _warHpReadAt = 0;
        _warHp = _warHp with { DeadReads = 0, ReviveReads = 0, LowReads = 0, DropArmed = false };
        _warHpUnreadSince = 0;
        _warHpUnreadWarned = false;
        Advance(PilotPhase.WarFight, 300);
    }

    /// <summary>
    /// The fight (design §4.5), sub-steps of <c>_warFightStep</c>; first on every tick (after a Shura release, which keeps its
    /// time) the zone check, the HP watch (<see cref="WarHpNext"/>: dead, or the buffs gone) and the rebuff
    /// (<see cref="WarRebuffDue"/>: at the Tab point, or 30 s overdue mid-attack).
    /// 0 Tab; 1 a target in the target box (a name, the HP numbers or the chest)
    /// - the X buff, else Tab again 1 s later (before the war has started, "Gate is Open" is looked for on every 2nd read);
    /// 2 the Shura key goes down; 3 it goes up after <see cref="WarSettings.ShuraHoldMs"/>; 4 the skill until the target is
    /// gone for <see cref="WarSettings.TargetGoneMs"/> - then Tab again; 5 after <see cref="WarWalkAfterTabs"/> Tabs without
    /// a target, a walk step toward the nearest player on screen, then Tab again.
    /// </summary>
    private async Task TickWarFightAsync(Mat frame)
    {
        long now = Environment.TickCount64;
        var war = _settings.War;
        if (!WarInFront(now))
        {
            // The Shura hold was let go before its time: the cycle starts again from Tab.
            if (_warFightStep == 3)
                _warFightStep = 0;
            return;
        }

        // The Shura key goes up on time, before this tick's reads.
        if (_warFightStep == 3)
        {
            TickWarShuraUp(now);
            return;
        }

        if (!await WarZoneCheckAsync(frame))
            return;

        if (now >= _warHpReadAt)
        {
            _warHpReadAt = now + 1000;
            var (verdict, hp) = await WatchWarHpAsync(frame, now);
            if (verdict == WarHpVerdict.Dead && hp is { } dead)
            {
                BeginWarDead(frame, dead);
                return;
            }

            if (verdict == WarHpVerdict.BuffsDropped && hp is { } low)
            {
                _warBuffsDroppedAt ??= now;
                Log?.Invoke(LogLevel.Info, $"HP maximum {low.Max} of {_warHp.AliveMax} - the buffs are gone (or a death went unseen); rebuffing at the next Tab.");
            }
        }

        long wanted = WarRebuffWantedAt(_warBuffedAt, war.BuffEveryMs, _warBuffsDroppedAt);
        if (WarRebuffDue(now, wanted, _warFightStep))
        {
            string why = _warBuffsDroppedAt == wanted ? "the buffs are gone" : $"{Clock(now - _warBuffedAt)} since the last buff";
            if (_warFightStep == 4)
                why += $", {Clock(now - wanted)} overdue - '{_warTarget}' still in the target box";
            BeginWarRebuff(why, 300);   // Advance lets go of a held key
            return;
        }

        switch (_warFightStep)
        {
            case 0:
                TickWarTab(now);
                return;
            case 1:
                await TickWarFindTargetAsync(frame, now);
                return;
            case 2:
                TickWarShuraDown(now);
                return;
            case 5:
                await TickWarWalkAsync(frame, now);
                return;
            default:
                await TickWarAttackAsync(frame, now);
                return;
        }
    }

    /// <summary>Step 0: Tab (<see cref="DungeonSettings.TabKey"/>), then the target box is read 700 ms later.</summary>
    private void TickWarTab(long now)
    {
        string tab = _settings.Dungeon.TabKey;
        if (!string.IsNullOrWhiteSpace(tab) && !_input.KeyTap(tab))
        {
            _detail = "Tab blocked - waiting for the game to be in front";
            _waitUntil = now + 300;
            return;
        }

        _warFightStep = 1;
        _detail = WarFightDetail("Tab - looking for a target", now);
        _waitUntil = now + 700;
    }

    /// <summary>
    /// Step 1: a name, the HP numbers or the chest in the target box (<see cref="ScreenAnalyzer.ReadWarTargetAsync"/>) = a
    /// target - the war has started, the "no target" spell ends, the X buff (<see cref="WarSettings.XBuffIcon"/>, skipped
    /// when unticked; not on the bar - skipped, <see cref="WarnXBuffMissing"/>) is clicked on this frame, then Shura.
    /// None: Tab again 1 s later - or, <see cref="WarWalkDue"/>, a walk step on the next tick's frame (step 5); before the
    /// war has started the whole client is read for <see cref="WarSettings.GateText"/> on every 2nd such read.
    /// </summary>
    private async Task TickWarFindTargetAsync(Mat frame, long now)
    {
        var war = _settings.War;
        var target = await _analyzer.ReadWarTargetAsync(frame);
        string? name = target.Name;
        if (target.Seen)
        {
            if (target.Chest is { } chest)
                _overlays.Add(new OverlayItem(chest.Bounds, $"target {chest.Score:0.00}", OverlayColor.Green));
            if (!_warStarted)
            {
                _warStarted = true;
                Log?.Invoke(LogLevel.Success, "A target in the target box - the war has started.");
            }

            _warTarget = name ?? "?";
            _warTargetSeenAt = _warClock.Now(now);
            _warTabPolls = 0;
            _warNoTargetTabs = 0;
            _warWalkSteps = 0;
            _warNoPlayersTold = false;
            _warTitleUnreadTold = false;
            bool xBuff = !string.IsNullOrWhiteSpace(war.XBuffIcon) && war.IsBuffOn(war.XBuffIcon);
            string shura = string.IsNullOrWhiteSpace(_settings.Lod.ShuraKey) ? "" : $"Shura {war.ShuraHoldMs} ms, ";
            string what = name != null ? $"Target '{name}' ({target.Via})"
                : target.HpLine is { } hp ? $"A target (HP numbers, name unread: '{hp.Text}')"
                : "A target (chest icon, name unread)";
            // Every cycle: Trace, so a war's hundreds of cycles stay out of the Info log.
            Log?.Invoke(LogLevel.Trace, $"{what} - {(xBuff ? "X buff, " : "")}{shura}then the skill until it is gone.");

            bool clicked = false;
            var x = xBuff ? _analyzer.FindSkillBarIcons(frame, [war.XBuffIcon], war.IconMatchScore)[0].Match : null;
            if (x != null)
            {
                _overlays.Add(new OverlayItem(x.Bounds, $"{war.XBuffIcon} {x.Score:0.00}", OverlayColor.Green));
                clicked = WarClick(x.Bounds, $"the X buff '{war.XBuffIcon}' ({x.Score:0.00})", LogLevel.Trace);
            }
            else if (xBuff && _warMissingIcons.Add(war.XBuffIcon))
            {
                WarnXBuffMissing(frame);
            }

            _warFightStep = 2;
            _waitUntil = now + (clicked ? WarBuffAnimationMs : 0);     // the X buff's animation before Shura
            return;
        }

        if (!_warStarted && ++_warTabPolls % 2 == 0)
        {
            var full = new PixelRect(0, 0, frame.Width, frame.Height);
            var (hit, _, _) = await _analyzer.FindTextAsync(frame, full, war.GateText);
            if (hit != null && TextMatcher.CoversMost(hit.Text, war.GateText))
            {
                _warStarted = true;
                _overlays.Add(new OverlayItem(hit.Bounds, war.GateText, OverlayColor.Green));
                Log?.Invoke(LogLevel.Success, $"'{hit.LineText}' - the war has started.");
            }
        }

        _warNoTargetTabs++;
        long clock = _warClock.Now(now);
        if (_warWalkSteps >= WarWalkMaxSteps && clock >= _warWalkRestUntil)
        {
            // The rest after a spell's last step is over: a new spell.
            _warWalkSteps = 0;
            _warNoPlayersTold = false;
            _warTitleUnreadTold = false;
        }

        if (WarWalkDue(_warNoTargetTabs, _warWalkSteps, clock, _warWalkRestUntil))
        {
            _warFightStep = 5;
            _detail = WarFightDetail($"no target after {_warNoTargetTabs} Tabs - looking for players on screen", now);
            _waitUntil = now + 100;     // the walk decides on the next tick's frame
            return;
        }

        _warFightStep = 0;
        _detail = WarFightDetail(_warWalkSteps >= WarWalkMaxSteps
            ? $"no target - Tab again (Tab only for {Clock(_warWalkRestUntil - clock)} after {WarWalkMaxSteps} walk steps)"
            : "no target - Tab again", now);
        _waitUntil = now + 1000;
    }

    /// <summary>
    /// Step 5, a walk step out of the respawn point toward other players (user, 2026-10-28), only while the last minimap
    /// title read was the war map: whole-screen OCR (x1) on this tick's frame, the nearest name tag in the game world
    /// (<see cref="WarWalkGoal"/>; not our own) - Ctrl+click (a walk; a plain click attacks) the floor just below it
    /// (<see cref="WarWalkClick"/>), never with Shura held; then Tab again
    /// <see cref="WarWalkWaitMs"/> later. No tag: no click, Tab again, "no players in view" once per spell. After
    /// <see cref="WarWalkMaxSteps"/> steps, Tab only for <see cref="WarWalkRestMs"/>. A dry run logs and sends nothing.
    /// </summary>
    private async Task TickWarWalkAsync(Mat frame, long now)
    {
        if (!_warTitleIsWarMap)
        {
            // A login or loading screen, a window over the minimap: its words are no name tags (a saved server list offers
            // "Online" as the nearest one) - no click until the minimap reads the war map again.
            if (!_warTitleUnreadTold)
            {
                _warTitleUnreadTold = true;
                Log?.Invoke(LogLevel.Info, $"No target after {_warNoTargetTabs} Tabs, but the minimap does not read '{_settings.War.Destination}' - not walking, Tab again (said once per spell).");
            }

            _warFightStep = 0;
            _detail = WarFightDetail("no target - Tab again (the minimap does not read the war map: no walk)", now);
            _waitUntil = now + 1000;
            return;
        }

        var full = new PixelRect(0, 0, frame.Width, frame.Height);
        var lines = ScreenAnalyzer.ToLines(await _analyzer.Ocr.RecognizeAsync(frame, full, 1.0));
        var goal = WarWalkGoal(lines, frame.Width, frame.Height, WarWorldBands(_settings.Regions, frame.Width, frame.Height));
        if (goal is not { } tag)
        {
            if (!_warNoPlayersTold)
            {
                _warNoPlayersTold = true;
                Log?.Invoke(LogLevel.Info, $"No target after {_warNoTargetTabs} Tabs and no players in view - Tab again (said once per spell).");
            }

            _warFightStep = 0;
            _detail = WarFightDetail("no target, no players in view - Tab again", now);
            _waitUntil = now + 1000;
            return;
        }

        ReleaseHeldSkill();     // never walk with Shura held (nothing is held in steps 0-1; this is the guard)
        var (x, y) = WarWalkClick(tag.Bounds, frame.Width, frame.Height);
        _overlays.Add(new OverlayItem(tag.Bounds, $"walk to {tag.Text}", OverlayColor.Orange));
        if (!_input.IsDryRun && !_input.CtrlClick(x, y))
        {
            _detail = "Ctrl+click blocked - waiting for the game to be in front";
            _waitUntil = now + 300;     // step 5 again: decided afresh on the next frame
            return;
        }

        _warWalkSteps++;
        if (!_input.IsDryRun)
            _lastClick = (x, y, now);
        Log?.Invoke(LogLevel.Info, $"No target after {_warNoTargetTabs} Tabs - walking toward '{tag.Text}' (step {_warWalkSteps}/{WarWalkMaxSteps})."
                                   + (_input.IsDryRun ? $" Dry run - would Ctrl+click {x},{y}." : ""));
        if (!_input.IsDryRun)
            Log?.Invoke(LogLevel.Trace, $"Ctrl+clicked {x},{y} for the tag '{tag.Text}' at {tag.Bounds.CenterX},{tag.Bounds.CenterY}.");
        if (_warWalkSteps == 1)
            WarShot(frame, "war_walk", (x, y));
        if (_warWalkSteps >= WarWalkMaxSteps)
        {
            _warWalkRestUntil = _warClock.Now(now) + WarWalkRestMs;
            Log?.Invoke(LogLevel.Info, $"{WarWalkMaxSteps} walk steps without a target - Tab only for {WarWalkRestMs / 1000}s, then walk again.");
        }

        _warFightStep = 0;
        _detail = WarFightDetail($"walking toward '{tag.Text}' (step {_warWalkSteps}/{WarWalkMaxSteps})", now);
        _waitUntil = now + WarWalkWaitMs;
    }

    /// <summary>
    /// The X buff, ticked on, not found on the skill bar at <see cref="WarSettings.IconMatchScore"/> in a fight cycle: one
    /// warning per Start (<c>_warMissingIcons</c>, shared with the buff round, so an icon it already warned about is not
    /// warned again) naming its best score at any scale, or that its PNG is missing (<see cref="DescribeMissingIcons"/>).
    /// With autobuff - or a Start inside Bohai with autobuff - nothing else ever looks for it. The cycle goes on without it.
    /// </summary>
    private void WarnXBuffMissing(Mat frame)
    {
        string icon = _settings.War.XBuffIcon;
        var bar = _settings.Regions.Hotbar.ToPixels(frame.Width, frame.Height);
        bool hasPng = _analyzer.Library.Get(icon) != null;
        // How close it came: any scale, so no scale is learned from a look-alike.
        var best = hasPng ? _analyzer.FindIconAnyScale(frame, icon, bar, 0.0).Match : null;
        if (best != null)
            _overlays.Add(new OverlayItem(best.Bounds, $"{icon}? {best.Score:0.00}", OverlayColor.Orange));
        Log?.Invoke(LogLevel.Warning, $"The X buff '{icon}' was not found on the skill bar (score "
                                      + $"{_settings.War.IconMatchScore.ToString("0.00", CultureInfo.InvariantCulture)} needed): "
                                      + $"{DescribeMissingIcons([(icon, best?.Score)], _ => hasPng)} - fighting on without it (a cooldown can hide it too; "
                                      + "said once per Start; untick it in Settings > Auto War > Buffs when this character does not use it).");
        WarShot(frame, "war_xbuff_missing");
    }

    /// <summary>
    /// Step 2: the Shura key goes down and stays down (<c>_heldSkill</c>, so every way out lets go of it). No Shura key (user,
    /// 2026-10-28: Shura is optional): no hold - the skill at once, said once per Start.
    /// </summary>
    private void TickWarShuraDown(long now)
    {
        var war = _settings.War;
        string shura = _settings.Lod.ShuraKey;
        if (string.IsNullOrWhiteSpace(shura))
        {
            if (!_warShuraSkipTold)
            {
                _warShuraSkipTold = true;
                Log?.Invoke(LogLevel.Info, "No Shura key is set ('Shura key (LOD)') - every fight cycle goes from the target straight to the skill, without the Shura hold.");
            }

            BeginWarAttack(now);
            return;
        }

        ReleaseHeldSkill();
        if (!_input.KeyDown(shura))
        {
            _detail = "Shura key blocked - waiting for the game to be in front";
            _waitUntil = now + 300;
            return;
        }

        _heldSkill = shura;
        _warStepAt = now + war.ShuraHoldMs;
        _warFightStep = 3;
        _detail = WarFightDetail($"holding Shura '{shura}' {war.ShuraHoldMs} ms on '{_warTarget}'", now);
        _waitUntil = _warStepAt;
    }

    /// <summary>Step 3: the Shura key goes up after its hold; let go early (nothing held any more) - Tab again.</summary>
    private void TickWarShuraUp(long now)
    {
        if (_heldSkill == null)
        {
            _warFightStep = 0;
            return;
        }

        if (now < _warStepAt)
        {
            _waitUntil = _warStepAt;
            return;
        }

        ReleaseHeldSkill();
        BeginWarAttack(now);
    }

    /// <summary>Step 4 from now: the first skill press at once, the target counted as seen at the start of the attack.</summary>
    private void BeginWarAttack(long now)
    {
        _warFightStep = 4;
        _warStepAt = now;
        _warTargetReadAt = now + 700;
        // One target read missing right after the hold must not end the attack at once.
        _warTargetSeenAt = _warClock.Now(now);
    }

    /// <summary>
    /// Step 4: the skill (<see cref="TapSkill"/>, with Ctrl + skill when that is on) every <see cref="LodSettings.SkillEveryMs"/>;
    /// the target box every 700 ms - a name, the HP numbers or the chest is a sight; none for <see cref="WarSettings.TargetGoneMs"/> on the
    /// war clock - Tab again.
    /// </summary>
    private async Task TickWarAttackAsync(Mat frame, long now)
    {
        var war = _settings.War;
        string skill = _settings.Dungeon.SkillKey;
        if (!string.IsNullOrWhiteSpace(skill) && now >= _warStepAt)
        {
            if (!TapSkill(skill))
            {
                _detail = "skill key blocked - waiting for the game to be in front";
                _waitUntil = now + 300;
                return;
            }

            _warStepAt = now + _settings.Lod.SkillEveryMs;
        }

        if (now >= _warTargetReadAt)
        {
            _warTargetReadAt = now + 700;
            var target = await _analyzer.ReadWarTargetAsync(frame);
            long clock = _warClock.Now(now);
            if (target.Seen)
            {
                _warTargetSeenAt = clock;
                if (target.Name != null)
                    _warTarget = target.Name;
            }
            else if (WarTargetGone(clock, _warTargetSeenAt, war.TargetGoneMs))
            {
                Log?.Invoke(LogLevel.Trace, $"Target '{_warTarget}' gone for {war.TargetGoneMs / 1000.0:0.#}s - Tab again.");
                _warFightStep = 0;
                _waitUntil = now + 200;
                return;
            }
        }

        double seen = (_warClock.Now(now) - _warTargetSeenAt) / 1000.0;
        _detail = WarFightDetail($"attacking '{_warTarget}' with {SkillText(skill)} (seen {seen:0.0}s ago)", now);
        long next = string.IsNullOrWhiteSpace(skill) ? _warTargetReadAt : Math.Min(_warStepAt, _warTargetReadAt);
        _waitUntil = Math.Max(now + 50, next);
    }

    /// <summary>The status line in the fight: what it does, whether the war has started, and when the next rebuff is due.</summary>
    private string WarFightDetail(string what, long now)
        => $"{what}{(_warStarted ? "" : " (war not started yet)")} - rebuff in {Clock(WarRebuffWantedAt(_warBuffedAt, _settings.War.BuffEveryMs, _warBuffsDroppedAt) - now)}";

    // ---------------------------------------------------------------- HP and death

    /// <summary>
    /// One HP read (<see cref="ScreenAnalyzer.ReadPlayerHpAsync"/>) through the watch (<see cref="WarHpNext"/>): its verdict,
    /// the read, and whether it told anything about the player's HP. A raise of the maximum refused as too big for a buff
    /// is a warning (once per Start, the watch counts it), whichever phase read it.
    /// </summary>
    private async Task<(WarHpVerdict Verdict, (int Cur, int Max)? Hp, bool Told)> ReadWarHpAsync(Mat frame, bool dead = false)
    {
        var hp = await _analyzer.ReadPlayerHpAsync(frame);
        (_warHp, var verdict, bool told) = WarHpNext(_warHp, hp, dead);
        if (verdict == WarHpVerdict.RaiseIgnored && hp is { } big)
            Log?.Invoke(LogLevel.Warning, $"The HP maximum read {big.Max} twice, more than 1.5 times the {_warHp.AliveMax} seen alive - not taken as the HP maximum (the MP line read in place of the HP line?).");
        return (verdict, hp, told);
    }

    /// <summary>
    /// The fight's HP read (design §4.6). No read of the player's HP - unread, or another "N / M" line read in its place -
    /// for <see cref="WarHpUnreadMs"/> on the war clock: a warning and <c>war_hp_unread</c> once per such spell, and the
    /// fight goes on as alive.
    /// </summary>
    private async Task<(WarHpVerdict Verdict, (int Cur, int Max)? Hp)> WatchWarHpAsync(Mat frame, long now)
    {
        var (verdict, hp, told) = await ReadWarHpAsync(frame);
        long clock = _warClock.Now(now);
        _warHpUnreadSince = WarHpUnreadSince(_warHpUnreadSince, told, clock);
        if (told)
        {
            _warHpUnreadWarned = false;
        }
        else if (!_warHpUnreadWarned && WarHpUnreadTooLong(clock, _warHpUnreadSince))
        {
            _warHpUnreadWarned = true;
            string last = hp is { } other ? $" (it reads {other.Cur} / {other.Max}, not the player's maximum {_warHp.AliveMax})" : "";
            Log?.Invoke(LogLevel.Warning, $"The HP numbers (top left) have not read for {WarHpUnreadMs / 1000}s{last} - fighting on as if alive.");
            WarShot(frame, "war_hp_unread");
        }

        return (verdict, hp);
    }

    /// <summary>Dead (design §4.6.2): every key let go, nothing pressed until the HP reads back on our feet.</summary>
    private void BeginWarDead(Mat frame, (int Cur, int Max) hp)
    {
        ReleaseHeldSkill();
        Log?.Invoke(LogLevel.Warning, $"Died in the war (HP {hp.Cur} / {hp.Max} {WarHpReadsInARow} times in a row) - waiting to revive.");
        WarShot(frame, "war_died");
        _warDeadSince = _warClock.Now(Environment.TickCount64);
        _warDeadWarned = false;
        _warHpReadAt = 0;
        _warHp = _warHp with { ReviveReads = 0 };
        Advance(PilotPhase.WarDead, 500);
    }

    /// <summary>
    /// Dead: the HP read every second; back on our feet (<see cref="WarHpVerdict.Revived"/>: HP above 0 out of the player's
    /// maximum, twice in a row - the MP line or the bar under the HP bar never pass) - every buff again, then the fight.
    /// Still dead after 30 s on the war clock: a warning and <c>war_dead</c> once per death, and on waiting. The zone check
    /// catches a death that sends the character to town.
    /// </summary>
    private async Task TickWarDeadAsync(Mat frame)
    {
        long now = Environment.TickCount64;
        if (!await WarZoneCheckAsync(frame))
            return;

        long dead = _warClock.Now(now) - _warDeadSince;
        if (now >= _warHpReadAt)
        {
            _warHpReadAt = now + 1000;
            var (verdict, hp, _) = await ReadWarHpAsync(frame, dead: true);
            if (verdict == WarHpVerdict.Revived && hp is { } alive)
            {
                Log?.Invoke(LogLevel.Info, $"Back on our feet (HP {alive.Cur} / {alive.Max}) after {dead / 1000}s.");
                BeginWarRebuff("after the death", 1000);
                return;
            }
        }

        if (dead >= 30000 && !_warDeadWarned)
        {
            _warDeadWarned = true;
            Log?.Invoke(LogLevel.Warning, $"Still dead after {dead / 1000}s - waiting for the revive.");
            WarShot(frame, "war_dead");
        }

        _detail = $"dead - waiting to revive ({dead / 1000}s)";
        _waitUntil = Math.Max(now + 100, _warHpReadAt);
    }
}

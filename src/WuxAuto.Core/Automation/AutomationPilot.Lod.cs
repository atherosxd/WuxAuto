using Emgu.CV;
using WuxAuto.Core;
using WuxAuto.Phone;
using WuxAuto.Vision;
using LogLevel = WuxAuto.Core.LogLevel;

namespace WuxAuto.Automation;

/// <summary>
/// Auto LOD (user, 2026-10-21): teleport to the Ultimate Den of Rebirth, face south, walk forward to
/// 1199,1782 pressing Tab and wait there tabbing (<see cref="LodSettings.WalkToBossSpot"/>, since
/// 2026-10-28; off = stay where the teleport put the character), and once "Bai Gu Great..." is
/// targeted spam the skill key until it is dead. The boss respawns, so the map is unlimited until its
/// timer runs out and the game sends the character to town (user) - the zone guard then teleports in
/// again. Killed by the boss, the character is put back at the map's starting point: face south and
/// walk in (or Tab) again.
/// </summary>
public sealed partial class AutomationPilot
{
    private bool _faceSouth;
    private long _lodTabAt;
    private long _lodSkillAt;
    private long _lodBossSeenAt;
    private long _lodCoordsAt;
    private (int X, int Y)? _lodAttackCoords;
    private int _lodKills;
    private int _lodStill;
    private bool _lodWaitLogged;
    private string? _heldSkill;

    // Set when the held key is an action (Auto War's Shura), not a skill; let go with ActionUp then.
    private GameAction? _heldAction;

    private long _lodBuffAt;
    private int _lodBuffs;
    private bool _lodHeadingUnknown;
    private int _lodDeathLines = -1;
    private long _lodLootAt;
    private HashSet<string>? _lodHitLines;
    private bool _lodApproached;
    private bool _lodApproachStopped;
    private int _lodApproachTries;
    private long _lodApproachClickAt;
    private int _lodApproachSnapshots;
    private long _lodRingCheckAt;
    private int _lodRingWalks;
    private bool _lodApproachRingOnly;

    private bool IsLod => _settings.Sequence.Mode == RunMode.AutoLod;

    /// <summary>
    /// Phone: the south-facing beam on the phone's LOD minimap (no crop yet - no LOD frame exists). The desktop's
    /// flashlightLOD is a crop of the PC minimap and is not used on the phone.
    /// </summary>
    public const string PhoneLodSouthTemplate = "phone_flashlightlod";

    // Phone without a south-beam picture: no south turn, so no heading for a walk - stand and Tab (LodRun) instead.
    private bool _lodStayPut;

    // Phone LOD (user, Xiaomi 2026-10-30): the up-left joystick walk on arrival, sword taps since the last boss read,
    // when the held attack was last pressed (re-pressed every PhoneSettings.HoldRepressMs).
    private bool _lodArrivalWalkDue;
    private int _lodBladeTaps;
    private long _lodHoldPressedAt;

    // Phone: attack taps since _phoneTapRateSince, for the real-rate line (the fight's OCR can stretch AttackTapEveryMs).
    private int _phoneTaps;
    private long _phoneTapRateSince;

    /// <summary>Phone only: counts an LOD attack tap and logs the real rate every 30 s.</summary>
    private void NotePhoneAttackTap(long now)
    {
        if (!PhoneTaps)
            return;
        if (_phoneTapRateSince == 0 || now - _phoneTapRateSince > 60_000)
        {
            _phoneTapRateSince = now;
            _phoneTaps = 0;
        }

        _phoneTaps++;
        long span = now - _phoneTapRateSince;
        if (span >= 30_000)
        {
            Log?.Invoke(LogLevel.Trace,
                $"Attack taps: {_phoneTaps - 1} in {span / 1000.0:0.0}s = one per {span / Math.Max(1, _phoneTaps - 1)} ms (asked: {PhoneTapEveryMs} ms).");
            _phoneTapRateSince = now;
            _phoneTaps = 1;
        }
    }

    /// <summary>The phone has no picture of the LOD south beam (<see cref="PhoneLodSouthTemplate"/>).</summary>
    private bool PhoneLodSouthMissing => Profile == PlatformProfile.Phone && !_analyzer.Library.TryGet(PhoneLodSouthTemplate, out _);

    /// <summary>Teleport list entry and minimap zone for the selected mode (Auto War: the cave).</summary>
    private string Destination => IsWar ? WarHomeMap : IsLod ? _settings.Lod.Destination : _settings.Sequence.DestinationText;

    /// <summary>
    /// True when the minimap title (or one OCR line) names <paramref name="destination"/>. The title is
    /// cut off at the panel edge ("[The Labyrinth (Adep"), so a long run of the destination's letters
    /// counts too ("[Ultimate Den of Re" for "Ultimate Den of Rebirth").
    /// </summary>
    private static bool ZoneMatches(string? title, string destination)
    {
        if (string.IsNullOrWhiteSpace(title))
            return false;

        string d = TextMatcher.Normalize(destination);
        string t = TextMatcher.Normalize(title);
        if (d.Length == 0 || t.Length == 0)
            return false;
        if (t.Contains(d, StringComparison.Ordinal))
            return true;

        int need = Math.Min(d.Length, 10);
        for (int i = 0; i + need <= t.Length; i++)
        {
            int n = 0;
            while (i + n < t.Length && n < d.Length && t[i + n] == d[n])
                n++;
            if (n >= need)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Auto-buff (key O, user): on arrival / after being put back at the start (<paramref name="force"/>)
    /// and whenever the last one is BuffEveryMs old. True when it pressed the key (give it a moment).
    /// </summary>
    private bool TryLodBuff(Mat? frame, long now, bool force, bool attacking = false)
    {
        var lod = _settings.Lod;
        if (!force && now - _lodBuffAt < lod.BuffEveryMs)
            return false;
        if (!_controls.Has(GameAction.Buff))
        {
            PhoneBuffMissing();
            return false;
        }

        bool wasHeld = _heldSkill != null;
        ReleaseHeldSkill();

        // Mid-fight the buff is ignored until the attack stops (user): Shura (or Space) first.
        string stopped = "";
        if (attacking)
        {
            if (!StopLodAttack(frame, lod.StopAttackPresses, out stopped))
                return false;
            Thread.Sleep(400);
        }
        else if (PhoneBuffChecks && wasHeld)
        {
            // Phone: a tap right after the held skill's lift is lost (TouchControls.LiftBeforeTapMs).
            Thread.Sleep(TouchControls.LiftBeforeTapMs);
        }

        if (!_controls.Press(GameAction.Buff))
            return false;

        _lodBuffAt = now;
        _lodBuffs++;
        PhoneBuffTapped(now);
        // Phone: the fight presses the skill again on its next tick - not before the buff tap has been taken.
        if (PhoneBuffChecks && (wasHeld || attacking))
            Thread.Sleep(TouchControls.TapBeforeRepressMs);
        Log?.Invoke(LogLevel.Info, attacking
            ? $"Re-buffed ({stopped}, then '{lod.BuffKey}') mid-fight - buff {_lodBuffs}."
            : $"Buffed ('{lod.BuffKey}') - buff {_lodBuffs}; again in {lod.BuffEveryMs / 1000}s.");
        return true;
    }

    /// <summary>
    /// Stops the attack sequence (user, 2026-10-25: Shura instead of spamming Space) before a mid-fight
    /// buff and before the walk up to the boss. The Shura icon (<see cref="LodSettings.ShuraTemplate"/>)
    /// on the skill bar is clicked <see cref="LodSettings.ShuraPresses"/> times,
    /// <see cref="LodSettings.ShuraEveryMs"/> apart; not on the bar (dimmed, moved, another layout) -> the
    /// <see cref="LodSettings.ShuraKey"/> the player set for its slot, the same way; no key either ->
    /// <see cref="LodSettings.StopAttackKey"/> x <paramref name="stopKeyPresses"/>, 100 ms apart.
    /// <paramref name="how"/> says which one went out; no <paramref name="frame"/> = no icon lookup.
    /// False = the input was blocked (the game is not in front); the caller retries.
    /// </summary>
    private bool StopLodAttack(Mat? frame, int stopKeyPresses, out string how)
    {
        var lod = _settings.Lod;
        var shura = frame == null ? null : FindShura(frame);
        if (shura != null)
        {
            for (int i = 0; i < lod.ShuraPresses; i++)
            {
                if (!_controls.Tap(shura.Bounds.CenterX, shura.Bounds.CenterY))
                {
                    how = "Shura click blocked";
                    return false;
                }

                Thread.Sleep(lod.ShuraEveryMs);
            }

            how = $"Shura clicked x{lod.ShuraPresses} at {shura.Bounds.CenterX},{shura.Bounds.CenterY} ({shura.Score:0.00})";
            return true;
        }

        if (_controls.Has(GameAction.ShuraKey))
        {
            for (int i = 0; i < lod.ShuraPresses; i++)
            {
                if (!_controls.Press(GameAction.ShuraKey))
                {
                    how = "Shura key blocked";
                    return false;
                }

                Thread.Sleep(lod.ShuraEveryMs);
            }

            how = $"Shura icon not on the bar - key '{lod.ShuraKey}' x{lod.ShuraPresses}";
            return true;
        }

        if (!_controls.Has(GameAction.StopAttack) || stopKeyPresses <= 0)
        {
            how = "no Shura icon, no Shura key and no stop key - nothing pressed";
            return true;
        }

        for (int i = 0; i < stopKeyPresses; i++)
        {
            if (!_controls.Press(GameAction.StopAttack))
            {
                how = "stop key blocked";
                return false;
            }

            Thread.Sleep(100);
        }

        how = $"no Shura icon or key - '{lod.StopAttackKey}' x{stopKeyPresses}";
        return true;
    }

    /// <summary>
    /// The Shura icon on the skill bar (<see cref="RegionSettings.Hotbar"/>), drawn on the preview. The
    /// best score wins; the same icon in two slots (a 1920x1080 bar had it at 1035 and 1240, both 1.00)
    /// -> the leftmost. Saved frames: 0.95-1.00 on every bar at 1024, 1152 and 1920 wide, at most
    /// 0.70 without one (login screen, a window over the game).
    /// </summary>
    private TemplateMatch? FindShura(Mat frame)
    {
        var lod = _settings.Lod;
        if (string.IsNullOrWhiteSpace(lod.ShuraTemplate))
            return null;

        var roi = _settings.Regions.Hotbar.ToPixels(frame.Width, frame.Height);
        var hits = _analyzer.FindIcons(frame, lod.ShuraTemplate, roi, lod.ShuraMatchScore, maxResults: 4);
        TemplateMatch? best = null;
        foreach (var hit in hits)
        {
            _overlays.Add(new OverlayItem(hit.Bounds, $"shura {hit.Score:0.00}", OverlayColor.Green));
            if (best == null || hit.Score > best.Score + 0.01 ||
                (Math.Abs(hit.Score - best.Score) <= 0.01 && hit.Bounds.CenterX < best.Bounds.CenterX))
                best = hit;
        }

        return best;
    }

    /// <summary>
    /// The dungeon keys in the item status list (bottom right, user): "[Temple of Fiends Key] N" =
    /// Labyrinth keys, "[Den of Rebirth] N" = LOD entries; gone = run out. The chosen mode out of keys
    /// -> the other mode; both out -> J in town (the game's auto war, user) and stop. OCR is noisy
    /// ("[TemPléof FiendS Key]", "[Denpf Rebirth]") and skips lines now and then, so: keywords without
    /// accents, three scales, a key counts as there if any read sees it. Returns true when it
    /// handled the turn (switched or stopped). A hidden list (no lines) decides nothing.
    /// </summary>
    internal async Task<bool> CheckKeysAsync(Mat frame)
    {
        // The phone reads key counts from ☰ → Durations (M6), not this PC item list, and must never switch
        // to the unported Laby / War flows (OutOfKeys rule).
        if (!_settings.Sequence.CheckKeys || Profile == PlatformProfile.Phone)
            return false;

        var roi = new NormRect(0.62, 0.50, 0.38, 0.50).ToPixels(frame.Width, frame.Height);
        var lines = new List<string>();
        foreach (double up in new[] { 1.5, 2.0, 1.0 })
            lines.AddRange((await _analyzer.Ocr.RecognizeAsync(frame, roi, up)).Select(w => Plain(w.LineText)));

        // The list shows a dozen buff/item lines with "(time(s))" / "(min(s))" when it is open.
        if (lines.Count(l => l.Contains("time") || l.Contains("min")) < 3)
            return false;

        bool temple = lines.Any(l => l.Contains("fiend") && l.Contains("key"));
        bool den = lines.Any(l => l.Contains("rebirth"));
        bool haveMode = IsLod ? den : temple;
        bool haveOther = IsLod ? temple : den;
        if (haveMode)
            return false;

        string mine = IsLod ? "Den of Rebirth entries" : "Temple of Fiends Keys";
        if (haveOther)
        {
            var next = IsLod ? RunMode.AutoLaby : RunMode.AutoLod;
            Log?.Invoke(LogLevel.Warning, $"Out of {mine} (not in the item list) - switching to {next}.");
            DebugArtifacts.SaveFrame(frame, _overlays, "out_of_keys");
            _settings.Sequence.Mode = next;
            SettingsStore.Save(_settings);
            ModeChanged?.Invoke();
            _waitUntil = Environment.TickCount64 + 300;
            return true;
        }

        // Both out: the game's own auto war (Regular War ticked + J in the city, user), then stop.
        DebugArtifacts.SaveFrame(frame, _overlays, "out_of_keys");
        BeginRegularWar("out of Temple of Fiends Keys and Den of Rebirth entries");
        return true;

        static string Plain(string t) => new string(t.Normalize(System.Text.NormalizationForm.FormD)
            .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
            .Where(char.IsLetter).Select(char.ToLowerInvariant).ToArray());
    }

    /// <summary>
    /// After the arrival zoom (or a respawn): buff, face south, then Tab for the boss (LodRun). <paramref name="arrival"/>:
    /// the character stands where the teleport puts it (arrival, back at the start after dying) - the phone walks up-left
    /// first (<see cref="PhoneSettings.LodArrivalWalkMs"/>).
    /// </summary>
    private void BeginFaceSouth(bool buff = true, bool arrival = false)
    {
        // Buffs first (user: "upon teleporting, do the auto buff then the other"); a blocked key is
        // caught by the timer in the walk.
        if (buff)
            TryLodBuff(null, Environment.TickCount64, force: true);
        // Phone without a picture of the south beam: no turn (it would spin on a score that never comes), no walk (the
        // steering needs the heading) - Tab for the boss from where the teleport put the character (spec §6.9 targets with
        // the sword anyway).
        if (PhoneLodSouthMissing)
        {
            if (!_lodStayPut)
                Log?.Invoke(LogLevel.Warning, $"No picture of the LOD minimap's south beam on the phone yet ('{PhoneLodSouthTemplate}') - " +
                                              "skipping the south turn; tabbing for the boss from here instead of walking.");
            _lodStayPut = true;
            _lodArrivalWalkDue = arrival && _settings.Phone.LodArrivalWalkMs > 0;
            _faceSouth = false;
            _lodHeadingUnknown = false;
            Advance(AfterFaceSouth(), 400);
            return;
        }

        _faceSouth = true;
        _earlyNorth = false;
        _compassAnchored = false;
        _bestFlashlightScore = 0;
        _bestFlashlightStep = 0;
        _tapsSinceBest.Clear();
        _refineDir = 0;
        Log?.Invoke(LogLevel.Info, _settings.Lod.WalkToBossSpot
            ? "Turning to face south for the walk to the boss."
            : "Turning to face south, then tabbing for the boss from here.");
        Advance(PilotPhase.FaceNorth, 400);
    }

    /// <summary>The south turn is done: screen-up is world-south (y up), screen-right world-west.</summary>
    private PilotPhase AfterFaceSouth()
    {
        _faceSouth = false;
        _lodHeadingUnknown = false;
        _headingBase = Math.PI;
        _headingError = Math.PI;
        _headingSamples = 0;
        _simpleClick = null;
        _lastCoords = null;
        _stalledSteps = 0;
        _lodTabAt = 0;
        _lodBladeTaps = 0;
        _lodStill = 0;
        _lodWaitLogged = false;
        return PilotPhase.LodRun;
    }

    /// <summary>The boss name on screen (the target bar shows it after Tab).</summary>
    private async Task<TextHit?> FindLodBossAsync(Mat frame) => (await ReadLodScreenAsync(frame)).Boss;

    /// <summary>One whole-screen OCR: the boss name, and how many lines read like the death text.</summary>
    private async Task<(TextHit? Boss, int DeathLines)> ReadLodScreenAsync(Mat frame)
    {
        var (boss, _, deathLines, _) = await ReadLodScreenFullAsync(frame);
        return (boss, deathLines);
    }

    /// <summary>
    /// One whole-screen OCR: the boss (= targeted, user: "the boss name or the treasure icon means it
    /// is already targeted") and the death-text line count. The name counts by whole OCR line - the
    /// best hit is sometimes a single word ("[LOD]") that is too short to pass as the name, and the
    /// pilot then walked or turned with the boss right there (user). Without a readable name the
    /// target bar's treasure chest (monstericon: 0.90 at the top centre on targeted frames, not found
    /// otherwise) counts too. HitLines: the "Attack succeeded" lines of the combat log (see
    /// <see cref="AttackLineKey"/>), for the first hit of a fight.
    /// </summary>
    private async Task<(TextHit? Boss, bool Targeted, int DeathLines, IReadOnlyList<string> HitLines)> ReadLodScreenFullAsync(Mat frame)
    {
        var lod = _settings.Lod;
        if (string.IsNullOrWhiteSpace(lod.BossName))
            return (null, false, 0, []);

        var roi = _settings.Regions.BossName.ToPixels(frame.Width, frame.Height);
        var (hit, _, words) = await _analyzer.FindTextAsync(frame, roi, lod.BossName);
        var lines = words.GroupBy(w => w.LineBounds).Select(g => (Bounds: g.Key, Text: g.First().LineText)).ToList();

        int deathLines = string.IsNullOrWhiteSpace(lod.DeathText) ? 0 : lines
            .Count(line => TextMatcher.Normalize(line.Text).Contains(TextMatcher.Normalize(lod.DeathText), StringComparison.Ordinal)
                           || TextMatcher.Similarity(line.Text, lod.DeathText) >= 0.85);

        // Short lines only: the name tag / target bar reads "[LOD] Bai Gu Great"; a sentence that
        // mentions the boss (this chat window over the game was once read as the target) is not it.
        string name = TextMatcher.Normalize(lod.BossName);
        int maxLetters = name.Length + 10;
        TextHit? boss = lines
            .Where(line => TextMatcher.Normalize(line.Text).Length <= maxLetters)
            .Where(line => TextMatcher.Normalize(line.Text).Contains(name, StringComparison.Ordinal)
                           || TextMatcher.Similarity(line.Text, lod.BossName) >= 0.85)
            .Select(line => new TextHit(line.Text, 1.0, line.Bounds, line.Text, line.Bounds, 0))
            .FirstOrDefault();
        if (boss == null && hit != null && TextMatcher.Normalize(hit.LineText).Length <= maxLetters
            && (IsFullName(hit, lod.BossName) || TextMatcher.CoversMost(hit.LineText, lod.BossName)))
            boss = hit;

        if (boss == null)
        {
            var bar = new NormRect(0.25, 0, 0.50, 0.16).ToPixels(frame.Width, frame.Height);
            var chest = _analyzer.FindIcon(frame, "monstericon", bar, 0.80);
            if (chest != null)
                boss = new TextHit($"treasure icon {chest.Score:0.00}", chest.Score, chest.Bounds, string.Empty, chest.Bounds, 0);
        }

        CheckBuffRefusal(lines.Select(line => line.Text), Environment.TickCount64);

        string hitText = _settings.Dungeon.AttackConfirmText;
        var log = _settings.Regions.AttackMessage.ToPixels(frame.Width, frame.Height);
        var hitLines = string.IsNullOrWhiteSpace(hitText) ? [] : lines
            .Where(line => log.Contains(line.Bounds.CenterX, line.Bounds.CenterY))
            .Where(line => TextMatcher.Similarity(line.Text, hitText) >= _settings.Detection.OcrMinSimilarity
                           && TextMatcher.CoversMost(line.Text, hitText))
            .Select(line => AttackLineKey(line.Text, hitText))
            .ToList();

        return (boss, boss != null, deathLines, hitLines);
    }

    /// <summary>
    /// While turning south: the boss name on screen (its name tag) stops the turn - attack it now
    /// (user). The heading is then unknown, so a later walk turns south first.
    /// </summary>
    private async Task<bool> LodBossInViewWhileTurningAsync(Mat frame)
    {
        // Tab spammed while turning (user), so the boss gets targeted as soon as it is near.
        TapLodTab(Environment.TickCount64, _settings.Lod.TurnTabEveryMs);

        var boss = await FindLodBossAsync(frame);
        if (boss == null)
            return false;

        _faceSouth = false;
        _lodHeadingUnknown = true;
        BeginLodFight();
        Log?.Invoke(LogLevel.Success, $"'{boss.Text}' in view while turning - stopped the turn, spamming skill {SkillText(_settings.Dungeon.SkillKey)}.");
        Advance(PilotPhase.LodAttack, 0);
        return true;
    }

    /// <summary>
    /// After the kill (death text or the target gone LootAfterMissingMs): spam the loot key for LootMs
    /// (at least 10 s, user), then look for the respawn. No Tab here (user) - it only starts again
    /// once the loot is over.
    /// </summary>
    private void TickLodLoot(Mat frame)
    {
        var lod = _settings.Lod;
        long now = Environment.TickCount64;
        if (now - _phaseStart >= lod.LootMs || !_controls.Has(GameAction.Loot))
        {
            if (Profile == PlatformProfile.Phone)
            {
                if (_controls.Has(GameAction.Loot) || _phoneLootTaps > 0)
                    Log?.Invoke(LogLevel.Info, $"Loot: {_phoneLootTaps} taps over {(now - _phaseStart) / 1000.0:0.#} s.");
                else if (!_phoneLootMissingWarned)
                {
                    _phoneLootMissingWarned = true;
                    Log?.Invoke(LogLevel.Warning, "Loot: the Pick up button is not on screen - nothing picked up (check the shortcut ring).");
                }
            }

            // The kill's second screenshot: what is left on the ground after looting.
            TakePendingKillShot(frame);
            // The kill alert waits for the loot step (user: the reminder is to check what it left).
            BossKilled?.Invoke($"{lod.BossName} down");
            Log?.Invoke(LogLevel.Info, $"Looted for {lod.LootMs / 1000}s - waiting for '{lod.BossName}' to respawn.");
            LeaveLodAttack();
            return;
        }

        if (now - _lodLootAt >= 150 && _controls.Press(GameAction.Loot))
        {
            _lodLootAt = now;
            _phoneLootTaps++;
        }
        _detail = $"looting ('{lod.LootKey}') {(now - _phaseStart) / 1000.0:0.0}s";
        _waitUntil = now + 60;
    }

    /// <summary>
    /// A new fight (the boss just targeted): fresh timers, death-text baseline and combat-log
    /// baseline, and one walk up to the boss after its first hit (<see cref="LodSettings.ApproachBoss"/>).
    /// </summary>
    private void BeginLodFight()
    {
        _lodBossSeenAt = Environment.TickCount64;
        _lodSkillAt = 0;
        _lodCoordsAt = 0;
        _lodAttackCoords = null;
        _lodDeathLines = -1;
        _lodHitLines = null;
        _lodApproached = false;
        _lodRingWalks = 0;
        _lodBladeTaps = 0;
    }

    /// <summary>Back to finding the boss: Tab (and the walk with WalkToBossSpot), or the south turn first if the heading is unknown.</summary>
    private void LeaveLodAttack()
    {
        _lastCoords = null;
        _simpleClick = null;
        if (_lodHeadingUnknown)
        {
            BeginFaceSouth(buff: false);
            return;
        }

        Advance(PilotPhase.LodRun, 0);
    }

    private bool TapLodTab(long now, int everyMs)
    {
        if (now - _lodTabAt < everyMs || !_controls.Has(GameAction.TargetNearest))
            return false;
        if (!_controls.Press(GameAction.TargetNearest))
            return false;
        _lodTabAt = now;
        return true;
    }

    /// <summary>
    /// Tab every TabEveryMs until the boss name shows. With <see cref="LodSettings.WalkToBossSpot"/> (on)
    /// the character walks to the boss spot meanwhile and waits there (user, 2026-10-28: 1199,1782);
    /// off, it stays where it is (user, 2026-10-27).
    /// </summary>
    private async Task TickLodRunAsync(Mat frame)
    {
        var lod = _settings.Lod;
        long now = Environment.TickCount64;

        await PrepareBuffCheckAsync(frame, now, force: false);
        if (TryLodBuff(frame, now, force: false))
        {
            _waitUntil = now + 700;
            return;
        }

        if (_lodArrivalWalkDue)
        {
            WalkUpLeftOnArrival(frame, now);
            return;
        }

        if (TapLodTab(now, lod.TabEveryMs))
        {
            if (Profile == PlatformProfile.Phone)
                _lodBladeTaps++;
            _waitUntil = now + 250;
            return;
        }

        var boss = await FindLodBossAsync(frame);
        if (boss == null && TapLodBoss(frame, now))
            return;
        if (boss != null)
        {
            Log?.Invoke(LogLevel.Success, $"'{boss.Text}' targeted - spamming skill {SkillText(_settings.Dungeon.SkillKey)} ({_lodKills} kill(s) so far).");
            BeginLodFight();
            Advance(PilotPhase.LodAttack, 0);
            return;
        }

        // Stay put (user, 2026-10-27: "stay still on the same place upon teleport, just keep tabbing
        // until the boss name shows") - no walk clicks, no coordinate reads. The phone without a south turn too.
        if (!lod.WalkToBossSpot || _lodStayPut)
        {
            if (!_lodWaitLogged)
            {
                _lodWaitLogged = true;
                Log?.Invoke(LogLevel.Info, $"Staying here - pressing Tab every {lod.TabEveryMs / 1000.0:0.#}s until '{lod.BossName}' shows.");
            }

            _detail = $"standing still, tabbing for '{lod.BossName}'";
            _waitUntil = now + 300;
            return;
        }

        var coords = await _analyzer.ReadCoordinatesAsync(frame);
        if (coords == null)
        {
            _detail = "reading the minimap coordinates";
            _waitUntil = now + 300;
            return;
        }

        double needX = lod.TargetX - coords.X;
        double needY = lod.TargetY - coords.Y;
        double distance = Math.Sqrt(needX * needX + needY * needY);
        _overlays.Add(new OverlayItem(coords.Bounds, $"{coords} d={distance:0}", OverlayColor.Yellow));

        // Learn the heading from the last step, as the Labyrinth run does.
        if (_lastCoords is (int px, int py) && _simpleClick is (double cx, double cy))
        {
            var moved = ((double)(coords.X - px), (double)(coords.Y - py));
            if (Math.Abs(moved.Item1) + Math.Abs(moved.Item2) >= 20)
            {
                LearnHeadingError((cx, cy), moved);
                _lodStill = 0;
            }
            else
            {
                _lodStill++;
            }
        }

        _lastCoords = (coords.X, coords.Y);

        if (distance <= lod.TargetTolerance)
        {
            _simpleClick = null;
            if (!_lodWaitLogged)
            {
                _lodWaitLogged = true;
                Log?.Invoke(LogLevel.Info, $"At {coords}, the boss spot - tabbing until '{lod.BossName}' shows.");
            }

            _detail = $"at the boss spot {coords}, waiting for '{lod.BossName}'";
            _waitUntil = now + 300;
            return;
        }

        _lodWaitLogged = false;
        Log?.Invoke(LogLevel.Info, $"LOD: at {coords} ({distance:0} from {lod.TargetX},{lod.TargetY}).");

        int centerX = frame.Width / 2;
        int centerY = frame.Height / 2;
        double reach = _settings.Dungeon.RunClickPx * Math.Clamp(distance / 300.0, 0.45, 1.0);
        double ox = needX / distance * reach;
        double oy = needY / distance * reach;
        double c = Math.Cos(_headingError), sn = Math.Sin(_headingError);
        (ox, oy) = (ox * c + oy * sn, -ox * sn + oy * c);

        // Stuck (a wall, a mob): shorter clicks, as in the Labyrinth run.
        if (_lodStill >= 2)
        {
            double shorter = _lodStill >= 4 ? 0.3 : 0.5;
            ox *= shorter;
            oy *= shorter;
        }

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
        _detail = $"{coords} -> {lod.TargetX},{lod.TargetY} ({distance:0} left)";
        _waitUntil = now + _settings.Dungeon.RunDashWaitMs;
    }

    /// <summary>
    /// Spam the skill on the targeted boss. A kill sends the character to town (the zone guard sees
    /// it). A jump in the coordinates = the boss killed us and we are back at the start: face south
    /// and Tab again. The name gone for BossGoneMs (Tab pressed meanwhile) = lost target: back to
    /// LodRun (Tab; the walk too with WalkToBossSpot).
    /// </summary>
    private async Task TickLodAttackAsync(Mat frame)
    {
        var lod = _settings.Lod;
        string skill = _settings.Dungeon.SkillKey;
        long now = Environment.TickCount64;

        // Re-buff on the timer, also mid-fight: stop attacking first (Shura / Space), the skill
        // resumes on the next tick.
        await PrepareBuffCheckAsync(frame, now, force: false);
        if (TryLodBuff(frame, now, force: false, attacking: true))
        {
            _waitUntil = now + 700;
            return;
        }

        if (!string.IsNullOrWhiteSpace(skill))
        {
            if (LodHoldSkill)
            {
                // Held down for the whole fight; ReleaseHeldSkill lets go on every way out. With
                // CtrlSkill it is let go every SkillEveryMs for a Ctrl + skill tap, then held again.
                if (_heldSkill != null && CtrlSkillOn && now - _lodSkillAt >= LodSkillEveryMs)
                {
                    ReleaseHeldSkill();
                    Thread.Sleep(CtrlSkillGapMs);
                    _controls.SkillCtrlTap(skill);
                    Thread.Sleep(CtrlSkillGapMs);
                }

                // Phone: another touch (a Tab, a loot tap) lifts the held finger; press it again at once (user: "skill
                // down all the way" until the boss dies).
                if (_heldSkill != null && !_controls.SkillStillHeld(_heldSkill))
                {
                    Log?.Invoke(LogLevel.Trace, $"The held '{_heldSkill}' was lifted by another touch - pressing it again.");
                    _heldSkill = null;
                    _heldAction = null;
                }

                // Phone: lift and press again every HoldRepressMs (user, Xiaomi 2026-10-30: the player's own touch can
                // end the held finger without the system saying so - "it also sometimes stop attacking").
                if (_heldSkill != null && PhoneHolds && _settings.Phone.HoldRepressMs > 0
                    && now - _lodHoldPressedAt >= _settings.Phone.HoldRepressMs)
                {
                    ReleaseHeldSkill();
                    Thread.Sleep(TouchControls.LiftBeforeTapMs);
                }

                if (_heldSkill == null && _controls.SkillDown(skill))
                {
                    _heldSkill = skill;
                    _lodSkillAt = now;
                    _lodHoldPressedAt = now;
                }
            }
            else if (now - _lodSkillAt >= LodSkillEveryMs && _controls.SkillTap(skill))
            {
                _lodSkillAt = now;
                NotePhoneAttackTap(now);
            }
        }

        var (boss, _, deathLines, hitLines) = await ReadLodScreenFullAsync(frame);

        // "see you in hell" = the boss died (user): loot it. Only a new line counts - an old one
        // may still be in the chat log.
        if (_lodDeathLines < 0)
        {
            _lodDeathLines = deathLines;
        }
        else if (deathLines > _lodDeathLines)
        {
            _lodKills++;
            ReleaseHeldSkill();
            Log?.Invoke(LogLevel.Success, $"'{lod.DeathText}' - '{lod.BossName}' killed (kill {_lodKills}); looting with '{lod.LootKey}' for {lod.LootMs / 1000}s.");
            SaveLodKillShot(frame, $"lod_kill{_lodKills}");
            _lodLootAt = 0;
            _phoneLootTaps = 0;
            Advance(PilotPhase.LodLoot, 0);
            return;
        }
        else
        {
            _lodDeathLines = Math.Min(_lodDeathLines, deathLines);
        }

        // Name or treasure icon = targeted (user). Gone for LootAfterMissingMs (2 s) = dead -> loot
        // below; no Tab before that or while looting (user, 2026-10-23), so Tab only
        // runs here when looting is off.
        if (boss != null)
        {
            _lodBossSeenAt = now;

            // The first "Attack succeeded" of the fight (a line that was not in the log before):
            // stop attacking and walk up to the boss (user, 2026-10-24).
            if (lod.ApproachBoss && !_lodApproached)
            {
                if (_lodHitLines == null)
                {
                    _lodHitLines = [.. hitLines];
                }
                else if (hitLines.FirstOrDefault(line => !_lodHitLines.Contains(line)) is { } hit)
                {
                    BeginLodApproach($"First hit on '{lod.BossName}' ({hit})");
                    return;
                }
                else
                {
                    // Lines the OCR missed on one frame and reads on the next are not new hits.
                    _lodHitLines.UnionWith(hitLines);
                }
            }
            // Keep to the ring (user, 2026-10-27: "try to go to the ring as much as possible, do a
            // periodical check"): after the first walk up, look again every RingCheckEveryMs and walk
            // back when the character has drifted off it. The ring only: out of view it is mostly the
            // boss's body over the camera with the character under the boss (live, 23:20), where a
            // "belt" read was a cyan patch at the boss's side.
            else if (lod.ApproachBoss && lod.RingCheckEveryMs > 0 && now - _lodRingCheckAt >= lod.RingCheckEveryMs)
            {
                _lodRingCheckAt = now;
                if (FindApproachSpot(frame, useBelt: false) is { } spot && spot.Away > lod.RingStayMax)
                {
                    _lodRingWalks++;
                    BeginLodApproach($"Ring check {_lodRingWalks}: off the {spot.What} ({spot.Away:0.00} of its radius out)", ringOnly: true);
                    return;
                }
            }
        }
        else if (!_controls.Has(GameAction.Loot) && now - _lodBossSeenAt > 1500)
            TapLodTab(now, 1500);

        // Moved by the game: the boss is dead (or we died and respawned) - start the next cycle.
        if (now - _lodCoordsAt >= 1500)
        {
            _lodCoordsAt = now;
            var coords = await _analyzer.ReadCoordinatesAsync(frame);
            if (coords != null)
            {
                if (_lodAttackCoords is (int ax, int ay) && coords.DistanceTo(ax, ay) > lod.TeleportJump)
                {
                    Log?.Invoke(LogLevel.Warning, $"Moved to {coords} (from {ax},{ay}) - killed by the boss? Turning south again from the start.");
                    DebugArtifacts.SaveFrame(frame, _overlays, "lod_died");
                    Advance(PilotPhase.LodRespawn, _settings.Lod.RespawnSettleMs);
                    return;
                }

                _lodAttackCoords = (coords.X, coords.Y);
            }
        }

        // Name / treasure icon gone 2 s: the boss is dead (user) - loot, even without the death text.
        if (now - _lodBossSeenAt > lod.LootAfterMissingMs && _controls.Has(GameAction.Loot))
        {
            ReleaseHeldSkill();
            Log?.Invoke(LogLevel.Info, $"'{lod.BossName}' / treasure icon gone for {lod.LootAfterMissingMs / 1000.0:0.#}s - looting with '{lod.LootKey}' for {lod.LootMs / 1000}s, then looking for it again.");
            SaveLodKillShot(frame, "lod_gone");
            _lodLootAt = 0;
            _phoneLootTaps = 0;
            Advance(PilotPhase.LodLoot, 0);
            return;
        }

        if (now - _lodBossSeenAt > lod.BossGoneMs)
        {
            Log?.Invoke(LogLevel.Warning, $"'{lod.BossName}' not seen for {lod.BossGoneMs / 1000}s - {(lod.WalkToBossSpot ? "walking and tabbing" : "tabbing")} again.");
            LeaveLodAttack();
            return;
        }

        _detail = $"spamming '{skill}' on '{lod.BossName}' (seen {(now - _lodBossSeenAt) / 1000.0:0.0}s ago)";
        _waitUntil = now + Math.Min(LodSkillEveryMs, 300);
    }

    private void BeginLodApproach(string why, bool ringOnly = false)
    {
        _lodApproached = true;
        _lodApproachRingOnly = ringOnly;
        _lodApproachStopped = false;
        _lodApproachTries = 0;
        _lodApproachClickAt = 0;
        Log?.Invoke(LogLevel.Info, $"{why} - stopping the attack to walk up to '{_settings.Lod.BossName}'.");
        Advance(PilotPhase.LodApproach, 0);
    }

    /// <summary>
    /// Where the character should stand: the pink ring round the boss's feet, else the feet just below
    /// its cyan belt (a Ctrl+click on the belt itself is a floor spot behind the boss - live, it walked
    /// ~190 units past it). Away = how far out the character is: the camera follows it, so its feet are
    /// at the middle of the client, a little low; in ring radii (<see cref="ScreenAnalyzer.DistanceFromRing"/>),
    /// or in 8 % of the client height from the feet. Drawn on the preview. Null = neither in view
    /// (<paramref name="useBelt"/> false: no ring).
    /// </summary>
    private (PixelRect Spot, string What, double Away)? FindApproachSpot(Mat frame, bool useBelt = true)
    {
        string what = "pink ring";
        var spot = _analyzer.FindBossRing(frame);
        bool ring = spot != null;
        if (spot == null && useBelt && _analyzer.FindBossBelt(frame) is { } belt)
        {
            int feetY = belt.Bottom + (int)(frame.Height * 0.06);
            spot = new PixelRect(belt.X, feetY - belt.Width / 2, belt.Width, belt.Width);
            what = "boss's feet below its belt";
            _overlays.Add(new OverlayItem(belt, "belt", OverlayColor.Blue));
        }

        if (spot is not { } found)
            return null;

        double away;
        if (ring)
        {
            away = ScreenAnalyzer.DistanceFromRing(found, frame.Width, frame.Height);
        }
        else
        {
            double dx = frame.Width / 2.0 - found.CenterX, dy = frame.Height * 0.54 - found.CenterY;
            away = Math.Sqrt(dx * dx + dy * dy) / (frame.Height * 0.08);
        }

        _overlays.Add(new OverlayItem(found, $"{what} {away:0.00}", OverlayColor.Green));
        return (found, what, away);
    }

    /// <summary>
    /// After the first hit of a fight (user, 2026-10-24), and whenever the ring check finds the
    /// character off the ring (user, 2026-10-27): stop attacking (Shura) and Ctrl+click the pink ring
    /// round the boss's feet - Ctrl+click walks there (live: 1183,1611 -> 1212,1775, at the boss's feet,
    /// and the attack carried on by itself); no ring before the first click: just below the belt
    /// (<see cref="FindApproachSpot"/>). Next to the boss after a click, no ring after a click (= under
    /// the boss), nothing found, or ApproachTries used up -> back to the attack.
    /// </summary>
    private async Task TickLodApproachAsync(Mat frame)
    {
        var lod = _settings.Lod;
        long now = Environment.TickCount64;

        if (_lodApproachClickAt > 0 && now - _lodApproachClickAt < lod.ApproachWaitMs)
        {
            _detail = $"walking up to '{lod.BossName}' (Ctrl+click {_lodApproachTries}/{lod.ApproachTries})";
            _waitUntil = _lodApproachClickAt + lod.ApproachWaitMs;
            return;
        }

        // The boss may have died while the character walked: the attack phase loots it.
        var (_, deathLines) = await ReadLodScreenAsync(frame);
        if (_lodDeathLines >= 0 && deathLines > _lodDeathLines)
        {
            await ResumeLodAttackAsync(frame, "the death text showed while walking", reached: true);
            return;
        }

        // Next to the boss = the inner part of its ring, or right at the feet below the belt. The
        // first Ctrl+click always goes out (user: after the first hit, Ctrl+click the ring - the skill
        // stops the character on the ring at about half its radius); this only decides a retry.
        // The belt only before the first click: after one, a missing ring is mostly the boss's body over
        // the camera with the character under the boss, and a cyan patch at its side then read as the
        // belt (live, 2026-10-27 23:20) - a click there would walk away from the boss.
        var found = FindApproachSpot(frame, useBelt: !_lodApproachRingOnly && _lodApproachTries == 0);
        if (found is { } near && _lodApproachTries > 0 && near.Away <= 0.6)
        {
            await ResumeLodAttackAsync(frame, $"next to it after {_lodApproachTries} Ctrl+click(s) ({near.What}, {near.Away:0.00} of its radius)", reached: true);
            return;
        }

        if (found == null && _lodApproachTries > 0)
        {
            await ResumeLodAttackAsync(frame, $"no ring in view after {_lodApproachTries} Ctrl+click(s) - under the boss", reached: true);
            return;
        }

        if (found is not { } target || _lodApproachTries >= lod.ApproachTries)
        {
            if (_lodApproachSnapshots++ < 3)
                DebugArtifacts.SaveFrame(frame, _overlays, "lod_approach_miss");
            await ResumeLodAttackAsync(frame, found == null
                ? "no pink ring or belt in view - attacking from here"
                : $"still not next to it after {_lodApproachTries} Ctrl+click(s) - attacking from here", reached: false);
            return;
        }

        var (spot, what, away) = target;

        // Stop attacking first (user: Shura, else Space), then take a fresh frame - the boss may move.
        if (!_lodApproachStopped)
        {
            if (!StopLodAttack(frame, lod.ApproachStopPresses, out string how))
            {
                _detail = $"{how} - waiting for the game to be in front";
                _waitUntil = now + 300;
                return;
            }

            _lodApproachStopped = true;
            _detail = $"stopped attacking ({how})";
            Log?.Invoke(LogLevel.Info, $"Stopped the attack to walk up: {how}.");
            _waitUntil = Environment.TickCount64 + 150;
            return;
        }

        // Inside the walk-click area, as the LodRun clicks: a ring low on the left would otherwise put
        // the click on the chat box, and the skill key would then type into the chat.
        int clickX = Math.Clamp(spot.CenterX, (int)(frame.Width * 0.25), (int)(frame.Width * RunClickRight));
        int clickY = Math.Clamp(spot.CenterY, (int)(frame.Height * RunClickTop), (int)(frame.Height * 0.78));
        if (!_controls.WalkTo(clickX, clickY, forceWalk: true))
        {
            _detail = "Ctrl+click blocked - waiting for the game to be in front";
            _waitUntil = now + 300;
            return;
        }

        _lodApproachTries++;
        _lodApproachStopped = false;
        _lodApproachClickAt = Environment.TickCount64;
        _lastClick = (clickX, clickY, _lodApproachClickAt);
        Log?.Invoke(LogLevel.Info, $"Ctrl+clicked the {what} at {clickX},{clickY} (character {away:0.00} of its radius out) - walking up to '{lod.BossName}' ({_lodApproachTries}/{lod.ApproachTries}).");
        if (_lodApproachTries == 1 && _lodApproachSnapshots++ < 3)
            DebugArtifacts.SaveFrame(frame, _overlays, "lod_approach", (clickX, clickY));
        _waitUntil = _lodApproachClickAt + lod.ApproachWaitMs;
    }

    /// <summary>
    /// Back to the skill spam after the walk, with a fresh "seen" timer so the walk is not taken for a
    /// lost target. The coordinates are the new death-jump baseline (the walk itself moves the
    /// character), but a jump past TeleportJump since before the walk is a death, as in LodAttack.
    /// The next ring check is RingCheckEveryMs away, 3x that when the walk did not get there
    /// (<paramref name="reached"/> false) - a ring it cannot reach would otherwise stop the attack
    /// every few seconds.
    /// </summary>
    private async Task ResumeLodAttackAsync(Mat frame, string why, bool reached)
    {
        var lod = _settings.Lod;
        var coords = await _analyzer.ReadCoordinatesAsync(frame);
        if (coords != null && _lodAttackCoords is (int ax, int ay) && coords.DistanceTo(ax, ay) > lod.TeleportJump)
        {
            Log?.Invoke(LogLevel.Warning, $"Moved to {coords} (from {ax},{ay}) while walking up to '{lod.BossName}' - killed by the boss? Turning south again from the start.");
            DebugArtifacts.SaveFrame(frame, _overlays, "lod_died");
            Advance(PilotPhase.LodRespawn, lod.RespawnSettleMs);
            return;
        }

        Log?.Invoke(LogLevel.Success, $"Walk up to '{lod.BossName}': {why} - attacking again.");
        long now = Environment.TickCount64;
        _lodBossSeenAt = now;
        _lodSkillAt = 0;
        _lodCoordsAt = now;
        _lodAttackCoords = coords == null ? null : (coords.X, coords.Y);
        _lodRingCheckAt = reached ? now : now + 2L * lod.RingCheckEveryMs;
        Advance(PilotPhase.LodAttack, 0);
    }

    /// <summary>
    /// Phone, LOD arrival: the joystick pushed up-left for <see cref="PhoneSettings.LodArrivalWalkMs"/> (user, Xiaomi
    /// 2026-10-30: "walk/run using joystick for 2s going northwest, as when teleporting in the LOD, the Boss Arena is just
    /// left of the character"). Up-left on the screen: the phone has no south turn, the camera keeps the teleport's heading.
    /// </summary>
    private void WalkUpLeftOnArrival(Mat frame, long now)
    {
        int ms = _settings.Phone.LodArrivalWalkMs;
        if (_controls is not TouchControls touch)
        {
            _lodArrivalWalkDue = false;
            return;
        }

        double reach = frame.Height * 0.2;
        int x = (int)Math.Round(frame.Width * JoystickMath.CharacterX - reach);
        int y = (int)Math.Round(frame.Height * JoystickMath.CharacterY - reach);
        if (!touch.WalkToward(x, y, ms))
        {
            _detail = "joystick walk blocked (no joystick found, or the game is not in front)";
            _waitUntil = now + 500;
            if (now - _phaseStart > 10_000)
            {
                _lodArrivalWalkDue = false;
                Log?.Invoke(LogLevel.Warning, "Could not walk up-left on arrival (no joystick found) - tabbing for the boss from here.");
            }

            return;
        }

        _lodArrivalWalkDue = false;
        Log?.Invoke(LogLevel.Info, $"Walked up-left with the joystick for {ms / 1000.0:0.#}s (the boss arena is left of the arrival spot).");
        _waitUntil = Environment.TickCount64 + 300;
    }

    /// <summary>
    /// Phone, LOD: <see cref="PhoneSettings.LodBossTapAfterTabs"/> sword taps without the boss targeted - tap the boss
    /// itself: its pink ring, else just below its belt (user, Xiaomi 2026-10-30: "if the blade doesn't select the boss,
    /// try tapping the boss, using the build of it or just below it"). True when it tapped.
    /// </summary>
    private bool TapLodBoss(Mat frame, long now)
    {
        int after = _settings.Phone.LodBossTapAfterTabs;
        if (Profile != PlatformProfile.Phone || after <= 0 || _lodBladeTaps < after)
            return false;

        _lodBladeTaps = 0;
        if (FindApproachSpot(frame, useBelt: true) is not { } found)
        {
            Log?.Invoke(LogLevel.Trace, $"{after} sword taps without '{_settings.Lod.BossName}' - no pink ring or belt in view to tap.");
            return false;
        }

        var (spot, what, _) = found;
        int x = Math.Clamp(spot.CenterX, (int)(frame.Width * 0.25), (int)(frame.Width * RunClickRight));
        int y = Math.Clamp(spot.CenterY, (int)(frame.Height * RunClickTop), (int)(frame.Height * 0.78));
        if (!_controls.Tap(x, y))
            return false;

        Log?.Invoke(LogLevel.Info, $"{after} sword taps did not target '{_settings.Lod.BossName}' - tapped the boss ({what}) at {x},{y}.");
        _waitUntil = now + 1000;
        return true;
    }

    /// <summary>Lets go of a held skill key (phase change, stop, panic).</summary>
    private void ReleaseHeldSkill()
    {
        if (_heldSkill == null)
            return;

        if (_heldAction is { } action)
            _controls.ActionUp(action);
        else
            _controls.SkillUp(_heldSkill);

        _heldSkill = null;
        _heldAction = null;
    }

    /// <summary>Back at the start after dying: wait for two equal coordinate readings, then face south.</summary>
    private async Task TickLodRespawnAsync(Mat frame)
    {
        long now = Environment.TickCount64;
        var coords = await _analyzer.ReadCoordinatesAsync(frame);
        bool still = coords != null && _lastCoords is (int lx, int ly) && coords.DistanceTo(lx, ly) < 20;
        _lastCoords = coords == null ? null : (coords.X, coords.Y);

        if (!still && now - _phaseStart < 20000)
        {
            _detail = "waiting for the character to settle after the kill";
            _waitUntil = now + 700;
            return;
        }

        BeginFaceSouth(arrival: true);
    }
}

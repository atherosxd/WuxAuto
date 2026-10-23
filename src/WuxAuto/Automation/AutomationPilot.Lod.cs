using OpenCvSharp;
using WuxAuto.Core;
using WuxAuto.Vision;
using LogLevel = WuxAuto.Core.LogLevel;

namespace WuxAuto.Automation;

/// <summary>
/// Auto LOD (user, 2026-10-21): teleport to the Ultimate Den of Rebirth, face south, walk towards
/// the boss spot pressing Tab, and once "Bai Gu Great..." is targeted spam the skill key until it is
/// dead. The boss respawns, so the map is unlimited until its timer runs out and the game sends the
/// character to town (user) - the zone guard then teleports in again. Killed by the boss, the
/// character is put back at the map's starting point: face south and walk in again.
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
    private long _lodBuffAt;
    private int _lodBuffs;
    private bool _lodHeadingUnknown;
    private int _lodDeathLines = -1;
    private long _lodLootAt;

    private bool IsLod => _settings.Sequence.Mode == RunMode.AutoLod;

    /// <summary>Teleport list entry and minimap zone for the selected mode.</summary>
    private string Destination => IsLod ? _settings.Lod.Destination : _settings.Sequence.DestinationText;

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
    private bool TryLodBuff(long now, bool force, bool attacking = false)
    {
        var lod = _settings.Lod;
        if (string.IsNullOrWhiteSpace(lod.BuffKey) || (!force && now - _lodBuffAt < lod.BuffEveryMs))
            return false;

        ReleaseHeldSkill();

        // Mid-fight the buff is ignored until the attack stops (user): Space a few times first.
        if (attacking && !string.IsNullOrWhiteSpace(lod.StopAttackKey))
        {
            for (int i = 0; i < lod.StopAttackPresses; i++)
            {
                if (!_input.KeyTap(lod.StopAttackKey))
                    return false;
                Thread.Sleep(100);
            }

            Thread.Sleep(400);
        }

        if (!_input.KeyTap(lod.BuffKey))
            return false;

        _lodBuffAt = now;
        _lodBuffs++;
        Log?.Invoke(LogLevel.Info, attacking
            ? $"Re-buffed ('{lod.StopAttackKey}' x{lod.StopAttackPresses}, then '{lod.BuffKey}') mid-fight - buff {_lodBuffs}."
            : $"Buffed ('{lod.BuffKey}') - buff {_lodBuffs}; again in {lod.BuffEveryMs / 1000}s.");
        return true;
    }

    /// <summary>
    /// The dungeon keys in the item status list (bottom right, user): "[Temple of Fiends Key] N" =
    /// Labyrinth keys, "[Den of Rebirth] N" = LOD entries; gone = run out. The chosen mode out of keys
    /// -> the other mode; both out -> J in town (the game's auto war, user) and stop. OCR is noisy
    /// ("[TemPléof FiendS Key]", "[Denpf Rebirth]") and skips lines now and then, so: keywords without
    /// accents, three scales, a key counts as there if any read sees it. Returns true when it
    /// handled the turn (switched or stopped). A hidden list (no lines) decides nothing.
    /// </summary>
    private async Task<bool> CheckKeysAsync(Mat frame)
    {
        if (!_settings.Sequence.CheckKeys)
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
        BeginAutoWar("out of Temple of Fiends Keys and Den of Rebirth entries");
        return true;

        static string Plain(string t) => new string(t.Normalize(System.Text.NormalizationForm.FormD)
            .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
            .Where(char.IsLetter).Select(char.ToLowerInvariant).ToArray());
    }

    /// <summary>After the arrival zoom (or a respawn): buff, face south, then the walk.</summary>
    private void BeginFaceSouth(bool buff = true)
    {
        // Buffs first (user: "upon teleporting, do the auto buff then the other"); a blocked key is
        // caught by the timer in the walk.
        if (buff)
            TryLodBuff(Environment.TickCount64, force: true);
        _faceSouth = true;
        _earlyNorth = false;
        _compassAnchored = false;
        _bestFlashlightScore = 0;
        _bestFlashlightStep = 0;
        _tapsSinceBest.Clear();
        _refineDir = 0;
        Log?.Invoke(LogLevel.Info, "Turning to face south for the walk to the boss.");
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
        _lodStill = 0;
        _lodWaitLogged = false;
        return PilotPhase.LodRun;
    }

    /// <summary>The boss name on screen (the target bar shows it after Tab).</summary>
    private async Task<TextHit?> FindLodBossAsync(Mat frame) => (await ReadLodScreenAsync(frame)).Boss;

    /// <summary>One whole-screen OCR: the boss name, and how many lines read like the death text.</summary>
    private async Task<(TextHit? Boss, int DeathLines)> ReadLodScreenAsync(Mat frame)
    {
        var (boss, _, deathLines) = await ReadLodScreenFullAsync(frame);
        return (boss, deathLines);
    }

    /// <summary>
    /// One whole-screen OCR: the boss (= targeted, user: "the boss name or the treasure icon means it
    /// is already targeted") and the death-text line count. The name counts by whole OCR line - the
    /// best hit is sometimes a single word ("[LOD]") that is too short to pass as the name, and the
    /// pilot then walked or turned with the boss right there (user). Without a readable name the
    /// target bar's treasure chest (monstericon: 0.90 at the top centre on targeted frames, not found
    /// otherwise) counts too.
    /// </summary>
    private async Task<(TextHit? Boss, bool Targeted, int DeathLines)> ReadLodScreenFullAsync(Mat frame)
    {
        var lod = _settings.Lod;
        if (string.IsNullOrWhiteSpace(lod.BossName))
            return (null, false, 0);

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

        return (boss, boss != null, deathLines);
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
        _lodBossSeenAt = Environment.TickCount64;
        _lodSkillAt = 0;
        _lodCoordsAt = 0;
        _lodAttackCoords = null;
        _lodDeathLines = -1;
        Log?.Invoke(LogLevel.Success, $"'{boss.Text}' in view while turning - stopped the turn, spamming skill '{_settings.Dungeon.SkillKey}'.");
        Advance(PilotPhase.LodAttack, 0);
        return true;
    }

    /// <summary>
    /// After the kill (death text or the target gone LootAfterMissingMs): spam the loot key for LootMs
    /// (at least 10 s, user), then look for the respawn. No Tab here (user) - it only starts again
    /// once the loot is over.
    /// </summary>
    private void TickLodLoot()
    {
        var lod = _settings.Lod;
        long now = Environment.TickCount64;
        if (now - _phaseStart >= lod.LootMs || string.IsNullOrWhiteSpace(lod.LootKey))
        {
            Log?.Invoke(LogLevel.Info, $"Looted for {lod.LootMs / 1000}s - waiting for '{lod.BossName}' to respawn.");
            LeaveLodAttack();
            return;
        }

        if (now - _lodLootAt >= 150 && _input.KeyTap(lod.LootKey))
            _lodLootAt = now;
        _detail = $"looting ('{lod.LootKey}') {(now - _phaseStart) / 1000.0:0.0}s";
        _waitUntil = now + 60;
    }

    /// <summary>Back to finding the boss: the walk/Tab, or the south turn first if the heading is unknown.</summary>
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
        if (now - _lodTabAt < everyMs || string.IsNullOrWhiteSpace(_settings.Dungeon.TabKey))
            return false;
        if (!_input.KeyTap(_settings.Dungeon.TabKey))
            return false;
        _lodTabAt = now;
        return true;
    }

    /// <summary>Walk towards the boss spot, Tab every TabEveryMs; the boss name ends the walk.</summary>
    private async Task TickLodRunAsync(Mat frame)
    {
        var lod = _settings.Lod;
        long now = Environment.TickCount64;

        if (TryLodBuff(now, force: false))
        {
            _waitUntil = now + 700;
            return;
        }

        if (TapLodTab(now, lod.TabEveryMs))
        {
            _waitUntil = now + 250;
            return;
        }

        var boss = await FindLodBossAsync(frame);
        if (boss != null)
        {
            Log?.Invoke(LogLevel.Success, $"'{boss.Text}' targeted - spamming skill '{_settings.Dungeon.SkillKey}' ({_lodKills} kill(s) so far).");
            _lodDeathLines = -1;
            _lodBossSeenAt = now;
            _lodSkillAt = 0;
            _lodCoordsAt = 0;
            _lodAttackCoords = null;
            Advance(PilotPhase.LodAttack, 0);
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
        if (!_input.Click(clickX, clickY))
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
    /// it). A jump in the coordinates = the boss killed us and we are back at the start: walk in
    /// again. The name gone for BossGoneMs (Tab pressed meanwhile) = lost target: walk/Tab again.
    /// </summary>
    private async Task TickLodAttackAsync(Mat frame)
    {
        var lod = _settings.Lod;
        string skill = _settings.Dungeon.SkillKey;
        long now = Environment.TickCount64;

        // Re-buff on the timer, also mid-fight: stop attacking first (Space x5), the skill resumes
        // on the next tick.
        if (TryLodBuff(now, force: false, attacking: true))
        {
            _waitUntil = now + 700;
            return;
        }

        if (!string.IsNullOrWhiteSpace(skill))
        {
            if (lod.HoldSkill)
            {
                // Held down for the whole fight; ReleaseHeldSkill lets go on every way out.
                if (_heldSkill == null && _input.KeyDown(skill))
                    _heldSkill = skill;
            }
            else if (now - _lodSkillAt >= lod.SkillEveryMs && _input.KeyTap(skill))
            {
                _lodSkillAt = now;
            }
        }

        var (boss, _, deathLines) = await ReadLodScreenFullAsync(frame);

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
            _lodLootAt = 0;
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
            _lodBossSeenAt = now;
        else if (string.IsNullOrWhiteSpace(lod.LootKey) && now - _lodBossSeenAt > 1500)
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
                    Log?.Invoke(LogLevel.Warning, $"Moved to {coords} (from {ax},{ay}) - killed by the boss? Walking back in.");
                    DebugArtifacts.SaveFrame(frame, _overlays, "lod_died");
                    Advance(PilotPhase.LodRespawn, _settings.Lod.RespawnSettleMs);
                    return;
                }

                _lodAttackCoords = (coords.X, coords.Y);
            }
        }

        // Name / treasure icon gone 2 s: the boss is dead (user) - loot, even without the death text.
        if (now - _lodBossSeenAt > lod.LootAfterMissingMs && !string.IsNullOrWhiteSpace(lod.LootKey))
        {
            ReleaseHeldSkill();
            Log?.Invoke(LogLevel.Info, $"'{lod.BossName}' / treasure icon gone for {lod.LootAfterMissingMs / 1000.0:0.#}s - looting with '{lod.LootKey}' for {lod.LootMs / 1000}s, then looking for it again.");
            _lodLootAt = 0;
            Advance(PilotPhase.LodLoot, 0);
            return;
        }

        if (now - _lodBossSeenAt > lod.BossGoneMs)
        {
            Log?.Invoke(LogLevel.Warning, $"'{lod.BossName}' not seen for {lod.BossGoneMs / 1000}s - walking and tabbing again.");
            LeaveLodAttack();
            return;
        }

        _detail = $"spamming '{skill}' on '{lod.BossName}' (seen {(now - _lodBossSeenAt) / 1000.0:0.0}s ago)";
        _waitUntil = now + Math.Min(lod.SkillEveryMs, 300);
    }

    /// <summary>Lets go of a held skill key (phase change, stop, panic).</summary>
    private void ReleaseHeldSkill()
    {
        if (_heldSkill == null)
            return;

        _input.KeyUp(_heldSkill);
        _heldSkill = null;
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

        BeginFaceSouth();
    }
}

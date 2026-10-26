using System.IO;
using OpenCvSharp;
using WuxAuto.Core;
using WuxAuto.Vision;
using LogLevel = WuxAuto.Core.LogLevel;

namespace WuxAuto.Automation;

/// <summary>
/// Kill screenshots (user, 2026-10-24: "to check whether it looted or not"): one when the pilot
/// sees the boss die, and a second one when it moves on - the end of the LOD loot, or in the
/// Labyrinth <see cref="KillShotAfterMs"/> later, or earlier, right before the map or the
/// inventory opens over the view. Saved to <see cref="AppPaths.KillsFolder"/>.
/// Kill alert (user, 2026-10-26): <see cref="BossKilled"/> at the same moments - a Labyrinth
/// boss's death, the end of an LOD kill's loot step - for the chime and the Windows notification.
/// </summary>
public sealed partial class AutomationPilot
{
    /// <summary>Labyrinth: the second shot, at the time the map is clicked (GateToMapDelayMs, 3 s).</summary>
    private const int KillShotAfterMs = 3000;

    private string? _killShotTag;
    private long _killShotDueAt;

    /// <summary>
    /// A boss died (Labyrinth, every stage) or an LOD kill's loot step ended - "Stage 2: Spectral
    /// down". Raised on the loop thread.
    /// </summary>
    public event Action<string>? BossKilled;

    /// <summary>Screenshot of the kill now, and the second one (<paramref name="afterTag"/>) due in <paramref name="afterMs"/>.</summary>
    private void SaveKillShot(Mat frame, string tag, string afterTag, int afterMs = KillShotAfterMs)
    {
        // A shot still due from the previous kill is taken first.
        TakePendingKillShot(frame);
        WriteKillShot(frame, tag);
        _killShotTag = afterTag;
        _killShotDueAt = Environment.TickCount64 + afterMs;
    }

    /// <summary>
    /// A Labyrinth boss died (gate banner, or its name gone): the kill alert, and the screenshots
    /// "laby_s2_Spectral", then "..._after".
    /// </summary>
    private void OnLabyBossDead(Mat frame)
    {
        string name = StageName(_stageIndex);
        BossKilled?.Invoke($"Stage {_stageIndex + 1}: {name} down");
        string tag = $"laby_s{_stageIndex + 1}_{Sanitize(name)}";
        SaveKillShot(frame, tag, $"{tag}_after");
    }

    /// <summary>
    /// The LOD boss died: "lod_kill3" (death text) or "lod_gone" (target gone), then "..._looted" when
    /// the loot step ends (<see cref="TickLodLoot"/>); the timer is only the fallback.
    /// </summary>
    private void SaveLodKillShot(Mat frame, string tag)
        => SaveKillShot(frame, tag, $"{tag}_looted", _settings.Lod.LootMs + 1000);

    /// <summary>The second shot now, if one is pending.</summary>
    private void TakePendingKillShot(Mat frame)
    {
        if (_killShotTag == null)
            return;

        WriteKillShot(frame, _killShotTag);
        _killShotTag = null;
    }

    /// <summary>
    /// Called every tick: the second shot once due, or as soon as the map / inventory is about to
    /// open over the view (the phase is ready to act). The timer only fires in a dungeon phase (the
    /// zone guard has seen the dungeon), never on the login screens after a disconnect; every
    /// restart drops a pending shot (<see cref="ResetRunState"/>).
    /// </summary>
    private void TickKillShot(Mat frame, long now)
    {
        if (_killShotTag == null)
            return;

        bool due = now >= _killShotDueAt && IsLabyrinthPhase(_phase);
        bool windowOpening = now >= _waitUntil && (_phase is PilotPhase.ClickMap or PilotPhase.OpenInventory);
        if (due || windowOpening)
            TakePendingKillShot(frame);
    }

    private void WriteKillShot(Mat frame, string tag)
    {
        string file = KillScreenshots.Save(frame, tag);
        Log?.Invoke(file.Length > 0 ? LogLevel.Trace : LogLevel.Warning,
            file.Length > 0 ? $"Kill screenshot: {Path.GetFileName(file)}" : $"Kill screenshot '{tag}' could not be saved.");
    }
}

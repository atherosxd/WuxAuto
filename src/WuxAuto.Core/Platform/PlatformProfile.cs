namespace WuxAuto.Core;

/// <summary>What differs between the platforms the pilot runs on; the pilot reads these instead of asking which OS it is.</summary>
public sealed record PlatformProfile(
    bool TeleportViaShortcut,      // phone: tap the Teleport Scroll shortcut; desktop: I + scroll right-click
    bool TargetShowsRing,          // phone: TargetNearest selects with the ring
    bool KeyCountsInDurations,     // phone: menu -> Durations (M6)
    bool ShortcutCheck,            // phone: check/add ring shortcuts on Start
    bool SupportsCtrlSkill,        // desktop only
    bool SupportsAutoConfirm)      // desktop only
{
    public static PlatformProfile Desktop { get; } = new(false, false, false, false, true, true);
    public static PlatformProfile Phone { get; } = new(true, true, true, true, false, false);
}

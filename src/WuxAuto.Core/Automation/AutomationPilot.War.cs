namespace WuxAuto.Automation;

public sealed partial class AutomationPilot
{
    /// <summary>True while Start runs the tribe war (<see cref="WuxAuto.Core.RunMode.AutoWar"/>).</summary>
    private bool IsWar => _settings.Sequence.Mode == WuxAuto.Core.RunMode.AutoWar;
}

using OpenCvSharp;

namespace WuxAuto.Automation;

public sealed partial class AutomationPilot
{
    // The teleport phases page to the caves' page for Start's check instead of clicking an entry.
    private bool _warListCheck;

    // Map pick (ClickMap / ReadStage / AdvanceStage) for Auto War: at Start the targeted monster is only
    // checked against the map's monster list (_warMapVerify); the map and monster found there wait in
    // the pending fields until the remember step saves them.
    private bool _warMapVerify;
    private string? _warPendingMonster;
    private string? _warPendingMap;
#pragma warning disable CS0414 // set by the map pick, read by the remember step (TickWarRememberAsync) once it is built - drop the pragma then
    private bool _warMonsterConfirmed;
#pragma warning restore CS0414

    /// <summary>True while Start runs the tribe war (<see cref="WuxAuto.Core.RunMode.AutoWar"/>).</summary>
    private bool IsWar => _settings.Sequence.Mode == WuxAuto.Core.RunMode.AutoWar;

    /// <summary>The cave to go back to: the one being remembered at Start, else the saved one.</summary>
    private string WarHomeMap => _warPendingMap ?? _settings.War.HomeMap;

    // stubs - replaced by Tasks 6-8
    private Task TickWarListCheckAsync(Mat frame)
    {
        Fail(null, "Auto War step not built yet: the teleport list check");
        return Task.CompletedTask;
    }

    private void BeginWarReturn(Mat? frame, string why) => Fail(null, $"Auto War step not built yet: {why}");

    private void WarStop(Mat? frame, string why) => Fail(null, $"Auto War step not built yet: {why}");

    private void BeginWarJoin(string why) => Fail(null, $"Auto War step not built yet: {why}");

    private void BeginWarEnter(string why) => Fail(null, $"Auto War step not built yet: {why}");
}

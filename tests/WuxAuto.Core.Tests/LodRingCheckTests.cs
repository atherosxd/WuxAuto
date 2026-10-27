using OpenCvSharp;
using WuxAuto.Automation;
using WuxAuto.Core;

namespace WuxAuto.Core.Tests;

/// <summary>
/// Auto LOD's walk up to the boss and the ring check (user, 2026-10-27: "try to go to the ring as
/// much as possible, do a periodical check"): a pink ring drawn on a blank 1440x900 frame, the boss in
/// the target bar and a combat log that gains a hit with every skill tap. The frame never changes, so
/// a ring away from the character stays away after every Ctrl+click.
/// </summary>
[Collection("AppPaths")] // AppPaths is static state
public class LodRingCheckTests
{
    private const int Width = 1440;
    private const int Height = 900;

    // The character stands at (0.5 w, 0.54 h) = (720, 486).
    private static readonly Point FarRing = new(500, 300);
    private static readonly Point RingUnderCharacter = new(720, 486);

    [Fact]
    public async Task RingAway_AfterTheFirstWalkUp_TheRingCheckWalksBackToIt()
    {
        var (settings, input, ocr) = Harness(ringCheckEveryMs: 200);
        using var frame = Frame(FarRing);

        var run = await PilotRun.RunAsync(settings, input, ocr, frame, PilotPhase.LodRun,
            (_, lines) => lines.Any(l => l.StartsWith("Ring check 1", StringComparison.Ordinal)) && CtrlClicks(input) >= 3);

        string log = string.Join("\n", run.Lines);
        Assert.Contains(run.Lines, l => l.StartsWith("First hit on", StringComparison.Ordinal));
        Assert.Contains(run.Lines, l => l.Contains("still not next to it after 2 Ctrl+click(s)", StringComparison.Ordinal));
        Assert.Contains(run.Lines, l => l.StartsWith("Ring check 1: off the pink ring", StringComparison.Ordinal));
        Assert.True(CtrlClicks(input) >= 3, "expected a third Ctrl+click from the ring check:\n" + log);

        // Every Ctrl+click goes to the ring's centre, and Shura (its key, no icon here) comes first.
        var clicks = input.Calls.Select((call, i) => (call, i)).Where(c => c.call.StartsWith("CtrlClick", StringComparison.Ordinal)).ToList();
        Assert.All(clicks, c => Assert.Equal($"CtrlClick {FarRing.X},{FarRing.Y}", c.call));
        Assert.All(clicks, c => Assert.Equal("KeyTap E", input.Calls[c.i - 1]));
    }

    [Fact]
    public async Task RingUnderTheCharacter_NoWalkAfterTheFirst()
    {
        var (settings, input, ocr) = Harness(ringCheckEveryMs: 200);
        using var frame = Frame(RingUnderCharacter);
        long resumedAt = 0;

        var run = await PilotRun.RunAsync(settings, input, ocr, frame, PilotPhase.LodRun, (_, lines) =>
        {
            if (resumedAt == 0 && lines.Any(l => l.Contains("attacking again", StringComparison.Ordinal)))
                resumedAt = Environment.TickCount64;
            return resumedAt > 0 && Environment.TickCount64 - resumedAt > 1500; // ~7 ring checks
        });

        string log = string.Join("\n", run.Lines);
        Assert.True(resumedAt > 0, "the walk up never ended:\n" + log);
        Assert.Contains(run.Lines, l => l.Contains("next to it after 1 Ctrl+click(s)", StringComparison.Ordinal));
        Assert.Equal(1, CtrlClicks(input)); // the first walk up's click always goes out
        Assert.DoesNotContain(run.Lines, l => l.StartsWith("Ring check", StringComparison.Ordinal));
        Assert.Equal(PilotPhase.LodAttack, run.Phase);
    }

    [Fact]
    public async Task RingCheckOff_OnlyTheFirstWalkUp()
    {
        var (settings, input, ocr) = Harness(ringCheckEveryMs: 0);
        using var frame = Frame(FarRing);
        long resumedAt = 0;

        var run = await PilotRun.RunAsync(settings, input, ocr, frame, PilotPhase.LodRun, (_, lines) =>
        {
            if (resumedAt == 0 && lines.Any(l => l.Contains("attacking again", StringComparison.Ordinal)))
                resumedAt = Environment.TickCount64;
            return resumedAt > 0 && Environment.TickCount64 - resumedAt > 1500;
        });

        Assert.True(resumedAt > 0, "the walk up never ended:\n" + string.Join("\n", run.Lines));
        Assert.Equal(settings.Lod.ApproachTries, CtrlClicks(input));
        Assert.DoesNotContain(run.Lines, l => l.StartsWith("Ring check", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RingGoneAfterTheClick_CountsAsUnderTheBoss()
    {
        // Live, 2026-10-27 23:20: after the Ctrl+click the camera was inside the boss's body - no ring,
        // and a cyan patch at the boss's side read as its belt. Neither may send a second click.
        var (settings, input, ocr) = Harness(ringCheckEveryMs: 200);
        using var frame = Frame(FarRing);
        bool swapped = false;

        var run = await PilotRun.RunAsync(settings, input, ocr, frame, PilotPhase.LodRun, (_, lines) =>
        {
            if (!swapped && CtrlClicks(input) >= 1)
            {
                swapped = true;
                frame.SetTo(Scalar.All(0));
                Cv2.Rectangle(frame, new Rect(300, 300, 20, 200), new Scalar(255, 255, 0, 255), -1);
            }

            return lines.Any(l => l.Contains("attacking again", StringComparison.Ordinal));
        });

        Assert.Contains(run.Lines, l => l.Contains("no ring in view after 1 Ctrl+click(s) - under the boss", StringComparison.Ordinal));
        Assert.Equal(1, CtrlClicks(input));
    }

    [Fact]
    public async Task RingCheck_IgnoresTheBelt()
    {
        // Live, 23:20: with the ring hidden under the boss a cyan patch at its side read as the belt.
        var (settings, input, ocr) = Harness(ringCheckEveryMs: 200);
        using var frame = Frame(RingUnderCharacter);
        long resumedAt = 0;

        var run = await PilotRun.RunAsync(settings, input, ocr, frame, PilotPhase.LodRun, (_, lines) =>
        {
            if (resumedAt == 0 && lines.Any(l => l.Contains("attacking again", StringComparison.Ordinal)))
            {
                resumedAt = Environment.TickCount64;
                frame.SetTo(Scalar.All(0));
                Cv2.Rectangle(frame, new Rect(300, 300, 20, 200), new Scalar(255, 255, 0, 255), -1); // a tall cyan "belt" far left
            }

            return resumedAt > 0 && Environment.TickCount64 - resumedAt > 1500;
        });

        Assert.True(resumedAt > 0, "the walk up never ended:\n" + string.Join("\n", run.Lines));
        Assert.Equal(1, CtrlClicks(input));
        Assert.DoesNotContain(run.Lines, l => l.StartsWith("Ring check", StringComparison.Ordinal));
    }

    private static int CtrlClicks(FakeGameInput input) => input.Calls.Count(c => c.StartsWith("CtrlClick", StringComparison.Ordinal));

    /// <summary>Black frame with a pink ring (H 155, S 135, V 255 - inside FindBossRing's band) round <paramref name="centre"/>.</summary>
    private static Mat Frame(Point centre)
    {
        var frame = new Mat(Height, Width, MatType.CV_8UC4, Scalar.All(0));
        Cv2.Ellipse(frame, centre, new Size(110, 40), 0, 0, 360, new Scalar(230, 120, 255, 255), 8);
        return frame;
    }

    private static (AppSettings Settings, FakeGameInput Input, FakeOcrEngine Ocr) Harness(int ringCheckEveryMs)
    {
        var settings = new AppSettings();
        settings.Sequence.Mode = RunMode.AutoLod;
        settings.Lod.BuffKey = string.Empty;
        settings.Lod.TabEveryMs = 500;
        settings.Lod.ApproachWaitMs = 100;
        settings.Lod.ShuraPresses = 1;
        settings.Lod.ShuraEveryMs = 10;
        settings.Lod.RingCheckEveryMs = ringCheckEveryMs;

        var input = new FakeGameInput();
        var ocr = new FakeOcrEngine();
        ocr.Scene = () =>
        {
            int taps = input.Calls.Count(c => c == "KeyTap " + settings.Dungeon.SkillKey);
            return
            [
                FakeOcrEngine.Line("[Ultimate Den of Rebirth]", 1000, 10),
                FakeOcrEngine.Line("1000 , 2000", 1100, 200, 120),
                FakeOcrEngine.Line("[LOD] " + settings.Lod.BossName, 640, 50),
                // the combat log (left 40 %): 8 lines, a new hit scrolls in with every skill tap
                .. Enumerable.Range(0, 8).Select(i =>
                    FakeOcrEngine.Line($"Attack succeeded!Normal damage(+{100 + i + taps}) Attribu", 20, 200 + i * 18, 300)),
            ];
        };
        return (settings, input, ocr);
    }
}

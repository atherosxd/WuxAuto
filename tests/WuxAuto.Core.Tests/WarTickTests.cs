using System.Drawing;
using Emgu.CV;
using Emgu.CV.Structure;
using WuxAuto.Automation;
using WuxAuto.Core;
using WuxAuto.Vision;

namespace WuxAuto.Core.Tests;

/// <summary>
/// Auto War phases ticked on a blank 1920x1080 frame with a scripted OCR scene (<see cref="PilotRun"/>): the trip back's map
/// pick by the row's whole name, and the war dialog joined only with the game in front.
/// </summary>
[Collection("AppPaths")] // AppPaths is static state
public class WarTickTests
{
    private const int Width = 1920;
    private const int Height = 1080;
    private const string Cave = "[Dark Soul Cave 1F (WD)]";

    // ---------------------------------------------------------------- the map pick on the trip back

    [Fact]
    public async Task MapPick_Goblin_ClicksTheGoblinRow_NotGoblinElite()
    {
        var (settings, input, ocr) = MapHarness("Goblin Elite 145 25", "Goblin 140 30");

        var run = await RunMapPickAsync(settings, input, ocr, done: () => input.Clicks > 0);

        // The row's name click (it highlights the row; its '>' is the next step): the Goblin row, at y 340.
        Assert.True(input.Calls.FirstOrDefault() == $"Click {RowX},{RowY(1)}",
            $"expected the Goblin row's click first:\n{string.Join("\n", input.Calls)}\nlog:\n{string.Join("\n", run.Lines)}");
        Assert.Contains(run.Lines, l => l.StartsWith("Map shows 'Goblin' (row 'Goblin 140 30'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MapPick_TwoRowsOfTheMonster_StopsWithoutAClick()
    {
        var (settings, input, ocr) = MapHarness("Goblin 140 30", "Goblin Elite 145 25", "Goblin 150 20");

        var run = await RunMapPickAsync(settings, input, ocr, done: () => false);

        Assert.Equal(0, input.Clicks);
        Assert.Contains(run.Lines, l => l.StartsWith("'Goblin' fits 2 rows of this map's monster list ('Goblin 140 30', 'Goblin 150 20')", StringComparison.Ordinal));
        Assert.Contains(run.Lines, l => l.StartsWith("Pilot stopped:", StringComparison.Ordinal));
    }

    private const int RowX = 1250 + 200;     // the centre of a 400 px wide row line at x 1250
    private static int RowY(int row) => 300 + 40 * row + 8;

    private static (AppSettings Settings, FakeGameInput Input, FakeOcrEngine Ocr) MapHarness(params string[] rows)
    {
        var settings = War();
        var input = new FakeGameInput();
        var ocr = new FakeOcrEngine();
        ocr.Scene = () =>
        {
            var words = new List<OcrWord>
            {
                FakeOcrEngine.Line(Cave, 1500, 10, 300, 16),        // minimap title (Regions.Minimap)
                FakeOcrEngine.Line("Spawns", 1700, 220, 70, 14),    // the list's header (Regions.MapScroll): the map is open
            };
            // Regions.MapBossList, word by word as the real engine reads it: "Goblin" alone is a word of "Goblin Elite".
            for (int i = 0; i < rows.Length; i++)
                words.AddRange(FakeOcrEngine.Words(rows[i], 1250, 300 + 40 * i, 400, 16));
            return words;
        };
        return (settings, input, ocr);
    }

    /// <summary>The trip back's map pick (ReadStage, the saved monster "Goblin"); a '>' drawn on every row shows the map is open.</summary>
    private static async Task<(PilotPhase Phase, List<string> Lines)> RunMapPickAsync(AppSettings settings, FakeGameInput input,
        FakeOcrEngine ocr, Func<bool> done)
    {
        using var frame = Blank.Bgra(Height, Width);
        for (int i = 0; i < 3; i++)
            DrawChevron(frame, 1800, 300 + 40 * i);

        return await PilotRun.RunAsync(settings, input, ocr, frame, PilotPhase.ReadStage, (_, _) => done(),
            assets: folder =>
            {
                using var chevron = Blank.Gray(18, 14);
                DrawChevron(chevron, 0, 0);
                CvInvoke.Imwrite(Path.Combine(folder, "rightchevron.png"), chevron);
            });
    }

    /// <summary>A white '&gt;' in a 14x18 box at (x, y).</summary>
    private static void DrawChevron(Mat image, int x, int y)
    {
        var white = new MCvScalar(255, 255, 255, 255);
        CvInvoke.Line(image, new Point(x + 3, y + 3), new Point(x + 10, y + 9), white, 2);
        CvInvoke.Line(image, new Point(x + 10, y + 9), new Point(x + 3, y + 15), white, 2);
    }

    // ---------------------------------------------------------------- joining the war

    [Fact]
    public async Task Join_BehindAnotherWindow_ReadsAndSendsNothing_ThenJoinsMidTimerRead()
    {
        var settings = War();
        var input = new FakeGameInput { InFront = false };
        var ocr = new FakeOcrEngine();
        ocr.Scene = () =>
        {
            var words = new List<OcrWord>
            {
                FakeOcrEngine.Line(Cave, 1500, 10, 300, 16),
                FakeOcrEngine.Line("Attack succeeded!Normal damage(+50)", 20, 600, 400, 16),      // the combat log (Regions.AttackMessage)
                // The war dialog, up all along (a second game client in front would show one too).
                FakeOcrEngine.Line("[Bohai Outpost (Adept)] Confirm to move?", 800, 500, 400, 17),
                FakeOcrEngine.Line("OK", 930, 580, 60, 17),
            };
            if (input.Calls.Contains("KeyTap J"))
                words.Add(FakeOcrEngine.Line("Auto hunt ended", 20, 640, 300, 16));
            return words;
        };

        using var frame = Blank.Bgra(Height, Width);
        long start = Environment.TickCount64;
        int readsBehind = -1, inputsBehind = -1;
        var run = await PilotRun.RunAsync(settings, input, ocr, frame, PilotPhase.WarWait, (pilot, _) =>
        {
            if (!input.InFront && Environment.TickCount64 - start > 1500)
            {
                readsBehind = ocr.Reads;
                inputsBehind = input.Calls.Count;
                input.InFront = true;
            }

            return pilot.Phase == PilotPhase.WarEnter;
        });

        string log = "\ninputs:\n" + string.Join("\n", input.Calls) + "\nlog:\n" + string.Join("\n", run.Lines);
        Assert.True(readsBehind != -1, "it joined the war while the game was behind another window" + log);
        Assert.True(readsBehind == 0, $"{readsBehind} OCR read(s) with the game behind another window" + log);
        Assert.True(inputsBehind == 0, $"{inputsBehind} input(s) with the game behind another window" + log);
        Assert.True(run.Phase == PilotPhase.WarEnter, $"phase {run.Phase}" + log);
        // The timer read had begun (step 1, the menu button) when the dialog was seen: the join takes over.
        Assert.Contains(run.Lines, l => l.StartsWith("The game is behind another window - WarWait holds", StringComparison.Ordinal));
        Assert.Contains(run.Lines, l => l.StartsWith("Joining the Bohai Outpost war (the war dialog is up)", StringComparison.Ordinal));
        int j = input.Calls.IndexOf("KeyTap J");
        int ok = input.Calls.IndexOf("Click 960,588");
        Assert.True(j >= 0 && ok > j, "expected J (autohunt off), then OK on the dialog" + log);
    }

    /// <summary>Auto War with a saved spot; the extra keys of the War phases left out of these tests.</summary>
    private static AppSettings War()
    {
        var settings = new AppSettings();
        settings.Sequence.Mode = RunMode.AutoWar;
        settings.War.HomeMap = Cave;
        settings.War.HomeMonster = "Goblin";
        return settings;
    }
}

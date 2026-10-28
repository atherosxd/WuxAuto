using OpenCvSharp;
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
    private const string Bohai = "[Bohai Outpost (Adept)]";

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
        using var frame = new Mat(Height, Width, MatType.CV_8UC4, Scalar.All(0));
        for (int i = 0; i < 3; i++)
            DrawChevron(frame, 1800, 300 + 40 * i);

        return await PilotRun.RunAsync(settings, input, ocr, frame, PilotPhase.ReadStage, (_, _) => done(),
            assets: folder =>
            {
                using var chevron = new Mat(18, 14, MatType.CV_8UC1, Scalar.All(0));
                DrawChevron(chevron, 0, 0);
                Cv2.ImWrite(Path.Combine(folder, "rightchevron.png"), chevron);
            });
    }

    /// <summary>A white '&gt;' in a 14x18 box at (x, y).</summary>
    private static void DrawChevron(Mat image, int x, int y)
    {
        var white = Scalar.All(255);
        Cv2.Line(image, new Point(x + 3, y + 3), new Point(x + 10, y + 9), white, 2);
        Cv2.Line(image, new Point(x + 10, y + 9), new Point(x + 3, y + 15), white, 2);
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

        using var frame = new Mat(Height, Width, MatType.CV_8UC4, Scalar.All(0));
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

    // ---------------------------------------------------------------- Start: optional buffs and Shura

    [Fact]
    public async Task Start_InACave_BuffIconsMissing_WarnsWithEachScore_AndCarriesOn()
    {
        var settings = War();
        settings.War.HomeMap = "";
        settings.Lod.ShuraKey = "";       // Shura is optional too: not looked for without its key
        var input = new FakeGameInput();
        var ocr = new FakeOcrEngine { Scene = () => [FakeOcrEngine.Line(Cave, 1500, 10, 300, 16)] };

        using var frame = new Mat(Height, Width, MatType.CV_8UC4, Scalar.All(0));
        var run = await PilotRun.RunAsync(settings, input, ocr, frame, PilotPhase.CheckZone,
            (pilot, _) => pilot.Phase == PilotPhase.OpenInventory);

        string log = "\nlog:\n" + string.Join("\n", run.Lines);
        Assert.True(run.Phase == PilotPhase.OpenInventory, $"phase {run.Phase}" + log);
        string? warning = run.Lines.FirstOrDefault(l => l.StartsWith("Not on the skill bar", StringComparison.Ordinal));
        Assert.True(warning != null, "no warning about the missing icons" + log);
        Assert.Contains("warbuff1 (not seen)", warning);
        Assert.Contains("warweapon2 (not seen)", warning);
        Assert.Contains("waritem2 (not seen)", warning);
        Assert.DoesNotContain("shura", warning);
        Assert.DoesNotContain(run.Lines, l => l.StartsWith("Pilot stopped:", StringComparison.Ordinal) && l != "Pilot stopped: test over.");
    }

    [Fact]
    public async Task Start_InBohai_NoCaveSaved_NoShuraKey_SaysSoAndGoesIn()
    {
        var settings = War();
        settings.War.HomeMap = "";
        settings.War.HomeMonster = "";
        settings.Lod.ShuraKey = "";
        var input = new FakeGameInput();
        var ocr = new FakeOcrEngine { Scene = () => [FakeOcrEngine.Line(Bohai, 1500, 10, 300, 16)] };

        using var frame = new Mat(Height, Width, MatType.CV_8UC4, Scalar.All(0));
        var run = await PilotRun.RunAsync(settings, input, ocr, frame, PilotPhase.CheckZone,
            (pilot, _) => pilot.Phase == PilotPhase.WarEnter);

        string log = "\nlog:\n" + string.Join("\n", run.Lines);
        Assert.True(run.Phase == PilotPhase.WarEnter, $"phase {run.Phase}" + log);
        Assert.Single(run.Lines, l => l == "No cave saved yet - after this war the pilot stops. Start it once inside the cave to remember it.");
        Assert.DoesNotContain(run.Lines, l => l.Contains("skill bar", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- buffing and the fight cycle

    private const int XBuffX = 800, ItemX = 1000, IconY = 1000, IconSide = 40;

    [Fact]
    public async Task Autobuff_ThenTheItemOnTheBar_TheXBuff_NoShuraKey_NoHold()
    {
        var settings = War();
        settings.Lod.ShuraKey = "";
        var (input, run) = await RunBuffAndFightAsync(settings);

        string log = Log(input, run.Lines);
        int o = input.Calls.IndexOf("KeyTap O");
        int item = input.Calls.IndexOf($"Click {ItemX + IconSide / 2},{IconY + IconSide / 2}");
        int tab = input.Calls.IndexOf("KeyTap Tab");
        int x = input.Calls.IndexOf($"Click {XBuffX + IconSide / 2},{IconY + IconSide / 2}");
        int skill = input.Calls.IndexOf("KeyTap 1");
        Assert.True(o >= 0 && item > o && tab > item && x > tab && skill > x, "expected O, the item buff, Tab, the X buff, the skill" + log);
        Assert.DoesNotContain(input.Calls, c => c.StartsWith("KeyDown", StringComparison.Ordinal));
        // Shura's skip is told once per Start, not once per cycle; the missing item buff is warned once.
        Assert.Single(run.Lines, l => l.StartsWith("No Shura key is set", StringComparison.Ordinal));
        Assert.Single(run.Lines, l => l.StartsWith("The buff icon 'waritem2' is not on the skill bar", StringComparison.Ordinal));
        Assert.True(input.Calls.Count(c => c == "KeyTap Tab") >= 2, "expected a second fight cycle" + log);
    }

    [Fact]
    public async Task UntickedBuffs_NeitherClickedNorWarnedAbout_TheXBuffSkipped()
    {
        var settings = War();
        settings.Lod.ShuraKey = "";
        settings.War.SetBuffOn("warbuff4", false);    // the X buff
        settings.War.SetBuffOn("waritem1", false);
        settings.War.SetBuffOn("waritem2", false);
        var (input, run) = await RunBuffAndFightAsync(settings);

        string log = Log(input, run.Lines);
        Assert.True(input.Clicks == 0, "a click on an unticked buff" + log);
        Assert.Contains("KeyTap O", input.Calls);
        Assert.DoesNotContain(run.Lines, l => l.Contains("'waritem", StringComparison.Ordinal));
        Assert.True(input.Calls.Count(c => c == "KeyTap Tab") >= 2, "expected a second fight cycle" + log);
    }

    [Fact]
    public async Task WithAShuraKey_TheHoldStays()
    {
        var settings = War();
        var (input, run) = await RunBuffAndFightAsync(settings);

        string log = Log(input, run.Lines);
        int down = input.Calls.IndexOf("KeyDown E");
        int up = input.Calls.IndexOf("KeyUp E");
        int skill = input.Calls.IndexOf("KeyTap 1");
        Assert.True(down >= 0 && up > down && skill > up, "expected Shura held, let go, then the skill" + log);
        Assert.DoesNotContain(run.Lines, l => l.StartsWith("No Shura key is set", StringComparison.Ordinal));
    }

    private static string Log(FakeGameInput input, IEnumerable<string> lines)
        => "\ninputs:\n" + string.Join("\n", input.Calls) + "\nlog:\n" + string.Join("\n", lines);

    /// <summary>
    /// From WarBuff in Bohai with an autobuff scroll in the item list, the X buff (warbuff4) and waritem1 on the skill bar
    /// (waritem2 not), until the second fight cycle's skill press: the target box shows an enemy until three skill presses
    /// after the last Tab, then nothing - TargetGoneMs later the cycle starts again.
    /// </summary>
    private static async Task<(FakeGameInput Input, (PilotPhase Phase, List<string> Lines) Run)> RunBuffAndFightAsync(AppSettings settings)
    {
        var input = new FakeGameInput();
        var ocr = new FakeOcrEngine();
        ocr.Scene = () =>
        {
            var words = new List<OcrWord>
            {
                FakeOcrEngine.Line(Bohai, 1500, 10, 300, 16),                        // minimap title
                FakeOcrEngine.Line("[Auto buff scroll] 02.11.2026", 1300, 700, 400, 16),   // item list (Regions.ItemList)
            };
            int lastTab = input.Calls.LastIndexOf("KeyTap Tab");
            if (lastTab >= 0 && input.Calls.Skip(lastTab).Count(c => c == "KeyTap 1") < 3)
            {
                words.Add(FakeOcrEngine.Line("Enemy Player", 900, 60, 200, 16));      // target box (Regions.TargetBox)
                words.Add(FakeOcrEngine.Line("41000 / 42833", 900, 80, 200, 16));
            }

            return words;
        };

        using var frame = new Mat(Height, Width, MatType.CV_8UC4, Scalar.All(0));
        using var xBuff = Noise(seed: 4);
        using var item = Noise(seed: 7);
        Paste(frame, xBuff, XBuffX, IconY);
        Paste(frame, item, ItemX, IconY);

        var run = await PilotRun.RunAsync(settings, input, ocr, frame, PilotPhase.WarBuff, (_, _) =>
            {
                int second = input.Calls.Select((c, i) => (c, i)).Where(p => p.c == "KeyTap Tab").Skip(1).Select(p => p.i).FirstOrDefault(-1);
                return second >= 0 && input.Calls.Skip(second).Contains("KeyTap 1");
            },
            timeoutMs: 20000,
            assets: folder =>
            {
                Cv2.ImWrite(Path.Combine(folder, "warbuff4.png"), xBuff);
                Cv2.ImWrite(Path.Combine(folder, "waritem1.png"), item);
            });
        return (input, run);
    }

    /// <summary>A grey noise square, the same for the same seed: a stand-in for a skill bar icon.</summary>
    private static Mat Noise(int seed)
    {
        var random = new Random(seed);
        var icon = new Mat(IconSide, IconSide, MatType.CV_8UC1);
        for (int y = 0; y < IconSide; y++)
            for (int x = 0; x < IconSide; x++)
                icon.Set(y, x, (byte)random.Next(256));
        return icon;
    }

    /// <summary>A grey image copied into the BGRA frame at (x, y).</summary>
    private static void Paste(Mat frame, Mat gray, int x, int y)
    {
        using var color = new Mat();
        Cv2.CvtColor(gray, color, ColorConversionCodes.GRAY2BGRA);
        using var roi = new Mat(frame, new Rect(x, y, gray.Width, gray.Height));
        color.CopyTo(roi);
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

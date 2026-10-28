using System.Drawing;
using Emgu.CV;
using Emgu.CV.CvEnum;
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

    // ---------------------------------------------------------------- Start: optional buffs and Shura

    [Fact]
    public async Task Start_InACave_BuffIconsMissing_WarnsWithEachScore_AndCarriesOn()
    {
        var settings = War();
        settings.War.HomeMap = "";
        settings.Lod.ShuraKey = "";       // Shura is optional too: not looked for without its key
        var input = new FakeGameInput();
        var ocr = new FakeOcrEngine { Scene = () => [FakeOcrEngine.Line(Cave, 1500, 10, 300, 16)] };

        using var frame = Blank.Bgra(Height, Width);
        var run = await PilotRun.RunAsync(settings, input, ocr, frame, PilotPhase.CheckZone,
            (pilot, _) => pilot.Phase == PilotPhase.OpenInventory);

        string log = "\nlog:\n" + string.Join("\n", run.Lines);
        Assert.True(run.Phase == PilotPhase.OpenInventory, $"phase {run.Phase}" + log);
        string? warning = run.Lines.FirstOrDefault(l => l.StartsWith("Not on the skill bar", StringComparison.Ordinal));
        Assert.True(warning != null, "no warning about the missing icons" + log);
        // No template file at all (an empty templates folder) is said as such, not as "(not seen)".
        Assert.Contains("warbuff1 (no warbuff1.png in templates/Assets)", warning);
        Assert.Contains("warweapon2 (no warweapon2.png in templates/Assets)", warning);
        Assert.Contains("waritem2 (no waritem2.png in templates/Assets)", warning);
        Assert.DoesNotContain("shura", warning);
        Assert.DoesNotContain(run.Lines, l => l.StartsWith("Pilot stopped:", StringComparison.Ordinal) && l != "Pilot stopped: test over.");
    }

    [Fact]
    public async Task Start_InACave_ShuraKeySet_ButNoShuraIcon_TheWarningSaysToClearTheKey()
    {
        var settings = War();
        settings.War.HomeMap = "";
        var input = new FakeGameInput();
        var ocr = new FakeOcrEngine { Scene = () => [FakeOcrEngine.Line(Cave, 1500, 10, 300, 16)] };

        using var frame = Blank.Bgra(Height, Width);
        var run = await PilotRun.RunAsync(settings, input, ocr, frame, PilotPhase.CheckZone,
            (pilot, _) => pilot.Phase == PilotPhase.OpenInventory);

        string log = "\nlog:\n" + string.Join("\n", run.Lines);
        string? warning = run.Lines.FirstOrDefault(l => l.StartsWith("Not on the skill bar", StringComparison.Ordinal));
        Assert.True(warning != null, "no warning about the missing icons" + log);
        Assert.Contains("shura (no shura.png in templates/Assets)", warning);
        Assert.Contains("- clear 'Shura key (LOD)' if this character has no Shura, or the fight still holds that key", warning);
        Assert.True(run.Phase == PilotPhase.OpenInventory, $"phase {run.Phase}" + log);
    }

    [Fact]
    public async Task Start_TitleUnreadable_TheStopNamesTheCaveAndBohai()
    {
        var settings = War();
        var input = new FakeGameInput();
        var ocr = new FakeOcrEngine();      // nothing reads

        using var frame = Blank.Bgra(Height, Width);
        var run = await PilotRun.RunAsync(settings, input, ocr, frame, PilotPhase.CheckZone, (pilot, _) => false);

        string log = "\nlog:\n" + string.Join("\n", run.Lines);
        Assert.True(run.Lines.Any(l => l.StartsWith("Pilot stopped: Auto War must be started inside a cave on page 3 of the teleport scroll, "
                                                    + "or inside Bohai Outpost", StringComparison.Ordinal)), "no stop naming the cave and Bohai" + log);
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

        using var frame = Blank.Bgra(Height, Width);
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
        Assert.Single(run.Lines, l => l.StartsWith("The buff icon 'waritem2' cannot be looked for (no waritem2.png in templates/Assets)", StringComparison.Ordinal));
        Assert.True(input.Calls.Count(c => c == "KeyTap Tab") >= 2, "expected a second fight cycle" + log);
    }

    [Fact]
    public async Task AMissingIcon_IsWarnedOncePerStart_NotAgainAtTheRebuff()
    {
        var settings = War();
        settings.Lod.ShuraKey = "";
        settings.War.BuffEveryMs = 1500;     // a rebuff at the first Tab point after 1.5 s: a second buff round in this Start
        var (input, run) = await RunBuffAndFightAsync(settings,
            done: lines => lines.Count(l => l.StartsWith("Buffed with 'O'", StringComparison.Ordinal)) >= 2,
            waritem2Png: true);

        string log = Log(input, run.Lines);
        Assert.True(run.Lines.Count(l => l.StartsWith("Buffed with 'O'", StringComparison.Ordinal)) >= 2, "expected two buff rounds" + log);
        Assert.Contains(run.Lines, l => l.StartsWith("Rebuffing (", StringComparison.Ordinal));
        // waritem2.png exists but is not on the bar: once, in the first round only.
        Assert.Single(run.Lines, l => l.StartsWith("The buff icon 'waritem2' is not on the skill bar", StringComparison.Ordinal));
        string item = $"Click {ItemX + IconSide / 2},{IconY + IconSide / 2}";
        Assert.True(input.Calls.Count(c => c == item) >= 2, "waritem1 clicked in both rounds" + log);
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
    public async Task TheXBuffNotOnTheBar_WarnedOncePerStart_WithItsBestScore_TheFightGoesOn()
    {
        // With autobuff the buff round never looks for warbuff4: only the fight cycle can tell it is missing.
        var settings = War();
        settings.Lod.ShuraKey = "";
        var (input, run) = await RunBuffAndFightAsync(settings, xBuffOnBar: false);

        string log = Log(input, run.Lines);
        var warning = Assert.Single(run.Lines, l => l.StartsWith("The X buff 'warbuff4' was not found on the skill bar", StringComparison.Ordinal));
        Assert.Contains("warbuff4 (best ", warning);
        Assert.True(input.Calls.Count(c => c == "KeyTap Tab") >= 2 && input.Calls.Contains("KeyTap 1"), "expected two fight cycles" + log);
        Assert.DoesNotContain($"Click {XBuffX + IconSide / 2},{IconY + IconSide / 2}", input.Calls);
    }

    [Fact]
    public async Task TheXBuffWithoutItsPng_TheWarningSaysSo()
    {
        var settings = War();
        settings.Lod.ShuraKey = "";
        var (input, run) = await RunBuffAndFightAsync(settings, xBuffOnBar: false, xBuffPng: false);

        string log = Log(input, run.Lines);
        var warning = Assert.Single(run.Lines, l => l.StartsWith("The X buff 'warbuff4' was not found on the skill bar", StringComparison.Ordinal));
        Assert.Contains("warbuff4 (no warbuff4.png in templates/Assets)", warning);
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

    // ---------------------------------------------------------------- a player target, the walk out of the respawn point

    [Fact]
    public async Task APlayerTarget_ItsNameUnread_TheHpNumbersAlone_TheSkillGoesOut()
    {
        // The user's sample (2026-10-28): the red name "zMADARAz" does not read; "49293/49293" on its bar does.
        var settings = Fight();
        var (input, run) = await RunFightAsync(settings, input =>
        {
            int lastTab = input.Calls.LastIndexOf("KeyTap Tab");
            return lastTab >= 0 && input.Calls.Skip(lastTab).Count(c => c == "KeyTap 1") < 3
                ? [FakeOcrEngine.Line("49293/49293", 900, 80, 140, 16)]
                : [];
        }, done: (input, _) => input.Calls.Contains("KeyTap 1"));

        string log = Log(input, run.Lines);
        int tab = input.Calls.IndexOf("KeyTap Tab");
        int skill = input.Calls.IndexOf("KeyTap 1");
        Assert.True(tab >= 0 && skill > tab, "expected Tab, then the skill" + log);
        Assert.Contains(run.Lines, l => l.StartsWith("A target (HP numbers, name unread: '49293/49293')", StringComparison.Ordinal));
        Assert.DoesNotContain(input.Calls, c => c.StartsWith("CtrlClick", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoTargetAfterFiveTabs_CtrlClicksTowardTheNearestPlayer_NotOurOwnName()
    {
        var settings = Fight();
        var (input, run) = await RunFightAsync(settings, _ =>
            [
                FakeOcrEngine.Line("OurOwnName", 910, 475, 100, 16),     // right above the character (0.5 w, 0.54 h)
                FakeOcrEngine.Line("zMADARAz", 1030, 835, 100, 16),
            ],
            done: (input, _) => input.Calls.Count(c => c.StartsWith("CtrlClick", StringComparison.Ordinal)) >= 2,
            timeoutMs: 30000);

        string log = Log(input, run.Lines);
        int first = input.Calls.FindIndex(c => c.StartsWith("CtrlClick", StringComparison.Ordinal));
        Assert.True(first >= 0, "expected a Ctrl+click" + log);
        Assert.Equal(5, input.Calls.Take(first).Count(c => c == "KeyTap Tab"));
        var (x, y) = AutomationPilot.WarWalkClick(new PixelRect(1030, 835, 100, 16), Width, Height);
        Assert.Equal($"CtrlClick {x},{y}", input.Calls[first]);
        Assert.Contains("No target after 5 Tabs - walking toward 'zMADARAz' (step 1/6).", run.Lines);
        Assert.Contains("No target after 6 Tabs - walking toward 'zMADARAz' (step 2/6).", run.Lines);
        Assert.DoesNotContain(input.Calls, c => c.StartsWith("Click", StringComparison.Ordinal) && !c.StartsWith("CtrlClick", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoTarget_NoPlayerInView_NoClick_SaidOncePerSpell()
    {
        var settings = Fight();
        var (input, run) = await RunFightAsync(settings, _ => [FakeOcrEngine.Line("OurOwnName", 910, 475, 100, 16)],
            done: (input, _) => input.Calls.Count(c => c == "KeyTap Tab") >= 8,
            timeoutMs: 30000);

        string log = Log(input, run.Lines);
        Assert.True(input.Calls.Count(c => c == "KeyTap Tab") >= 8, "expected 8 Tabs" + log);
        Assert.DoesNotContain(input.Calls, c => c.StartsWith("CtrlClick", StringComparison.Ordinal));
        Assert.Single(run.Lines, l => l.Contains("no players in view", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoTarget_TheMinimapTitleUnread_NoWalk()
    {
        // A login screen, a loading screen, a window over the minimap: no "Bohai Outpost" on the last read - no Ctrl+click
        // (a saved server-list frame offers "• Online" as the nearest "name tag").
        var settings = Fight();
        var (input, run) = await RunFightAsync(settings, _ => [FakeOcrEngine.Line("zMADARAz", 1030, 835, 100, 16)],
            done: (input, _) => input.Calls.Count(c => c == "KeyTap Tab") >= 8,
            timeoutMs: 30000, titleGoneAtTheFirstTab: true);

        string log = Log(input, run.Lines);
        Assert.True(input.Calls.Count(c => c == "KeyTap Tab") >= 8, "expected 8 Tabs" + log);
        Assert.DoesNotContain(input.Calls, c => c.StartsWith("CtrlClick", StringComparison.Ordinal));
        Assert.Single(run.Lines, l => l.Contains("the minimap does not read", StringComparison.Ordinal));
    }

    /// <summary>Auto War with autobuff and nothing to click: no buff icons, no Shura key - straight to the fight cycle.</summary>
    private static AppSettings Fight()
    {
        var settings = War();
        settings.Lod.ShuraKey = "";
        foreach (string icon in new[] { "warbuff4", "waritem1", "waritem2" })
            settings.War.SetBuffOn(icon, false);
        return settings;
    }

    /// <summary>
    /// From WarBuff in Bohai with an autobuff scroll (the buff key, no icons), on a blank frame; <paramref name="scene"/> adds
    /// the OCR lines of the moment (given the inputs so far) to the minimap title and the item list.
    /// </summary>
    private static async Task<(FakeGameInput Input, (PilotPhase Phase, List<string> Lines) Run)> RunFightAsync(AppSettings settings,
        Func<FakeGameInput, IEnumerable<OcrWord>> scene, Func<FakeGameInput, IReadOnlyList<string>, bool> done, int timeoutMs = 20000,
        bool titleGoneAtTheFirstTab = false)
    {
        var input = new FakeGameInput();
        var ocr = new FakeOcrEngine
        {
            Scene = () =>
            [
                .. titleGoneAtTheFirstTab && input.Calls.Contains("KeyTap Tab") ? [] : new[] { FakeOcrEngine.Line(Bohai, 1500, 10, 300, 16) },
                FakeOcrEngine.Line("[Auto buff scroll] 02.11.2026", 1300, 700, 400, 16),
                .. scene(input),
            ],
        };

        using var frame = Blank.Bgra(Height, Width);
        var run = await PilotRun.RunAsync(settings, input, ocr, frame, PilotPhase.WarBuff, (_, lines) => done(input, lines), timeoutMs);
        return (input, run);
    }

    private static string Log(FakeGameInput input, IEnumerable<string> lines)
        => "\ninputs:\n" + string.Join("\n", input.Calls) + "\nlog:\n" + string.Join("\n", lines);

    /// <summary>
    /// From WarBuff in Bohai with an autobuff scroll in the item list, the X buff (warbuff4; on the bar unless
    /// <paramref name="xBuffOnBar"/> is false - then only a faded copy -, its PNG unless <paramref name="xBuffPng"/> is false) and waritem1 on the skill
    /// bar (waritem2 not; its PNG only with <paramref name="waritem2Png"/>), until <paramref name="done"/> (given the log) or
    /// else the second fight cycle's skill press: the target box shows an enemy until three skill presses after the last Tab,
    /// then nothing - TargetGoneMs later the cycle starts again.
    /// </summary>
    private static async Task<(FakeGameInput Input, (PilotPhase Phase, List<string> Lines) Run)> RunBuffAndFightAsync(AppSettings settings,
        Func<IReadOnlyList<string>, bool>? done = null, bool waritem2Png = false, bool xBuffOnBar = true, bool xBuffPng = true)
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

        using var frame = Blank.Bgra(Height, Width);
        using var xBuff = Noise(seed: 4);
        using var item = Noise(seed: 7);
        using var item2 = Noise(seed: 11);
        if (xBuffOnBar)
        {
            Paste(frame, xBuff, XBuffX, IconY);
        }
        else
        {
            // Half the X buff, half other noise (like a cooldown overlay): a best score well under the 0.90 gate.
            using var other = Noise(seed: 99);
            using var faded = new Mat();
            CvInvoke.AddWeighted(xBuff, 0.5, other, 0.5, 0, faded);
            Paste(frame, faded, XBuffX, IconY);
        }
        Paste(frame, item, ItemX, IconY);

        var run = await PilotRun.RunAsync(settings, input, ocr, frame, PilotPhase.WarBuff, (_, lines) =>
            {
                if (done != null)
                    return done(lines);
                int second = input.Calls.Select((c, i) => (c, i)).Where(p => p.c == "KeyTap Tab").Skip(1).Select(p => p.i).FirstOrDefault(-1);
                return second >= 0 && input.Calls.Skip(second).Contains("KeyTap 1");
            },
            timeoutMs: 20000,
            assets: folder =>
            {
                if (xBuffPng)
                    CvInvoke.Imwrite(Path.Combine(folder, "warbuff4.png"), xBuff);
                CvInvoke.Imwrite(Path.Combine(folder, "waritem1.png"), item);
                if (waritem2Png)
                    CvInvoke.Imwrite(Path.Combine(folder, "waritem2.png"), item2);
            });
        return (input, run);
    }

    /// <summary>A grey noise square, the same for the same seed: a stand-in for a skill bar icon.</summary>
    private static Mat Noise(int seed)
    {
        var random = new Random(seed);
        var pixels = new byte[IconSide * IconSide];
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = (byte)random.Next(256);   // row by row, as before
        // A Mat header over the pinned array, cloned: OpenCV copies the pixels (CLAUDE.md §10).
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            using var view = new Mat(IconSide, IconSide, DepthType.Cv8U, 1, handle.AddrOfPinnedObject(), IconSide);
            return view.Clone();
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>A grey image copied into the BGRA frame at (x, y).</summary>
    private static void Paste(Mat frame, Mat gray, int x, int y)
    {
        using var color = new Mat();
        CvInvoke.CvtColor(gray, color, ColorConversion.Gray2Bgra);
        using var roi = new Mat(frame, new Rectangle(x, y, gray.Width, gray.Height));
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

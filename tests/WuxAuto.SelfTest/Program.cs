using System.Drawing;
using System.IO;
using System.Text.RegularExpressions;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using WuxAuto.Automation;
using WuxAuto.Core;
using WuxAuto.SelfTest;
using WuxAuto.Vision;

// Offline sanity check for the vision stack: OpenCV natives, template matching, OCR and the
// compass detector. Everything runs against a synthetic frame, so no game (and no input) is
// involved. Run with:  dotnet run --project tests\WuxAuto.SelfTest [assetsFolder]

// OCR text can be non-ASCII; piped stdout would otherwise use the OEM code page and mangle it.
Console.OutputEncoding = System.Text.Encoding.UTF8;

if (args.Contains("--dxgi"))
{
    DxgiProbe.Run();
    return 0;
}

if (args.Contains("--capture"))
{
    await CaptureProbe.RunAsync();
    return 0;
}

int inputIndex = Array.IndexOf(args, "--input");
if (inputIndex >= 0)
{
    var keys = new List<string>();
    for (int i = inputIndex + 1; i < args.Length && !args[i].StartsWith("--"); i++)
        keys.Add(args[i]);

    return await InputProbe.RunAsync(keys.Count == 0 ? new[] { "I" } : keys.ToArray());
}

int doIndex = Array.IndexOf(args, "--do");
if (doIndex >= 0)
{
    var steps = new List<string>();
    for (int i = doIndex + 1; i < args.Length; i++)
    {
        if (args[i] == "--do")
            continue;
        steps.Add(args[i]);
    }

    return await ActionProbe.RunAsync(steps);
}

int updateCheckIndex = Array.IndexOf(args, "--update-check");
if (updateCheckIndex >= 0)
{
    string repository = updateCheckIndex + 1 < args.Length ? args[updateCheckIndex + 1] : UpdateService.Repository;
    return await UpdateProbe.CheckAsync(repository);
}

int updateInstallIndex = Array.IndexOf(args, "--update-install");
if (updateInstallIndex >= 0)
{
    if (updateInstallIndex + 2 >= args.Length)
    {
        Console.WriteLine("usage: --update-install <appFolder> <release zip>");
        return 1;
    }

    return UpdateProbe.Install(args[updateInstallIndex + 1], args[updateInstallIndex + 2]);
}

int alertIndex = Array.IndexOf(args, "--alert");
if (alertIndex >= 0)
{
    string? soundFile = alertIndex + 1 < args.Length && !args[alertIndex + 1].StartsWith("--") ? args[alertIndex + 1] : null;
    return AlertProbe.Run(soundFile);
}

int runSkillIndex = Array.IndexOf(args, "--runskill");
if (runSkillIndex >= 0 && runSkillIndex + 1 < args.Length)
    return FrameProbe.RunSkill(args[runSkillIndex + 1]);

int confirmIndex = Array.IndexOf(args, "--confirm");
if (confirmIndex >= 0 && confirmIndex + 1 < args.Length)
    return await FrameProbe.ConfirmAsync(args[confirmIndex + 1]);

int shuraIndex = Array.IndexOf(args, "--shura");
if (shuraIndex >= 0 && shuraIndex + 1 < args.Length)
    return FrameProbe.Shura(args[shuraIndex + 1]);

int hpIndex = Array.IndexOf(args, "--hp");
if (hpIndex >= 0 && hpIndex + 1 < args.Length)
    return await WarProbe.HpAsync(args[hpIndex + 1]);

int targetIndex = Array.IndexOf(args, "--target");
if (targetIndex >= 0 && targetIndex + 1 < args.Length)
    return await WarProbe.TargetAsync(args[targetIndex + 1]);

int uiIndex = Array.IndexOf(args, "--warui");
if (uiIndex >= 0 && uiIndex + 1 < args.Length)
    return await WarProbe.UiAsync(args[uiIndex + 1]);

int walkIndex = Array.IndexOf(args, "--warwalk");
if (walkIndex >= 0 && walkIndex + 1 < args.Length)
    return await WarProbe.WalkAsync(args[walkIndex + 1]);

int warBarIndex = Array.IndexOf(args, "--warbar");
if (warBarIndex >= 0 && warBarIndex + 1 < args.Length)
    return WarProbe.WarBar(args[warBarIndex + 1]);

int menuIndex = Array.IndexOf(args, "--menu");
if (menuIndex >= 0 && menuIndex + 1 < args.Length)
    return await WarProbe.MenuAsync(args[menuIndex + 1]);

int tribeIndex = Array.IndexOf(args, "--tribe");
if (tribeIndex >= 0 && tribeIndex + 1 < args.Length)
    return await WarProbe.TribeAsync(args[tribeIndex + 1]);

int ringIndex = Array.IndexOf(args, "--ring");
if (ringIndex >= 0 && ringIndex + 1 < args.Length)
    return FrameProbe.Ring(args[ringIndex + 1]);

int mapOpenIndex = Array.IndexOf(args, "--mapopen");
if (mapOpenIndex >= 0 && mapOpenIndex + 1 < args.Length)
    return await FrameProbe.MapOpenAsync(args[mapOpenIndex + 1]);

int lodPartsIndex = Array.IndexOf(args, "--lodparts");
if (lodPartsIndex >= 0 && lodPartsIndex + 1 < args.Length)
    return FrameProbe.LodParts(args[lodPartsIndex + 1]);

int parityIndex = Array.IndexOf(args, "--parity");
if (parityIndex >= 0)
{
    if (parityIndex + 1 >= args.Length)
    {
        Console.WriteLine("usage: --parity <folder of frames>");
        return 1;
    }

    return await ParityProbe.RunAsync(args[parityIndex + 1]);
}

int titlesIndex = Array.IndexOf(args, "--titles");
if (titlesIndex >= 0 && titlesIndex + 1 < args.Length)
    return await FrameProbe.TitlesAsync(args[titlesIndex + 1]);

int frameIndex = Array.IndexOf(args, "--frame");
if (frameIndex >= 0)
{
    if (frameIndex + 1 >= args.Length)
    {
        Console.WriteLine("usage: --frame <path-to-png>");
        return 1;
    }

    return await FrameProbe.RunAsync(args[frameIndex + 1], args);
}

string? assetsArg = args.FirstOrDefault(a => !a.StartsWith("--"));
string assets = assetsArg ?? Path.GetFullPath(Path.Combine(
    AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "WuxAuto", "Assets"));

bool verbose = args.Contains("--verbose");

Console.WriteLine($"assets: {assets}");
Console.WriteLine($"OpenCV: {Regex.Match(CvInvoke.BuildInformation, @"OpenCV (\S+)").Groups[1].Value}");

using var library = new TemplateLibrary(assets);
using var matcher = new TemplateMatcher(library);
using var compass = new CompassDetector(library);
using var ocr = new OcrService();

Console.WriteLine($"templates: {string.Join(", ", library.Names)}");

int failures = 0;

void Check(string name, bool ok, string detail)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}  {detail}");
    if (!ok) failures++;
}

double AngleError(double a, double b) => Math.Abs((a - b + 540) % 360 - 180);

// ---------------------------------------------------------------- synthetic frame
using var frame = new Mat(1080, 1920, DepthType.Cv8U, 4);
frame.SetTo(new MCvScalar(22, 20, 18, 255));

void PasteGray(string template, int x, int y)
{
    var image = library.Get(template) ?? throw new InvalidOperationException($"missing {template}");
    using var color = new Mat();
    CvInvoke.CvtColor(image.Gray, color, ColorConversion.Gray2Bgra);
    using var roi = new Mat(frame, new Rectangle(x, y, color.Width, color.Height));
    color.CopyTo(roi);
}

PasteGray("teleportscroll", 300, 400);
PasteGray("rightchevron", 900, 420);
PasteGray("mapicon", 1800, 40);

var settings = new DetectionSettings();
var full = new PixelRect(0, 0, frame.Width, frame.Height);

// ---------------------------------------------------------------- template matching
foreach (var (name, expectedX, expectedY) in new[]
         {
             ("teleportscroll", 300, 400),
             ("rightchevron", 900, 420),
             ("mapicon", 1800, 40),
         })
{
    TemplateMatch? match = null;
    var timings = new List<double>();
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    for (int i = 0; i < 11; i++)
    {
        stopwatch.Restart();
        match = matcher.FindBest(frame, name, full, 1.0, 0.7);
        stopwatch.Stop();
        if (i > 0)
            timings.Add(stopwatch.Elapsed.TotalMilliseconds);
    }

    timings.Sort();
    string detail = match == null
        ? "no hit"
        : $"{match} median {timings[timings.Count / 2]:0.00}ms over {timings.Count} runs";
    Check($"match {name}",
        match != null && Math.Abs(match.Bounds.X - expectedX) <= 2 && Math.Abs(match.Bounds.Y - expectedY) <= 2,
        detail);
}

// ---------------------------------------------------------------- OCR
CvInvoke.PutText(frame, "Labyrinth", new Point(700, 520), HersheyFonts.Simplex, 1.1,
    new MCvScalar(245, 245, 245, 255), 2, LineType.AntiAlias);
var ocrArea = new PixelRect(650, 460, 520, 110);
var words = await ocr.RecognizeAsync(frame, ocrArea, 1.5);
var hit = TextMatcher.FindBest(words, "Labyrinth", 0.7);
Check("ocr Labyrinth", hit != null,
    hit != null ? $"{hit} in {ocr.LastDurationMs}ms" : $"saw {words.Count} word(s): {string.Join("|", words.Select(w => w.Text))}");

// ---------------------------------------------------------------- compass
// A beam rotated by N degrees counter-clockwise points N degrees anti-clockwise from "up",
// i.e. -N in the clockwise-positive convention the app uses.
foreach (int visualAngle in new[] { 0, 40, 135, 200, 330 })
{
    const int side = 420;
    using var scene = new Mat(side, side, DepthType.Cv8U, 4);
    scene.SetTo(new MCvScalar(0, 0, 0, 255));

    var flash = library.Get("flashlight")!;
    int padSize = (int)Math.Ceiling(Math.Sqrt(flash.Width * (double)flash.Width + flash.Height * (double)flash.Height)) + 2;
    if (padSize % 2 != 0)
        padSize++;

    using var padded = new Mat(padSize, padSize, DepthType.Cv8U, 1);
    padded.SetTo(new MCvScalar(0, 0, 0));
    int left = (padSize - flash.Width) / 2;
    int top = (padSize - flash.Height) / 2;
    using (var inner = new Mat(padded, new Rectangle(left, top, flash.Width, flash.Height)))
        flash.Gray.CopyTo(inner);

    using var rotation = new Mat();
    CvInvoke.GetRotationMatrix2D(new PointF(padSize / 2f, padSize / 2f), visualAngle, 1.0, rotation);
    using var rotated = new Mat();
    CvInvoke.WarpAffine(padded, rotated, rotation, new Size(padSize, padSize), Inter.Linear, Warp.Default, BorderType.Constant, new MCvScalar(0, 0, 0));

    using var rotatedColor = new Mat();
    CvInvoke.CvtColor(rotated, rotatedColor, ColorConversion.Gray2Bgra);
    int offset = (side - padSize) / 2;
    using (var roi = new Mat(scene, new Rectangle(offset, offset, padSize, padSize)))
        rotatedColor.CopyTo(roi);

    if (verbose)
        CompassDetector.TraceScores = (angle, score) => Console.WriteLine($"    angle {angle,3} -> {score:0.0000}");

    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    var (templateReading, brightnessReading) = compass.DetectBoth(scene, new PixelRect(0, 0, side, side), 1.0, settings);
    stopwatch.Stop();
    CompassDetector.TraceScores = null;

    double expected = visualAngle > 180 ? 360 - visualAngle : -visualAngle;
    string detail = $"template={templateReading?.AngleFromNorth:0.0} ({templateReading?.Score:0.00}) " +
                    $"brightness={brightnessReading?.AngleFromNorth:0.0} ({brightnessReading?.Score:0.00}) " +
                    $"expected {expected}, {stopwatch.Elapsed.TotalMilliseconds:0.0}ms";
    bool templateOk = templateReading != null && AngleError(templateReading.AngleFromNorth, expected) <= 12;
    bool brightnessOk = brightnessReading != null && AngleError(brightnessReading.AngleFromNorth, expected) <= 30;
    Check($"compass {visualAngle} deg", templateOk && brightnessOk, detail);
}

// ---------------------------------------------------------------- updater (no network)
foreach (var (name, ok, detail) in UpdateProbe.OfflineChecks())
    Check(name, ok, detail);

// ---------------------------------------------------------------- kill alert chime
foreach (var (name, ok, detail) in AlertProbe.OfflineChecks())
    Check(name, ok, detail);

// ---------------------------------------------------------------- high level scan
using var analyzer = new ScreenAnalyzer(new AppSettings(), new OcrService());
var report = await analyzer.TestScanAsync(frame);
Check("analyzer scan", report.Overlays.Count > 0, $"{report.Overlays.Count} overlay(s)");
if (verbose)
{
    foreach (string detail in report.Details)
        Console.WriteLine($"    {detail}");
}

// ---------------------------------------------------------------- LOD boss ring + belt
// The pale pink ring round the boss's feet (live: H ~150, S 50-80, V 255), cut into three arcs by the
// boss's legs and the character, plus a pink speck elsewhere; four cyan circles stacked as the belt,
// plus two side by side (the boss's eyes) that are not a belt.
using (var lodFrame = new Mat(1080, 1920, DepthType.Cv8U, 4))
{
    lodFrame.SetTo(new MCvScalar(22, 20, 18, 255));
    var lavender = new MCvScalar(250, 170, 235, 255);
    foreach (var (from, to) in new[] { (10, 80), (100, 170), (190, 350) })
        CvInvoke.Ellipse(lodFrame, new Point(1000, 450), new Size(130, 55), 0, from, to, lavender, 6, LineType.AntiAlias);
    CvHelpers.DrawRectangle(lodFrame, new Rectangle(500, 800, 10, 10), lavender, -1);

    var cyan = new MCvScalar(230, 230, 0, 255);
    foreach (int y in new[] { 230, 270, 310, 350 })
        CvInvoke.Circle(lodFrame, new Point(990, y), 14, cyan, -1, LineType.AntiAlias);
    CvInvoke.Circle(lodFrame, new Point(900, 150), 10, cyan, -1);
    CvInvoke.Circle(lodFrame, new Point(940, 150), 10, cyan, -1);

    var bossRing = analyzer.FindBossRing(lodFrame);
    Check("lod boss ring",
        bossRing is { } r && Math.Abs(r.CenterX - 1000) <= 15 && Math.Abs(r.CenterY - 450) <= 15 && r.Width >= 230,
        bossRing?.ToString() ?? "not found");

    var bossBelt = analyzer.FindBossBelt(lodFrame);
    Check("lod boss belt",
        bossBelt is { } b && Math.Abs(b.CenterX - 990) <= 6 && b.Y <= 220 && b.Bottom >= 360,
        bossBelt?.ToString() ?? "not found");
}

// ---------------------------------------------------------------- kill screenshots
// Plain JPEGs at the frame's size; only the newest maxFiles are kept. A temp folder, so the real
// kills folder is never pruned by the test.
string killsFolder = Path.Combine(Path.GetTempPath(), $"wuxauto-kills-{Guid.NewGuid():N}");
try
{
    string[] saved = new[] { "a_first", "b_second", "c_third" }.Select(tag => KillScreenshots.Save(frame, tag, killsFolder, maxFiles: 2)).ToArray();
    var left = Directory.GetFiles(killsFolder).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
    using var reread = ImageFile.Read(saved[^1], ImreadModes.ColorBgr);
    Check("kill screenshots",
        saved.All(f => f.Length > 0) && left.Length == 2 && left[0]!.EndsWith("_b_second.jpg") && left[1]!.EndsWith("_c_third.jpg")
        && reread.Width == frame.Width && reread.Height == frame.Height && reread.NumberOfChannels == 3,
        $"kept {string.Join(", ", left)}; reread {reread.Width}x{reread.Height}x{reread.NumberOfChannels}");
}
finally
{
    try { Directory.Delete(killsFolder, recursive: true); } catch { }
}

{
    // AutohuntOff: only a line that is new since the key press counts, the bottom-most new one decides,
    // OCR noise in the tail is tolerated, and an unchanged (full) log reads as nothing.
    const string started = "Auto hunt started", ended = "Auto hunt ended";
    string[] before = ["autohuntended", "autohuntstarted"];
    bool? a = AutohuntLog.NewState(before, ["autohuntended", "autohuntstarted", "autohuntended"], started, ended, out var la);
    bool? b = AutohuntLog.NewState(before, ["autohuntstarted", "autohuntended", "autohuntstarted"], started, ended, out _);
    bool? c = AutohuntLog.NewState(before, ["autohuntended", "autohuntstarted"], started, ended, out _);
    bool? d = AutohuntLog.NewState([], ["autohuntendeď"], started, ended, out _);
    bool? e = AutohuntLog.NewState(["autohuntstarted"], ["autohuntstarted", "autohuntstarted"], started, ended, out _);
    string? k = AutohuntLog.Key("[12:01] Auto hunt ended", AutohuntLog.Prefix(started, ended)!);
    Check("autohunt log",
        a == false && b == true && c == null && d == false && e == true && k == "autohuntended",
        $"new ended={a} ({la}), new started={b}, unchanged={c}, noisy ended={d}, repeated started={e}, key={k}");
}

// ---------------------------------------------------------------- Auto War "Buffs" check boxes
// The settings panel's switches (WuxAuto.ViewModels.WarBuffSwitch), on a scratch data folder - the real templates folder is
// never touched: labels and order, tooltips, unticking writes War.DisabledBuffs, and a damaged PNG never throws (the next
// folder, else the label alone).
{
    string root = AppPaths.Root, assetsFolder = AppPaths.AssetsFolder;
    string scratch = Path.Combine(Path.GetTempPath(), $"wuxauto-buffs-{Guid.NewGuid():N}");
    string scratchAssets = Path.Combine(scratch, "assets");
    try
    {
        AppPaths.Configure(scratch, scratchAssets);
        Directory.CreateDirectory(AppPaths.TemplateFolder);
        Directory.CreateDirectory(scratchAssets);
        File.WriteAllText(Path.Combine(AppPaths.TemplateFolder, "warbuff1.png"), "not a png");   // damaged here, fine in Assets
        File.Copy(Path.Combine(assets, "warbuff1.png"), Path.Combine(scratchAssets, "warbuff1.png"));
        File.WriteAllText(Path.Combine(AppPaths.TemplateFolder, "warbuff3.png"), "not a png");   // damaged in both
        File.WriteAllBytes(Path.Combine(scratchAssets, "warbuff3.png"), [0x89, 0x50, 0x4E, 0x47, 0, 0, 0]);

        var buffSettings = new AppSettings();
        var switches = WuxAuto.ViewModels.WarBuffSwitch.For(buffSettings);
        string labels = string.Join(", ", switches.Select(w => w.Label));
        var weapon2 = switches.First(w => w.Label == "Weapon 2");
        int changed = 0;
        weapon2.PropertyChanged += (_, _) => changed++;
        weapon2.IsOn = false;
        string disabledOff = string.Join(",", buffSettings.War.DisabledBuffs);
        weapon2.IsOn = true;
        var xBuff = switches.First(w => w.Template == "warbuff4");
        var item = switches.First(w => w.Template == "waritem1");
        Check("war buff switches",
            labels == "Buff 1, Buff 2, Buff 3, Buff 4, Buff 5, Weapon 1 (sword), Weapon 2, Weapon 3, Item 1, Item 2"
            && switches[5].Template == "warbuff2" && weapon2.Template == "warweapon1"
            && disabledOff == "warweapon1" && buffSettings.War.DisabledBuffs.Count == 0 && changed == 2
            && xBuff.Label == "Buff 3" && xBuff.ToolTip.Contains("X buff") && xBuff.ToolTip.EndsWith("warbuff4.png")
            && item.ToolTip.Contains("also clicked with autobuff") && item.ToolTip.EndsWith("waritem1.png")
            && switches[0].Icon != null && switches[1].Icon == null && item.Icon == null,
            $"{labels}; untick Weapon 2 -> DisabledBuffs [{disabledOff}], tick -> [{string.Join(",", buffSettings.War.DisabledBuffs)}], "
            + $"{changed} change(s); icons: warbuff1 {(switches[0].Icon != null ? "from Assets" : "none")}, warbuff3 "
            + $"{(switches[1].Icon != null ? "loaded?" : "none (damaged)")}; X buff tip '{xBuff.ToolTip}'");
    }
    catch (Exception ex)
    {
        Check("war buff switches", false, ex.ToString());
    }
    finally
    {
        AppPaths.Configure(root, assetsFolder);
        try { Directory.Delete(scratch, recursive: true); } catch { }
    }
}

Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;

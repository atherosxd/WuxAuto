using System.IO;
using OpenCvSharp;
using WuxAuto.Automation;
using WuxAuto.Core;
using WuxAuto.SelfTest;
using WuxAuto.Vision;

// Offline sanity check for the vision stack: OpenCV natives, template matching, OCR and the
// compass detector. Everything runs against a synthetic frame, so no game (and no input) is
// involved. Run with:  dotnet run --project tests\WuxAuto.SelfTest [assetsFolder]

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

int shuraIndex = Array.IndexOf(args, "--shura");
if (shuraIndex >= 0 && shuraIndex + 1 < args.Length)
    return FrameProbe.Shura(args[shuraIndex + 1]);

int lodPartsIndex = Array.IndexOf(args, "--lodparts");
if (lodPartsIndex >= 0 && lodPartsIndex + 1 < args.Length)
    return FrameProbe.LodParts(args[lodPartsIndex + 1]);

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
Console.WriteLine($"OpenCV: {Cv2.GetVersionString()}");

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
using var frame = new Mat(1080, 1920, MatType.CV_8UC4, new Scalar(22, 20, 18, 255));

void PasteGray(string template, int x, int y)
{
    var image = library.Get(template) ?? throw new InvalidOperationException($"missing {template}");
    using var color = new Mat();
    Cv2.CvtColor(image.Gray, color, ColorConversionCodes.GRAY2BGRA);
    using var roi = new Mat(frame, new Rect(x, y, color.Width, color.Height));
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
Cv2.PutText(frame, "Labyrinth", new Point(700, 520), HersheyFonts.HersheySimplex, 1.1,
    new Scalar(245, 245, 245, 255), 2, LineTypes.AntiAlias);
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
    using var scene = new Mat(side, side, MatType.CV_8UC4, new Scalar(0, 0, 0, 255));

    var flash = library.Get("flashlight")!;
    int padSize = (int)Math.Ceiling(Math.Sqrt(flash.Width * (double)flash.Width + flash.Height * (double)flash.Height)) + 2;
    if (padSize % 2 != 0)
        padSize++;

    using var padded = new Mat(padSize, padSize, MatType.CV_8UC1, Scalar.Black);
    int left = (padSize - flash.Width) / 2;
    int top = (padSize - flash.Height) / 2;
    using (var inner = new Mat(padded, new Rect(left, top, flash.Width, flash.Height)))
        flash.Gray.CopyTo(inner);

    using var rotation = Cv2.GetRotationMatrix2D(new Point2f(padSize / 2f, padSize / 2f), visualAngle, 1.0);
    using var rotated = new Mat();
    Cv2.WarpAffine(padded, rotated, rotation, new Size(padSize, padSize), InterpolationFlags.Linear, BorderTypes.Constant, Scalar.Black);

    using var rotatedColor = new Mat();
    Cv2.CvtColor(rotated, rotatedColor, ColorConversionCodes.GRAY2BGRA);
    int offset = (side - padSize) / 2;
    using (var roi = new Mat(scene, new Rect(offset, offset, padSize, padSize)))
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

// ---------------------------------------------------------------- high level scan
using var analyzer = new ScreenAnalyzer(new AppSettings());
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
using (var lodFrame = new Mat(1080, 1920, MatType.CV_8UC4, new Scalar(22, 20, 18, 255)))
{
    var lavender = new Scalar(250, 170, 235, 255);
    foreach (var (from, to) in new[] { (10, 80), (100, 170), (190, 350) })
        Cv2.Ellipse(lodFrame, new Point(1000, 450), new Size(130, 55), 0, from, to, lavender, 6, LineTypes.AntiAlias);
    Cv2.Rectangle(lodFrame, new Rect(500, 800, 10, 10), lavender, -1);

    var cyan = new Scalar(230, 230, 0, 255);
    foreach (int y in new[] { 230, 270, 310, 350 })
        Cv2.Circle(lodFrame, new Point(990, y), 14, cyan, -1, LineTypes.AntiAlias);
    Cv2.Circle(lodFrame, new Point(900, 150), 10, cyan, -1);
    Cv2.Circle(lodFrame, new Point(940, 150), 10, cyan, -1);

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
    using var reread = Cv2.ImRead(saved[^1]);
    Check("kill screenshots",
        saved.All(f => f.Length > 0) && left.Length == 2 && left[0]!.EndsWith("_b_second.jpg") && left[1]!.EndsWith("_c_third.jpg")
        && reread.Width == frame.Width && reread.Height == frame.Height && reread.Channels() == 3,
        $"kept {string.Join(", ", left)}; reread {reread.Width}x{reread.Height}x{reread.Channels()}");
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

Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;

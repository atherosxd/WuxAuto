using System.Drawing;
using Emgu.CV;
using Emgu.CV.CvEnum;
using WuxAuto.Automation;
using WuxAuto.Core;
using WuxAuto.Vision;

namespace WuxAuto.Core.Tests;

/// <summary>
/// The phone's teleport list on a lagging capture (live MuMu 2026-10-30, user: "it is going to page 3 instead of page
/// 2"): a frame shows the list as it was a second ago, so a page tap is only seen later. The pilot must act on a frame
/// whose "n/N" counter shows the page it is on.
/// </summary>
public class PhoneTeleportListTests : AppPathsScope
{
    const int LagMs = 1000;
    static readonly Rectangle Next = new(1062, 264, 24, 32);  // the list's ">" (MuMu 1920x1080: 1073,280)
    static readonly Rectangle Prev = new(834, 264, 24, 32);   // its "<"

    static string[][] Pages =>
    [
        ["[Piercing City]", "[Crystal Waters]", "[Bamboo Crane Woods]", "[Fragrance Village]", "[Bounded Dragon Pass]", "[Ghost Valley]"],
        ["[Bohai Outpost (Adept)]", "[Highlands (Adept)]", "[Coliseum]", "[ATTR Dungeon 5 (B.Dragon)]", "[Maya Cliff]", "[The Labyrinth (Adept)]"],
        ["[Mystical Grounds (WD)]", "[Dark Soul Cave 1F (WD)]", "[Dark Soul Cave 2F (WD)]", "[Taji Island (WD)]", "[Yinyang Island (WD)]", "[Four Realms Island (WD)]"],
    ];

    static int RowY(int row) => 330 + row * 40;

    [Theory]
    [InlineData(true)]  // the list opens late: the first read sees no list at all
    [InlineData(false)]
    public async Task LaggingFrames_PageOnce_ThenTapTheLabyrinthOnPage2(bool opensLate)
    {
        string assets = Path.Combine(TempRoot(), "assets");
        Directory.CreateDirectory(assets);
        foreach (string png in Directory.GetFiles(PhonePilot.PhoneAssets, "*.png")
                     .Concat(Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Assets"), "*.png")))
            File.Copy(png, Path.Combine(assets, Path.GetFileName(png)), overwrite: true);

        var settings = PhonePilot.PhoneLodSettings();
        settings.Sequence.Mode = RunMode.AutoLaby;
        using var phone = new PhonePilot(TempRoot(), settings, assets);

        // The game: the real page changes at once on a tap of ">", the capture shows it LagMs later.
        int realPage = 1;
        long openedAt = Environment.TickCount64 + (opensLate ? 700 : -LagMs);
        var history = new List<(long At, int Page)> { (long.MinValue, 1) };
        int? tappedLabyOnPage = null;
        phone.Touch.OnTap = (x, y) =>
        {
            if (Next.Contains(x, y) && realPage < 3)
                history.Add((Environment.TickCount64, ++realPage));
            else if (Math.Abs(y - RowY(5) - 10) <= 12 && x is > 800 and < 1120)
                tappedLabyOnPage ??= realPage;
        };

        int Shown()
        {
            long seen = Environment.TickCount64 - LagMs;
            return history.Last(h => h.At <= seen).Page;
        }

        bool ListUp() => Environment.TickCount64 - LagMs >= openedAt;

        using var grey = PhoneFrames.Grey();
        var frames = Enumerable.Range(1, 3).Select(p => ListFrame(assets, p)).ToArray();
        phone.Ocr.Scene = () =>
        {
            if (!ListUp())
                return [FakeOcrEngine.Line("WTS WLC BOX X5 300 GP", 640, 700)]; // chat only: the list is not up yet
            int page = Shown();
            return
            [
                FakeOcrEngine.Line("TELEPORT SCROLL:", 820, 190, 300, 30),
                FakeOcrEngine.Line($"{page}/3", 940, 270, 40, 20),
                .. Pages[page - 1].Select((name, row) => FakeOcrEngine.Line(name, 840, RowY(row), 260, 20)),
            ];
        };

        phone.Pilot.Start(PilotPhase.FindDestination);
        long deadline = Environment.TickCount64 + 20000;
        while (tappedLabyOnPage == null && phone.Pilot.IsRunning && Environment.TickCount64 < deadline)
        {
            await phone.Pilot.TickAsync(ListUp() ? frames[Shown() - 1] : grey, CancellationToken.None);
            await Task.Delay(15);
        }

        phone.Pilot.Stop("test over");
        foreach (var f in frames)
            f.Dispose();
        Assert.True(tappedLabyOnPage == 2, $"the Labyrinth was tapped on page {tappedLabyOnPage?.ToString() ?? "-"}:\n{phone.Log}");
        Assert.Equal(2, realPage); // one page tap, not two
    }

    [Theory]
    [InlineData("2/3", 2)]
    [InlineData(" 1 / 3 ", 1)]
    [InlineData("3/3", 3)]
    [InlineData("4/3", null)]
    [InlineData("12/3", null)]
    [InlineData("Lv 145", null)]
    public void TeleportListPage_ReadsTheCounter(string line, int? page) =>
        Assert.Equal(page, AutomationPilot.TeleportListPage([FakeOcrEngine.Line(line, 0, 0)]));

    /// <summary>A grey frame with the list's arrows as on <paramref name="page"/> of 3 (no "&lt;" on 1, no "&gt;" on 3).</summary>
    static Mat ListFrame(string assets, int page)
    {
        var frame = PhoneFrames.Grey();
        using var chevron = CvInvoke.Imread(Path.Combine(assets, "rightchevron.png"), ImreadModes.ColorBgr);
        using var bgra = new Mat();
        CvInvoke.CvtColor(chevron, bgra, ColorConversion.Bgr2Bgra);
        if (page < 3)
        {
            using var target = new Mat(frame, new Rectangle(Next.X, Next.Y, bgra.Width, bgra.Height));
            bgra.CopyTo(target);
        }

        if (page > 1)
        {
            using var mirrored = new Mat();
            CvInvoke.Flip(bgra, mirrored, FlipType.Horizontal);
            using var target = new Mat(frame, new Rectangle(Prev.X, Prev.Y, mirrored.Width, mirrored.Height));
            mirrored.CopyTo(target);
        }

        return frame;
    }
}

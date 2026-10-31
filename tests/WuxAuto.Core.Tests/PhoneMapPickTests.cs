using System.Drawing;
using Emgu.CV;
using Emgu.CV.CvEnum;
using WuxAuto.Automation;
using WuxAuto.Core;
using WuxAuto.Vision;

namespace WuxAuto.Core.Tests;

/// <summary>
/// The phone's Labyrinth map pick (live MuMu 2026-10-30: four entries lost at stage 1 - the OCR timed out, an empty read
/// paged the list away from a name that was on screen, and after the name tap the slow re-reads lost the row until the
/// stage gate closed).
/// </summary>
public class PhoneMapPickTests : AppPathsScope
{
    const int ChevronX = 1645;
    static readonly string[] Rows =
    [
        "Sicus Wielder", "Zahar Locksmith", "Gluttony Demon", "Serpent Knight", "Goblinkin Chieftain", "Ragesoul Heathen",
        "Sacred Sky Lord", "Sanctified Unicorn", "Spectral", "Sundersword", "Reanimated Master",
    ];

    static int RowY(int row) => 275 + row * 62;

    [Fact]
    public async Task EmptyReads_NoPaging_TheRowsChevronTappedAtOnce()
    {
        string assets = Path.Combine(TempRoot(), "assets");
        Directory.CreateDirectory(assets);
        foreach (string png in Directory.GetFiles(PhonePilot.PhoneAssets, "*.png")
                     .Concat(Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Assets"), "*.png")))
            File.Copy(png, Path.Combine(assets, Path.GetFileName(png)), overwrite: true);

        var settings = PhonePilot.PhoneLodSettings();
        settings.Sequence.Mode = RunMode.AutoLaby;
        using var phone = new PhonePilot(TempRoot(), settings, assets);

        // The map window: its title, "Spawns", one '>' per row. The first three reads of the monster list see no rows (a
        // timed-out OCR, the wide fallback read too) - the list region is the only area wide enough to hold both the names and the '>' column.
        int listReads = 0, screenReads = 0, wideReads = 0;
        bool mapOpen = true;
        phone.Touch.OnTap = (x, _) =>
        {
            if (x > ChevronX - 30)
                mapOpen = false; // a '>' tap walks the character there and shuts the map
        };
        phone.Ocr.SceneFor = area =>
        {
            if (area.Width >= 1900 && area.Height >= 1000)
                screenReads++;
            else if (area.X < 700 && area.Width > 1200)
                wideReads++;
            if (!mapOpen)
                return [FakeOcrEngine.Line("[The Labyrinth (Adept)]", 1600, 60)];
            List<OcrWord> lines = [FakeOcrEngine.Line("The Labyrinth (Adept)", 310, 85, 350, 30), FakeOcrEngine.Line("Spawns", 1560, 220, 60, 16)];
            bool listRead = area.X <= 1176 && area.Right >= ChevronX && area.Y <= RowY(0) && area.Bottom >= RowY(10);
            if (!listRead || ++listReads > 3)
                lines.AddRange(Rows.Select((name, row) => FakeOcrEngine.Line(name, 1176, RowY(row) - 8, 180, 16)));
            return lines;
        };

        using var frame = PhoneFrames.Grey();
        using (var chevron = CvInvoke.Imread(Path.Combine(assets, "rightchevron.png"), ImreadModes.ColorBgr))
        using (var bgra = new Mat())
        {
            CvInvoke.CvtColor(chevron, bgra, ColorConversion.Bgr2Bgra);
            for (int row = 0; row < Rows.Length; row++)
            {
                using var target = new Mat(frame, new Rectangle(ChevronX, RowY(row) - bgra.Height / 2, bgra.Width, bgra.Height));
                bgra.CopyTo(target);
            }
        }

        // The list's down arrow, a light glyph above "Spawns" (what the old flow paged with).
        CvInvoke.Rectangle(frame, new Rectangle(1596, 186, 22, 12), new Emgu.CV.Structure.MCvScalar(235, 235, 235, 255), -1);

        phone.Pilot.Start(PilotPhase.ReadStage, stageIndex: 0);
        long deadline = Environment.TickCount64 + 20000;
        while (phone.Pilot.Phase != PilotPhase.WaitBoss && phone.Pilot.IsRunning && Environment.TickCount64 < deadline)
        {
            await phone.Pilot.TickAsync(frame, CancellationToken.None);
            await Task.Delay(15);
        }

        var phase = phone.Pilot.Phase;
        phone.Pilot.Stop("test over");
        Assert.True(phase == PilotPhase.WaitBoss, phone.Log);
        // Cheaper reads (user, 2026-10-31): no whole-screen read and no wide fallback read during the phone's map pick.
        Assert.Equal(0, screenReads);
        Assert.Equal(0, wideReads);
        Assert.DoesNotContain(phone.Lines, l => l.Contains("is not visible", StringComparison.Ordinal)); // never paged (arrow or swipe)
        Assert.DoesNotContain(phone.Touch.Calls, c => c.StartsWith("Swipe", StringComparison.Ordinal) || c.StartsWith("Drag", StringComparison.Ordinal));
        Assert.DoesNotContain(phone.Lines, l => l.Contains("Selected the", StringComparison.Ordinal)); // no name tap on the phone
        var tap = Assert.Single(phone.Touch.Taps);
        Assert.InRange(tap.X, ChevronX - 10, ChevronX + 40);
        Assert.InRange(tap.Y, RowY(7) - 16, RowY(7) + 16); // Sanctified Unicorn's row

    }
}

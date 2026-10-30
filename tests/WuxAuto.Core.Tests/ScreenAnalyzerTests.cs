using System.Drawing;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using WuxAuto.Vision;

namespace WuxAuto.Core.Tests;

public class ScreenAnalyzerTests
{
    static OcrWord W(string word, string line, PixelRect lineBounds)
        => new(word, new PixelRect(lineBounds.X, lineBounds.Y, 10, lineBounds.Height), line, lineBounds);

    static TemplateMatch Hit(double score, int x) => new("shura", score, new PixelRect(x, 990, 40, 40), 1.0);

    [Fact]
    public void ToLines_OneEntryPerLineBox_TopToBottom()
    {
        var mp = new PixelRect(180, 61, 120, 14);
        var hp = new PixelRect(180, 34, 120, 16);
        var right = new PixelRect(400, 34, 60, 16);
        var words = new[]
        {
            W("9990", "9990 / 11467", mp), W("/", "9990 / 11467", mp), W("11467", "9990 / 11467", mp),
            W("10/10", "10/10", right),
            W("42833", "42833 / 42833", hp), W("/", "42833 / 42833", hp), W("42833", "42833 / 42833", hp),
        };

        var lines = ScreenAnalyzer.ToLines(words);

        Assert.Equal(
            new[] { ("42833 / 42833", hp), ("10/10", right), ("9990 / 11467", mp) },
            lines.ToArray());
    }

    [Fact]
    public void ToLines_NoWords_NoLines() => Assert.Empty(ScreenAnalyzer.ToLines([]));

    [Fact]
    public async Task ReadSettingRow_ChevronPastThe30HeightBand_FoundOnlyWithChevronReach()
    {
        // MuMu 1920x1080: "Font size" 14 px high ends at x 779, so the 30-height band ends at 1199; the right chevron sits
        // at 1200 (left one at 999, value "110%" at 1083) - the phone's visit could not raise the font.
        var ocr = new FakeOcrEngine
        {
            Scene = () =>
            [
                .. FakeOcrEngine.Words("Font size", 702, 348, 77, 14),
                FakeOcrEngine.Line("110%", 1083, 348, 43, 14),
            ],
        };
        using var analyzer = new ScreenAnalyzer(new AppSettings(), ocr);
        using var frame = new Mat(1080, 1920, DepthType.Cv8U, 4);
        frame.SetTo(new MCvScalar(30, 30, 30, 255));
        var white = new MCvScalar(255, 255, 255, 255);
        CvInvoke.Rectangle(frame, new Rectangle(999, 342, 12, 22), white, -1);   // '<'
        CvInvoke.Rectangle(frame, new Rectangle(1200, 342, 12, 22), white, -1);  // '>'

        var desktop = await analyzer.ReadSettingRowAsync(frame, "Font size");
        var phone = await analyzer.ReadSettingRowAsync(frame, "Font size", chevronReach: 10);

        Assert.Equal(110, desktop?.Percent);
        Assert.Equal(999, desktop?.Left?.X);
        Assert.Null(desktop?.Right);                                              // the desktop's band, unchanged
        Assert.Equal(999, phone?.Left?.X);
        Assert.Equal(1200, phone?.Right?.X);
    }

    [Fact]
    public async Task ReadScaleRow_MinusIsTheDash_NotTheAutoButtonsBorder()
    {
        // MuMu 2560x1440 at scale 1.11: "Interface scale [AUTO] - 1.11 +" - the AUTO button's right border (a tall thin
        // line) lies between the button and the dash, and was tapped as the '-' (the scale went to AUTO).
        var ocr = new FakeOcrEngine
        {
            Scene = () =>
            [
                .. FakeOcrEngine.Words("Interface scale", 100, 200, 180, 24),
                FakeOcrEngine.Line("AUTO", 330, 200, 60, 24),
                FakeOcrEngine.Line("1.11", 520, 200, 50, 24),
            ],
        };
        using var analyzer = new ScreenAnalyzer(new AppSettings(), ocr);
        using var frame = new Mat(400, 900, DepthType.Cv8U, 4);
        frame.SetTo(new MCvScalar(30, 30, 30, 255));
        var white = new MCvScalar(255, 255, 255, 255);
        CvInvoke.Rectangle(frame, new Rectangle(404, 196, 3, 32), white, -1);   // the AUTO button's right border
        CvInvoke.Rectangle(frame, new Rectangle(450, 211, 24, 2), white, -1);   // the dash, 2 px tall as at 2560x1440
        CvInvoke.Rectangle(frame, new Rectangle(620, 200, 24, 24), white, -1);  // the plus

        var row = await analyzer.ReadScaleRowAsync(frame);

        Assert.NotNull(row);
        Assert.Equal(1.11, row.Value);
        Assert.False(row.Auto);
        Assert.Equal((450, 211), (row.Minus?.X, row.Minus?.Y));          // the dash, not the border at 404
        Assert.Equal((620, 200), (row.Plus?.X, row.Plus?.Y));
    }

    [Fact]
    public void BestLeftmost_NoHits_Null() => Assert.Null(ScreenAnalyzer.BestLeftmost([]));

    [Fact]
    public void BestLeftmost_ClearlyBetterScore_WinsEvenWhenFurtherRight()
    {
        var left = Hit(0.95, 600);
        var right = Hit(0.99, 1200);

        Assert.Same(right, ScreenAnalyzer.BestLeftmost([left, right]));
    }

    [Fact]
    public void BestLeftmost_ScoresWithinOneHundredth_LeftmostWins()
    {
        // The same icon in two slots (1920x1080 bar: Shura at 1035 and 1240), in the matcher's score order.
        var right = Hit(1.000, 1240);
        var left = Hit(0.992, 1035);
        var far = Hit(0.95, 700);

        Assert.Same(left, ScreenAnalyzer.BestLeftmost([right, left, far]));
        Assert.Same(left, ScreenAnalyzer.BestLeftmost([far, left, right]));
    }

    // One text line 16 px high: glyphs 10x16, dots 3x3 (a colon's at y 4 and y 11).
    static PixelRect Glyph(int x) => new(x, 0, 10, 16);
    static PixelRect Dot(int x, int y) => new(x, y, 3, 3);

    [Fact]
    public void ColonCuts_TheTwoColonsOfATime()
    {
        // "00:04:25", in the order the blob search may hand them over.
        var blobs = new[]
        {
            Dot(56, 11), Glyph(0), Glyph(12), Dot(25, 4), Dot(25, 11), Glyph(31), Glyph(43), Dot(56, 4), Glyph(62), Glyph(74),
        };

        Assert.Equal(new[] { 28, 59 }, ScreenAnalyzer.ColonCuts(blobs, 16));
    }

    [Fact]
    public void ColonCuts_FullStopAndSideBySideDots_AreNone()
    {
        Assert.Empty(ScreenAnalyzer.ColonCuts([Glyph(0), Dot(12, 13), Glyph(17)], 16));                // "4.2"
        Assert.Empty(ScreenAnalyzer.ColonCuts([Glyph(0), Dot(12, 13), Dot(17, 13), Glyph(22)], 16));   // "4..2"
    }

    [Fact]
    public void ColonCuts_NeedAGlyphCloseOnBothSides()
    {
        Assert.Empty(ScreenAnalyzer.ColonCuts([Glyph(0), Glyph(12), Dot(25, 4), Dot(25, 11)], 16));                // "Time:" at the line's end
        Assert.Empty(ScreenAnalyzer.ColonCuts([Glyph(0), Dot(25, 4), Dot(25, 11), Glyph(48)], 16));                // a glyph 20 px away (over a text height)
        Assert.Empty(ScreenAnalyzer.ColonCuts([new PixelRect(0, -10, 10, 40), Dot(12, 4), Dot(12, 11), Glyph(17)], 16));  // the window border is no glyph
    }

    static bool Inside(PixelRect inner, PixelRect outer)
        => inner.X >= outer.X && inner.Y >= outer.Y && inner.Right <= outer.Right && inner.Bottom <= outer.Bottom;

    // The user's TRIBE STATUS screenshot (795x829): title box, X, the window's right edge at 795.
    static readonly PixelRect TribeTitle = new(322, 8, 145, 16);
    static readonly PixelRect TribeX = new(753, 12, 14, 14);

    [Fact]
    public void WarCloseBand_TribeStatus_HoldsTheXAndStopsInsideTheWindow()
    {
        var band = ScreenAnalyzer.WarCloseBand(TribeTitle, 22, 2.7, 795, 829);

        Assert.True(Inside(TribeX, band), band.ToString());
        Assert.True(band.Right < 795, band.ToString());
        Assert.Equal((0, 32), (band.Y, band.Bottom));   // the title row, half a title height above and below
    }

    [Theory]
    [InlineData(14)]   // OCR's title box a little shorter: the height bound still reaches the X
    [InlineData(19)]   // taller: the width bound keeps the band inside the window
    public void WarCloseBand_TribeStatus_TitleHeightReadDifferently(int height)
    {
        var band = ScreenAnalyzer.WarCloseBand(TribeTitle with { Height = height }, 22, 2.7, 795, 829);

        Assert.True(band.X <= TribeX.X && band.Right >= TribeX.Right, band.ToString());
        Assert.True(band.Right < 795, band.ToString());
    }

    [Fact]
    public void WarCloseBand_TribeStatus_InAFullFrame_StopsAtTheWindowNotTheFrame()
        => Assert.True(ScreenAnalyzer.WarCloseBand(TribeTitle, 22, 2.7, 1920, 1080).Right < 795);

    [Fact]
    public void WarCloseBand_Menu_HoldsTheX_OnTheTitlesRows_StopsInsideTheWindow()
    {
        // The menu screenshot's faint title glyphs, its X and its right edge (~380).
        var title = new PixelRect(171, 22, 55, 13);
        var band = ScreenAnalyzer.WarCloseBand(title, 12, 3.2, 1920, 1080);

        Assert.True(Inside(new PixelRect(352, 26, 14, 13), band), band.ToString());
        Assert.True(band.Right < 380, band.ToString());
        Assert.True(band.Bottom <= title.Bottom + title.Height / 2 && band.Y >= title.Y - title.Height / 2, band.ToString());
    }

    static readonly PixelRect TribeCornerOrnament = new(775, 0, 10, 13);   // between the X and the window's edge

    [Fact]
    public void PickWarClose_TribeStatus_TheXNotTheCornerOrnamentRightOfIt()
        => Assert.Equal(TribeX, ScreenAnalyzer.PickWarClose([new PixelRect(480, 12, 8, 10), TribeCornerOrnament, TribeX], TribeTitle, 2.52));

    [Fact]
    public void PickWarClose_NothingNearTheXsPlace_IsNone()
        => Assert.Null(ScreenAnalyzer.PickWarClose([new PixelRect(480, 12, 8, 10), TribeCornerOrnament], TribeTitle, 2.52));

    [Fact]
    public void PickWarClose_TitleBoxReadAFewPixelsNarrower_StillTheX()
        => Assert.Equal(TribeX, ScreenAnalyzer.PickWarClose([TribeCornerOrnament, TribeX], TribeTitle with { Width = 142 }, 2.52));

    [Fact]
    public void PickWarClose_Menu_TheXNotTheTileLabelsLeftOfIt()
    {
        // The scrolled grid's "Inventory (I)" shares the MENU title's rows (the user's screenshot).
        var x = new PixelRect(352, 26, 14, 13);
        var labels = new[] { new PixelRect(279, 28, 3, 11), new PixelRect(290, 31, 6, 8), new PixelRect(320, 31, 6, 8) };

        Assert.Equal(x, ScreenAnalyzer.PickWarClose([.. labels, x], new PixelRect(171, 22, 55, 13), 2.93));
    }

    [Fact]
    public void CountdownBand_RightOfTheRow_AtMostTwentyLineHeights()
    {
        var row = new PixelRect(449, 243, 152, 16);   // the screenshot's row; its time is at 704-772

        Assert.Equal(PixelRect.FromBounds(601, 235, 795, 267), ScreenAnalyzer.CountdownBand(row, 795, 829));    // clamped to the frame
        Assert.Equal(PixelRect.FromBounds(601, 235, 921, 267), ScreenAnalyzer.CountdownBand(row, 1920, 1080)); // 20 x 16 px
    }
    [Fact]
    public void RedTextMask_RedTextBright_WhiteGreyBlackAndOrangeDark()
    {
        // BGRA: the sample's red name, the white HP numbers, black, grey, an orange-ish brown, a pale pink.
        var colours = new[]
        {
            (new MCvScalar(40, 40, 220, 255), true),     // red name
            (new MCvScalar(60, 50, 180, 255), true),     // darker red
            (new MCvScalar(240, 240, 240, 255), false),  // white numbers
            (new MCvScalar(0, 0, 0, 255), false),
            (new MCvScalar(128, 128, 128, 255), false),
            (new MCvScalar(40, 140, 200, 255), false),   // orange: green too high
            (new MCvScalar(200, 190, 230, 255), false),  // pale pink: too little red over the rest
        };
        using var image = new Mat(1, colours.Length, DepthType.Cv8U, 4);
        for (int i = 0; i < colours.Length; i++)
        {
            using var px = new Mat(image, new Rectangle(i, 0, 1, 1));
            px.SetTo(colours[i].Item1);
        }

        using var mask = ScreenAnalyzer.RedTextMask(image);
        Assert.Equal(1, mask.NumberOfChannels);
        var values = new byte[colours.Length];
        mask.CopyTo(values);
        for (int i = 0; i < colours.Length; i++)
            Assert.True(colours[i].Item2 ? values[i] == 255 : values[i] == 0, $"colour {i}: {values[i]}");
    }
}

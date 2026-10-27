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
}

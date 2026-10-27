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
}

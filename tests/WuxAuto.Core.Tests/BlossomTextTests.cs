using WuxAuto.Automation;
using WuxAuto.Core;

namespace WuxAuto.Core.Tests;

/// <summary>Auto Blossom's text rules (user, 2026-10-30): full-health targets only, and the chat's AFK Checker count.</summary>
public class BlossomTextTests
{
    private const int W = 2048, H = 1152;
    private static readonly PixelRect Box = new(512, 0, 1024, 184);   // the target box (0.25, 0, 0.50, 0.16)

    [Theory]
    [InlineData("615378/750000", 615378, 750000)]   // live: a Jaso another player was hitting
    [InlineData("750000/750000", 750000, 750000)]
    [InlineData("75OOOO / 750000", 750000, 750000)]
    public void TargetHp_ReadsTheBarsNumbers(string text, int cur, int max)
        => Assert.Equal((cur, max), BlossomText.TargetHp([(text, new PixelRect(960, 100, 130, 20))], Box));

    [Fact]
    public void TargetHp_NoNumbersLine_IsNull()
        => Assert.Null(BlossomText.TargetHp([("[S]Jaso Practitioner", new PixelRect(940, 70, 170, 20))], Box));

    [Fact]
    public void AfkChecker_TheNewestLineDecides()
    {
        // Live snapshot 10:20: "Start 1/5", then a chat line, then "Reset 0/5" further down.
        (string, PixelRect)[] lines =
        [
            ("(Whisper) [AFK Checker] : Start 1/5", new PixelRect(40, 900, 320, 20)),
            ("[CLAN][MaYaRi] trade 3k cp to SOE", new PixelRect(40, 930, 320, 20)),
            ("(Whisper) [AFK Checker] : Reset 0/5", new PixelRect(40, 960, 320, 20)),
        ];
        Assert.Equal(0, BlossomText.AfkCheckerCount(lines, "AFK Checker", W, H));
        Assert.Equal(1, BlossomText.AfkCheckerCount(lines[..2], "AFK Checker", W, H));
    }

    [Theory]
    [InlineData("(Whisper) [AFK Checker] : Start 3/5", 3)]
    [InlineData("(Whisper) [AFK Checker]: Start 2 / 5", 2)]
    public void AfkChecker_ReadsTheCount(string text, int count)
        => Assert.Equal(count, BlossomText.AfkCheckerCount([(text, new PixelRect(40, 960, 320, 20))], "AFK Checker", W, H));

    [Fact]
    public void AfkChecker_OutsideTheChat_OrNoCount_IsNull()
    {
        Assert.Null(BlossomText.AfkCheckerCount([("[AFK Checker] : Start 1/5", new PixelRect(1500, 960, 320, 20))], "AFK Checker", W, H));
        Assert.Null(BlossomText.AfkCheckerCount([("[AFK Checker] : Start 1/5", new PixelRect(40, 300, 320, 20))], "AFK Checker", W, H));
        Assert.Null(BlossomText.AfkCheckerCount([("[AFK Checker] says hi", new PixelRect(40, 960, 320, 20))], "AFK Checker", W, H));
        Assert.Null(BlossomText.AfkCheckerCount([("[AFK Checker] : Start 1/5", new PixelRect(40, 960, 320, 20))], "", W, H));
    }
}

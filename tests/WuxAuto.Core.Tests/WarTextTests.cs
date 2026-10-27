using WuxAuto.Automation;
using WuxAuto.Core;

namespace WuxAuto.Core.Tests;

public class WarTextTests
{
    static (string, PixelRect) L(string t, int x, int y, int w = 120, int h = 16) => (t, new PixelRect(x, y, w, h));

    [Theory]
    [InlineData("00:04:25", 265)]
    [InlineData("00 : 04 : 25", 265)]
    [InlineData("0O:O4:25", 265)]
    [InlineData("01:00:00", 3600)]
    [InlineData("Bohai Outpost (Adept) 00:04:25", 265)]
    public void ParseCountdown_ReadsNoisyTimes(string text, int seconds)
        => Assert.Equal(TimeSpan.FromSeconds(seconds), WarText.ParseCountdown(text));

    [Theory]
    [InlineData("In progress")]
    [InlineData("00:61:00")]
    [InlineData("")]
    public void ParseCountdown_RejectsNonTimes(string text) => Assert.Null(WarText.ParseCountdown(text));

    [Fact]
    public void FindWarRow_SeparateLinesOnOneRow()
    {
        var lines = new[] { L("NEXT WARS", 560, 212), L("Bohai Outpost (Adept)", 448, 241), L("00:04:25", 704, 241, 68),
                            L("Dragonfall Outpost", 448, 270), L("In progress", 694, 270, 78) };
        Assert.Equal(new WarCountdown(TimeSpan.FromSeconds(265), false), WarText.FindWarRow(lines, "Bohai Outpost (Adept)"));
    }

    [Fact]
    public void FindWarRow_MergedLineAndInProgress()
    {
        Assert.Equal(new WarCountdown(TimeSpan.FromSeconds(265), false),
            WarText.FindWarRow([L("Bohai Outpost (Adept) 00:04:25", 448, 241, 330)], "Bohai Outpost (Adept)"));
        Assert.Equal(new WarCountdown(null, true),
            WarText.FindWarRow([L("Bohai Outpost (Adept)", 448, 241), L("In progress", 694, 242, 78)], "Bohai Outpost (Adept)"));
    }

    [Fact]
    public void FindWarRow_MissingRowOrTimeIsNull()
    {
        Assert.Null(WarText.FindWarRow([L("Dragonfall Outpost", 448, 270), L("00:04:25", 704, 270, 68)], "Bohai Outpost (Adept)"));
        Assert.Null(WarText.FindWarRow([L("Bohai Outpost (Adept)", 448, 241)], "Bohai Outpost (Adept)"));
    }

    [Theory]
    [InlineData("[Bohai Outpost (Adept)] Confirm to move?", true)]
    [InlineData("[Bohai Outpost (Adept)] [10] minute(s) to opening of the entrance", false)]
    [InlineData("Fist Scroll Box Confirm to use?", false)]
    public void IsWarDialogLine(string line, bool expected) => Assert.Equal(expected, WarText.IsWarDialogLine(line, "Bohai"));

    [Fact]
    public void BestListEntry_CutTitlePicksTheEntry()
    {
        var entries = new[] { "[Dark Soul Cave]", "[Maya Cliff]", "[Coliseum]" };
        Assert.Equal("[Dark Soul Cave]", WarText.BestListEntry("[Dark Soul Ca", entries, out bool amb));
        Assert.False(amb);
    }

    [Fact]
    public void BestListEntry_NoisySuffixStillPicksTheLongerMatch()
    {
        var entries = new[] { "[Dark Soul Cave]", "[Dark Soul Cave X]" };
        Assert.Equal("[Dark Soul Cave X]", WarText.BestListEntry("[Dark Soul Cave X", entries, out bool amb));
        Assert.False(amb);
    }

    [Fact]
    public void BestListEntry_SharedPrefixIsAmbiguous()
    {
        var entries = new[] { "[Dark Soul Cave I]", "[Dark Soul Cave II]" };
        WarText.BestListEntry("[Dark Soul Cave IF", entries, out bool amb);
        Assert.True(amb);
    }

    [Fact]
    public void BestListEntry_NothingCloseIsNull()
        => Assert.Null(WarText.BestListEntry("[Piercing City]", ["[Dark Soul Cave]", "[Maya Cliff]"], out _));
}

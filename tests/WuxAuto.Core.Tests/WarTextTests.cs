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

    [Theory]
    [InlineData("[Dark Soul Cave", "[Dark Soul Cave I]", "[Dark Soul Cave II]")]
    [InlineData("[Dark Soul Ca", "[Dark Soul Cave I]", "[Dark Soul Cave II]")]
    [InlineData("[Dark Soul Cave", "[Dark Soul Cave]", "[Dark Soul Cave X]")]
    public void BestListEntry_PurePrefixOfTwoEntriesIsAmbiguous(string title, string a, string b)
    {
        WarText.BestListEntry(title, [a, b], out bool amb);
        Assert.True(amb);
    }

    [Fact]
    public void BestListEntry_CompleteTitleWinsOverLongerEntry()
    {
        Assert.Equal("[Dark Soul Cave]", WarText.BestListEntry("[Dark Soul Cave]", ["[Dark Soul Cave]", "[Dark Soul Cave X]"], out bool amb));
        Assert.False(amb);
    }

    [Fact]
    public void FindWarRow_NoticeLineBeforeTheRealRowIsSkipped()
    {
        var lines = new[] { L("[Bohai Outpost (Adept)] [10] minute(s) to opening of the entrance", 20, 400, 500),
                            L("Bohai Outpost (Adept)", 448, 241), L("00:04:25", 704, 241, 68) };
        Assert.Equal(new WarCountdown(TimeSpan.FromSeconds(265), false), WarText.FindWarRow(lines, "Bohai Outpost (Adept)"));
    }

    [Fact]
    public void BestListEntry_NothingCloseIsNull()
        => Assert.Null(WarText.BestListEntry("[Piercing City]", ["[Dark Soul Cave]", "[Maya Cliff]"], out _));

    [Fact]
    public void ParseHp_TopmostPairIsHp()
    {
        // 1920x1080 lod_no_entries frame: HP, MP, the item counter 10/10, the 0/3000 bar.
        var lines = new[] { L("9990 / 11467", 195, 61), L("42833 / 42833", 184, 34), L("10/10", 500, 50, 40), L("0/3000", 328, 110, 52) };
        Assert.Equal((42833, 42833), WarText.ParseHp(lines));
    }

    [Fact]
    public void ParseHp_ZeroAndOcrO()
    {
        Assert.Equal((0, 42833), WarText.ParseHp([L("0 / 42833", 184, 34), L("9990 / 11467", 195, 61)]));
        Assert.Equal((0, 42833), WarText.ParseHp([L("O / 42833", 184, 34)]));
        Assert.Null(WarText.ParseHp([L("[The Labyrinth (Adep", 1000, 70)]));
    }

    [Theory]
    [InlineData(0, 42833, 42833, true)]
    [InlineData(0, 3000, 42833, false)]    // the bar under the HP bar
    [InlineData(0, 11467, 42833, false)]   // MP read alone
    [InlineData(120, 42833, 42833, false)]
    [InlineData(0, 42833, null, true)]
    [InlineData(0, 300, null, false)]
    public void IsDeadRead(int cur, int max, int? aliveMax, bool dead) => Assert.Equal(dead, WarText.IsDeadRead((cur, max), aliveMax));

    [Fact]
    public void FindTargetName_NameAboveItsHpNumbers()
    {
        // 1273x1024 Laby frame: target bar "Spectral" with 9070530/47372000 under it, version line above.
        var lines = new[] { L("Ver: 0.2.492.1 — 10/25/2026 02:29:00", 675, 20, 330), L("Spectral", 630, 64, 70),
                            L("9070530/47372000", 590, 92, 140, 14), L("297", 890, 66, 30) };
        Assert.Equal("Spectral", WarText.FindTargetName(lines));
    }

    [Fact]
    public void FindTargetName_NoticesAndVersionLineAreNotTargets()
    {
        Assert.Null(WarText.FindTargetName([L("Ver: 0.2.492.1 — 10/24/2026 09:03:10", 1282, 20, 250), L("62ms — FPS: 41", 1610, 20, 120)]));
        Assert.Null(WarText.FindTargetName([L("[PvP] (Snake) iMstrG [Cape Box].", 833, 110, 250)]));
        Assert.Null(WarText.FindTargetName([L("297", 890, 66, 30), L("100/100", 880, 92, 60)]));
        // Each junk line with a numbers line right under it: only the ':' / '.' rules keep it out.
        Assert.Null(WarText.FindTargetName([L("Ver: 0.2.492.1 — 10/24/2026 09:03:10", 1282, 20, 250), L("100/100", 1300, 40, 60)]));
        Assert.Null(WarText.FindTargetName([L("62ms — FPS: 41", 1610, 20, 120), L("100/100", 1620, 40, 60)]));
        Assert.Null(WarText.FindTargetName([L("[PvP] (Snake) iMstrG [Cape Box].", 833, 110, 250), L("5/10", 900, 130, 40)]));
    }

    [Theory]
    [InlineData("Arrived: Cave Bat", "Cave Bat", true)]
    [InlineData("Arived: Cave Bat", "Cave Bat", true)]
    [InlineData("Arrived: Dark Spider", "Cave Bat", false)]
    [InlineData("Attack succeeded!Normal damage(+50)", "Cave Bat", false)]
    public void IsArrivedLine(string line, string monster, bool expected) => Assert.Equal(expected, WarText.IsArrivedLine(line, "Arrived", monster));

    [Theory]
    [InlineData("[Auto buff scroll]  02.11.2026", true)]
    [InlineData("[Auto buff scrol] 02.11.2026", true)]
    [InlineData("AUTO BUFF", false)]
    [InlineData("[Slayer Scroll] 171 (time(s))", false)]
    public void IsAutoBuffScrollLine(string line, bool expected) => Assert.Equal(expected, WarText.IsAutoBuffScrollLine(line));

    [Fact]
    public void NewLines_IsAMultisetDifference()
    {
        Assert.Equal(new[] { "arrivedcavebat" }, WarText.NewLines(["a", "b"], ["a", "b", "arrivedcavebat"]));
        Assert.Empty(WarText.NewLines(["arrivedcavebat", "a"], ["a", "arrivedcavebat"]));   // an old line moved up
        Assert.Equal(new[] { "a" }, WarText.NewLines(["a"], ["a", "a"]));
    }

    [Theory]
    [InlineData("Bohai Outpost (Adept)", true)]
    [InlineData("Bohai Outpost (Adep", true)]
    [InlineData("[Bohai Outpost (Adept)] [10] minute(s) to opening of the entrance", true)]
    [InlineData("Dragonfall Outpost", false)]
    [InlineData("Legacy Holy Stone", false)]
    public void IsWarRowLine(string line, bool expected) => Assert.Equal(expected, WarText.IsWarRowLine(line, "Bohai Outpost (Adept)"));

    [Fact]
    public void IsWarRowLine_EmptyRowTextMatchesNothing() => Assert.False(WarText.IsWarRowLine("Bohai Outpost (Adept)", " "));

    [Fact]
    public void WarRowLines_TheBareRowComesBeforeANoticeAboveIt()
    {
        var notice = L("[Bohai Outpost (Adept)] [10] minute(s) to opening", 10, 100, 400);
        var row = L("Bohai Outpost (Adept)", 448, 241);
        var lines = new[] { notice, L("NEXT WARS", 560, 212), row, L("Dragonfall Outpost", 448, 270) };

        Assert.Equal(new[] { row, notice }, WarText.WarRowLines(lines, "Bohai Outpost (Adept)"));
    }

    [Fact]
    public void WarRowLines_FragmentsAreNoCandidates_ARowSplitInTwoKeepsItsLongerPart()
    {
        // "Adept" (TRIBE STATUS's HIGHLANDS column) and "OUTPOST" name the row by containment only.
        var lines = new[] { L("Adept", 576, 373, 40), L("OUTPOST", 36, 344, 70), L("Bohai Outpost", 448, 241, 100), L("(Adept)", 552, 241, 50) };

        Assert.Equal(new[] { lines[2] }, WarText.WarRowLines(lines, "Bohai Outpost (Adept)"));
    }

    [Fact]
    public void TribeStatusShows_ByItsTitleOrTheBareRow_NotByTheChatNotice()
    {
        const string row = "Bohai Outpost (Adept)";
        var notice = L("[Bohai Outpost (Adept)] [10] minute(s) to opening", 10, 700, 400);

        Assert.True(WarText.TribeStatusShows([L("TRIBE STATUS", 322, 8, 145, 16), notice], row));
        Assert.True(WarText.TribeStatusShows([L("Bohai Outpost (Adept)", 449, 243)], row));
        Assert.False(WarText.TribeStatusShows([notice, L("Tribe Status", 21, 491, 81, 13)], row));   // the menu's tile is no title
        Assert.False(WarText.TribeStatusShows([], row));
    }

    [Fact]
    public void FindWindowTitle_OnlyTheCapitalsTitle_NotTheMenuTile()
    {
        var tile = L("Tribe Status", 21, 491, 81, 13);
        var title = L("TRIBE STATUS", 322, 8, 145, 16);

        Assert.Equal(title, WarText.FindWindowTitle([tile, title], "Tribe Status"));
        Assert.Null(WarText.FindWindowTitle([tile], "Tribe Status"));
    }

    [Theory]
    [InlineData("TRlBE STATUS", "Tribe Status", true)]   // one OCR slip, still mostly capitals
    [InlineData("TRIBE STATU", "Tribe Status", true)]
    [InlineData("MENU", "MENU", true)]
    [InlineData("Menu", "MENU", false)]
    [InlineData("HIDE ITEM APPLY STATUS", "Tribe Status", false)]
    [InlineData("DRAGON TRIBE", "Tribe Status", false)]
    [InlineData("MAIN", "MENU", false)]
    public void FindWindowTitle_Reads(string text, string title, bool found)
        => Assert.Equal(found, WarText.FindWindowTitle([L(text, 300, 10)], title) != null);
}

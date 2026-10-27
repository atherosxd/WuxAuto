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
    [InlineData(0, 34493, 42833, true)]    // buffs that raised the maximum dropped at death
    [InlineData(0, 21417, 42833, true)]    // half of it, rounded up
    [InlineData(0, 21416, 42833, false)]
    [InlineData(0, 44974, 42833, true)]    // up to 5 % over it
    [InlineData(0, 44975, 42833, false)]
    [InlineData(0, 50000, 42833, false)]
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

    [Theory]
    [InlineData("AUTO BUFF", true)]
    [InlineData("Auto buff", true)]
    [InlineData("AUTO BUF", true)]                          // one letter off
    [InlineData("AUT0 BUFF", true)]
    [InlineData("[Auto buff scroll]  02.11.2026", false)]   // the item list's line, not the dialog
    [InlineData("Auto buff scroll", false)]
    [InlineData("BUFF", false)]
    [InlineData("", false)]
    public void IsAutoBuffTitle(string line, bool expected) => Assert.Equal(expected, WarText.IsAutoBuffTitle(line));

    [Theory]
    [InlineData("Regular War", true)]
    [InlineData("Regu1ar War", true)]
    [InlineData("[] Regular War", true)]
    [InlineData("Regular", true)]
    [InlineData("Regular War participation is automatic", false)]   // a sentence, not the box's label
    [InlineData("[Tribe] Victory!", false)]
    [InlineData("", false)]
    public void IsRegularWarLine(string line, bool expected) => Assert.Equal(expected, WarText.IsRegularWarLine(line));

    [Fact]
    public void NewLines_IsAMultisetDifference()
    {
        Assert.Equal(new[] { "arrivedcavebat" }, WarText.NewLines(["a", "b"], ["a", "b", "arrivedcavebat"]));
        Assert.Empty(WarText.NewLines(["arrivedcavebat", "a"], ["a", "arrivedcavebat"]));   // an old line moved up
        Assert.Equal(new[] { "a" }, WarText.NewLines(["a"], ["a", "a"]));
    }

    [Fact]
    public void NewLines_AnOldLineReReadWithOneLetterOfNoise_IsNotNew()
    {
        Assert.Empty(WarText.NewLines(["arrivedgoblinelite"], ["arrivedgob1inelite"]));
        // the noisy old line is used up once: a second, real arrival line is new
        Assert.Equal(new[] { "arrivedgoblinelite" }, WarText.NewLines(["arrivedgob1inelite"], ["arrivedgoblinelite", "arrivedgoblinelite"]));
        // an exact match is not taken away by a one-letter match read before it
        Assert.Equal(new[] { "arrivedgoblinelitf" }, WarText.NewLines(["arrivedgoblinelite"], ["arrivedgoblinelitf", "arrivedgoblinelite"]));
        Assert.Equal(new[] { "arriveddarkspider" }, WarText.NewLines(["arrivedgoblinelite"], ["arrivedgoblinelite", "arriveddarkspider"]));
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

    // The menu screenshot's tile row: "Tribe Status" with "Level Bonus" and "Item Mall" beside it.
    static readonly (string, PixelRect) Tile = L("Tribe Status", 21, 491, 81, 13);
    static readonly (string, PixelRect)[] TileRow = [Tile, L("Level Bonus", 150, 491, 81, 12), L("Item Mall", 285, 491, 70, 12)];

    [Fact]
    public void IsMenuTileHit_TheMenusMixedCaseTile()
        => Assert.True(WarText.IsMenuTileHit(Tile.Item1, Tile.Item2, TileRow, "Tribe Status"));

    [Fact]
    public void IsMenuTileHit_TheTileWithTribeStatusOpenElsewhere_IsStillTheTile()
        => Assert.True(WarText.IsMenuTileHit(Tile.Item1, Tile.Item2, [.. TileRow, L("TRIBE STATUS", 322, 8, 145, 16)], "Tribe Status"));

    [Theory]
    [InlineData("TRIBE STATUS")]    // the window title itself
    [InlineData("Tribe STATUS")]    // a title read with too few capitals for FindWindowTitle (7 of 11)
    [InlineData("TRIBE Status")]
    public void IsMenuTileHit_AHitMostlyInCapitals_IsATitle(string text)
        => Assert.False(WarText.IsMenuTileHit(text, new PixelRect(322, 8, 145, 16), [], "Tribe Status"));   // the hit alone decides

    [Fact]
    public void IsMenuTileHit_AMixedCaseHitOnTheRowOfACapitalsTitle_IsATitle()
    {
        // e.g. the title's two words merged into one mixed-case word, its line read in capitals.
        var line = L("TRIBE STATUS", 322, 8, 145, 16);
        Assert.False(WarText.IsMenuTileHit("TribeStatus", new PixelRect(322, 9, 145, 15), [line], "Tribe Status"));
    }

    [Fact]
    public void IsMenuTileHit_TooLittleOfTheTileText_IsNone()
        => Assert.False(WarText.IsMenuTileHit("status", new PixelRect(1359, 872, 37, 10), [L("Hide item apply status", 1250, 872, 146, 10)], "Tribe Status"));

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

    [Theory]
    [InlineData("[Dark Soul Cave X]", "[Dark Soul Cave X]")]
    [InlineData("[Dark Soul Cave X] ", "[Dark Soul Cave X]")]
    [InlineData("[Dark Soul Cave X]", "[Dark Soul Cave X")]        // saved without its ']': the letters decide
    [InlineData("[The Labyrinth (Adep", "[The Labyrinth (Adept)]")]  // cut at the minimap panel's edge
    [InlineData("[Dark Soul Cave X", "[Dark Soul Cave X]")]         // ']' cut off or not read
    [InlineData("[Dark Soul Ca", "[Dark Soul Cave X]")]             // 10 letters: still a cut read
    [InlineData("[Coliseum", "[Coliseum]")]                         // a short name read whole, ']' dropped
    [InlineData("[Maya Cliff)", "[Maya Cliff]")]                     // list entries at 1024x768 read some ']' as ')'
    [InlineData("[Dark Soul Cave IF", "[Dark Soul Cave IF (WD)]")]  // page 3 at 2048x1152 (OCR reads "1F" as "IF"), cut
    [InlineData("[Dark Soul Cave 1F", "[Dark Soul Cave IF (WD)]")]  // i, l and | fold to 1, o to 0 (any case)
    [InlineData("[Dark Soul Cave lF (WD)]", "[Dark Soul Cave 1F (WD)]")]
    [InlineData("[Bohai 0utpost (Adept)]", "[Bohai Outpost (Adept)]")]
    public void SameZone_TheSavedMap(string read, string home) => Assert.True(WarText.SameZone(read, home));

    [Theory]
    [InlineData("[Dark Soul Cave IX]", "[Dark Soul Cave X]")]
    [InlineData("[Dark Soul Cave X]", "[Dark Soul Cave IX]")]
    [InlineData("[Dark Soul Cave IX", "[Dark Soul Cave X]")]        // a cut read of the sibling is no prefix of it
    [InlineData("[Dark Soul Cave X", "[Dark Soul Cave IX]")]
    [InlineData("[Dark Soul Cave I", "[Dark Soul Cave X]")]
    [InlineData("[Dark Soul Cave XI]", "[Dark Soul Cave X]")]       // complete and longer
    [InlineData("[Dark Soul Cave]", "[Dark Soul Cave X]")]          // complete and shorter: another map
    [InlineData("[Dark Soul C", "[Dark Soul Cave X]")]              // 9 letters: too short to tell the caves apart
    [InlineData("[Piercing City]", "[Dark Soul Cave X]")]
    [InlineData("[Dark Soul Cave IF", "[Dark Soul Cave IX]")]       // OCR noise is no cut
    [InlineData("[Dark Soul Cave 2F", "[Dark Soul Cave IF (WD)]")]  // the floor below, cut
    [InlineData("[Dark Soul Cave 2F (WD)]", "[Dark Soul Cave IF (WD)]")]
    [InlineData("", "[Dark Soul Cave X]")]
    [InlineData(null, "[Dark Soul Cave X]")]
    [InlineData("[Dark Soul Cave X]", "")]
    public void SameZone_NotTheSavedMap(string? read, string home) => Assert.False(WarText.SameZone(read, home));

    [Fact]
    public void TeleportListEntries_TheBracketLinesUnderTheTitle()
    {
        // 1920x1080, the lod_no_entries frame: TELEPORT SCROLL: 847-1070 x 168-190, the entries centred under it.
        var title = new PixelRect(847, 168, 223, 22);
        var lines = new[]
        {
            L("[PvP] (Snake) iMstrG [Cape Box].", 833, 110, 252, 18),   // chat line above the window
            L("TELEPORT SCROLL:", 847, 168, 223, 22),
            L("2 / 3", 940, 248, 38, 16),
            L("[Bohai Outpost (Adept)]", 864, 290, 189, 18),
            L("[ATTR Dungeon 5 (B.Dragon)]", 843, 399, 232, 18),
            L("[Maya Cliff]", 914, 436, 89, 18),
            L("[Vice Guild Master] THEOS", 450, 268, 206, 18),          // a name tag left of the window
        };

        var entries = WarText.TeleportListEntries(lines, title);

        Assert.Equal(["[Bohai Outpost (Adept)]", "[ATTR Dungeon 5 (B.Dragon)]", "[Maya Cliff]"], entries.Select(e => e.Text));
    }

    [Theory]
    [InlineData("Goblin Elite 145 25", "Goblin Elite")]
    [InlineData("o Goblin Elite 145 25", "o Goblin Elite")]     // the row's marker read as a letter stays: MonsterRows also tries without it
    [InlineData("■ Goblin Elite 145 25 >", "Goblin Elite")]
    [InlineData("Goblin Elite.", "Goblin Elite")]
    [InlineData("Red Mandolin Maiden 145 25", "Red Mandolin Maiden")]
    [InlineData("Bandit (Elite) 120 4", "Bandit (Elite)")]
    [InlineData("S undersword", "S undersword")]                // a real 1024x768 read: the split stays (the comparisons normalise)
    [InlineData("Goblin 140 30 [Rage Elixir] 25 (min(s))", "Goblin")]   // the numbers end the name: the item list beside it is not part of it
    public void MonsterNameColumn_TheNameBeforeTheNumbers(string line, string name)
        => Assert.Equal(name, WarText.MonsterNameColumn(line));

    [Theory]
    [InlineData("145 25")]
    [InlineData("■ >")]
    [InlineData("")]
    public void MonsterNameColumn_NoNameIsNull(string line) => Assert.Null(WarText.MonsterNameColumn(line));

    static IReadOnlyList<(string Text, PixelRect Bounds)> Rows(params string[] lines)
        => lines.Select((t, i) => (t, new PixelRect(639, 222 + 33 * i, 300, 12))).ToList();

    [Fact]
    public void MonsterRows_Goblin_IsTheGoblinRow_NotGoblinElite()
    {
        // The best single word of the list would be "Goblin" on the Goblin Elite row (similarity 1.00).
        var rows = WarText.MonsterRows(Rows("Goblin Elite 145 25", "Goblin 140 30"), "Goblin");
        var row = Assert.Single(rows);
        Assert.Equal("Goblin 140 30", row.Line);
        Assert.Equal("Goblin", row.Name);
        Assert.Equal(255, row.Bounds.Y);
    }

    [Fact]
    public void MonsterRows_CaveBat_IsFound()
    {
        // The best single word was "Cave" (0.886), short of most of "Cave Bat": no row at all.
        var row = Assert.Single(WarText.MonsterRows(Rows("Cave Spider 120 10", "Cave Bat 120 30", "Cave Bat Lord 150 1"), "Cave Bat"));
        Assert.Equal("Cave Bat", row.Name);
    }

    [Theory]
    [InlineData("Goblin")]                  // only the start of a longer name is on this page
    [InlineData("Elite")]
    [InlineData("Sacred Sky Lord")]
    [InlineData("")]
    public void MonsterRows_AMissingName_IsNone(string monster)
        => Assert.Empty(WarText.MonsterRows(Rows("Goblin Elite 145 25", "Goblinkin Chieftain 150 2", "Spawns"), monster));

    [Fact]
    public void MonsterRows_TwoIdenticalRows_AreBoth_NeverAGuess()
        => Assert.Equal(2, WarText.MonsterRows(Rows("Goblin 140 30", "Goblin Elite 145 25", "Goblin 150 20"), "Goblin").Count);

    [Theory]
    [InlineData("o Goblin Elite 145 25", "Goblin Elite", "Goblin Elite")]       // the row's marker
    [InlineData("Goblin Elite 145", "'Goblin Elite.", "Goblin Elite")]          // stray characters of the target bar read
    [InlineData("Goblin Elite 145 25", "Gobiin Elite", "Goblin Elite")]         // the list's spelling wins over an OCR slip (i / l folded)
    [InlineData("Goblin E1ite 145 25", "Goblin Elite", "Goblin E1ite")]
    [InlineData("S undersword", "Sundersword", "S undersword")]
    [InlineData("Sundersword 140 3", "S undersword", "Sundersword")]           // the saved split spelling finds the row read whole
    [InlineData("Red Mandolin Maiden 145 25", "Red Mandolin Maidem", "Red Mandolin Maiden")]   // one letter off a long name
    public void MonsterRows_TheListsSpellingOfTheName(string line, string monster, string name)
        => Assert.Equal(name, Assert.Single(WarText.MonsterRows(Rows(line), monster)).Name);

    [Theory]
    [InlineData("Goblin Elites 145 25", "Goblin Elite")]    // a longer name is another monster
    [InlineData("Cave Rat 120 30", "Cave Bat")]             // one letter off a short name is another monster
    [InlineData("o Goblin 140 30", "Goblin Elite")]
    public void MonsterRows_AnotherMonster_IsNone(string line, string monster)
        => Assert.Empty(WarText.MonsterRows(Rows(line), monster));

    [Theory]
    [InlineData("[Dark Soul Cave X]", "[Dark Soul Cave X]", true)]
    [InlineData("[Dark Soul Cave X)", "[Dark Soul Cave X]", true)]          // 1024x768 reads ']' as ')'
    [InlineData("[Dark Soul Cave X|", "[Dark Soul Cave X]", true)]          // ... or as '|': a closing '|' is a bracket
    [InlineData("[Dark Soul Cave X|", "[Dark Soul Cave X1]", false)]
    [InlineData("[Dark Soul Cave 1F (WD)]", "[Dark Soul Cave IF (WD)]", true)]
    [InlineData("[Dark Soul Cave X)", "[Dark Soul Cave XI]", false)]        // an entry is complete text, never a cut read
    [InlineData("[Dark Soul Cave X", "[Dark Soul Cave XI]", false)]
    [InlineData("[Dark Soul Cave IX]", "[Dark Soul Cave X]", false)]
    [InlineData("[Dark Soul Cave X]", "[Dark Soul Cave IX]", false)]
    [InlineData("[Dark Soul Cave 2F (WD)]", "[Dark Soul Cave IF (WD)]", false)]
    [InlineData("", "[Dark Soul Cave X]", false)]
    [InlineData("[Dark Soul Cave X]", "", false)]
    public void SameEntry_ListEntriesAreCompleteText(string entry, string saved, bool same)
        => Assert.Equal(same, WarText.SameEntry(entry, saved));

    [Fact]
    public void BestListEntryFolded_TheMinimapsOneReadAsTheListsI()
    {
        string[] page3 = ["[Mystical Grounds (WD)]", "[Dark Soul Cave IF (WD)]", "[Dark Soul Cave 2F (WD)]"];
        Assert.Equal("[Dark Soul Cave IF (WD)]", WarText.BestListEntryFolded("[Dark Soul Cave 1F", page3, out bool amb));
        Assert.False(amb);
        Assert.Equal("[Dark Soul Cave 2F (WD)]", WarText.BestListEntryFolded("[Dark Soul Cave 2F", page3, out amb));
        Assert.False(amb);
    }

    [Fact]
    public void BestListEntryFolded_TwoEntriesThatFoldTheSame_AreAmbiguous()
    {
        WarText.BestListEntryFolded("[Cave I]", ["[Cave I]", "[Cave 1]"], out bool amb);
        Assert.True(amb);
    }

    [Fact]
    public void BestListEntryFolded_KeepsIXAndXApart()
    {
        Assert.Equal("[Dark Soul Cave X]", WarText.BestListEntryFolded("[Dark Soul Cave X]", ["[Dark Soul Cave IX]", "[Dark Soul Cave X]"], out bool amb));
        Assert.False(amb);
    }

    [Theory]
    [InlineData("SOUL", "Soul")]                      // OCR case flips: folding works on the lower-cased key
    [InlineData("OUTPOST", "Outpost")]
    [InlineData("ISLAND", "island")]
    [InlineData("[Dark Soul Cave IF (WD)]", "[dark soul cave 1f (wd)]")]
    [InlineData("[Dark Soul Cave X|", "[Dark Soul Cave X]")]
    public void FoldedKey_SameAcrossCaseAndConfusableCharacters(string a, string b) => Assert.Equal(WarText.FoldedKey(a), WarText.FoldedKey(b));

    [Theory]
    [InlineData("[Dark Soul Cave IX]", "[Dark Soul Cave X]")]
    [InlineData("[Dark Soul Cave 1F (WD)]", "[Dark Soul Cave 2F (WD)]")]
    public void FoldedKey_KeepsCavesApart(string a, string b) => Assert.NotEqual(WarText.FoldedKey(a), WarText.FoldedKey(b));

    [Theory]
    [InlineData("[Dark SOUL Cave X]", "[Dark Soul Cave X]")]
    [InlineData("[dark soul cave if", "[Dark Soul Cave IF (WD)]")]
    public void SameZone_ACaseFlipIsTheSameZone(string read, string home) => Assert.True(WarText.SameZone(read, home));

    [Theory]
    [InlineData("[Bohai OUTPOST (Adept)]", "[Bohai Outpost (Adept)]")]
    [InlineData("[Taji ISLAND (WD)]", "[Taji Island (WD)]")]
    public void SameEntry_ACaseFlipIsTheSameEntry(string entry, string saved) => Assert.True(WarText.SameEntry(entry, saved));

    [Theory]
    [InlineData("[Dark Soul Cave 2F (WD)]", true)]
    [InlineData("[Dark Soul Cave 2F (WD)", true)]     // a digit in the name (page 3)
    [InlineData("[Bohai Outpost (Adept)] [10] minute(s) to opening of the entrance", false)]   // the war notice, not a title
    [InlineData("[Bohai Outpost (Adept)) 10 minute(s) to opening", false)]
    [InlineData("[Piercing City]", true)]
    [InlineData("[The Labyrinth (Adep", true)]
    [InlineData("[3592 , 7297", false)]                // the coordinates line
    [InlineData("[3489. 6867", false)]
    [InlineData("10362 , 13793", false)]
    [InlineData("[12]", false)]
    [InlineData("Piercing City", false)]
    public void IsZoneTitleLine(string line, bool title) => Assert.Equal(title, WarText.IsZoneTitleLine(line));
}

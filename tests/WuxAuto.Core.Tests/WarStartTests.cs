using WuxAuto.Automation;
using Read = WuxAuto.Automation.AutomationPilot.WarListRead;
using Verdict = WuxAuto.Automation.AutomationPilot.WarListVerdict;

namespace WuxAuto.Core.Tests;

public class WarStartTests
{
    [Fact]
    public void MissingIcons_NameEachWithItsBestScore()
        => Assert.Equal("warbuff2 (best 0.84), shura (not seen)",
            AutomationPilot.DescribeMissingIcons([("warbuff2", 0.8412), ("shura", null)]));

    [Fact]
    public void MissingIcons_ScoresRoundDown_InvariantCulture()
    {
        // 0.8996 missed a 0.90 threshold, so it must not read as "0.90".
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("warbuff6 (best 0.89)", AutomationPilot.DescribeMissingIcons([("warbuff6", 0.8996)]));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = culture;
        }
    }

    // ---------------------------------------------------------------- combining the reads of one list page

    static readonly Read Unique = new("[Dark Soul Cave IF (WD)]", false, null);
    static readonly Read Missing = new(null, false, "missing");
    static readonly Read Ambiguous = new(null, true, "two caves");

    [Fact]
    public void ListReads_AmbiguousThenUnique_IsAmbiguous()
        => Assert.Equal((Verdict.Ambiguous, 0), AutomationPilot.DecideListReads([Ambiguous, Unique], 3));

    [Fact]
    public void ListReads_MissingThenUnique_IsUnique()
        => Assert.Equal((Verdict.Pick, 1), AutomationPilot.DecideListReads([Missing, Unique], 3));

    [Fact]
    public void ListReads_UniqueThenAmbiguous_IsStillAmbiguous()
        => Assert.Equal((Verdict.Ambiguous, 1), AutomationPilot.DecideListReads([Unique, Ambiguous], 3));

    [Fact]
    public void ListReads_MissingOrUnreadBeforeTheLastRead_ReadsAgain()
    {
        Assert.Equal(Verdict.ReadAgain, AutomationPilot.DecideListReads([Missing], 3).Verdict);
        Assert.Equal(Verdict.ReadAgain, AutomationPilot.DecideListReads([null, Missing], 3).Verdict);
    }

    [Fact]
    public void ListReads_MissingAfterEveryRead_IsMissing_TheLastReadThatShowedTheList()
        => Assert.Equal((Verdict.Missing, 1), AutomationPilot.DecideListReads([Missing, Missing, null], 3));

    [Fact]
    public void ListReads_TheListNeverShowed_IsNoList()
        => Assert.Equal((Verdict.NoList, -1), AutomationPilot.DecideListReads([null, null, null], 3));

    // ---------------------------------------------------------------- one read of a list page

    static readonly string[] Page3 = ["[Mystical Grounds (WD)]", "[Dark Soul Cave IF (WD)]", "[Dark Soul Cave 2F (WD)]", "[Taji Island (WD)]"];

    [Fact]
    public void StartRead_TheCutTitlesEntry()
        => Assert.Equal(new Read("[Dark Soul Cave IF (WD)]", false, null), AutomationPilot.JudgeListRead("[Dark Soul Cave 1F", Page3, 3, returnTrip: false));

    [Fact]
    public void StartRead_TheClosestEntryOfAnotherCave_IsNoPick()
    {
        // BestListEntry's score rule would take Y (12 of 13 letters, unique) for the cave the minimap names.
        var read = AutomationPilot.JudgeListRead("[Dark Soul Cave X]", ["[Dark Soul Cave Y]"], 3, returnTrip: false);
        Assert.Null(read.Entry);
        Assert.False(read.Ambiguous);
        Assert.Equal("The minimap reads '[Dark Soul Cave X]', the closest entry on page 3 is '[Dark Soul Cave Y]' - not sure it is this cave", read.Why);
    }

    [Fact]
    public void StartRead_ACutTitleOfTwoEntries_IsAmbiguous_NamingBoth()
    {
        var read = AutomationPilot.JudgeListRead("[Dark Soul Cave", Page3, 3, returnTrip: false);
        Assert.True(read.Ambiguous);
        Assert.Contains("'[Dark Soul Cave IF (WD)]'", read.Why);
        Assert.Contains("'[Dark Soul Cave 2F (WD)]'", read.Why);
    }

    [Fact]
    public void StartRead_NotOnThePage()
        => Assert.Equal(new Read(null, false, "'[Maya Cliff]' is not on page 3 of the teleport scroll"),
            AutomationPilot.JudgeListRead("[Maya Cliff]", Page3, 3, returnTrip: false));

    [Fact]
    public void ReturnRead_TheSavedEntry_FoldedOneAndI()
        => Assert.Equal("[Dark Soul Cave 1F (WD)]",
            AutomationPilot.JudgeListRead("[Dark Soul Cave IF (WD)]", ["[Dark Soul Cave 1F (WD)]", "[Dark Soul Cave 2F (WD)]"], 3, returnTrip: true).Entry);

    [Fact]
    public void ReturnRead_AnEntryWithoutItsBracket_IsNoCutRead()
    {
        // 1024x768 reads ']' as ')': "[Dark Soul Cave X)" is cave X, never a cut read of the saved XI.
        var read = AutomationPilot.JudgeListRead("[Dark Soul Cave XI]", ["[Dark Soul Cave X)", "[Dark Soul Cave IX]"], 3, returnTrip: true);
        Assert.Null(read.Entry);
        Assert.False(read.Ambiguous);
    }

    [Fact]
    public void ReturnRead_TwoEntriesThatFoldTheSame_AreAmbiguous()
    {
        var read = AutomationPilot.JudgeListRead("[Cave I]", ["[Cave I]", "[Cave 1]"], 3, returnTrip: true);
        Assert.Null(read.Entry);
        Assert.True(read.Ambiguous);
    }

    // ---------------------------------------------------------------- trips back to the cave

    const long Minute = 60_000;

    [Fact]
    public void ReturnTrips_ThreeWithinHalfAnHour_TheFourthIsRefused()
    {
        var trips = new List<long>();
        Assert.True(AutomationPilot.WarReturnAllowed(trips, 0));
        Assert.True(AutomationPilot.WarReturnAllowed(trips, 5 * Minute));
        Assert.True(AutomationPilot.WarReturnAllowed(trips, 10 * Minute));
        Assert.False(AutomationPilot.WarReturnAllowed(trips, 29 * Minute));
        Assert.Equal(3, trips.Count);     // a refused trip is not counted
    }

    [Fact]
    public void ReturnTrips_OlderThanHalfAnHour_NoLongerCount()
    {
        var trips = new List<long> { 0, 5 * Minute, 10 * Minute };
        Assert.True(AutomationPilot.WarReturnAllowed(trips, 31 * Minute));
        Assert.Equal([5 * Minute, 10 * Minute, 31 * Minute], trips);
    }

    // ---------------------------------------------------------------- back in after a re-login

    [Fact]
    public void ReloginZone_TheWarMap_ACutTitle()
        => Assert.Equal("Bohai Outpost", AutomationPilot.WarReloginZone(["[Bohai Outpost (Adep", "3592 , 7297"], "Bohai Outpost", loginScreen: false));

    [Fact]
    public void ReloginZone_AnyZoneTitle_ACaveNotSavedYet()
        => Assert.Equal("[Dark Soul Cave 2F (WD)]", AutomationPilot.WarReloginZone(["3592 , 7297", "[Dark Soul Cave 2F (WD)]"], "Bohai Outpost", loginScreen: false));

    [Fact]
    public void ReloginZone_NothingWhileALoginScreenShows()
    {
        // The server list's row reads like a zone title, and the character screen shows the location.
        Assert.Null(AutomationPilot.WarReloginZone(["[CH-01] - Wuxen2 Online"], "Bohai Outpost", loginScreen: true));
        Assert.Null(AutomationPilot.WarReloginZone(["[Bohai Outpost (Adept)]"], "Bohai Outpost", loginScreen: true));
    }

    [Fact]
    public void ReloginZone_NoTitle_IsNull()
        => Assert.Null(AutomationPilot.WarReloginZone(["3592 , 7297", "[12]", "Lv 145"], "Bohai Outpost", loginScreen: false));
}

using WuxAuto.Automation;
using Clock = WuxAuto.Automation.AutomationPilot.FrontClock;
using ItemList = WuxAuto.Automation.AutomationPilot.WarItemList;

namespace WuxAuto.Core.Tests;

public class WarFightTests
{
    const long Now = 1_000_000;
    const int BuffEvery = 345_000;

    // ---------------------------------------------------------------- rebuff

    static long Timer(long buffedAt) => AutomationPilot.WarRebuffWantedAt(buffedAt, BuffEvery, droppedAt: null);

    [Fact]
    public void Rebuff_DueAtTheTabPointOnceBuffEveryMsHasPassed()
    {
        Assert.True(AutomationPilot.WarRebuffDue(Now + BuffEvery, Timer(Now), fightStep: 0));
        Assert.True(AutomationPilot.WarRebuffDue(Now + BuffEvery + 60_000, Timer(Now), fightStep: 0));
    }

    [Fact]
    public void Rebuff_NotBeforeBuffEveryMs()
        => Assert.False(AutomationPilot.WarRebuffDue(Now + BuffEvery - 1, Timer(Now), fightStep: 0));

    [Theory]
    [InlineData(1)]   // reading the target
    [InlineData(2)]   // Shura going down
    [InlineData(3)]   // Shura held
    public void Rebuff_NeverBetweenTabAndTheAttack(int step)
        => Assert.False(AutomationPilot.WarRebuffDue(Now + 2 * BuffEvery, Timer(Now), step));

    [Fact]
    public void Rebuff_MidAttack_OnlyOnceItIsThirtySecondsOverdue()
    {
        // A target that keeps reading (out of reach) keeps step 4 going: the buffs must not wait for it forever.
        Assert.False(AutomationPilot.WarRebuffDue(Now + BuffEvery, Timer(Now), fightStep: 4));
        Assert.False(AutomationPilot.WarRebuffDue(Now + BuffEvery + AutomationPilot.WarRebuffOverdueMs - 1, Timer(Now), fightStep: 4));
        Assert.True(AutomationPilot.WarRebuffDue(Now + BuffEvery + AutomationPilot.WarRebuffOverdueMs, Timer(Now), fightStep: 4));
    }

    [Fact]
    public void Rebuff_BuffsSeenGone_WantedFromThenOn_AsTheTimerIs()
    {
        long wanted = AutomationPilot.WarRebuffWantedAt(Now, BuffEvery, droppedAt: Now + 60_000);
        Assert.Equal(Now + 60_000, wanted);
        Assert.True(AutomationPilot.WarRebuffDue(Now + 60_000, wanted, fightStep: 0));
        Assert.False(AutomationPilot.WarRebuffDue(Now + 60_000, wanted, fightStep: 4));
        Assert.True(AutomationPilot.WarRebuffDue(Now + 60_000 + AutomationPilot.WarRebuffOverdueMs, wanted, fightStep: 4));
        // The timer, when it comes first, still decides.
        Assert.Equal(Now + BuffEvery, AutomationPilot.WarRebuffWantedAt(Now, BuffEvery, droppedAt: Now + BuffEvery + 5000));
    }

    // ---------------------------------------------------------------- the HP watch

    const int Max = 42833;
    static readonly (int, int) Dead = (0, Max);
    static readonly (int, int) Full = (41000, Max);
    static readonly (int, int) MpLine = (8000, 11467);
    static readonly (int, int) LowBar = (1500, 3000);

    static readonly AutomationPilot.WarHpWatch Fresh = default;

    /// <summary>The player's maximum known (learnt from two reads), the buffs-gone check armed.</summary>
    static readonly AutomationPilot.WarHpWatch Known = default(AutomationPilot.WarHpWatch) with { AliveMax = Max, PendingMax = Max, DropArmed = true };

    /// <summary>Every read through the watch: the last state and every verdict on the way.</summary>
    static (AutomationPilot.WarHpWatch Watch, List<AutomationPilot.WarHpVerdict> Verdicts) Run(
        AutomationPilot.WarHpWatch watch, bool dead, params (int Cur, int Max)?[] reads)
    {
        var verdicts = new List<AutomationPilot.WarHpVerdict>();
        foreach (var hp in reads)
        {
            (watch, var verdict, _) = AutomationPilot.WarHpNext(watch, hp, dead);
            verdicts.Add(verdict);
        }

        return (watch, verdicts);
    }

    static AutomationPilot.WarHpVerdict Last(AutomationPilot.WarHpWatch watch, bool dead, params (int Cur, int Max)?[] reads)
        => Run(watch, dead, reads).Verdicts[^1];

    [Fact]
    public void Seeding_NeedsTwoAliveReadsThatAgree()
    {
        Assert.Null(Run(Fresh, false, Full).Watch.AliveMax);
        Assert.Equal(Max, Run(Fresh, false, Full, Full).Watch.AliveMax);
        Assert.Equal(42900, Run(Fresh, false, Full, null, (30000, 42900)).Watch.AliveMax);   // within 1 % (the later one); an unread between
        Assert.Null(Run(Fresh, false, Full, (40000, 51200)).Watch.AliveMax);                 // they disagree
        Assert.Null(Run(Fresh, false, Full, Dead, Full).Watch.AliveMax);                     // a death read breaks the pair
        Assert.Null(Run(Fresh, false, (500, 900), (500, 900)).Watch.AliveMax);              // under 1000
    }

    [Fact]
    public void MpLineAndTheLowBar_NeitherMoveTheMaximumNorCountAsARevive()
    {
        var (watch, verdicts) = Run(Known, dead: true, MpLine, MpLine, LowBar, LowBar);
        Assert.Equal(Max, watch.AliveMax);
        Assert.All(verdicts, v => Assert.Equal(AutomationPilot.WarHpVerdict.None, v));

        Assert.Equal(Max, Run(Known, false, MpLine, MpLine, LowBar, LowBar).Watch.AliveMax);
    }

    [Fact]
    public void Maximum_RaisedByTwoAgreeingHigherReads_NeverLowered()
    {
        Assert.Equal(Max, Run(Known, false, (51000, 51200)).Watch.AliveMax);                       // one read is not enough
        Assert.Equal(51200, Run(Known, false, (51000, 51200), (50000, 51200)).Watch.AliveMax);    // buffed
        Assert.Equal(Max, Run(Known, false, (30000, 34493), (30000, 34493), (30000, 34493)).Watch.AliveMax);
    }

    [Fact]
    public void Maximum_RaisedByAtMostHalfAgain_ABiggerJumpIsIgnoredAndReportedOnce()
    {
        // The user's buff: 34493 -> 42833 (about 1.24 x).
        var unbuffed = Known with { AliveMax = 34493, PendingMax = 34493 };
        Assert.Equal(Max, Run(unbuffed, false, Full, Full).Watch.AliveMax);

        // The MP line of a character whose MP maximum is far higher, read in place of the HP line: not the HP maximum.
        var (watch, verdicts) = Run(Known, false, (90000, 90000), (90000, 90000), (90000, 90000));
        Assert.Equal(Max, watch.AliveMax);
        Assert.Equal(new[] { AutomationPilot.WarHpVerdict.None, AutomationPilot.WarHpVerdict.RaiseIgnored, AutomationPilot.WarHpVerdict.None }, verdicts);
        Assert.Equal(AutomationPilot.WarHpVerdict.None, Last(watch, false, (90000, 90000), (90000, 90000)));   // once per Start

        // 1.5 x, rounded down, is the last raise taken.
        Assert.Equal(64249, Run(Known, false, (60000, 64249), (60000, 64249)).Watch.AliveMax);
        Assert.Equal(Max, Run(Known, false, (60000, 64250), (60000, 64250)).Watch.AliveMax);
    }

    [Fact]
    public void ForeignReads_TellNothing_SoTheyRunTheUnreadTimer()
    {
        // With the maximum known, a read of another "N / M" line (the MP line, the bar under the HP bar) is as good as
        // none: it says nothing about the HP, and ten seconds of only such reads save war_hp_unread.
        var watch = Known;
        long since = 0;
        for (long t = 0; t <= AutomationPilot.WarHpUnreadMs; t += 1000)
        {
            (watch, _, bool told) = AutomationPilot.WarHpNext(watch, t % 2000 == 0 ? MpLine : LowBar, dead: false);
            Assert.False(told);
            since = AutomationPilot.WarHpUnreadSince(since, told, Now + t);
        }

        Assert.True(AutomationPilot.WarHpUnreadTooLong(Now + AutomationPilot.WarHpUnreadMs, since));

        // A read of the player's HP tells, and ends the unread spell.
        var (_, _, hpTold) = AutomationPilot.WarHpNext(watch, Full, dead: false);
        Assert.True(hpTold);
        Assert.Equal(0, AutomationPilot.WarHpUnreadSince(since, hpTold, Now + 11_000));
        Assert.False(AutomationPilot.WarHpNext(watch, null, dead: false).Told);
    }

    [Fact]
    public void Death_TwoDeathReadsInARow()
    {
        Assert.Equal(AutomationPilot.WarHpVerdict.None, Last(Known, false, Dead));
        Assert.Equal(AutomationPilot.WarHpVerdict.Dead, Last(Known, false, Dead, Dead));
        Assert.Equal(AutomationPilot.WarHpVerdict.Dead, Last(Known, false, (0, 34493), (0, 34493)));   // the buffs went with the death
        Assert.Equal(AutomationPilot.WarHpVerdict.Dead, Last(Known, false, Dead, null, Dead));         // an unread neither counts nor breaks
        Assert.Equal(AutomationPilot.WarHpVerdict.None, Last(Known, false, Dead, Full, Dead));
        // Another line's read (outside the player's maximum) between: like an unread one, it does not break the row.
        Assert.Equal(AutomationPilot.WarHpVerdict.Dead, Last(Known, false, Dead, (0, 3000), Dead));
        Assert.Equal(AutomationPilot.WarHpVerdict.Dead, Last(Known, false, Dead, MpLine, Dead));
        Assert.Equal(AutomationPilot.WarHpVerdict.None, Last(Known, false, (0, 3000), (0, 3000)));
    }

    [Fact]
    public void Death_NoMaximumYet_OnlyAMaxOf1000OrMoreCounts()
    {
        Assert.Equal(AutomationPilot.WarHpVerdict.Dead, Last(Fresh, false, Dead, Dead));
        Assert.Equal(AutomationPilot.WarHpVerdict.None, Last(Fresh, false, (0, 300), (0, 300)));
    }

    [Fact]
    public void Revive_TwoAliveReadsOfThePlayersMaximum()
    {
        Assert.Equal(AutomationPilot.WarHpVerdict.None, Last(Known, true, (900, 34493)));
        Assert.Equal(AutomationPilot.WarHpVerdict.Revived, Last(Known, true, (900, 34493), (2000, 34493)));
        Assert.Equal(AutomationPilot.WarHpVerdict.None, Last(Known, true, (900, 34493), Dead));
        Assert.Equal(AutomationPilot.WarHpVerdict.None, Last(Known, true, (900, 34493), MpLine));
        Assert.Equal(AutomationPilot.WarHpVerdict.Revived, Last(Known, true, (900, 34493), MpLine, (2000, 34493)));   // the MP line breaks no row
        Assert.Equal(AutomationPilot.WarHpVerdict.None, Last(Known, false, Full, Full));   // only while dead
    }

    [Fact]
    public void BuffsDropped_TwoAliveReadsUnderNineTenths_OncePerDrop()
    {
        var (watch, verdicts) = Run(Known, false, (30000, 34493), (30000, 34493), (30000, 34493));
        Assert.Equal(new[] { AutomationPilot.WarHpVerdict.None, AutomationPilot.WarHpVerdict.BuffsDropped, AutomationPilot.WarHpVerdict.None }, verdicts);
        Assert.Equal(Max, watch.AliveMax);

        // Back up (rebuffed) arms it again.
        Assert.Equal(AutomationPilot.WarHpVerdict.BuffsDropped, Last(watch, false, Full, (30000, 34493), (30000, 34493)));
    }

    [Fact]
    public void BuffsDropped_NotBeforeAReadAtTheMaximum_NorFromTheMpLine()
    {
        var disarmed = Known with { DropArmed = false };
        Assert.Equal(AutomationPilot.WarHpVerdict.None, Last(disarmed, false, (30000, 34493), (30000, 34493)));
        Assert.Equal(AutomationPilot.WarHpVerdict.None, Last(Known, false, MpLine, MpLine));
    }

    // ---------------------------------------------------------------- keys

    static readonly AutomationPilot.WarKey[] Keys =
    [
        new("Shura key (LOD)", "E", Required: true),
        new("Buff key (LOD)", "", Required: false),
        new("Skill key", "1", Required: false),
    ];

    static bool Known1(string key) => key is "E" or "1" or "O";

    [Fact]
    public void Keys_AllKnownOrOptionalAndEmpty_NoProblem()
        => Assert.Null(AutomationPilot.WarKeyProblem(Keys, Known1));

    [Fact]
    public void Keys_ANameThatDoesNotMap_IsNamedWithItsSetting()
    {
        var keys = Keys.Append(new AutomationPilot.WarKey("Target key", "Tabb", Required: false)).ToArray();
        string? problem = AutomationPilot.WarKeyProblem(keys, Known1);
        Assert.NotNull(problem);
        Assert.Contains("'Tabb'", problem);
        Assert.Contains("Target key", problem);
    }

    [Fact]
    public void Keys_ARequiredKeyLeftEmpty_IsNamed()
    {
        string? problem = AutomationPilot.WarKeyProblem([new("Shura key (LOD)", " ", Required: true)], Known1);
        Assert.NotNull(problem);
        Assert.Contains("Shura key (LOD)", problem);
    }

    static bool Pressable(string key) => key is "E" or "1" or "Tab" or "J" or "I" or "O" or "S";

    [Fact]
    public void WarKeys_AnEmptyShuraKey_IsNoProblem()
    {
        // The user (2026-10-28): Shura is optional - without it the fight cycle skips the hold.
        var settings = new AppSettings();
        settings.Lod.ShuraKey = "";
        Assert.Null(AutomationPilot.WarKeyProblem(AutomationPilot.WarKeys(settings), Pressable));
    }

    [Fact]
    public void WarKeys_ASetShuraKey_MustStillBePressable()
    {
        var settings = new AppSettings();
        settings.Lod.ShuraKey = "Shura";
        string? problem = AutomationPilot.WarKeyProblem(AutomationPilot.WarKeys(settings), Pressable);
        Assert.NotNull(problem);
        Assert.Contains("'Shura'", problem);
    }

    [Theory]
    [InlineData("Skill key")]
    [InlineData("Target key")]
    [InlineData("Autohunt key")]
    [InlineData("Inventory key")]
    public void WarKeys_TheSkillTargetAutohuntAndInventoryKeys_StayRequired(string setting)
    {
        var settings = new AppSettings();
        settings.Lod.ShuraKey = "";
        switch (setting)
        {
            case "Skill key": settings.Dungeon.SkillKey = ""; break;
            case "Target key": settings.Dungeon.TabKey = ""; break;
            case "Autohunt key": settings.Dungeon.AutohuntKey = ""; break;
            default: settings.Input.InventoryKey = ""; break;
        }

        string? problem = AutomationPilot.WarKeyProblem(AutomationPilot.WarKeys(settings), Pressable);
        Assert.NotNull(problem);
        Assert.Contains($"'{setting}'", problem);
    }

    // ---------------------------------------------------------------- which buff icons a round clicks

    static WarSettings Buffs(params string[] disabled)
    {
        var war = new WarSettings();
        foreach (string icon in disabled)
            war.SetBuffOn(icon, false);
        return war;
    }

    [Theory]
    [InlineData(0, 800)]
    [InlineData(300, 800)]
    [InlineData(800, 800)]
    [InlineData(1000, 1000)]
    public void BuffWait_NeverUnderTheAnimation(int gap, int expected)
        // The user (2026-10-28): "allow at least 0.8s for buff animation before going next".
        => Assert.Equal(expected, AutomationPilot.WarBuffWaitMs(gap));

    [Fact]
    public void BuffClicks_NoAutobuff_TheBuffsThenTheWeaponBuffsThenTheItemBuffs()
        => Assert.Equal(
            ["warbuff1", "warbuff3", "warbuff4", "warbuff5", "warbuff6", "warbuff2", "warweapon1", "warweapon2", "waritem1", "waritem2"],
            AutomationPilot.WarBuffClicks(Buffs(), byBuffKey: false));

    [Fact]
    public void BuffClicks_Autobuff_OnlyTheItemBuffs()
        // The user (2026-10-28): with autobuff (O) the item buffs on the skill bar are still clicked.
        => Assert.Equal(["waritem1", "waritem2"], AutomationPilot.WarBuffClicks(Buffs(), byBuffKey: true));

    [Fact]
    public void BuffClicks_UntickedOnes_AreLeftOut_IgnoringCase()
    {
        var war = Buffs("warweapon2", "WARITEM1", "warbuff4");
        Assert.Equal(["warbuff1", "warbuff3", "warbuff5", "warbuff6", "warbuff2", "warweapon1", "waritem2"],
            AutomationPilot.WarBuffClicks(war, byBuffKey: false));
        Assert.Equal(["waritem2"], AutomationPilot.WarBuffClicks(war, byBuffKey: true));
    }

    [Fact]
    public void BuffClicks_EverythingUnticked_IsNothing()
    {
        var war = new WarSettings();
        foreach (string icon in war.BuffIcons.Concat(war.WeaponBuffIcons).Concat(war.ItemBuffIcons))
            war.SetBuffOn(icon, false);
        Assert.Empty(AutomationPilot.WarBuffClicks(war, byBuffKey: false));
        Assert.Empty(AutomationPilot.WarBuffClicks(war, byBuffKey: true));
    }

    [Fact]
    public void BuffClicks_BlanksSkipped_AnIconInTwoLists_ClickedOnce_AsAnItemWithAutobuff()
    {
        var war = new WarSettings
        {
            BuffIcons = ["warbuff1", " ", "waritem1"],
            WeaponBuffIcons = ["", "WARBUFF1", "warweapon1"],
            ItemBuffIcons = ["", "waritem1", "WARBUFF1"],
        };
        Assert.Equal(["warbuff1", "waritem1", "warweapon1"], AutomationPilot.WarBuffClicks(war, byBuffKey: false));
        // With autobuff an icon listed as an item is still an item: clicked.
        Assert.Equal(["waritem1", "WARBUFF1"], AutomationPilot.WarBuffClicks(war, byBuffKey: true));
    }

    [Fact]
    public void BuffClicks_TheWeaponBuffs_AreSkillBuffs_AfterTheSixBeforeTheItems_NotWithAutobuff()
    {
        var war = new WarSettings { BuffIcons = ["warbuff1"], WeaponBuffIcons = ["warweapon2", "warweapon1"], ItemBuffIcons = ["waritem1"] };
        Assert.Equal(["warbuff1", "warweapon2", "warweapon1", "waritem1"], AutomationPilot.WarBuffClicks(war, byBuffKey: false));
        Assert.Equal(["waritem1"], AutomationPilot.WarBuffClicks(war, byBuffKey: true));
    }

    // ---------------------------------------------------------------- the war clock

    [Fact]
    public void Clock_RunsWithTheGameInFront()
    {
        var clock = new Clock();
        long start = clock.Now(Now);
        Assert.Equal(1500, clock.Now(Now + 1500) - start);
    }

    [Fact]
    public void Clock_StandsStillBehindAnotherWindow_AndGoesOnWithoutAJump()
    {
        var clock = new Clock();
        long start = clock.Now(Now);
        clock.Hide(Now + 1000);
        clock.Hide(Now + 5000);                 // still behind: no change
        Assert.Equal(1000, clock.Now(Now + 30_000) - start);
        Assert.Equal(59_000, clock.Show(Now + 60_000));
        Assert.Equal(1000, clock.Now(Now + 60_000) - start);
        Assert.Equal(1500, clock.Now(Now + 60_500) - start);
        Assert.Equal(0, clock.Show(Now + 61_000));   // in front already
    }

    [Fact]
    public void TargetGone_TheTimeBehindAnotherWindowDoesNotCount()
    {
        var clock = new Clock();
        long seen = clock.Now(Now);
        clock.Hide(Now + 1500);                 // 1.5 s after the last sight, another window covers the game for a minute
        clock.Show(Now + 61_500);

        Assert.False(AutomationPilot.WarTargetGone(clock.Now(Now + 61_900), seen, targetGoneMs: 2000));
        Assert.True(AutomationPilot.WarTargetGone(clock.Now(Now + 62_000), seen, targetGoneMs: 2000));
    }

    [Fact]
    public void TargetGone_AfterTargetGoneMs()
    {
        Assert.False(AutomationPilot.WarTargetGone(Now + 1999, Now, 2000));
        Assert.True(AutomationPilot.WarTargetGone(Now + 2000, Now, 2000));
    }

    [Fact]
    public void HpUnread_TenSecondsOfFrontTime_NotWhileHidden()
    {
        var clock = new Clock();
        long since = clock.Now(Now);
        clock.Hide(Now + 4000);
        clock.Show(Now + 120_000);

        Assert.False(AutomationPilot.WarHpUnreadTooLong(clock.Now(Now + 125_999), since));
        Assert.True(AutomationPilot.WarHpUnreadTooLong(clock.Now(Now + 126_000), since));
        Assert.False(AutomationPilot.WarHpUnreadTooLong(Now + 60_000, unreadSince: 0));   // it reads
    }

    // ---------------------------------------------------------------- autobuff from the item list

    [Fact]
    public void ItemList_AnAutoBuffScrollLine_IsAutoBuff()
        => Assert.Equal(ItemList.AutoBuff, AutomationPilot.AutoBuffInItemList(
            ["[Slayer Scroll] 171 (time(s))", "[Auto buff scrol] 02.11.2026", "[Temple of Fiends Key] 62 (time(s))"]));

    [Fact]
    public void ItemList_ShownWithoutTheScroll_IsNoAutoBuff()
        => Assert.Equal(ItemList.NoAutoBuff, AutomationPilot.AutoBuffInItemList(
            ["[Slayer Scroll] 171 (time(s))", "[Temple of Fiends Key] 62 (time(s))", "[Den of Rebirth] 3 (time(s))"]));

    [Fact]
    public void ItemList_FewerThanThreeLines_IsUnread()
    {
        Assert.Equal(ItemList.Unread, AutomationPilot.AutoBuffInItemList([]));
        Assert.Equal(ItemList.Unread, AutomationPilot.AutoBuffInItemList(["[Slayer Scroll] 171 (time(s))", "ok", "  "]));
        // The same line read at two scales is one line.
        Assert.Equal(ItemList.Unread, AutomationPilot.AutoBuffInItemList(
            ["[Slayer Scroll] 171 (time(s))", "[Slayer Scroll] 171 (time(s))", "[Den of Rebirth] 3 (time(s))"]));
    }

    [Fact]
    public void ItemList_TheAutoBuffDialogTitle_IsNotTheScroll()
        => Assert.Equal(ItemList.Unread, AutomationPilot.AutoBuffInItemList(["AUTO BUFF"]));

    // ---------------------------------------------------------------- the walk out of the respawn point

    const int W = 1920, H = 1080;
    // The character's spot: (0.5 w, 0.54 h).
    const int SpotX = 960, SpotY = 583;

    static (string, PixelRect) Tag(string text, int centreX, int centreY, int w = 100, int h = 16)
        => (text, new PixelRect(centreX - w / 2, centreY - h / 2, w, h));

    static IReadOnlyList<PixelRect> Bands => AutomationPilot.WarWorldBands(new RegionSettings(), W, H);

    [Fact]
    public void WorldBands_TheTopStrip_TheMinimap_TheCombatLog_TheSkillBar_TheItemList()
    {
        var regions = new RegionSettings();
        var bands = AutomationPilot.WarWorldBands(regions, W, H);
        Assert.Contains(new PixelRect(0, 0, W, (int)(0.13 * H)), bands);
        Assert.Contains(regions.Minimap.ToPixels(W, H), bands);
        Assert.Contains(regions.AttackMessage.ToPixels(W, H), bands);
        Assert.Contains(regions.Hotbar.ToPixels(W, H), bands);
        Assert.Contains(regions.ItemList.ToPixels(W, H), bands);
    }

    [Fact]
    public void WalkGoal_TheEventNoticesBetweenTheMinimapAndTheItemList_DoNotCount()
    {
        // A saved 1024x768 Labyrinth frame: "EXP x 2 Event" ... "Mount Exp x 0.050" right-aligned under the minimap panel,
        // the last two below Regions.Minimap (0.42 h) and above Regions.ItemList (0.50 h).
        var bands = AutomationPilot.WarWorldBands(new RegionSettings(), 1024, 768);
        var lines = new[] { Tag("Pet Exp x 2%", 960, 335, 98, 12), Tag("Mount Exp x 0.050", 950, 350, 118, 12) };
        Assert.Null(AutomationPilot.WarWalkGoal(lines, 1024, 768, bands));
    }

    [Fact]
    public void WalkGoal_TheNearestTag_NotOurOwnName()
    {
        var lines = new[]
        {
            Tag("OurOwnName", SpotX, SpotY - 100),          // 100 px above the spot: within 0.22 h (238 px) - our own name tag
            Tag("FarAway", SpotX + 150, SpotY - 400),
            Tag("zMADARAz", SpotX + 120, SpotY + 260),
        };
        Assert.Equal("zMADARAz", AutomationPilot.WarWalkGoal(lines, W, H, Bands)?.Text);
    }

    [Fact]
    public void WalkGoal_OurOwnTagsUpToAFifthOfTheHeightAway_DoNotCount()
    {
        // Saved frames (--warwalk): our own "[Premium]" / name tags sit up to 0.176 h from the spot (1024x768: "Premium)"
        // centred at 512,280, 135 px above 512,415; 2048x1152: 191 px) - 0.12 h took them for other players.
        var lines = new[] { Tag("Premium)", 512, 280, 49, 11), Tag("SHCorp", 492, 294, 40, 12) };
        Assert.Null(AutomationPilot.WarWalkGoal(lines, 1024, 768, AutomationPilot.WarWorldBands(new RegionSettings(), 1024, 768)));
        Assert.Null(AutomationPilot.WarWalkGoal([Tag("Premium", 1018, 431, 80, 14)], 2048, 1152,
            AutomationPilot.WarWorldBands(new RegionSettings(), 2048, 1152)));
    }

    [Fact]
    public void WalkGoal_TagsInTheUiBands_SentencesAndShortLines_DoNotCount()
    {
        var lines = new[]
        {
            Tag("TargetBoxName", SpotX, 60),                // top 13 %
            Tag("[Bohai Outpost (Adept)]", 1700, 30),       // minimap
            Tag("Attack succeeded!Normal damage(+50)", 300, 500, 300),   // combat log
            Tag("Skillbar", SpotX, 1000),                   // skill bar
            Tag("[Auto buff scroll] 02.11.2026", 1500, 800, 300),        // item list
            Tag("Gate is Open.", SpotX, 300),
            Tag("Ab", SpotX + 200, 400),
            Tag("Ver: 0.2", SpotX - 200, 400),
        };
        Assert.Null(AutomationPilot.WarWalkGoal(lines, W, H, Bands));
    }

    [Fact]
    public void WalkDue_AfterFiveTabsWithoutATarget_AtMostSixSteps_NotWhileResting()
    {
        Assert.Equal(5, AutomationPilot.WarWalkAfterTabs);
        Assert.Equal(6, AutomationPilot.WarWalkMaxSteps);
        Assert.False(AutomationPilot.WarWalkDue(noTargetTabs: 4, walkSteps: 0, clockNow: Now, restUntil: 0));
        Assert.True(AutomationPilot.WarWalkDue(noTargetTabs: 5, walkSteps: 0, clockNow: Now, restUntil: 0));
        Assert.True(AutomationPilot.WarWalkDue(noTargetTabs: 10, walkSteps: 5, clockNow: Now, restUntil: 0));
        Assert.False(AutomationPilot.WarWalkDue(noTargetTabs: 11, walkSteps: 6, clockNow: Now, restUntil: 0));
        Assert.False(AutomationPilot.WarWalkDue(noTargetTabs: 20, walkSteps: 0, clockNow: Now, restUntil: Now + 1));
        Assert.True(AutomationPilot.WarWalkDue(noTargetTabs: 20, walkSteps: 0, clockNow: Now + 1, restUntil: Now + 1));
    }

    [Fact]
    public void WalkGoal_NoLines_NoGoal() => Assert.Null(AutomationPilot.WarWalkGoal([], W, H, Bands));

    [Fact]
    public void WalkClick_ANearTag_JustBelowIt()
    {
        var (x, y) = AutomationPilot.WarWalkClick(new PixelRect(1050, 400, 100, 16), W, H);
        Assert.Equal(1100, x);
        Assert.Equal(408 + (int)Math.Round(0.04 * H), y);
    }

    [Fact]
    public void WalkClick_AFarTag_PulledInToAQuarterOfTheHeight()
    {
        // Straight up from the spot, 450 px: pulled in to 0.25 h (270 px), the floor point first (+4 % h).
        var (x, y) = AutomationPilot.WarWalkClick(new PixelRect(SpotX - 50, 100, 100, 16), W, H);
        Assert.Equal(SpotX, x);
        Assert.Equal(SpotY - 270, y);
        double away = Math.Sqrt(Math.Pow(x - SpotX, 2) + Math.Pow(y - SpotY, 2));
        Assert.True(away <= 0.25 * H + 1, $"{away} px from the spot");
    }

    [Fact]
    public void WalkClick_ClampedToTheWalkClickArea()
    {
        // Straight down: pulled in to 270 px (y 853), still past 0.78 h (842) - the chat box and the skill bar.
        var (x, y) = AutomationPilot.WarWalkClick(new PixelRect(SpotX - 50, 892, 100, 16), W, H);
        Assert.Equal(SpotX, x);
        Assert.Equal((int)(0.78 * H), y);
        // Low on the left: never left of 0.25 w, above 0.25 h or below 0.78 h.
        foreach (var tag in new[] { new PixelRect(0, 1000, 100, 16), new PixelRect(0, 0, 100, 16), new PixelRect(1800, 200, 100, 16) })
        {
            (x, y) = AutomationPilot.WarWalkClick(tag, W, H);
            Assert.True(x >= (int)(0.25 * W) && x <= (int)(0.74 * W), $"x {x}");
            Assert.True(y >= (int)(0.25 * H) && y <= (int)(0.78 * H), $"y {y}");
        }
    }
}

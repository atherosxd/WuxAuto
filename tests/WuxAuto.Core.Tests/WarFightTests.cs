using WuxAuto.Automation;
using Clock = WuxAuto.Automation.AutomationPilot.FrontClock;
using ItemList = WuxAuto.Automation.AutomationPilot.WarItemList;

namespace WuxAuto.Core.Tests;

public class WarFightTests
{
    const long Now = 1_000_000;
    const int BuffEvery = 345_000;

    // ---------------------------------------------------------------- rebuff

    [Fact]
    public void Rebuff_DueAtTheTabPointOnceBuffEveryMsHasPassed()
    {
        Assert.True(AutomationPilot.WarRebuffDue(Now + BuffEvery, Now, BuffEvery, fightStep: 0));
        Assert.True(AutomationPilot.WarRebuffDue(Now + BuffEvery + 60_000, Now, BuffEvery, fightStep: 0));
    }

    [Fact]
    public void Rebuff_NotBeforeBuffEveryMs()
        => Assert.False(AutomationPilot.WarRebuffDue(Now + BuffEvery - 1, Now, BuffEvery, fightStep: 0));

    [Theory]
    [InlineData(1)]   // reading the target
    [InlineData(2)]   // Shura going down
    [InlineData(3)]   // Shura held
    [InlineData(4)]   // attacking
    public void Rebuff_NeverInsideAFightCycle(int step)
        => Assert.False(AutomationPilot.WarRebuffDue(Now + 2 * BuffEvery, Now, BuffEvery, step));

    // ---------------------------------------------------------------- the HP watch

    static readonly (int, int) Dead = (0, 42833);
    static readonly (int, int) Alive = (41000, 42833);

    static int Reads(int? aliveMax, params (int Cur, int Max)?[] reads)
        => reads.Aggregate(0, (count, hp) => AutomationPilot.WarDeadReads(count, hp, aliveMax));

    [Fact]
    public void DeadReads_TwoInARow_IsDeath()
    {
        Assert.Equal(1, Reads(42833, Dead));
        Assert.Equal(AutomationPilot.WarDeadReadsNeeded, Reads(42833, Dead, Dead));
    }

    [Fact]
    public void DeadReads_AnAliveReadBetween_StartsAgain()
        => Assert.Equal(1, Reads(42833, Dead, Alive, Dead));

    [Fact]
    public void DeadReads_AnUnreadBetween_NeitherCountsNorBreaksTheRow()
        => Assert.Equal(2, Reads(42833, Dead, null, Dead));

    [Fact]
    public void DeadReads_TheBarUnderTheHpBarOrTheMpLine_IsNoDeath_AndBreaksTheRow()
    {
        Assert.Equal(0, Reads(42833, (0, 3000)));
        Assert.Equal(0, Reads(42833, (0, 11467)));
        Assert.Equal(1, Reads(42833, Dead, (0, 3000), Dead));
    }

    [Fact]
    public void DeadReads_NoAliveMaxYet_OnlyAMaxOf1000OrMoreCounts()
    {
        Assert.Equal(2, Reads(null, Dead, Dead));
        Assert.Equal(0, Reads(null, (0, 300), (0, 300)));
    }

    [Fact]
    public void AliveMax_SetByEveryReadAboveZero_KeptOtherwise()
    {
        Assert.Equal(42833, AutomationPilot.WarAliveMax(null, Alive));
        Assert.Equal(51200, AutomationPilot.WarAliveMax(42833, (51200, 51200)));   // buffed: a higher maximum
        Assert.Equal(42833, AutomationPilot.WarAliveMax(42833, (0, 3000)));
        Assert.Equal(42833, AutomationPilot.WarAliveMax(42833, null));
        Assert.Null(AutomationPilot.WarAliveMax(null, Dead));
    }

    [Fact]
    public void AliveMax_SeededBeforeTheFight_KeepsTheBarUnderTheHpBarFromCountingAsDeath()
    {
        int? max = AutomationPilot.WarAliveMax(null, Alive);   // the read on arrival
        Assert.Equal(0, Reads(max, (0, 3000), (0, 3000)));
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
}

using WuxAuto.Automation;

namespace WuxAuto.Core.Tests;

public class WarTimerTests
{
    const long Now = 1_000_000;

    static WarCountdown Left(int seconds) => new(TimeSpan.FromSeconds(seconds), false);

    [Fact]
    public void Countdown_SetsTheWarStart_AndClearsTheFails()
    {
        var plan = AutomationPilot.PlanTimerRead(Now, Left(265), warAt: null, fails: 2);

        Assert.Equal(Now + 265_000, plan.WarAt);
        Assert.Equal(Now + 145_000, plan.NextReadAt);   // 2 min before the war
        Assert.Equal(0, plan.Fails);
    }

    [Theory]
    [InlineData(181, 61)]            // just over 3 min away: 2 min before the war
    [InlineData(16 * 60, 14 * 60)]
    [InlineData(17 * 60, 15 * 60)]   // 2 min before and the 15 min cap agree
    [InlineData(3600, 15 * 60)]      // far away: no later than 15 min from now
    [InlineData(23 * 3600, 15 * 60)]
    public void Countdown_MoreThanThreeMinutes_ReadAgainTwoMinutesBefore_AtMostFifteenOn(int left, int readAgainIn)
        => Assert.Equal(Now + readAgainIn * 1000L, AutomationPilot.PlanTimerRead(Now, Left(left), null, 0).NextReadAt);

    [Theory]
    [InlineData(180)]
    [InlineData(60)]
    [InlineData(0)]
    public void Countdown_ThreeMinutesOrLess_NotReadAgain(int left)
        => Assert.Equal(long.MaxValue, AutomationPilot.PlanTimerRead(Now, Left(left), null, 0).NextReadAt);

    [Fact]
    public void InProgress_ThisWarIsMissed_ReadAgainInFiveMinutes()
    {
        var plan = AutomationPilot.PlanTimerRead(Now, new WarCountdown(null, true), warAt: Now + 5_000, fails: 1);

        Assert.Null(plan.WarAt);
        Assert.Equal(Now + 300_000, plan.NextReadAt);
        Assert.Equal(0, plan.Fails);
    }

    [Fact]
    public void Unread_KeepsTheKnownStart_RetriesAtOnce_FromTheThirdInARowEveryTwoMinutes()
    {
        long known = Now + 600_000;

        var first = AutomationPilot.PlanTimerRead(Now, null, known, 0);
        var second = AutomationPilot.PlanTimerRead(Now, null, known, first.Fails);
        var third = AutomationPilot.PlanTimerRead(Now, null, known, second.Fails);
        var fourth = AutomationPilot.PlanTimerRead(Now, null, known, third.Fails);

        Assert.Equal((known, Now, 1), (first.WarAt, first.NextReadAt, first.Fails));
        Assert.Equal((known, Now, 2), (second.WarAt, second.NextReadAt, second.Fails));
        Assert.Equal((known, Now + 120_000, 3), (third.WarAt, third.NextReadAt, third.Fails));
        Assert.Equal((known, Now + 120_000, 4), (fourth.WarAt, fourth.NextReadAt, fourth.Fails));
    }

    [Fact]
    public void Unread_NoStartKnown_StaysUnknown() => Assert.Null(AutomationPilot.PlanTimerRead(Now, null, null, 0).WarAt);

    [Fact]
    public void JoinDeadline_FromTheLead_IsTheTimersStartPlusTheDialogWait()
        => Assert.Equal(Now + 10_000 + 120_000, AutomationPilot.WarJoinDeadline(Now, Now + 10_000, leadMs: 10_000, dialogWaitMs: 120_000));

    [Theory]
    [InlineData(null)]          // no timer read: the dialog is up now
    [InlineData(-600_000L)]     // a start from an earlier wait
    [InlineData(1_800_000L)]    // the dialog came long before the timer said
    [InlineData(-2_000L)]       // the lead tick came a little late
    public void JoinDeadline_OtherwiseCountsFromNow(long? warAtFromNow)
        => Assert.Equal(Now + 120_000, AutomationPilot.WarJoinDeadline(Now, Now + warAtFromNow, leadMs: 10_000, dialogWaitMs: 120_000));
}

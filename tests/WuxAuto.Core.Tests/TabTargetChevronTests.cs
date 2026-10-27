using WuxAuto.Automation;

namespace WuxAuto.Core.Tests;

public class TabTargetChevronTests
{
    // Laby/LOD ask for Default: the decision must be today's - "already Default" when
    // IsDefaultTabTarget says so, else one click on the left chevron (also for an unrecognised read).
    [Theory]
    [InlineData("Default")]
    [InlineData("Defau1t")]
    [InlineData("Defaul")]
    [InlineData("Nearest enemy")]
    [InlineData("Nearest enemv")]
    [InlineData("Enemy worth CP")]
    [InlineData("Enemy worth C")]
    [InlineData("Nearest monster")]
    [InlineData("Nearest")]
    [InlineData("Sound")]
    [InlineData("100%")]
    [InlineData("")]
    public void Default_DecidesAsBefore(string value)
        => Assert.Equal(AutomationPilot.IsDefaultTabTarget(value) ? 0 : -1, AutomationPilot.TabTargetChevron(value, "Default"));

    [Theory]
    [InlineData("Default", 1)]
    [InlineData("Nearest enemy", 1)]
    [InlineData("Enemy worth CP", 0)]
    [InlineData("Nearest monster", -1)]
    public void EnemyWorthCp_StepsEitherWay(string value, int chevron)
        => Assert.Equal(chevron, AutomationPilot.TabTargetChevron(value, "Enemy worth CP"));

    [Fact]
    public void UnreadValue_OnlyStepsTowardsDefault()
        => Assert.Null(AutomationPilot.TabTargetChevron("Sound", "Enemy worth CP"));
}

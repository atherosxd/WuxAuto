using WuxAuto.Automation;

namespace WuxAuto.Core.Tests;

public class TabTargetOptionsTests
{
    [Theory]
    [InlineData("Default", "Enemy worth CP", 2)]
    [InlineData("Nearest monster", "Enemy worth CP", -1)]
    [InlineData("Enemy worth CP", "Enemy worth CP", 0)]
    [InlineData("Nearest enemy", "Default", -1)]
    [InlineData("Enemy worth C", "Default", -2)]
    public void StepsTo(string current, string wanted, int steps) => Assert.Equal(steps, TabTargetOptions.StepsTo(current, wanted));

    [Fact] public void StepsTo_UnreadValueIsNull() => Assert.Null(TabTargetOptions.StepsTo("Sound", "Default"));
}

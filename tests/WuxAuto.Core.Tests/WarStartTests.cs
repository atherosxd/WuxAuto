using WuxAuto.Automation;

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
}

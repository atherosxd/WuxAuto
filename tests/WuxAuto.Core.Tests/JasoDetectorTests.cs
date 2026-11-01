using System.Drawing;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using WuxAuto.Automation;
using WuxAuto.Vision;

namespace WuxAuto.Core.Tests;

/// <summary>
/// Auto Blossom without Tab (user, 2026-11-01: "they have disabled tabbing in the Blossom, can you possibly click this mob
/// instead by using the color, shape"): a Jaso Practitioner seen in the game world - a dark magenta-red fan over an
/// orange-red skirt, a standing figure (live 2026-11-01 at 2048x1152: 38x66, fan H 172-177 S ~120 V ~67, skirt H 4-9 S ~157).
/// </summary>
public class JasoDetectorTests
{
    private const int W = 1440, H = 900;

    // BGR: grass (yellow-green), the fan (dark, blue above green: magenta side), the skirt (orange-red), red-brown wood.
    private static readonly MCvScalar Grass = new(62, 128, 127), Fan = new(45, 30, 70), Skirt = new(40, 80, 170), Wood = new(19, 19, 48);

    private static Mat Field()
    {
        var bgr = new Mat(H, W, DepthType.Cv8U, 3);
        bgr.SetTo(Grass);
        return bgr;
    }

    private static void DrawJaso(Mat bgr, int x, int y)
    {
        CvInvoke.Rectangle(bgr, new Rectangle(x, y, 30, 15), Fan, -1);            // the fan, on top
        CvInvoke.Rectangle(bgr, new Rectangle(x + 8, y + 15, 14, 40), Skirt, -1);  // the body and skirt under it
    }

    private static IReadOnlyList<PixelRect> Find(Mat bgr)
    {
        using var bgra = new Mat();
        CvInvoke.CvtColor(bgr, bgra, ColorConversion.Bgr2Bgra);
        return ScreenAnalyzer.FindJasos(bgra, AutomationPilot.WarWalkBands(W, H));
    }

    [Fact]
    public void AJasoInTheOpen_IsFound()
    {
        using var f = Field();
        DrawJaso(f, 600, 250);

        var hit = Assert.Single(Find(f));
        Assert.InRange(hit.CenterX, 600, 630);
        Assert.InRange(hit.CenterY, 250, 305);
    }

    [Fact]
    public void Several_NearestToTheCharacterFirst()
    {
        using var f = Field();
        DrawJaso(f, 1050, 220);   // far up right
        DrawJaso(f, 560, 470);    // left of the character, about level with it

        var hits = Find(f);
        Assert.Equal(2, hits.Count);
        Assert.InRange(hits[0].CenterX, 560, 590);
    }

    [Fact]
    public void TheCharactersOwnSpot_IsLeftOut()
    {
        // Live 2026-10-30: the character's dark wings with red flame edges read like a Jaso.
        using var f = Field();
        DrawJaso(f, W / 2 - 15, H / 2 - 20);

        Assert.Empty(Find(f));
    }

    [Fact]
    public void ARedBrownPost_WithOrange_IsNotAJaso()
    {
        // Live 2026-10-30 11:50: a shrine gate's dark red-brown posts (blue equal to green, hue ~0, not magenta).
        using var f = Field();
        CvInvoke.Rectangle(f, new Rectangle(600, 250, 30, 15), Wood, -1);
        CvInvoke.Rectangle(f, new Rectangle(608, 265, 14, 40), Skirt, -1);

        Assert.Empty(Find(f));
    }

    [Fact]
    public void AFanWithoutAnOrangeBody_IsNotAJaso()
    {
        using var f = Field();
        CvInvoke.Rectangle(f, new Rectangle(600, 250, 30, 55), Fan, -1);

        Assert.Empty(Find(f));
    }

    [Fact]
    public void UnderTheUi_IsLeftOut()
    {
        using var f = Field();
        DrawJaso(f, 100, 300);   // the combat log on the left

        Assert.Empty(Find(f));
    }

    [Fact]
    public void GrassOnly_NothingFound()
    {
        using var f = Field();
        Assert.Empty(Find(f));
        Assert.Empty(Candidates(f));
    }

    // ---- candidates (user, 2026-11-01: "it should be able to click a jaso from afar with just some of the characteristics
    // of the monster"): smaller and partial figures too - the pilot clicks one only where the cursor turns into a sword.

    private static IReadOnlyList<JasoCandidate> Candidates(Mat bgr)
    {
        using var bgra = new Mat();
        CvInvoke.CvtColor(bgr, bgra, ColorConversion.Bgr2Bgra);
        return ScreenAnalyzer.FindJasoCandidates(bgra, AutomationPilot.WarWalkBands(W, H));
    }

    [Fact]
    public void AFullJaso_IsAFullCandidate()
    {
        using var f = Field();
        DrawJaso(f, 600, 250);

        var c = Assert.Single(Candidates(f));
        Assert.True(c.Full);
        Assert.InRange(c.Box.CenterX, 600, 630);
    }

    [Fact]
    public void AFarSmallJaso_IsAPartialCandidate_NotAFullHit()
    {
        // Far up the field (live 2026-11-01 11:36): about 15x30 px at 2048x1152, under the full hit's 3 % of the height.
        using var f = Field();
        CvInvoke.Rectangle(f, new Rectangle(900, 200, 14, 7), Fan, -1);
        CvInvoke.Rectangle(f, new Rectangle(904, 207, 6, 12), Skirt, -1);

        Assert.Empty(Find(f));
        var c = Assert.Single(Candidates(f));
        Assert.False(c.Full);
        Assert.InRange(c.Box.CenterX, 898, 916);
    }

    [Fact]
    public void AFanOnly_IsAPartialCandidate()
    {
        // Seen from behind: only the wide fan.
        using var f = Field();
        CvInvoke.Rectangle(f, new Rectangle(600, 250, 40, 22), Fan, -1);

        Assert.Empty(Find(f));
        var c = Assert.Single(Candidates(f));
        Assert.False(c.Full);
    }

    [Fact]
    public void AFlowerDot_IsNoCandidate()
    {
        using var f = Field();
        CvInvoke.Rectangle(f, new Rectangle(700, 300, 5, 5), new MCvScalar(160, 90, 230), -1);   // bright pink

        Assert.Empty(Candidates(f));
    }

    [Fact]
    public void Candidates_LeaveOutTheUi()
    {
        using var f = Field();
        DrawJaso(f, 100, 300);   // the combat log on the left

        Assert.Empty(Candidates(f));
    }

    [Fact]
    public void AJasoAtTheCharactersSpot_IsAPartialCandidate_NeverAFullHit()
    {
        // User, 2026-11-01 13:45: "doesn't target the moving monster / or making it's AOE attack. that is near the character
        // and continues to rotate the cam" - the own-spot rule (the character's wings read like a Jaso) hid her. As a partial
        // candidate she is clicked only where the cursor turns into a sword - never the character's own wings.
        using var f = Field();
        DrawJaso(f, W / 2 - 15, H / 2 - 20);

        Assert.Empty(Find(f));
        var c = Assert.Single(Candidates(f));
        Assert.False(c.Full);
        Assert.True(ScreenAnalyzer.IsOwnSpot(c.Box, W, H));
    }
}

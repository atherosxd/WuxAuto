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

    private static void DrawJaso(Mat bgr, int x, int y, double scale = 1)
    {
        int S(double v) => (int)Math.Round(v * scale);
        CvInvoke.Rectangle(bgr, new Rectangle(x, y, S(30), S(15)), Fan, -1);                  // the fan, on top
        CvInvoke.Rectangle(bgr, new Rectangle(x + S(8), y + S(15), S(14), S(40)), Skirt, -1);  // the body and skirt under it
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

    // ---- 2026-11-01 16:23 (live 0.25.8 click snapshots): near trees the game pulls the camera in, the character is drawn big
    // and low (its HP bar at 0.75 h instead of 0.56 h) - its red mask and legs were clicked as Jasos. The character is found
    // by its own HP bar: a red strip over a blue one, same left edge, centred under its feet.

    /// <summary>The character's own HP / MP bar at <paramref name="y"/> (sizes of the 2048x1152 live bar, scaled to 900 h).</summary>
    private static void DrawOwnBar(Mat bgr, int y)
    {
        CvInvoke.Rectangle(bgr, new Rectangle(W / 2 - 42, y, 84, 6), new MCvScalar(40, 40, 200), -1);        // HP, red
        CvInvoke.Rectangle(bgr, new Rectangle(W / 2 - 42, y + 9, 72, 5), new MCvScalar(220, 110, 40), -1);   // MP, blue
    }

    [Fact]
    public void TheOwnHpBar_GivesTheCharactersBox_BigWhenTheCameraIsClose()
    {
        using var near = Field();
        DrawOwnBar(near, (int)(0.556 * H));
        using var close = Field();
        DrawOwnBar(close, (int)(0.752 * H));

        var n = AsBgra(near, ScreenAnalyzer.FindOwnCharacter);
        var c = AsBgra(close, ScreenAnalyzer.FindOwnCharacter);

        Assert.NotNull(n);
        Assert.NotNull(c);
        Assert.InRange(n!.Value.Bottom, (int)(0.556 * H) - 4, (int)(0.556 * H) + 20);
        Assert.InRange(n.Value.Height, (int)(0.15 * H), (int)(0.22 * H));     // drawn small
        Assert.InRange(c!.Value.Height, (int)(0.40 * H), (int)(0.55 * H));    // drawn big
        Assert.InRange(c.Value.CenterX, W / 2 - 10, W / 2 + 10);
    }

    [Fact]
    public void CameraClose_AFigureOnTheCharacter_IsNotAJaso_OneBesideIs()
    {
        using var f = Field();
        int barY = (int)(0.752 * H);
        DrawOwnBar(f, barY);
        DrawJaso(f, W / 2 - 15, barY - 360);           // the character's red mask, high above its feet (centre 0.38 h)
        DrawJaso(f, W / 2 - 15 + (int)(0.25 * H), barY - 360);   // a Jaso beside it

        var hit = Assert.Single(Find(f));
        Assert.InRange(hit.CenterX, W / 2 + (int)(0.25 * H) - 10, W / 2 + (int)(0.25 * H) + 20);
    }

    [Fact]
    public void AtTheCharactersDepth_AFigureFarSmallerThanItIsNotAJaso_OneAsTallIs()
    {
        // Live 18:37-18:45 (0.25.11): looking aside showed the character side-on, and its small red pet trotting beside it was
        // clicked as a Jaso a dozen times (also a red rose in front of it). A Jaso standing as near as the character is about
        // as tall as the character (0.13-0.17 h); the pet was ~0.07 h. Further up the screen (further off) figures are small.
        using var f = Field();
        int barY = (int)(0.556 * H);
        DrawOwnBar(f, barY);
        DrawJaso(f, 600, barY - 55);                  // pet-sized (55 px), left of the character, feet level with its own
        DrawJaso(f, 850, barY - 107, scale: 2.5);     // Jaso-sized (137 px), right of the character, a little nearer
        DrawJaso(f, 600, 150);                        // small, far up the screen: a far Jaso
        DrawJaso(f, 990, barY - 55 - (int)(0.07 * H)); // small, feet 0.07 h above the character's: far off (F11 03:17:00)

        var hits = Find(f);

        string seen = string.Join(", ", hits);
        Assert.True(hits.Count == 3, seen);
        Assert.Contains(hits, h => Math.Abs(h.X - 990) <= 4);           // the far one a little up
        Assert.Contains(hits, h => h.CenterX > W / 2 + 100 && h.Height > 100);   // the tall one beside
        Assert.Contains(hits, h => h.CenterY < 250);                    // the far one
        Assert.DoesNotContain(Candidates(f), c => c.Full && c.Box.CenterY > 350 && c.Box.CenterX < W / 2);
    }

    [Fact]
    public void NoHpBar_NoOwnBox()
    {
        using var f = Field();
        Assert.Null(AsBgra(f, ScreenAnalyzer.FindOwnCharacter));
    }

    private static T AsBgra<T>(Mat bgr, Func<Mat, T> read)
    {
        using var bgra = new Mat();
        CvInvoke.CvtColor(bgr, bgra, ColorConversion.Bgr2Bgra);
        return read(bgra);
    }

    [Fact]
    public void AJasoBesideTheCharacter_IsAFullHit()
    {
        // User, 2026-11-01 15:05: "it doesn't detect the Jaso just beside the character" - only the character's own body
        // column is left out now; its wings are told apart by their navy (below).
        using var f = Field();
        DrawJaso(f, W / 2 - 15 - (int)(0.08 * H), H / 2 - 20);   // 0.08 h left of the character

        var hit = Assert.Single(Find(f));
        Assert.InRange(hit.CenterX, W / 2 - (int)(0.08 * H) - 20, W / 2 - (int)(0.08 * H) + 20);
    }

    [Fact]
    public void AJasoInFrontOfTheRiver_IsFound()
    {
        // F11 2026-11-02 03:16:57: a Jaso beside the character with the river behind her read navy 0.20 over her box (the
        // water at her sides) and was taken for the character's wings.
        using var f = Field();
        int x = W / 2 - 15 - (int)(0.12 * H), y = H / 2 - 40;
        CvInvoke.Rectangle(f, new Rectangle(0, y + 15, W, 40), new MCvScalar(140, 80, 40), -1);   // the river, navy blue
        DrawJaso(f, x, y);

        var hit = Assert.Single(Find(f));
        Assert.InRange(hit.CenterX, x, x + 30);
    }

    [Fact]
    public void TheCharactersWings_NavyWithRedEdges_AreNotAJaso()
    {
        // Live 2026-10-30 (two snapshots): the character's dark navy wings with red flame edges passed the fan and skirt
        // rules beside the character - navy 26-54 % of their box, 0-0.3 % on four real Jasos.
        using var f = Field();
        int x = W / 2 - (int)(0.10 * H) - 25, y = H / 2 - 35;
        CvInvoke.Rectangle(f, new Rectangle(x, y, 50, 70), new MCvScalar(90, 40, 20), -1);        // navy wing
        CvInvoke.Rectangle(f, new Rectangle(x, y, 50, 12), Fan, -1);                               // dark red-magenta edge on top
        CvInvoke.Rectangle(f, new Rectangle(x, y, 5, 70), Skirt, -1);                              // red-orange flame edges round it
        CvInvoke.Rectangle(f, new Rectangle(x + 45, y, 5, 70), Skirt, -1);
        CvInvoke.Rectangle(f, new Rectangle(x + 10, y + 50, 30, 20), Skirt, -1);

        Assert.Empty(Find(f));
        Assert.DoesNotContain(Candidates(f), c => c.Full);
    }

    [Fact]
    public void ASideOnJaso_TheClickPointIsOnHerBody_NotTheMiddleOfHerBox()
    {
        // User, 2026-11-01 15:05: "it detected the jaso from afar. it click on its right not the body of the monster" - the
        // fan sticks out to one side; the click point is the middle of her orange skirt.
        using var f = Field();
        CvInvoke.Rectangle(f, new Rectangle(600, 250, 60, 15), Fan, -1);            // wide fan, out to the right
        CvInvoke.Rectangle(f, new Rectangle(600, 265, 14, 40), Skirt, -1);           // her body, at the left

        var c = Assert.Single(Candidates(f));
        Assert.InRange(c.Body.X, 600, 614);
        Assert.InRange(c.Body.Y, 265, 305);
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

    [Fact]
    public void AFigureCutOffByTheScreenEdge_IsNoCandidate()
    {
        // Live 2026-11-02 04:01 (0.25.17): a small red figure cut off by the left edge of the screen was clicked at 6,283 -
        // nothing targeted. Half of her is not on the screen, so nothing says what she is, and a click there is not on her.
        using var f = Field();
        DrawJaso(f, -10, 400);       // her left half off the screen
        DrawJaso(f, W - 20, 600);    // her right half off the screen

        using var bgra = new Mat();
        CvInvoke.CvtColor(f, bgra, ColorConversion.Bgr2Bgra);
        Assert.Empty(ScreenAnalyzer.FindJasoCandidates(bgra, []));
    }

    [Fact]
    public void NoHpBar_CameraClose_AFigureUnderTheMiddle_IsTheCharactersOwn_OneBesideIsAJaso()
    {
        // Live 2026-11-02 04:01 (0.25.17): in the reeds the camera pulled in, the character filled the screen with its HP bar
        // out of sight, and its own gear below the middle (centre 0.04 h aside, 0.65 h down) was clicked as a Jaso.
        using var f = Field();
        int y = (int)(0.65 * H) - 27;
        DrawJaso(f, W / 2 - 15 + (int)(0.04 * H), y);   // the character's gear, under the middle
        DrawJaso(f, W / 2 - 15 + (int)(0.15 * H), y);   // a Jaso beside it

        var hit = Assert.Single(Find(f));
        Assert.InRange(hit.CenterX, W / 2 + (int)(0.15 * H) - 10, W / 2 + (int)(0.15 * H) + 25);
        Assert.Contains(Candidates(f), c => !c.Full && c.AtOwnSpot);
    }
}

using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using WuxAuto.Vision;

namespace WuxAuto.Core.Tests;

/// <summary>CvHelpers' pixel copies size a managed array from the Mat and let OpenCV fill it, so a Mat of the wrong kind is refused first.</summary>
public class CvHelpersTests
{
    [Fact]
    public void Int32s_RefusesAWrongDepthMat()
    {
        using var doubles = new Mat(2, 5, DepthType.Cv64F, 1);
        Assert.Throws<ArgumentException>(() => CvHelpers.Int32s(doubles));
    }

    [Fact]
    public void Int32s_RefusesAMultiChannelMat()
    {
        using var pairs = new Mat(2, 5, DepthType.Cv32S, 2);
        Assert.Throws<ArgumentException>(() => CvHelpers.Int32s(pairs));
    }

    [Fact]
    public void Bytes_RefusesAWrongDepthMat()
    {
        using var shorts = new Mat(4, 4, DepthType.Cv16U, 1);
        Assert.Throws<ArgumentException>(() => CvHelpers.Bytes(shorts));
    }

    [Fact]
    public void Int32sAndBytes_CopyMatchingMats()
    {
        using var stats = new Mat(2, 5, DepthType.Cv32S, 1);
        stats.SetTo(new MCvScalar(7));
        Assert.Equal(Enumerable.Repeat(7, 10), CvHelpers.Int32s(stats));

        using var bgra = new Mat(3, 3, DepthType.Cv8U, 4);
        bgra.SetTo(new MCvScalar(1, 2, 3, 4));
        byte[] pixels = CvHelpers.Bytes(bgra);
        Assert.Equal(36, pixels.Length);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, pixels[32..]);
    }
}

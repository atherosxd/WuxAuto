using System.Drawing;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using Emgu.CV.Util;

namespace WuxAuto.Vision;

/// <summary>
/// Emgu CV calls with the OpenCV defaults the vision code was written against (Emgu leaves them to
/// the caller), and pixel reads that copy out of a Mat through OpenCV rather than touching its
/// memory from managed code.
/// </summary>
internal static class CvHelpers
{
    /// <summary>cv::minMaxLoc: the maximum and its (first, in raster order) location.</summary>
    public static (double Max, Point Location) MaxLoc(IInputArray image)
    {
        double min = 0, max = 0;
        Point minLocation = default, maxLocation = default;
        CvInvoke.MinMaxLoc(image, ref min, ref max, ref minLocation, ref maxLocation);
        return (max, maxLocation);
    }

    /// <summary>
    /// cv::rectangle(img, Rect, ...): corners (x, y) and (x + w - 1, y + h - 1), nothing for an empty
    /// rectangle - as the vision code drew before the Emgu port. Emgu's CvInvoke.Rectangle draws to
    /// (x + w, y + h), one pixel more to the right and down, which changed TemplateMatcher's peak
    /// suppression (parity probe).
    /// </summary>
    public static void DrawRectangle(IInputOutputArray image, Rectangle rect, MCvScalar color, int thickness, LineType lineType = LineType.EightConnected)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
            return;

        CvInvoke.Rectangle(image, new Rectangle(rect.X, rect.Y, rect.Width - 1, rect.Height - 1), color, thickness, lineType);
    }

    /// <summary>cv::inRange with scalar bounds.</summary>
    public static void InRange(IInputArray src, MCvScalar lower, MCvScalar upper, IOutputArray dst)
    {
        using var low = new ScalarArray(lower);
        using var high = new ScalarArray(upper);
        CvInvoke.InRange(src, low, high, dst);
    }

    /// <summary>cv::getStructuringElement with the anchor at the kernel centre.</summary>
    public static Mat StructuringElement(MorphShapes shape, Size size)
        => CvInvoke.GetStructuringElement(shape, size, new Point(-1, -1));

    /// <summary>cv::dilate: anchor at the kernel centre, one iteration, constant border of morphologyDefaultBorderValue.</summary>
    public static void Dilate(IInputArray src, IOutputArray dst, IInputArray kernel)
        => CvInvoke.Dilate(src, dst, kernel, new Point(-1, -1), 1, BorderType.Constant, CvInvoke.MorphologyDefaultBorderValue);

    /// <summary>cv::morphologyEx: anchor at the kernel centre, one iteration, constant border of morphologyDefaultBorderValue.</summary>
    public static void MorphologyEx(IInputArray src, IOutputArray dst, MorphOp operation, IInputArray kernel)
        => CvInvoke.MorphologyEx(src, dst, operation, kernel, new Point(-1, -1), 1, BorderType.Constant, CvInvoke.MorphologyDefaultBorderValue);

    /// <summary>
    /// The outer contours of a binary mask (RETR_EXTERNAL, CHAIN_APPROX_SIMPLE) in OpenCV's order:
    /// the bounding box and the area of each.
    /// </summary>
    public static List<(Rectangle Box, double Area)> ExternalContours(IInputOutputArray mask)
    {
        using var contours = new VectorOfVectorOfPoint();
        CvInvoke.FindContours(mask, contours, null, RetrType.External, ChainApproxMethod.ChainApproxSimple);
        var list = new List<(Rectangle, double)>(contours.Size);
        for (int i = 0; i < contours.Size; i++)
        {
            using var contour = contours[i];
            list.Add((CvInvoke.BoundingRectangle(contour), CvInvoke.ContourArea(contour)));
        }

        return list;
    }

    /// <summary>A copy of an 8-bit Mat's pixels, rows packed one after the other (index y * Width * channels + x * channels + c).</summary>
    /// <exception cref="ArgumentException">The Mat is not 8-bit (any channel count).</exception>
    public static byte[] Bytes(Mat mat)
    {
        // The array is sized from the Mat's own element size; the native copy trusts it, so a Mat of
        // another depth must be refused before it writes past the managed array.
        if (mat.Depth != DepthType.Cv8U)
            throw new ArgumentException($"CvHelpers.Bytes needs an 8-bit Mat, got {mat.Depth} with {mat.NumberOfChannels} channel(s).", nameof(mat));

        var data = new byte[mat.Rows * mat.Cols * mat.ElementSize];
        mat.CopyTo(data);
        return data;
    }

    /// <summary>A copy of a single-channel 32-bit integer Mat (connectedComponentsWithStats' stats), row-major.</summary>
    /// <exception cref="ArgumentException">The Mat is not single-channel 32-bit signed.</exception>
    public static int[] Int32s(Mat mat)
    {
        // One int per element: anything wider (more channels, a 64-bit depth) would make the native
        // copy write past the managed array.
        if (mat.Depth != DepthType.Cv32S || mat.NumberOfChannels != 1)
            throw new ArgumentException($"CvHelpers.Int32s needs a single-channel 32-bit integer Mat, got {mat.Depth} with {mat.NumberOfChannels} channel(s).", nameof(mat));

        var data = new int[mat.Rows * mat.Cols];
        mat.CopyTo(data);
        return data;
    }
}

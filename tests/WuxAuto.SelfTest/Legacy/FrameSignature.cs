// FROZEN COPY for the Emgu parity probe: the pre-port OpenCvSharp vision code. Do not edit.
using System.Numerics;
using OpenCvSharp;
using WuxAuto.Core;
using WuxAuto.Vision;

namespace WuxAuto.Legacy;

/// <summary>Tiny average-hash signature used to notice that the screen finished loading.</summary>
public static class FrameSignature
{
    public static ulong Compute(Mat frameBgra, PixelRect roi)
    {
        var area = roi.Clamp(frameBgra.Width, frameBgra.Height);
        if (area.Width < 8 || area.Height < 8)
            return 0;

        using var gray = TemplateMatcher.ToGray(frameBgra, area);
        using var small = new Mat();
        Cv2.Resize(gray, small, new Size(8, 8), 0, 0, InterpolationFlags.Area);

        double sum = 0;
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
                sum += small.At<byte>(y, x);
        }

        double mean = sum / 64.0;
        ulong hash = 0;
        int bit = 0;
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                if (small.At<byte>(y, x) > mean)
                    hash |= 1UL << bit;
                bit++;
            }
        }

        return hash;
    }

    public static int Distance(ulong a, ulong b) => BitOperations.PopCount(a ^ b);
}

using System.Runtime.InteropServices;
using Emgu.CV;
using Emgu.CV.CvEnum;
using WuxAuto.Capture;

namespace WuxAuto.SelfTest;

/// <summary>Captured frames as Emgu Mats for the live probes (--capture, --input, --do).</summary>
internal static class CaptureMats
{
    /// <summary>
    /// A BGRA copy of <paramref name="frame"/>: a Mat header over the pinned capture buffer, cloned
    /// before the pin is released, so the result owns its pixels.
    /// </summary>
    public static Mat Copy(CaptureFrame frame)
    {
        var handle = GCHandle.Alloc(frame.Buffer, GCHandleType.Pinned);
        try
        {
            using var view = new Mat(frame.Height, frame.Width, DepthType.Cv8U, 4, handle.AddrOfPinnedObject(), frame.Stride);
            return view.Clone();
        }
        finally
        {
            handle.Free();
        }
    }
}

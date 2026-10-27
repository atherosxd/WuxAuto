using System.IO;
using OpenCvSharp;
using WuxAuto.Core;

namespace WuxAuto.Vision;

/// <summary>
/// Plain screenshots (no overlays) of a boss kill, so the user can check afterwards whether the
/// drops were picked up (user, 2026-10-24). JPEG keeps them small; only the newest
/// <see cref="MaxFiles"/> are kept, since an LOD session can kill the boss many times.
/// </summary>
public static class KillScreenshots
{
    public const int MaxFiles = 500;

    /// <returns>The file written, or an empty string when it could not be saved.</returns>
    public static string Save(Mat frameBgra, string tag, string? folder = null, int maxFiles = MaxFiles)
    {
        try
        {
            folder ??= AppPaths.KillsFolder;
            Directory.CreateDirectory(folder);

            using var bgr = new Mat();
            if (frameBgra.Channels() == 4)
                Cv2.CvtColor(frameBgra, bgr, ColorConversionCodes.BGRA2BGR);
            else
                frameBgra.CopyTo(bgr);

            // Encoded in memory and written by .NET: Cv2.ImWrite cannot open paths with
            // non-ASCII characters (a user name in the profile path) on Windows.
            Cv2.ImEncode(".jpg", bgr, out byte[] jpeg, new ImageEncodingParam(ImwriteFlags.JpegQuality, 90));
            string safeTag = string.Concat(tag.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
            string file = Path.Combine(folder, $"{DateTime.Now:yyyyMMdd_HHmmss_fff}_{safeTag}.jpg");
            File.WriteAllBytes(file, jpeg);

            Prune(folder, maxFiles);
            return file;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>Deletes all but the newest <paramref name="maxFiles"/> shots (the names start with the time).</summary>
    private static void Prune(string folder, int maxFiles)
    {
        var files = Directory.GetFiles(folder, "*.jpg");
        if (files.Length <= maxFiles)
            return;

        foreach (string old in files.OrderByDescending(Path.GetFileName, StringComparer.Ordinal).Skip(Math.Max(1, maxFiles)))
        {
            try
            {
                File.Delete(old);
            }
            catch
            {
                // in use (open in a viewer) - the next save tries again
            }
        }
    }
}

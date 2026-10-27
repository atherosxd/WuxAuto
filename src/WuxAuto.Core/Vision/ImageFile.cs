using System.IO;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Util;

namespace WuxAuto.Vision;

/// <summary>
/// Image files through .NET file I/O, decoded and encoded in memory: OpenCV's own imread / imwrite
/// cannot open paths with non-ASCII characters (a user name in the profile path) on Windows.
/// </summary>
public static class ImageFile
{
    /// <summary>
    /// Decodes <paramref name="path"/>. Like imread, a missing, unreadable or undecodable file gives
    /// an empty Mat rather than an exception.
    /// </summary>
    public static Mat Read(string path, ImreadModes mode = ImreadModes.Unchanged)
    {
        var image = new Mat();
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length > 0)
                CvInvoke.Imdecode(bytes, mode, image);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CvException)
        {
            image.Dispose();
            image = new Mat();
        }

        return image;
    }

    /// <summary>Encodes <paramref name="image"/> in the format of <paramref name="path"/>'s extension and writes it.</summary>
    /// <exception cref="InvalidOperationException">The image could not be encoded.</exception>
    public static void Write(string path, IInputArray image, params KeyValuePair<ImwriteFlags, int>[] parameters)
    {
        byte[]? encoded = CvInvoke.Imencode(Path.GetExtension(path), image, parameters);
        if (encoded == null)
            throw new InvalidOperationException($"Could not encode {Path.GetFileName(path)}.");

        File.WriteAllBytes(path, encoded);
    }
}

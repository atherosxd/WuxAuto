using System.IO;
using System.Runtime.InteropServices;
using NVorbis;

namespace WuxAuto.Core;

/// <summary>
/// Ogg Vorbis to 16-bit PCM WAV (user, 2026-10-27: "also allow ogg"). Windows has no OGG decoder of
/// its own - WPF's <see cref="System.Windows.Media.MediaPlayer"/> answers "Media file download
/// failed" - so a picked .ogg is decoded with NVorbis (managed code, MIT) and kept as a WAV, which
/// plays on every Windows, N and "lite" builds included.
/// </summary>
public static class OggToWav
{
    /// <summary>Longer sounds are refused: the WAV would be ~10 MB a minute, and the chime plays in full on every kill.</summary>
    public static readonly TimeSpan MaxLength = TimeSpan.FromMinutes(5);

    public static bool IsOgg(string file) =>
        string.Equals(Path.GetExtension(file), ".ogg", StringComparison.OrdinalIgnoreCase);

    /// <summary>Decodes <paramref name="ogg"/> into <paramref name="wav"/> (created or overwritten).</summary>
    /// <returns>Empty when it worked; otherwise what went wrong (a part-written <paramref name="wav"/> is deleted).</returns>
    public static string Convert(string ogg, string wav)
    {
        string error = Decode(ogg, wav);
        if (error.Length > 0)
        {
            try
            {
                File.Delete(wav);
            }
            catch
            {
                // a leftover temp file does no harm
            }
        }

        return error;
    }

    private static string Decode(string ogg, string wav)
    {
        try
        {
            string kind = Sniff(ogg);
            if (kind.Length > 0)
                return kind;

            using var reader = new VorbisReader(ogg);
            int channels = reader.Channels;
            int rate = reader.SampleRate;
            if (channels < 1 || rate < 1)
                return "it has no sound";

            using var output = new FileStream(wav, FileMode.Create, FileAccess.Write);
            using var writer = new BinaryWriter(output);
            WriteHeader(writer, channels, rate, 0);

            var samples = new float[channels * 4096];
            var pcm = new short[samples.Length];
            long maxFrames = (long)(rate * MaxLength.TotalSeconds);
            long frames = 0;
            int read;
            while ((read = reader.ReadSamples(samples, 0, samples.Length)) > 0)
            {
                for (int i = 0; i < read; i++)
                    pcm[i] = (short)Math.Round(Math.Clamp(samples[i], -1f, 1f) * short.MaxValue);
                writer.Write(MemoryMarshal.AsBytes(pcm.AsSpan(0, read)));
                frames += read / channels;
                if (frames > maxFrames)
                    return $"it is longer than {MaxLength.TotalMinutes:0} minutes - pick a short sound";
            }

            if (frames == 0)
                return "it has no sound";

            output.Position = 0;
            WriteHeader(writer, channels, rate, frames * channels * sizeof(short));
            return string.Empty;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException and not EndOfStreamException)
        {
            return ex.Message;   // reading the OGG or writing the WAV (in use, disk full, ...)
        }
        catch
        {
            // NVorbis's own words ("Could not load the specified container!") mean nothing to the user.
            return "it is not Ogg Vorbis or it is damaged";
        }
    }

    /// <summary>
    /// A clear word for the files NVorbis cannot take: no Ogg file at all, or Ogg with Opus inside
    /// (also sold as .ogg). Empty = an Ogg file, handed to NVorbis.
    /// </summary>
    private static string Sniff(string file)
    {
        var head = new byte[512];
        int length;
        using (var stream = File.OpenRead(file))
            length = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);

        var bytes = head.AsSpan(0, length);
        if (!bytes.StartsWith("OggS"u8))
            return "it is not an OGG file";
        if (bytes.IndexOf("OpusHead"u8) >= 0)
            return "it is Ogg Opus - only Ogg Vorbis is supported (save it as MP3, WAV or Ogg Vorbis)";
        return string.Empty;
    }

    private static void WriteHeader(BinaryWriter writer, int channels, int rate, long dataBytes)
    {
        int blockAlign = channels * sizeof(short);
        writer.Write("RIFF"u8);
        writer.Write((int)(36 + dataBytes));
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);                       // PCM header size
        writer.Write((short)1);                 // PCM
        writer.Write((short)channels);
        writer.Write(rate);
        writer.Write(rate * blockAlign);        // bytes per second
        writer.Write((short)blockAlign);
        writer.Write((short)16);                // bits per sample
        writer.Write("data"u8);
        writer.Write((int)dataBytes);
    }
}

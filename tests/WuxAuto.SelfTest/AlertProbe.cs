using System.IO;
using Windows.UI.Notifications;
using WuxAuto.Core;

namespace WuxAuto.SelfTest;

/// <summary>Offline checks and a manual probe for <see cref="KillAlert"/> (the boss-kill chime and notification).</summary>
internal static class AlertProbe
{
    /// <summary>The built-in chime is a well-formed 16-bit mono WAV of the expected length, loud but not clipped.</summary>
    public static IEnumerable<(string Name, bool Ok, string Detail)> OfflineChecks()
    {
        byte[] wav = KillAlert.GenerateChime();
        bool header = wav.Length > 44
                      && wav.AsSpan(0, 4).SequenceEqual("RIFF"u8)
                      && wav.AsSpan(8, 4).SequenceEqual("WAVE"u8)
                      && wav.AsSpan(36, 4).SequenceEqual("data"u8)
                      && BitConverter.ToInt32(wav, 4) == wav.Length - 8
                      && BitConverter.ToInt32(wav, 40) == wav.Length - 44
                      && BitConverter.ToInt16(wav, 22) == 1
                      && BitConverter.ToInt16(wav, 34) == 16;
        int rate = BitConverter.ToInt32(wav, 24);
        double seconds = (wav.Length - 44) / 2.0 / Math.Max(1, rate);

        int peak = 0;
        for (int i = 44; i + 1 < wav.Length; i += 2)
            peak = Math.Max(peak, Math.Abs((int)BitConverter.ToInt16(wav, i)));
        short last = BitConverter.ToInt16(wav, wav.Length - 2);
        double peakShare = peak / (double)short.MaxValue;

        yield return ("kill chime wav", header && rate == 44100 && Math.Abs(seconds - 1.6) < 0.01
                                        && peakShare is > 0.75 and < 0.85 && last == 0,
            $"{wav.Length} bytes, {rate} Hz, {seconds:0.00} s, peak {peakShare:0.00}, last sample {last}");
    }

    /// <summary>
    /// --alert: plays the chime (the built-in one, or chime.wav from the templates folder), shows a
    /// test notification and reads back whether Windows keeps it for WuxAuto. The generated chime is
    /// also written to %TEMP%\wuxauto-chime.wav.
    /// </summary>
    public static async Task<int> RunAsync()
    {
        string wavFile = Path.Combine(Path.GetTempPath(), "wuxauto-chime.wav");
        await File.WriteAllBytesAsync(wavFile, KillAlert.GenerateChime());
        Console.WriteLine($"built-in chime written to {wavFile}");
        Console.WriteLine(File.Exists(KillAlert.CustomChimeFile)
            ? $"custom chime: {KillAlert.CustomChimeFile}"
            : $"no custom chime ({KillAlert.CustomChimeFile}) - the built-in one plays");

        using var alert = new KillAlert();
        string chime = alert.PlayChime();
        Console.WriteLine(chime.Length == 0 ? "chime: playing" : $"chime: FAILED - {chime}");

        int before = DeliveredCount();
        string notify = alert.Notify("WuxAuto: boss down", $"Test - check the loot ({DateTime.Now:HH:mm})");
        Console.WriteLine(notify.Length == 0 ? "notification: handed to Windows" : $"notification: FAILED - {notify}");

        // The chime plays on a background thread: keep the process alive until it has finished.
        await Task.Delay(2200);
        int after = DeliveredCount();
        Console.WriteLine($"notification centre for {KillAlert.AppId}: {before} -> {after}");
        return chime.Length == 0 && notify.Length == 0 && after > before ? 0 : 1;
    }

    private static int DeliveredCount()
    {
        try
        {
            return ToastNotificationManager.History.GetHistory(KillAlert.AppId).Count;
        }
        catch
        {
            return -1;
        }
    }
}

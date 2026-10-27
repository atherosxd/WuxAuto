using System.IO;
using System.Windows.Media;
using System.Windows.Threading;
using Windows.UI.Notifications;
using WuxAuto.Core;

namespace WuxAuto.SelfTest;

/// <summary>Offline checks and a manual probe for <see cref="KillAlert"/> (the boss-kill chime and notification).</summary>
internal static class AlertProbe
{
    /// <summary>
    /// The built-in chime is a well-formed 16-bit mono WAV of the expected length, loud but not
    /// clipped; a sound file is copied into the sounds folder (same contents reused, other contents
    /// get a free name, a file that does not play is refused) - in a scratch data folder.
    /// </summary>
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

        foreach (var check in OnDispatcher(ImportChecksAsync))
            yield return check;
    }

    /// <summary>
    /// --alert: plays the chime the app would play (the sound picked in the settings, chime.wav from
    /// the templates folder or the built-in one), shows a test notification and reads back whether
    /// Windows keeps it for WuxAuto. The generated chime is also written to %TEMP%\wuxauto-chime.wav.
    /// --alert &lt;file&gt;: only the sound - opens the file, copies it into a scratch sounds folder,
    /// checks that it plays (position moves) and plays it out loud.
    /// </summary>
    public static int Run(string? soundFile) =>
        OnDispatcher(() => soundFile == null ? RunAlertAsync() : RunSoundAsync(soundFile));

    private static async Task<int> RunAlertAsync()
    {
        string wavFile = Path.Combine(Path.GetTempPath(), "wuxauto-chime.wav");
        await File.WriteAllBytesAsync(wavFile, KillAlert.GenerateChime());
        Console.WriteLine($"built-in chime written to {wavFile}");

        string soundFile = SettingsStore.Load().Sequence.KillSound;
        Console.WriteLine(soundFile.Length > 0 ? $"sound picked in the settings: {soundFile}"
            : File.Exists(KillAlert.CustomChimeFile) ? $"custom chime: {KillAlert.CustomChimeFile}"
            : $"no sound picked, no custom chime ({KillAlert.CustomChimeFile}) - the built-in one plays");

        using var alert = new KillAlert();
        string chime = await alert.PlayChimeAsync(soundFile);
        Console.WriteLine(chime.Length == 0 ? "chime: playing" : $"chime: FAILED - {chime}");

        int before = DeliveredCount();
        string notify = alert.Notify("WuxAuto: boss down", $"Test - check the loot ({DateTime.Now:HH:mm})");
        Console.WriteLine(notify.Length == 0 ? "notification: handed to Windows" : $"notification: FAILED - {notify}");

        // The chime plays in the background: keep the process alive until it has finished.
        await Task.Delay(2200);
        int after = DeliveredCount();
        Console.WriteLine($"notification centre for {KillAlert.AppId}: {before} -> {after}");
        return chime.Length == 0 && notify.Length == 0 && after > before ? 0 : 1;
    }

    private static async Task<int> RunSoundAsync(string soundFile)
    {
        var (length, error) = await KillAlert.ProbeAsync(soundFile);
        Console.WriteLine(error.Length == 0 ? $"opens: {length.TotalSeconds:0.00} s" : $"opens: FAILED - {error}");
        if (error.Length > 0)
            return 1;

        string moved = await PositionAfterAsync(soundFile, TimeSpan.FromMilliseconds(800));
        Console.WriteLine($"plays: {moved}");

        string root = AppPaths.Root, assetsFolder = AppPaths.AssetsFolder;
        string scratch = Path.Combine(Path.GetTempPath(), "wuxauto-sound-" + Guid.NewGuid().ToString("N"));
        AppPaths.Configure(scratch);
        try
        {
            var (copy, _, importError) = await KillAlert.ImportSoundAsync(soundFile);
            Console.WriteLine(importError.Length == 0 ? $"copied: {copy}" : $"copy: FAILED - {importError}");
            if (importError.Length > 0)
                return 1;

            using var alert = new KillAlert();
            string played = await alert.PlayChimeAsync(copy);
            Console.WriteLine(played.Length == 0 ? "chime: playing the copy" : $"chime: FAILED - {played}");
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(length.TotalSeconds, 10) + 0.3));
            return played.Length == 0 && moved.StartsWith("yes", StringComparison.Ordinal) ? 0 : 1;
        }
        finally
        {
            AppPaths.Configure(root, assetsFolder);
            try { Directory.Delete(scratch, true); } catch { /* a temp folder */ }
        }
    }

    /// <summary>Plays the file muted for <paramref name="wait"/> and reports whether its position moved.</summary>
    private static async Task<string> PositionAfterAsync(string file, TimeSpan wait)
    {
        var player = new MediaPlayer { Volume = 0 };
        try
        {
            player.Open(new Uri(Path.GetFullPath(file)));
            player.Play();
            await Task.Delay(wait);
            var position = player.Position;
            return position > TimeSpan.Zero ? $"yes, at {position.TotalSeconds:0.00} s after {wait.TotalSeconds:0.0} s" : "NO - the position did not move";
        }
        finally
        {
            player.Close();
        }
    }

    private static async Task<List<(string Name, bool Ok, string Detail)>> ImportChecksAsync()
    {
        var checks = new List<(string, bool, string)>();
        string root = AppPaths.Root, assetsFolder = AppPaths.AssetsFolder;
        string scratch = Path.Combine(Path.GetTempPath(), "wuxauto-sound-" + Guid.NewGuid().ToString("N"));
        AppPaths.Configure(scratch);
        try
        {
            string picks = Path.Combine(scratch, "picked");
            Directory.CreateDirectory(picks);
            string chime = Path.Combine(picks, "my #1 chime.wav");
            await File.WriteAllBytesAsync(chime, KillAlert.GenerateChime());

            var (length, probeError) = await KillAlert.ProbeAsync(chime);
            checks.Add(("kill sound opens", probeError.Length == 0 && Math.Abs(length.TotalSeconds - 1.6) < 0.05,
                probeError.Length == 0 ? $"{length.TotalSeconds:0.00} s" : probeError));

            var first = await KillAlert.ImportSoundAsync(chime);
            var again = await KillAlert.ImportSoundAsync(chime);
            string expected = Path.Combine(AppPaths.SoundsFolder, "my #1 chime.wav");
            checks.Add(("kill sound copy", first.Error.Length == 0 && first.File == expected && again.File == expected
                                           && Directory.GetFiles(AppPaths.SoundsFolder).Length == 1,
                $"{first.File} / again {again.File} {first.Error}{again.Error}"));

            // Same name, other contents: a second copy beside the first.
            byte[] louder = KillAlert.GenerateChime();
            louder[^2] = 1;
            await File.WriteAllBytesAsync(chime, louder);
            var other = await KillAlert.ImportSoundAsync(chime);
            string expectedOther = Path.Combine(AppPaths.SoundsFolder, "my #1 chime-2.wav");
            checks.Add(("kill sound copy, same name", other.Error.Length == 0 && other.File == expectedOther,
                $"{other.File} {other.Error}"));

            // A file already in the sounds folder is used where it is.
            var inPlace = await KillAlert.ImportSoundAsync(expected);
            checks.Add(("kill sound in the sounds folder", inPlace.Error.Length == 0 && inPlace.File == expected
                                                           && Directory.GetFiles(AppPaths.SoundsFolder).Length == 2,
                $"{inPlace.File} {inPlace.Error}"));

            string junk = Path.Combine(picks, "junk.mp3");
            await File.WriteAllTextAsync(junk, "not a sound");
            var bad = await KillAlert.ImportSoundAsync(junk);
            checks.Add(("kill sound refuses junk", bad.Error.Length > 0 && bad.File.Length == 0
                                                   && !File.Exists(Path.Combine(AppPaths.SoundsFolder, "junk.mp3")),
                bad.Error));
        }
        finally
        {
            AppPaths.Configure(root, assetsFolder);
            try { Directory.Delete(scratch, true); } catch { /* a temp folder */ }
        }

        return checks;
    }

    /// <summary>
    /// Runs <paramref name="work"/> on a thread of its own with a WPF dispatcher: <see cref="MediaPlayer"/>
    /// raises its events through the dispatcher of the thread that made it, so that thread must pump.
    /// </summary>
    private static T OnDispatcher<T>(Func<Task<T>> work)
    {
        T result = default!;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    result = await work();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    dispatcher.InvokeShutdown();
                }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null)
            throw new InvalidOperationException("alert probe failed", failure);
        return result;
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

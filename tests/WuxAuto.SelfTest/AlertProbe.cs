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
    /// get a free name, a file that does not play is refused, an OGG is decoded to WAV) - in a
    /// scratch data folder.
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
    /// --alert &lt;file&gt;: only the sound - copies it into a scratch sounds folder (an OGG decoded
    /// to WAV), checks that the copy opens and plays (position moves) and plays it out loud.
    /// </summary>
    public static int Run(string? soundFile) =>
        OnDispatcher(() => soundFile == null ? RunAlertAsync() : RunSoundAsync(soundFile));

    private static async Task<int> RunAlertAsync()
    {
        string wavFile = Path.Combine(Path.GetTempPath(), "wuxauto-chime.wav");
        await File.WriteAllBytesAsync(wavFile, KillAlert.GenerateChime());
        Console.WriteLine($"built-in chime written to {wavFile}");

        string soundFile = SettingsStore.Load(persistMigration: false).Sequence.KillSound;
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
        string root = AppPaths.Root, assetsFolder = AppPaths.AssetsFolder;
        string scratch = Path.Combine(Path.GetTempPath(), "wuxauto-sound-" + Guid.NewGuid().ToString("N"));
        AppPaths.Configure(scratch);
        try
        {
            // Import first: an OGG only plays as the WAV it is turned into.
            var (copy, length, importError) = await KillAlert.ImportSoundAsync(soundFile);
            Console.WriteLine(importError.Length == 0
                ? $"copied: {copy} ({length.TotalSeconds:0.00} s{(OggToWav.IsOgg(soundFile) ? ", converted from OGG" : string.Empty)})"
                : $"copy: FAILED - {importError}");
            if (importError.Length > 0)
                return 1;

            string moved = await PositionAfterAsync(copy, TimeSpan.FromSeconds(3));
            Console.WriteLine($"plays: {moved}");

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

    /// <summary>
    /// Plays the file muted and reports whether its position moved within <paramref name="wait"/>
    /// (checked every 100 ms: the first file after a start can take a moment to open).
    /// </summary>
    private static async Task<string> PositionAfterAsync(string file, TimeSpan wait)
    {
        var player = new MediaPlayer { Volume = 0 };
        try
        {
            player.Open(new Uri(Path.GetFullPath(file)));
            player.Play();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (player.Position <= TimeSpan.Zero && clock.Elapsed < wait)
                await Task.Delay(100);
            var position = player.Position;
            return position > TimeSpan.Zero
                ? $"yes, at {position.TotalSeconds:0.00} s after {clock.Elapsed.TotalSeconds:0.0} s"
                : $"NO - the position did not move in {wait.TotalSeconds:0} s";
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

            checks.AddRange(await OggChecksAsync(picks));
        }
        finally
        {
            AppPaths.Configure(root, assetsFolder);
            try { Directory.Delete(scratch, true); } catch { /* a temp folder */ }
        }

        return checks;
    }

    /// <summary>
    /// OGG (Windows cannot play it): the shipped 1 s 48 kHz stereo tone becomes "boss tone.wav" in the
    /// sounds folder with the same rate and channels, both channels sounding; a second pick reuses
    /// it. Opus inside an .ogg, a file that is not Ogg and a broken one are refused, and nothing is
    /// left in the sounds folder or the temp folder.
    /// </summary>
    private static async Task<List<(string Name, bool Ok, string Detail)>> OggChecksAsync(string picks)
    {
        var checks = new List<(string, bool, string)>();
        string source = Path.Combine(AppContext.BaseDirectory, "Sounds", "tone.ogg");
        if (!File.Exists(source))
        {
            checks.Add(("kill sound ogg", false, $"{source} is missing"));
            return checks;
        }

        int tempBefore = TempOggFiles();
        string ogg = Path.Combine(picks, "boss tone.ogg");
        File.Copy(source, ogg, overwrite: true);
        var first = await KillAlert.ImportSoundAsync(ogg);
        var again = await KillAlert.ImportSoundAsync(ogg);
        string expected = Path.Combine(AppPaths.SoundsFolder, "boss tone.wav");
        bool ok = first.Error.Length == 0 && first.File == expected && again.File == expected
                  && Math.Abs(first.Length.TotalSeconds - 1.0) < 0.05;
        string detail = $"{first.File} / again {again.File}, {first.Length.TotalSeconds:0.00} s {first.Error}{again.Error}";
        if (ok)
        {
            byte[] wav = await File.ReadAllBytesAsync(expected);
            short channels = BitConverter.ToInt16(wav, 22);
            int rate = BitConverter.ToInt32(wav, 24);
            short bits = BitConverter.ToInt16(wav, 34);
            // Per channel: the peak, and the pitch from the sign changes (a sine crosses zero twice a
            // cycle) - 440 Hz left and 880 Hz right prove the samples are interleaved in order.
            int width = Math.Max((int)channels, 1);
            var peak = new double[width];
            var crossings = new int[width];
            var last = new short[width];
            for (int i = 44, n = 0; i + 1 < wav.Length; i += 2, n++)
            {
                short sample = BitConverter.ToInt16(wav, i);
                int ch = n % width;
                peak[ch] = Math.Max(peak[ch], Math.Abs((int)sample) / (double)short.MaxValue);
                if ((sample < 0) != (last[ch] < 0) && n >= width)
                    crossings[ch]++;
                last[ch] = sample;
            }

            double seconds = (wav.Length - 44) / (double)(width * sizeof(short)) / Math.Max(rate, 1);
            var hz = crossings.Select(c => c / 2.0 / Math.Max(seconds, 0.001)).ToArray();
            ok = channels == 2 && rate == 48000 && bits == 16 && peak.All(p => p > 0.05)
                 && Math.Abs(hz[0] - 440) < 22 && Math.Abs(hz[1] - 880) < 44
                 && BitConverter.ToInt32(wav, 40) == wav.Length - 44 && BitConverter.ToInt32(wav, 4) == wav.Length - 8;
            detail = $"{Path.GetFileName(expected)}, {first.Length.TotalSeconds:0.00} s, {channels} ch, {rate} Hz, {bits} bit, "
                     + $"peak {string.Join(" / ", peak.Select(p => p.ToString("0.00")))}, "
                     + $"pitch {string.Join(" / ", hz.Select(h => h.ToString("0")))} Hz, again {Path.GetFileName(again.File)}";
        }

        checks.Add(("kill sound ogg", ok, detail));

        byte[] opusHead = [.. "OggS"u8.ToArray(), .. new byte[24], .. "OpusHead"u8.ToArray(), .. new byte[11]];
        byte[] broken = [.. "OggS"u8.ToArray(), .. new byte[400]];
        foreach (var (name, bytes, expect) in new[]
                 {
                     ("opus.ogg", opusHead, "Opus"),
                     ("text.ogg", "not a sound"u8.ToArray(), "not an OGG file"),
                     ("broken.ogg", broken, "Ogg Vorbis"),
                 })
        {
            string file = Path.Combine(picks, name);
            await File.WriteAllBytesAsync(file, bytes);
            int kept = Directory.GetFiles(AppPaths.SoundsFolder).Length;
            var refused = await KillAlert.ImportSoundAsync(file);
            checks.Add(($"kill sound refuses {name}",
                refused.File.Length == 0 && refused.Error.Contains(expect, StringComparison.Ordinal)
                && Directory.GetFiles(AppPaths.SoundsFolder).Length == kept,
                refused.Error));
        }

        int tempAfter = TempOggFiles();
        checks.Add(("kill sound ogg temp files", tempAfter <= tempBefore, $"{tempBefore} -> {tempAfter} wuxauto-ogg-*.wav in {Path.GetTempPath()}"));
        return checks;
    }

    private static int TempOggFiles() => Directory.GetFiles(Path.GetTempPath(), "wuxauto-ogg-*.wav").Length;

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

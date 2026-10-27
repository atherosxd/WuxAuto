using System.IO;
using System.Media;
using System.Resources;
using System.Security;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace WuxAuto.Core;

/// <summary>
/// Boss-kill alert (user, 2026-10-26: friends run the pilot on a second PC next to their main one,
/// and LOD does not always pick up every drop - a reminder to go and check). A chime on this PC's
/// speakers and a Windows notification. The chime is the sound file picked in the UI
/// (<c>Sequence.KillSound</c>, MP3 or WAV, user 2026-10-27; a picked OGG is kept as a WAV), else <see cref="CustomChimeName"/> from
/// the templates folder when that file exists, else <see cref="GenerateChime"/> (written to
/// <see cref="BuiltInChimeFile"/>: a sound played in the background must come from a file - from
/// memory Windows keeps reading a buffer that .NET only holds still during the call). Sound files
/// play through WPF's <see cref="MediaPlayer"/> (MP3 and every WAV kind); the built-in chime keeps
/// <see cref="SoundPlayer"/>, which needs nothing from Windows Media, so the fallback always plays.
/// Used on the UI thread only (<see cref="MediaPlayer"/> belongs to the thread that made it).
/// </summary>
public sealed class KillAlert : IDisposable
{
    /// <summary>The app's identity in the Windows notification settings (registered under HKCU on first use).</summary>
    public const string AppId = "atherosxd.WuxAuto";

    /// <summary>A WAV file of this name in the templates folder replaces the built-in chime when no sound is picked.</summary>
    public const string CustomChimeName = "chime.wav";

    /// <summary>How long a sound file may take to open before it counts as broken.</summary>
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(5);

    /// <summary>One sound file at a time: a second kill while the first file still opens waits for it.</summary>
    private readonly SemaphoreSlim _mediaGate = new(1, 1);

    private SoundPlayer? _player;
    private string? _playerSource;
    private MediaPlayer? _media;
    private string? _mediaSource;
    private bool _builtInWritten;
    private ToastNotifier? _notifier;

    public static string CustomChimeFile => Path.Combine(AppPaths.TemplateFolder, CustomChimeName);

    public static string BuiltInChimeFile => Path.Combine(AppPaths.Root, "chime-builtin.wav");

    /// <summary>
    /// Starts the chime (it plays in the background): <paramref name="soundFile"/> when one is set,
    /// else <see cref="CustomChimeName"/> from the templates folder, else the built-in chime. A
    /// sound file that does not play falls back to the built-in chime.
    /// </summary>
    /// <returns>Empty when it played; otherwise what went wrong.</returns>
    public async Task<string> PlayChimeAsync(string? soundFile)
    {
        string? custom = !string.IsNullOrWhiteSpace(soundFile) ? soundFile.Trim()
            : File.Exists(CustomChimeFile) ? CustomChimeFile
            : null;
        if (custom == null)
            return PlayBuiltIn();

        string error = File.Exists(custom) ? await PlayFileAsync(custom) : "the file is not there any more";
        if (error.Length == 0)
            return string.Empty;

        string name = Path.GetFileName(custom);
        return PlayBuiltIn().Length == 0
            ? $"{name} could not be played ({error}) - played the built-in chime instead."
            : $"{name} could not be played ({error}).";
    }

    /// <summary>
    /// Copies a picked sound file into <see cref="AppPaths.SoundsFolder"/> (so it keeps playing after
    /// the original is moved or deleted) and checks that it opens and has sound. An OGG file is
    /// decoded into a WAV of the same name (<see cref="OggToWav"/>; Windows cannot play OGG). A file
    /// of the same name with other contents is kept: the copy gets a free name ("name-2.mp3"). A file
    /// that does not play is not kept.
    /// </summary>
    /// <returns>The copy and its length, or what went wrong (then <c>File</c> is empty).</returns>
    public static async Task<(string File, TimeSpan Length, string Error)> ImportSoundAsync(string source)
    {
        string? converted = null;
        try
        {
            if (!File.Exists(source))
                return (string.Empty, TimeSpan.Zero, "the file is not there");

            Directory.CreateDirectory(AppPaths.SoundsFolder);
            string target = Path.GetFullPath(source);
            bool copied = false;
            if (OggToWav.IsOgg(source))
            {
                converted = Path.Combine(Path.GetTempPath(), $"wuxauto-ogg-{Guid.NewGuid():N}.wav");
                string decodeError = await Task.Run(() => OggToWav.Convert(source, converted));
                if (decodeError.Length > 0)
                    return (string.Empty, TimeSpan.Zero, decodeError);

                target = FreeTarget(Path.ChangeExtension(Path.GetFileName(source), ".wav"), converted);
                if (!File.Exists(target))
                {
                    File.Move(converted, target);
                    copied = true;
                }
            }
            else if (!IsInSoundsFolder(target))
            {
                target = FreeTarget(Path.GetFileName(source), source);
                if (!File.Exists(target))
                {
                    File.Copy(source, target);
                    copied = true;
                }
            }

            var (length, error) = await ProbeAsync(target);
            if (error.Length == 0)
                return (target, length, string.Empty);

            if (copied)
                TryDelete(target);
            return (string.Empty, TimeSpan.Zero, error);
        }
        catch (Exception ex)
        {
            return (string.Empty, TimeSpan.Zero, ex.Message);
        }
        finally
        {
            if (converted != null && File.Exists(converted))
                TryDelete(converted);
        }
    }

    /// <summary>Opens a sound file without playing it: its length, or why it does not play.</summary>
    public static async Task<(TimeSpan Length, string Error)> ProbeAsync(string file)
    {
        var player = new MediaPlayer();
        try
        {
            string error = await OpenAsync(player, file);
            var length = player.NaturalDuration.HasTimeSpan ? player.NaturalDuration.TimeSpan : TimeSpan.Zero;
            return (length, error);
        }
        catch (Exception ex)
        {
            return (TimeSpan.Zero, ex.Message);
        }
        finally
        {
            player.Close();
        }
    }

    /// <summary>A Windows notification (silent: the chime is the sound).</summary>
    /// <returns>Empty when it was handed to Windows; otherwise what went wrong.</returns>
    public string Notify(string title, string body)
    {
        try
        {
            _notifier ??= CreateNotifier();
            var setting = ReadSetting(_notifier);
            if (setting is { } off && off != NotificationSetting.Enabled)
                return $"Windows notifications are off for WuxAuto ({off}) - Windows Settings > System > Notifications.";

            var xml = new XmlDocument();
            xml.LoadXml(
                "<toast><visual><binding template=\"ToastGeneric\">" +
                $"<text>{SecurityElement.Escape(title)}</text><text>{SecurityElement.Escape(body)}</text>" +
                "</binding></visual><audio silent=\"true\"/></toast>");
            // An LOD session kills the boss every minute or two: old ones leave the notification centre.
            _notifier.Show(new ToastNotification(xml) { ExpirationTime = DateTimeOffset.Now.AddHours(2) });
            return string.Empty;
        }
        catch (Exception ex)
        {
            _notifier = null;
            return ex.Message;
        }
    }

    /// <summary>
    /// A rising three-note chime (A5, C#6, E6) with a bell-like decay, 1.6 s, as a 16-bit mono WAV.
    /// Built in code so there is no sound file to ship.
    /// </summary>
    public static byte[] GenerateChime()
    {
        const int rate = 44100;
        const double length = 1.6;
        const double noteGap = 0.14;
        const double decay = 0.45;
        const double fadeOut = 0.1;
        double[] notes = [880.0, 1108.73, 1318.51];

        int samples = (int)(rate * length);
        var mix = new double[samples];
        for (int n = 0; n < notes.Length; n++)
        {
            double f = notes[n];
            int start = (int)(rate * noteGap * n);
            for (int i = start; i < samples; i++)
            {
                double t = (i - start) / (double)rate;
                double envelope = Math.Min(1.0, t / 0.004) * Math.Exp(-t / decay);
                mix[i] += envelope * (Math.Sin(2 * Math.PI * f * t)
                    + 0.35 * Math.Exp(-t / 0.15) * Math.Sin(4 * Math.PI * f * t)
                    + 0.12 * Math.Exp(-t / 0.08) * Math.Sin(6 * Math.PI * f * t));
            }
        }

        double peak = mix.Max(Math.Abs);
        double gain = peak > 0 ? 0.8 * short.MaxValue / peak : 0;
        int fadeFrom = samples - (int)(rate * fadeOut);

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        int dataBytes = samples * sizeof(short);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);                       // PCM header size
        writer.Write((short)1);                 // PCM
        writer.Write((short)1);                 // mono
        writer.Write(rate);
        writer.Write(rate * sizeof(short));     // bytes per second
        writer.Write((short)sizeof(short));     // block align
        writer.Write((short)16);                // bits per sample
        writer.Write("data"u8);
        writer.Write(dataBytes);
        for (int i = 0; i < samples; i++)
        {
            // The tail is faded to zero so the end does not click.
            double fade = i < fadeFrom ? 1.0 : (samples - i) / (double)(samples - fadeFrom);
            writer.Write((short)Math.Round(mix[i] * gain * fade));
        }

        writer.Flush();
        return stream.ToArray();
    }

    public void Dispose()
    {
        CloseMedia();
        _player?.Dispose();
        _player = null;
    }

    /// <summary>A sound file through <see cref="MediaPlayer"/>, kept open between kills.</summary>
    private async Task<string> PlayFileAsync(string file)
    {
        await _mediaGate.WaitAsync();
        try
        {
            string source = SourceKey(file);
            if (_media == null || source != _mediaSource)
            {
                CloseMedia();
                var player = new MediaPlayer();
                string error = await OpenAsync(player, file);
                if (error.Length > 0)
                {
                    player.Close();
                    return error;
                }

                player.MediaFailed += OnMediaFailed;
                _media = player;
                _mediaSource = source;
            }

            _media.Stop();   // back to the start, also when the last kill's sound still plays
            _media.Play();
            return string.Empty;
        }
        catch (Exception ex)
        {
            CloseMedia();
            return ex.Message;
        }
        finally
        {
            _mediaGate.Release();
        }
    }

    /// <summary>Waits for <paramref name="player"/> to open <paramref name="file"/>: empty when it did and has sound.</summary>
    private static async Task<string> OpenAsync(MediaPlayer player, string file)
    {
        var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Opened(object? sender, EventArgs e) =>
            done.TrySetResult(player.HasAudio ? string.Empty : "it has no sound");
        // The exception is often just the media error code ("0xC00D11B1").
        void Failed(object? sender, ExceptionEventArgs e) =>
            done.TrySetResult($"Windows cannot play it: {e.ErrorException?.Message ?? "unknown error"}");

        player.MediaOpened += Opened;
        player.MediaFailed += Failed;
        try
        {
            player.Volume = 1.0;   // MediaPlayer starts at half volume; SoundPlayer plays at full
            player.Open(new Uri(Path.GetFullPath(file), UriKind.Absolute));
            var finished = await Task.WhenAny(done.Task, Task.Delay(OpenTimeout));
            return finished == done.Task
                ? await done.Task
                : $"it did not open within {OpenTimeout.TotalSeconds:0} s";
        }
        finally
        {
            player.MediaOpened -= Opened;
            player.MediaFailed -= Failed;
        }
    }

    /// <summary>A file that fails while playing is opened again on the next kill.</summary>
    private void OnMediaFailed(object? sender, ExceptionEventArgs e) => _mediaSource = null;

    private void CloseMedia()
    {
        if (_media != null)
        {
            _media.MediaFailed -= OnMediaFailed;
            _media.Close();
            _media = null;
        }

        _mediaSource = null;
    }

    /// <summary>The file's time is part of the key, so a replaced file is picked up without a restart.</summary>
    private static string SourceKey(string file) => $"{Path.GetFullPath(file)}|{File.GetLastWriteTimeUtc(file).Ticks}";

    private static bool IsInSoundsFolder(string fullPath) =>
        string.Equals(Path.GetDirectoryName(fullPath), Path.GetFullPath(AppPaths.SoundsFolder).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Where a copy of <paramref name="contents"/> named <paramref name="fileName"/> goes: that name in
    /// the sounds folder, or "name-2.ext", "name-3.ext", ... when another file of that name is there.
    /// A file with the same contents is reused.
    /// </summary>
    private static string FreeTarget(string fileName, string contents)
    {
        string name = Path.GetFileNameWithoutExtension(fileName);
        if (name.Length == 0)
            name = "sound";
        string extension = Path.GetExtension(fileName);
        for (int n = 1; n < 1000; n++)
        {
            string candidate = Path.Combine(AppPaths.SoundsFolder, n == 1 ? name + extension : $"{name}-{n}{extension}");
            if (!File.Exists(candidate) || SameContents(contents, candidate))
                return candidate;
        }

        return Path.Combine(AppPaths.SoundsFolder, $"{name}-{Guid.NewGuid():N}{extension}");
    }

    private static bool SameContents(string a, string b) =>
        new FileInfo(a).Length == new FileInfo(b).Length
        && File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch
        {
            // a leftover copy in the sounds folder does no harm
        }
    }

    /// <summary>The generated chime, written once per app start (so a new version's sound replaces the old file).</summary>
    private string PlayBuiltIn()
    {
        string file = BuiltInChimeFile;
        try
        {
            if (!_builtInWritten)
            {
                Directory.CreateDirectory(AppPaths.Root);
                File.WriteAllBytes(file, GenerateChime());
                _builtInWritten = true;
            }
        }
        catch (Exception ex) when (!File.Exists(file))
        {
            return ex.Message;
        }
        catch
        {
            // in use (another WuxAuto playing it): the copy already there is the same sound
        }

        return PlayWav(file);
    }

    /// <summary>A PCM WAV through <see cref="SoundPlayer"/> (the built-in chime).</summary>
    private string PlayWav(string file)
    {
        try
        {
            string source = SourceKey(file);
            if (_player == null || source != _playerSource)
            {
                _player?.Dispose();
                _player = null;
                var player = new SoundPlayer(file);
                player.Load();   // throws here on a file that is not a WAV
                _player = player;
                _playerSource = source;
            }

            _player.Play();
            return string.Empty;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Null when Windows cannot say (it throws "Element not found" for some unpackaged apps).</summary>
    private static NotificationSetting? ReadSetting(ToastNotifier notifier)
    {
        try
        {
            return notifier.Setting;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// An unpackaged app needs its id registered (HKCU\Software\Classes\AppUserModelId) before
    /// Windows shows its notifications; the name and icon there are what the notification shows.
    /// </summary>
    private static ToastNotifier CreateNotifier()
    {
        using (var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{AppId}"))
        {
            key.SetValue("DisplayName", "WuxAuto");
            string icon = WriteIcon();
            if (icon.Length > 0)
                key.SetValue("IconUri", icon);
        }

        return ToastNotificationManager.CreateToastNotifier(AppId);
    }

    /// <summary>The window icon (largest size) as a PNG next to the settings, for the notification.</summary>
    private static string WriteIcon()
    {
        try
        {
            var assembly = typeof(KillAlert).Assembly;
            var resources = new ResourceManager($"{assembly.GetName().Name}.g", assembly);
            using var stream = resources.GetStream("wuxauto.ico");
            if (stream == null)
                return string.Empty;

            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var largest = decoder.Frames.MaxBy(frame => frame.PixelWidth);
            if (largest == null)
                return string.Empty;

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(largest));
            Directory.CreateDirectory(AppPaths.Root);
            string file = Path.Combine(AppPaths.Root, "notify-icon.png");
            using var output = File.Create(file);
            encoder.Save(output);
            return file;
        }
        catch
        {
            // the notification just shows without the icon
            return string.Empty;
        }
    }
}

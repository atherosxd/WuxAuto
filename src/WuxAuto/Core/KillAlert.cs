using System.IO;
using System.Media;
using System.Resources;
using System.Security;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace WuxAuto.Core;

/// <summary>
/// Boss-kill alert (user, 2026-10-26: friends run the pilot on a second PC next to their main one,
/// and LOD does not always pick up every drop - a reminder to go and check). A chime on this PC's
/// speakers and a Windows notification. The chime is <see cref="CustomChimeName"/> from the
/// templates folder when that file exists, else <see cref="GenerateChime"/> (written to
/// <see cref="BuiltInChimeFile"/>: a sound played in the background must come from a file - from
/// memory Windows keeps reading a buffer that .NET only holds still during the call).
/// Used on the UI thread only.
/// </summary>
public sealed class KillAlert : IDisposable
{
    /// <summary>The app's identity in the Windows notification settings (registered under HKCU on first use).</summary>
    public const string AppId = "atherosxd.WuxAuto";

    /// <summary>A WAV file of this name in the templates folder replaces the built-in chime.</summary>
    public const string CustomChimeName = "chime.wav";

    private SoundPlayer? _player;
    private string? _playerSource;
    private bool _builtInWritten;
    private ToastNotifier? _notifier;

    public static string CustomChimeFile => Path.Combine(AppPaths.TemplateFolder, CustomChimeName);

    public static string BuiltInChimeFile => Path.Combine(AppPaths.Root, "chime-builtin.wav");

    /// <summary>Starts the chime (it plays in the background).</summary>
    /// <returns>Empty when it played; otherwise what went wrong.</returns>
    public string PlayChime()
    {
        string custom = CustomChimeFile;
        if (!File.Exists(custom))
            return PlayBuiltIn();

        string error = Play(custom);
        if (error.Length == 0)
            return string.Empty;

        return PlayBuiltIn().Length == 0
            ? $"{CustomChimeName} could not be played ({error}) - played the built-in chime instead."
            : error;
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
        _player?.Dispose();
        _player = null;
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

        return Play(file);
    }

    private string Play(string file)
    {
        try
        {
            // The file's time is part of the key, so a replaced chime.wav is picked up without a restart.
            string source = $"{file}|{File.GetLastWriteTimeUtc(file).Ticks}";
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

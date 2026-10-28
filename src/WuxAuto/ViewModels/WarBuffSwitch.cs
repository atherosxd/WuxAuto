using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using WuxAuto.Core;

namespace WuxAuto.ViewModels;

/// <summary>
/// One check box of the Auto War "Buffs" row (user, 2026-10-28: "a check box that enables or disables those buff"): the
/// buff's icon and a short label; ticking it off puts the template into <see cref="WarSettings.DisabledBuffs"/>, saved with
/// the other settings.
/// </summary>
public sealed class WarBuffSwitch : ObservableObject
{
    private readonly AppSettings _settings;

    private WarBuffSwitch(AppSettings settings, string template, bool item)
    {
        _settings = settings;
        Template = template;
        Label = LabelFor(template);
        ToolTip = item ? "Clicked when buffing (uses an item; also clicked with autobuff)" : "Clicked when buffing";
        Icon = LoadIcon(template);
    }

    /// <summary>The template name ("warweapon1").</summary>
    public string Template { get; }

    /// <summary>"Buff 1" ... "Buff 6", "Weapon 1", "Weapon 2", "Item 1", "Item 2"; another template shows its own name.</summary>
    public string Label { get; }

    public string ToolTip { get; }

    /// <summary>The template picture, null when it cannot be read (the box shows the label only).</summary>
    public ImageSource? Icon { get; }

    public bool IsOn
    {
        get => _settings.War.IsBuffOn(Template);
        set
        {
            if (value == IsOn)
                return;

            _settings.War.SetBuffOn(Template, value);
            OnPropertyChanged();
        }
    }

    /// <summary>One switch per buff the pilot clicks: the skill buffs, then the item buffs, each once.</summary>
    public static IReadOnlyList<WarBuffSwitch> For(AppSettings settings)
    {
        var war = settings.War;
        var items = new HashSet<string>(war.ItemBuffIcons, StringComparer.OrdinalIgnoreCase);
        return war.BuffIcons.Concat(war.ItemBuffIcons)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(t => new WarBuffSwitch(settings, t, items.Contains(t)))
            .ToList();
    }

    private static string LabelFor(string template)
    {
        foreach (var (prefix, label) in new[] { ("warbuff", "Buff"), ("warweapon", "Weapon"), ("waritem", "Item") })
        {
            if (template.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(template.AsSpan(prefix.Length), out int n))
                return $"{label} {n}";
        }

        return template;
    }

    /// <summary>
    /// The template's PNG as the pilot finds it (TemplateLibrary): the templates folder first, then Assets, an unreadable
    /// file skipped. Read into memory at once (<see cref="BitmapCacheOption.OnLoad"/>), so the file is not kept open and
    /// stays free to be re-cropped or updated while the app runs.
    /// </summary>
    private static ImageSource? LoadIcon(string template)
    {
        foreach (string folder in new[] { AppPaths.TemplateFolder, AppPaths.AssetsFolder })
        {
            string path = Path.Combine(folder, template + ".png");
            if (!File.Exists(path))
                continue;

            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                image.UriSource = new Uri(path);
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FormatException or ArgumentException)
            {
                // Not a picture WPF can read: the next folder, else the label alone.
            }
        }

        return null;
    }
}

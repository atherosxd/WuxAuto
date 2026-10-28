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
    /// <summary>What a template is, where the user told us (shown after its label).</summary>
    private static readonly Dictionary<string, string> Notes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["warbuff2"] = "sword",
    };

    private readonly AppSettings _settings;

    private WarBuffSwitch(AppSettings settings, string template, string label, string toolTip)
    {
        _settings = settings;
        Template = template;
        Label = label;
        ToolTip = toolTip;
        Icon = LoadIcon(template);
    }

    /// <summary>The template name ("warweapon1").</summary>
    public string Template { get; }

    /// <summary>"Buff 1".."Buff 5", "Weapon 1 (sword)", "Weapon 2", "Weapon 3", "Item 1", "Item 2": its list and place in it.</summary>
    public string Label { get; }

    /// <summary>One line: when it is clicked, and its template file (the log names templates).</summary>
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

    /// <summary>
    /// One switch per buff the pilot clicks, in its click order: the buffs ("Buff N"), the weapon buffs ("Weapon N"), the
    /// item buffs ("Item N"), each template once (the first list naming it gives the label).
    /// </summary>
    public static IReadOnlyList<WarBuffSwitch> For(AppSettings settings)
    {
        var war = settings.War;
        var items = new HashSet<string>(war.ItemBuffIcons ?? [], StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var switches = new List<WarBuffSwitch>();
        foreach (var (group, icons) in new[] { ("Buff", war.BuffIcons), ("Weapon", war.WeaponBuffIcons), ("Item", war.ItemBuffIcons) })
        {
            int n = 0;
            foreach (string icon in icons ?? [])
            {
                if (string.IsNullOrWhiteSpace(icon) || !seen.Add(icon))
                    continue;

                n++;
                string label = Notes.TryGetValue(icon, out string? note) ? $"{group} {n} ({note})" : $"{group} {n}";
                switches.Add(new WarBuffSwitch(settings, icon, label, ToolTipFor(war, icon, items.Contains(icon))));
            }
        }

        return switches;
    }

    private static string ToolTipFor(WarSettings war, string icon, bool item)
    {
        string when = item ? "Clicked when buffing (uses an item; also clicked with autobuff)" : "Clicked when buffing";
        if (string.Equals(icon, war.XBuffIcon, StringComparison.OrdinalIgnoreCase))
            when += "; also the X buff clicked in every fight cycle - unticked, it is not clicked there either";
        return $"{when} - {icon}.png";
    }

    /// <summary>
    /// The template's PNG as the pilot finds it (TemplateLibrary): the templates folder first, then Assets, a file that
    /// cannot be read skipped. Read into memory at once (<see cref="BitmapCacheOption.OnLoad"/>), so the file is not kept
    /// open and stays free to be re-cropped or updated while the app runs. Never throws: a picture must not stop the app
    /// from starting - without one the box shows its label alone.
    /// </summary>
    private static ImageSource? LoadIcon(string template)
    {
        foreach (string folder in new[] { AppPaths.TemplateFolder, AppPaths.AssetsFolder })
        {
            try
            {
                string path = Path.Combine(folder, template + ".png");
                if (!File.Exists(path))
                    continue;

                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                image.UriSource = new Uri(path);
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch (Exception)
            {
                // Anything at all (a damaged PNG, a bad name, a locked file): the next folder, else the label alone.
            }
        }

        return null;
    }
}

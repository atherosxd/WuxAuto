using Android.Content;
using Android.Util;
using WuxAuto.Core;

namespace WuxAuto.Mobile;

/// <summary>
/// Puts the shipped templates where Core's <see cref="Vision.TemplateLibrary"/> looks for them. The APK carries them as
/// assets (assets/shared = the desktop templates, assets/phone = the phone crops); TemplateLibrary reads files from the
/// top of <see cref="AppPaths.AssetsFolder"/> only, so both sets are copied flat into one folder (the phone names start
/// with phone_, nothing collides; if a name ever did, the phone copy wins). Everything lives in the app's external
/// files dir (Android/data/io.github.atherosxd.wuxauto/files), which adb and a USB cable can reach: settings.json,
/// logs, debug frames and the user's own templates\ overrides sit next to assets\.
/// </summary>
public static class AssetInstaller
{
    /// <summary>APK asset folders, in copy order: a later folder overwrites an earlier one's file of the same name.</summary>
    static readonly string[] Sources = ["shared", "phone"];

    /// <summary>The external files dir (the data root), set by <see cref="Install"/>.</summary>
    public static string? Root { get; private set; }

    /// <summary>
    /// Copies the shipped templates into &lt;external files&gt;/assets (replacing them, and deleting PNGs no longer
    /// shipped, so an app update never leaves an old crop behind), then points <see cref="AppPaths"/> at the external
    /// files dir and that folder. Returns the assets folder.
    /// </summary>
    public static string Install(Context context)
    {
        var external = context.GetExternalFilesDir(null)
            ?? throw new InvalidOperationException("No external files dir (shared storage not mounted).");
        string root = external.AbsolutePath;
        string assetsDir = Path.Combine(root, "assets");
        Directory.CreateDirectory(assetsDir);

        var am = context.Assets ?? throw new InvalidOperationException("No asset manager.");
        var shipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string folder in Sources)
        {
            foreach (string name in am.List(folder) ?? [])
            {
                if (!name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    continue;

                using (var input = am.Open($"{folder}/{name}"))
                using (var output = File.Create(Path.Combine(assetsDir, name)))
                    input.CopyTo(output);
                shipped.Add(name);
            }
        }

        foreach (string file in Directory.EnumerateFiles(assetsDir, "*.png"))
        {
            if (!shipped.Contains(Path.GetFileName(file)))
                File.Delete(file);
        }

        AppPaths.Configure(root, assetsDir);
        AppPaths.EnsureCreated();
        Root = root;
        Log.Info(MainApplication.Tag, $"assets: {shipped.Count} template(s) in {assetsDir}");
        return assetsDir;
    }
}

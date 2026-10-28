using WuxAuto.Phone;

namespace WuxAuto.Mobile;

/// <summary>
/// "Send debug bundle": today's log, the last 20 debug pictures and settings.json
/// (<see cref="DebugBundleContents"/>) zipped into the app's cache and handed to the Android share sheet. The share
/// goes through MAUI's share API, which grants the receiving app a content:// URI from MAUI's own FileProvider
/// (declared by MAUI in the merged manifest) - nothing is copied to shared storage.
/// </summary>
public static class DebugBundle
{
    /// <summary>Builds the zip and opens the share sheet; returns a line for the log / an alert.</summary>
    public static async Task<string> ShareAsync()
    {
        var now = DateTime.Now;
        string folder = Path.Combine(FileSystem.CacheDirectory, "bundles");
        Directory.CreateDirectory(folder);
        foreach (string old in Directory.EnumerateFiles(folder, "*.zip"))
        {
            try { File.Delete(old); } catch (IOException) { }
        }

        string zip = Path.Combine(folder, $"wuxauto-debug-{now:yyyyMMdd-HHmmss}.zip");
        int count = await Task.Run(() => DebugBundleContents.Create(zip, now));
        if (count == 0)
            return "Nothing to send yet (no log, debug picture or settings file).";

        await Share.Default.RequestAsync(new ShareFileRequest
        {
            Title = "WuxAuto debug bundle",
            File = new ShareFile(zip, "application/zip"),
        });
        return $"Debug bundle: {count} file(s), {new FileInfo(zip).Length / 1024} KB.";
    }
}

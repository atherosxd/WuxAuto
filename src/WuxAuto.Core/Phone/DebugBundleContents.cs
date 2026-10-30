using System.IO.Compression;
using WuxAuto.Core;

namespace WuxAuto.Phone;

/// <summary>
/// What the phone app's "Send debug bundle" packs (the player shares it from the phone instead of reaching into
/// Android/data): today's log files, the newest <see cref="MaxPictures"/> debug pictures and settings.json, all from
/// <see cref="AppPaths"/>.
/// </summary>
public static class DebugBundleContents
{
    public const int MaxPictures = 20;

    /// <summary>The files to pack, with their names inside the zip (logs/…, debug/…, settings.json).</summary>
    public static IReadOnlyList<(string Path, string EntryName)> Pick(DateTime now)
    {
        var picked = new List<(string, string)>();

        // Today's logs: written today (a run that began before midnight is still today's), one file per app start.
        foreach (var log in Files(AppPaths.LogFolder, "*.log")
                     .Where(f => f.LastWriteTime.Date == now.Date)
                     .OrderBy(f => f.Name, StringComparer.Ordinal))
            picked.Add((log.FullName, "logs/" + log.Name));

        foreach (var picture in Files(AppPaths.DebugFolder, "*.png")
                     .OrderByDescending(f => f.LastWriteTimeUtc)
                     .ThenByDescending(f => f.Name, StringComparer.Ordinal)
                     .Take(MaxPictures))
            picked.Add((picture.FullName, "debug/" + picture.Name));

        if (File.Exists(AppPaths.SettingsFile))
            picked.Add((AppPaths.SettingsFile, "settings.json"));
        return picked;
    }

    /// <summary>Writes the zip to <paramref name="zipPath"/> (replacing it); returns the number of files packed.</summary>
    public static int Create(string zipPath, DateTime now)
    {
        var files = Pick(now);
        if (File.Exists(zipPath))
            File.Delete(zipPath);

        int packed = 0;
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var (path, entryName) in files)
        {
            try
            {
                // Shared read: today's log is still open for appending.
                using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var output = archive.CreateEntry(entryName, CompressionLevel.Optimal).Open();
                input.CopyTo(output);
                packed++;
            }
            catch (IOException)
            {
                // deleted meanwhile (the debug folder keeps a limited number): left out
            }
        }

        return packed;
    }

    private static IEnumerable<FileInfo> Files(string folder, string pattern) =>
        Directory.Exists(folder) ? new DirectoryInfo(folder).EnumerateFiles(pattern) : [];
}

using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace WuxAuto.Core;

/// <summary>A published release that carries the app zip.</summary>
public sealed record UpdateInfo(Version Version, string Tag, string Notes, string ZipUrl, long ZipSize, string PageUrl)
{
    public string VersionText => UpdateService.Format(Version);
}

/// <summary>
/// Self-update from GitHub Releases (user, 2026-10-24). The newest release of <see cref="Repository"/>
/// is read from the public API (no login), its <see cref="AssetName"/> is downloaded and unpacked in
/// %TEMP%, and a small cmd script copies it over the app folder once the app has exited, then starts
/// it again. Settings, templates, logs and snapshots live in %LOCALAPPDATA%\WuxAuto and are not touched.
/// </summary>
public static class UpdateService
{
    public const string Repository = "atherosxd/WuxAuto";
    public const string AssetName = "WuxAuto-win-x64.zip";
    public const string ExeName = "WuxAuto.exe";

    public static string ReleasesPage => $"https://github.com/{Repository}/releases";

    /// <summary>The running version (release.ps1 stamps it from the tag), as major.minor.build.</summary>
    public static Version CurrentVersion { get; } = Normalize(typeof(UpdateService).Assembly.GetName().Version);

    public static string Format(Version version) => $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";

    /// <summary>The latest release, or null when the repository has none yet.</summary>
    public static async Task<UpdateInfo?> GetLatestAsync(string repository = Repository, CancellationToken cancel = default)
    {
        using var http = CreateClient(TimeSpan.FromSeconds(20), api: true);
        using var response = await http.GetAsync($"https://api.github.com/repos/{repository}/releases/latest", cancel);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancel);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancel);
        return Parse(document.RootElement);
    }

    /// <summary>Reads a GitHub "release" object. Throws when the tag is not a version or the zip is missing.</summary>
    public static UpdateInfo Parse(JsonElement release)
    {
        string tag = GetString(release, "tag_name");
        if (!TryParseVersion(tag, out var version))
            throw new InvalidDataException($"Release tag '{tag}' is not a version number.");

        if (release.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                if (!string.Equals(GetString(asset, "name"), AssetName, StringComparison.OrdinalIgnoreCase))
                    continue;

                string url = GetString(asset, "browser_download_url");
                if (url.Length == 0)
                    break;

                long size = asset.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out long s) ? s : 0;
                return new UpdateInfo(version, tag, GetString(release, "body").Trim(), url, size, GetString(release, "html_url"));
            }
        }

        throw new InvalidDataException($"Release {tag} has no {AssetName}.");
    }

    public static bool IsNewer(UpdateInfo update) => update.Version > CurrentVersion;

    /// <summary>"v1.2.3" / "1.2" / "1.2.3.4" -> major.minor.build.</summary>
    public static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        string trimmed = (text ?? string.Empty).Trim().TrimStart('v', 'V');
        if (!Version.TryParse(trimmed, out var parsed))
            return false;

        version = Normalize(parsed);
        return true;
    }

    /// <summary>
    /// Why this copy cannot replace itself, or null when it can. Only a published (self-contained)
    /// package updates: a build folder would be overwritten by the next build anyway, and a folder
    /// without write access (Program Files) would fail half-way.
    /// </summary>
    public static string? CannotInstallReason(string? appFolder = null)
    {
        string folder = appFolder ?? AppContext.BaseDirectory;
        if (!File.Exists(Path.Combine(folder, "coreclr.dll")) || !File.Exists(Path.Combine(folder, ExeName)))
            return "This copy runs from a build folder, not from the release zip - it does not update itself.";

        try
        {
            string probe = Path.Combine(folder, $".update_probe_{Environment.ProcessId}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return $"WuxAuto cannot write to its folder ({folder}). Move the WuxAuto folder somewhere you own, e.g. the Desktop.";
        }

        return null;
    }

    /// <summary>Downloads the release zip and unpacks it; returns the unpacked app folder.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo update, IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        string staging = StagingFolder(update.Tag);
        string zip = Path.Combine(staging, AssetName);

        using (var http = CreateClient(TimeSpan.FromMinutes(10)))
        using (var response = await http.GetAsync(update.ZipUrl, HttpCompletionOption.ResponseHeadersRead, cancel))
        {
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? update.ZipSize;

            await using var source = await response.Content.ReadAsStreamAsync(cancel);
            await using var target = File.Create(zip);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancel)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancel);
                done += read;
                if (total > 0)
                    progress?.Report(Math.Min(1.0, done / (double)total));
            }

            if (update.ZipSize > 0 && done != update.ZipSize)
                throw new InvalidDataException($"Download incomplete: {done} of {update.ZipSize} bytes.");
        }

        return Unpack(zip, staging);
    }

    /// <summary>Unpacks a local release zip (also used by the self test); returns the app folder inside it.</summary>
    public static string Unpack(string zipPath, string? staging = null)
    {
        staging ??= StagingFolder("local");
        string files = Path.Combine(staging, "files");
        if (Directory.Exists(files))
            Directory.Delete(files, recursive: true);

        ZipFile.ExtractToDirectory(zipPath, files);

        // publish.ps1 zips the WuxAuto folder itself, so the exe sits one level down.
        string? root = File.Exists(Path.Combine(files, ExeName))
            ? files
            : Directory.GetDirectories(files).FirstOrDefault(d => File.Exists(Path.Combine(d, ExeName)));

        return root ?? throw new InvalidDataException($"The update zip does not contain {ExeName}.");
    }

    /// <summary>
    /// Starts the hidden copy script and returns; the caller must exit right after so the files are
    /// no longer locked. The script waits for <paramref name="processId"/> to end, copies
    /// <paramref name="newFiles"/> over <paramref name="appFolder"/> (files only in the app folder stay),
    /// writes logs\update_*.log and, with <paramref name="restart"/>, starts the app again.
    /// </summary>
    public static string StartInstaller(string newFiles, string appFolder, int processId, bool restart)
    {
        string staging = Path.Combine(Path.GetTempPath(), "WuxAuto-update");
        Directory.CreateDirectory(staging);
        string script = Path.Combine(staging, "install_update.cmd");

        Directory.CreateDirectory(AppPaths.LogFolder);
        string log = Path.Combine(AppPaths.LogFolder, $"update_{DateTime.Now:yyyyMMdd_HHmmss}.log");
        string target = Path.GetFullPath(appFolder).TrimEnd('\\');
        string source = Path.GetFullPath(newFiles).TrimEnd('\\');

        // cmd expands %...% even inside quotes: double them. "pid" in the find pattern is padded with
        // spaces so 123 does not match 1234. ping is the sleep: timeout fails without a console.
        static string Q(string value) => value.Replace("%", "%%");
        var lines = new List<string>
        {
            "@echo off",
            "chcp 65001 >nul",
            $"set \"LOG={Q(log)}\"",
            $"echo Updating \"{Q(target)}\" from \"{Q(source)}\" > \"%LOG%\"",
            "set /a WAITED=0",
            ":wait",
            $"tasklist /FI \"PID eq {processId}\" /NH | find \" {processId} \" >nul",
            "if errorlevel 1 goto copy",
            "set /a WAITED+=1",
            "if %WAITED% GEQ 120 (echo The app did not exit within 2 minutes - update skipped.>> \"%LOG%\" & exit /b 1)",
            "ping -n 2 127.0.0.1 >nul",
            "goto wait",
            ":copy",
            "ping -n 2 127.0.0.1 >nul",
            $"robocopy \"{Q(source)}\" \"{Q(target)}\" /E /R:30 /W:1 /NP /NDL /NJH >> \"%LOG%\"",
            "if errorlevel 8 (echo Copy FAILED, robocopy code %errorlevel%.>> \"%LOG%\") else (echo Copy done.>> \"%LOG%\")",
        };

        if (restart)
            lines.Add($"start \"\" /D \"{Q(target)}\" \"{Q(Path.Combine(target, ExeName))}\"");

        File.WriteAllText(script, string.Join("\r\n", lines) + "\r\n", new UTF8Encoding(false));

        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"\"{script}\"\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = staging,
        });

        return log;
    }

    private static string StagingFolder(string tag)
    {
        string safeTag = string.Concat(tag.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        string folder = Path.Combine(Path.GetTempPath(), "WuxAuto-update", safeTag);
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static HttpClient CreateClient(TimeSpan timeout, bool api = false)
    {
        var http = new HttpClient { Timeout = timeout };
        // GitHub's API refuses requests without a User-Agent.
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"WuxAuto/{Format(CurrentVersion)}");
        if (api)
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    private static string GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static Version Normalize(Version? version)
        => version == null ? new Version(0, 0, 0) : new Version(version.Major, version.Minor, Math.Max(0, version.Build));
}

using System.Diagnostics;
using System.IO;
using System.Text.Json;
using WuxAuto.Core;

namespace WuxAuto.SelfTest;

/// <summary>Offline checks and manual probes for <see cref="UpdateService"/>.</summary>
internal static class UpdateProbe
{
    /// <summary>Release parsing and version comparison on synthetic GitHub JSON (no network).</summary>
    public static IEnumerable<(string Name, bool Ok, string Detail)> OfflineChecks()
    {
        const string release = """
            {
              "tag_name": "v1.2.3",
              "html_url": "https://github.com/x/y/releases/tag/v1.2.3",
              "body": "  Fixed things.  ",
              "assets": [
                { "name": "notes.txt", "browser_download_url": "https://example/notes.txt", "size": 5 },
                { "name": "WuxAuto-win-x64.zip", "browser_download_url": "https://example/app.zip", "size": 97150020 }
              ]
            }
            """;

        using var document = JsonDocument.Parse(release);
        var info = UpdateService.Parse(document.RootElement);
        yield return ("update parse", info.Version == new Version(1, 2, 3) && info.ZipUrl == "https://example/app.zip"
                                      && info.ZipSize == 97150020 && info.Notes == "Fixed things.",
            $"v{info.VersionText} {info.ZipUrl} {info.ZipSize} '{info.Notes}'");

        using var noZip = JsonDocument.Parse("""{ "tag_name": "v2.0.0", "assets": [] }""");
        bool threw = false;
        try { UpdateService.Parse(noZip.RootElement); }
        catch (InvalidDataException) { threw = true; }
        yield return ("update parse no zip", threw, threw ? "rejected" : "accepted a release without the zip");

        bool newer = UpdateService.TryParseVersion("v0.10.0", out var a) && UpdateService.TryParseVersion("0.9.9", out var b) && a > b;
        bool equal = UpdateService.TryParseVersion("1.2", out var c) && UpdateService.TryParseVersion("v1.2.0.7", out var d) && c == d;
        bool bad = !UpdateService.TryParseVersion("latest", out _);
        yield return ("update versions", newer && equal && bad, $"0.10.0>0.9.9 {newer}, 1.2==1.2.0.7 {equal}, 'latest' rejected {bad}");
    }

    /// <summary>--update-check [owner/repo]: what the app would see online.</summary>
    public static async Task<int> CheckAsync(string repository)
    {
        Console.WriteLine($"this build: v{UpdateService.Format(UpdateService.CurrentVersion)}, repository {repository}");
        try
        {
            var latest = await UpdateService.GetLatestAsync(repository);
            if (latest == null)
            {
                Console.WriteLine("no release published");
                return 0;
            }

            Console.WriteLine($"latest: v{latest.VersionText} ({latest.Tag}) newer={UpdateService.IsNewer(latest)}");
            Console.WriteLine($"zip: {latest.ZipUrl} ({latest.ZipSize} bytes)");
            Console.WriteLine($"page: {latest.PageUrl}");
            Console.WriteLine($"notes: {latest.Notes}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"check failed: {ex.GetBaseException().Message}");
            return 1;
        }
    }

    /// <summary>
    /// --update-install &lt;appFolder&gt; &lt;zip&gt;: unpacks a local release zip and runs the real copy
    /// script against <c>appFolder</c> (no restart); it waits for this process to exit first.
    /// </summary>
    public static int Install(string appFolder, string zip)
    {
        string? blocked = UpdateService.CannotInstallReason(appFolder);
        if (blocked != null)
        {
            Console.WriteLine($"cannot install: {blocked}");
            return 1;
        }

        var stopwatch = Stopwatch.StartNew();
        string files = UpdateService.Unpack(zip);
        string log = UpdateService.StartInstaller(files, appFolder, Environment.ProcessId, restart: false);
        Console.WriteLine($"unpacked to {files} in {stopwatch.ElapsedMilliseconds}ms; copy script started, log: {log}");
        Console.WriteLine("exiting so the script can copy (it waits for this process).");
        return 0;
    }
}

using System.IO.Compression;
using WuxAuto.Phone;

namespace WuxAuto.Core.Tests;

public class DebugBundleContentsTests : AppPathsScope
{
    static readonly DateTime Now = new(2026, 10, 28, 15, 30, 0);

    string Setup()
    {
        string root = TempRoot();
        AppPaths.Configure(root);
        AppPaths.EnsureCreated();
        return root;
    }

    static void Touch(string path, DateTime when, string text = "x")
    {
        File.WriteAllText(path, text);
        File.SetLastWriteTime(path, when);
    }

    [Fact]
    public void Picks_TodaysLogs_Newest20Pictures_AndTheSettings()
    {
        Setup();
        Touch(Path.Combine(AppPaths.LogFolder, "wuxauto_20261028_090000.log"), Now.AddHours(-6));
        Touch(Path.Combine(AppPaths.LogFolder, "wuxauto_20261027_230000.log"), Now.AddDays(-1));   // yesterday: left out
        Touch(Path.Combine(AppPaths.LogFolder, "notes.txt"), Now);                                  // not a log
        for (int i = 0; i < 25; i++)
            Touch(Path.Combine(AppPaths.DebugFolder, $"pic{i:00}.png"), Now.AddMinutes(-60 + i));
        Touch(AppPaths.SettingsFile, Now, "{}");

        var picked = DebugBundleContents.Pick(Now);
        var names = picked.Select(p => p.EntryName).ToList();

        Assert.Contains("logs/wuxauto_20261028_090000.log", names);
        Assert.DoesNotContain("logs/wuxauto_20261027_230000.log", names);
        Assert.DoesNotContain("logs/notes.txt", names);
        Assert.Contains("settings.json", names);
        var pictures = names.Where(n => n.StartsWith("debug/", StringComparison.Ordinal)).ToList();
        Assert.Equal(DebugBundleContents.MaxPictures, pictures.Count);
        Assert.Contains("debug/pic24.png", pictures);
        Assert.Contains("debug/pic05.png", pictures);
        Assert.DoesNotContain("debug/pic04.png", pictures);
    }

    [Fact]
    public void EmptyFolders_PickNothing()
    {
        Setup();

        Assert.Empty(DebugBundleContents.Pick(Now));
    }

    [Fact]
    public void Create_WritesTheZip_AndReplacesAnOldOne()
    {
        string root = Setup();
        Touch(Path.Combine(AppPaths.LogFolder, "wuxauto_20261028_120000.log"), Now, "a log line");
        Touch(AppPaths.SettingsFile, Now, "{ }");
        string zip = Path.Combine(root, "out", "bundle.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(zip)!);
        File.WriteAllText(zip, "stale");

        int count = DebugBundleContents.Create(zip, Now);

        Assert.Equal(2, count);
        using var archive = ZipFile.OpenRead(zip);
        var entry = archive.GetEntry("logs/wuxauto_20261028_120000.log");
        Assert.NotNull(entry);
        using var reader = new StreamReader(entry.Open());
        Assert.Equal("a log line", reader.ReadToEnd());
        Assert.NotNull(archive.GetEntry("settings.json"));
    }

    [Fact]
    public void Create_ReadsALogStillOpenForWriting()
    {
        string root = Setup();
        string log = Path.Combine(AppPaths.LogFolder, "wuxauto_20261028_120000.log");
        using var writer = new FileStream(log, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        writer.Write("live"u8);
        writer.Flush();
        File.SetLastWriteTime(log, Now); // "today" is the test's fixed Now, not the machine's date
        string zip = Path.Combine(root, "bundle.zip");

        Assert.Equal(1, DebugBundleContents.Create(zip, Now));
    }
}

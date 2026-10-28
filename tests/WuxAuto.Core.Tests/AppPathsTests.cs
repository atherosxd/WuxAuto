using WuxAuto.Core;

namespace WuxAuto.Core.Tests;

[Collection("AppPaths")] // static state: never in parallel
public class AppPathsTests : AppPathsScope
{
    [Fact]
    public void Configure_MovesEveryFolderAndSettingsStoreFollows()
    {
        string root = TempRoot();
        AppPaths.Configure(root, Path.Combine(root, "assets"));
        Assert.Equal(Path.Combine(root, "settings.json"), AppPaths.SettingsFile);
        Assert.Equal(Path.Combine(root, "debug"), AppPaths.DebugFolder);
        Assert.Equal(Path.Combine(root, "kills"), AppPaths.KillsFolder);
        Assert.Equal(Path.Combine(root, "assets"), AppPaths.AssetsFolder);
        var settings = new AppSettings();
        settings.Sequence.Mode = RunMode.AutoLod;
        SettingsStore.Save(settings);
        Assert.True(File.Exists(Path.Combine(root, "settings.json")));
        Assert.Equal(RunMode.AutoLod, SettingsStore.Load().Sequence.Mode);
    }

    [Fact]
    public void TrySave_ReportsAFailedWrite_AndSucceedsOtherwise()
    {
        string root = TempRoot();
        AppPaths.Configure(root, Path.Combine(root, "assets"));
        var settings = new AppSettings();

        Assert.True(SettingsStore.TrySave(settings, out string? none));
        Assert.Null(none);

        // settings.json taken by a folder: the write fails, and says why instead of being swallowed.
        File.Delete(AppPaths.SettingsFile);
        Directory.CreateDirectory(AppPaths.SettingsFile);
        Assert.False(SettingsStore.TrySave(settings, out string? error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public async Task TrySave_FromManyThreads_LeavesAReadableFile()
    {
        string root = TempRoot();
        AppPaths.Configure(root, Path.Combine(root, "assets"));
        var settings = new AppSettings();

        var saves = Enumerable.Range(0, 16).Select(_ => Task.Run(() => SettingsStore.TrySave(settings, out string? _))).ToArray();
        bool[] results = await Task.WhenAll(saves);

        Assert.All(results, Assert.True);
        Assert.Equal(settings.Sequence.Mode, SettingsStore.Load(persistMigration: false).Sequence.Mode);
    }

    [Fact]
    public void KillSound_IsSavedTrimmed_AndAMissingOrNullValueLoadsEmpty()
    {
        string root = Path.Combine(Path.GetTempPath(), "wux-core-" + Guid.NewGuid());
        AppPaths.Configure(root, Path.Combine(root, "assets"));
        try
        {
            Assert.Equal(Path.Combine(root, "sounds"), AppPaths.SoundsFolder);

            var settings = new AppSettings();
            Assert.Equal(string.Empty, settings.Sequence.KillSound);
            settings.Sequence.KillSound = "  C:\\sounds\\boss down.mp3 ";
            SettingsStore.Save(settings);
            Assert.Equal("C:\\sounds\\boss down.mp3", SettingsStore.Load().Sequence.KillSound);

            // An older file has no KillSound; a hand-edited one may say null.
            File.WriteAllText(AppPaths.SettingsFile, File.ReadAllText(AppPaths.SettingsFile)
                .Replace("\"KillSound\": \"C:\\\\sounds\\\\boss down.mp3\"", "\"KillSound\": null"));
            Assert.Contains("\"KillSound\": null", File.ReadAllText(AppPaths.SettingsFile));
            Assert.Equal(string.Empty, SettingsStore.Load().Sequence.KillSound);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Configure_WithoutAssetsFolder_KeepsTheBaseDirectoryDefault()
    {
        string root = TempRoot();
        AppPaths.Configure(root);
        Assert.Equal(root, AppPaths.Root);
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "Assets"), AppPaths.AssetsFolder);
    }
}

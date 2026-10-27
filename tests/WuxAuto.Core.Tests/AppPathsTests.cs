using WuxAuto.Core;

namespace WuxAuto.Core.Tests;

[Collection("AppPaths")] // static state: never in parallel
public class AppPathsTests
{
    [Fact]
    public void Configure_MovesEveryFolderAndSettingsStoreFollows()
    {
        string root = Path.Combine(Path.GetTempPath(), "wux-core-" + Guid.NewGuid());
        AppPaths.Configure(root, Path.Combine(root, "assets"));
        try
        {
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
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Configure_WithoutAssetsFolder_KeepsTheBaseDirectoryDefault()
    {
        string root = Path.Combine(Path.GetTempPath(), "wux-core-" + Guid.NewGuid());
        AppPaths.Configure(root);
        Assert.Equal(root, AppPaths.Root);
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "Assets"), AppPaths.AssetsFolder);
    }
}

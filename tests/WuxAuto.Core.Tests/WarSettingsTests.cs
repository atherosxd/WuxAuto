using WuxAuto.Core;

namespace WuxAuto.Core.Tests;

[Collection("AppPaths")]
public class WarSettingsTests
{
    [Fact]
    public void WarSettings_RoundTrip_AndOldFileLoadsDefaults()
    {
        string root = Path.Combine(Path.GetTempPath(), "wux-core-" + Guid.NewGuid());
        AppPaths.Configure(root, Path.Combine(root, "assets"));
        try
        {
            var s = new AppSettings();
            Assert.Equal("Bohai Outpost", s.War.Destination);
            Assert.Equal(345000, s.War.BuffEveryMs);
            Assert.Equal(1200, s.War.ShuraHoldMs);
            Assert.Equal(3, s.War.ListPage);
            Assert.Equal(new[] { "warbuff1", "warbuff2", "warbuff3", "warbuff4", "warbuff5", "warbuff6" }, s.War.BuffIcons);
            s.Sequence.Mode = RunMode.AutoWar;
            s.War.HomeMap = "[Dark Soul Cave]";
            s.War.HomeMonster = "Cave Bat";
            SettingsStore.Save(s);
            var loaded = SettingsStore.Load();
            Assert.Equal(RunMode.AutoWar, loaded.Sequence.Mode);
            Assert.Equal("[Dark Soul Cave]", loaded.War.HomeMap);
            Assert.Equal("Cave Bat", loaded.War.HomeMonster);

            // A file written before Auto War has no "War" section.
            File.WriteAllText(AppPaths.SettingsFile, "{ \"Version\": 21 }");
            Assert.Equal("Enemy worth CP", SettingsStore.Load().War.TabTargetOption);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    [Fact]
    public void WarSettings_NormalizeClampsAndRefillsEmptyLists()
    {
        var s = new AppSettings();
        s.War.BuffIcons = [];
        s.War.ShuraHoldMs = -5;
        s.War.ListPage = 0;
        s.War.HomeMap = "  [Dark Soul Cave]  ";
        SettingsStore.Normalize(s);
        Assert.Equal(6, s.War.BuffIcons.Count);
        Assert.Equal(0, s.War.ShuraHoldMs);
        Assert.Equal(1, s.War.ListPage);
        Assert.Equal("[Dark Soul Cave]", s.War.HomeMap);
    }
}

using WuxAuto.Core;

namespace WuxAuto.Core.Tests;

public class WarSettingsTests : AppPathsScope
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
            Assert.Equal(new[] { "warbuff1", "warbuff3", "warbuff4", "warbuff5", "warbuff6" }, s.War.BuffIcons);
            Assert.Equal(new[] { "warbuff2", "warweapon1", "warweapon2" }, s.War.WeaponBuffIcons);
            Assert.Equal(new[] { "waritem1", "waritem2" }, s.War.ItemBuffIcons);
            Assert.Empty(s.War.DisabledBuffs);
            s.Sequence.Mode = RunMode.AutoWar;
            s.War.HomeMap = "[Dark Soul Cave]";
            s.War.HomeMonster = "Cave Bat";
            s.War.SetBuffOn("warweapon2", false);
            SettingsStore.Save(s);
            var loaded = SettingsStore.Load();
            Assert.Equal(RunMode.AutoWar, loaded.Sequence.Mode);
            Assert.Equal("[Dark Soul Cave]", loaded.War.HomeMap);
            Assert.Equal("Cave Bat", loaded.War.HomeMonster);
            Assert.Equal(["warweapon2"], loaded.War.DisabledBuffs);

            // A file written before Auto War has no "War" section.
            File.WriteAllText(AppPaths.SettingsFile, "{ \"Version\": 21 }");
            var old = SettingsStore.Load();
            Assert.Equal("Enemy worth CP", old.War.TabTargetOption);
            Assert.Equal(5, old.War.BuffIcons.Count);
            Assert.Equal(["warbuff2", "warweapon1", "warweapon2"], old.War.WeaponBuffIcons);
            Assert.Equal(["waritem1", "waritem2"], old.War.ItemBuffIcons);
            Assert.Empty(old.War.DisabledBuffs);
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
        Assert.Equal(5, s.War.BuffIcons.Count);
        Assert.Equal(0, s.War.ShuraHoldMs);
        Assert.Equal(1, s.War.ListPage);
        Assert.Equal("[Dark Soul Cave]", s.War.HomeMap);
    }

    [Theory]
    [InlineData(100, 800)]      // the buff animation needs 0.8 s (user, 2026-10-28)
    [InlineData(800, 800)]
    [InlineData(1000, 1000)]
    public void WarSettings_NormalizeKeepsTheBuffGapAtLeastTheAnimation(int saved, int expected)
    {
        var s = new AppSettings();
        s.War.BuffClickGapMs = saved;
        SettingsStore.Normalize(s);
        Assert.Equal(expected, s.War.BuffClickGapMs);
    }

    [Fact]
    public void WarSettings_NormalizeDropsBlankBuffIcons()
    {
        var s = new AppSettings();
        s.War.BuffIcons = ["warbuff1", "", "   ", " warbuff3 ", null!];
        SettingsStore.Normalize(s);
        Assert.Equal(["warbuff1", "warbuff3"], s.War.BuffIcons);
    }

    [Fact]
    public void WarSettings_NormalizeCleansTheWeaponItemAndDisabledLists()
    {
        var s = new AppSettings();
        s.War.WeaponBuffIcons = [" warweapon2 ", "", null!, "WarWeapon2", "warweapon1"];
        s.War.ItemBuffIcons = [" waritem2 ", "", null!, "WARITEM2", "waritem1"];
        s.War.DisabledBuffs = ["warweapon2", "  ", null!, " WarWeapon2", " waritem1 "];
        SettingsStore.Normalize(s);
        Assert.Equal(["warweapon2", "warweapon1"], s.War.WeaponBuffIcons);
        Assert.Equal(["waritem2", "waritem1"], s.War.ItemBuffIcons);
        Assert.Equal(["warweapon2", "waritem1"], s.War.DisabledBuffs);
    }

    [Fact]
    public void WarSettings_AnEmptyOrMissingWeaponOrItemList_StaysEmpty_NoRefill()
    {
        // The user may have taken the weapon or item buffs out on purpose: unlike BuffIcons, nothing refills them.
        var s = new AppSettings();
        s.War.WeaponBuffIcons = [];
        s.War.ItemBuffIcons = [];
        s.War.DisabledBuffs = null!;
        SettingsStore.Normalize(s);
        Assert.Empty(s.War.WeaponBuffIcons);
        Assert.Empty(s.War.ItemBuffIcons);
        Assert.Empty(s.War.DisabledBuffs);

        s.War.WeaponBuffIcons = null!;
        s.War.ItemBuffIcons = null!;
        SettingsStore.Normalize(s);
        Assert.Empty(s.War.WeaponBuffIcons);
        Assert.Empty(s.War.ItemBuffIcons);
    }

    [Fact]
    public void WarSettings_BuffSwitches_OnByDefault_OffAndOnAgain_IgnoringCase()
    {
        var war = new WarSettings();
        Assert.True(war.IsBuffOn("warweapon1"));

        war.SetBuffOn("warweapon1", false);
        war.SetBuffOn("WARWEAPON1", false);          // already off: listed once
        Assert.False(war.IsBuffOn("WarWeapon1"));
        Assert.Equal(["warweapon1"], war.DisabledBuffs);

        war.SetBuffOn("Warweapon1", true);
        Assert.True(war.IsBuffOn("warweapon1"));
        Assert.Empty(war.DisabledBuffs);
    }

    [Fact]
    public void WarSettings_SetBuffOn_ReplacesTheList_SoAReaderKeepsItsCopy()
    {
        // The pilot's loop thread may be going through the list while the settings panel ticks a box.
        var war = new WarSettings();
        var before = war.DisabledBuffs;
        war.SetBuffOn("waritem1", false);
        Assert.NotSame(before, war.DisabledBuffs);
        Assert.Empty(before);
    }
}

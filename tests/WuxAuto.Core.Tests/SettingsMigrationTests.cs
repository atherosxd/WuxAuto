using System.Text.Json.Nodes;

namespace WuxAuto.Core.Tests;

/// <summary>
/// settings.json migrations: a saved value wins over the code default, so a default that must reach
/// existing installs needs a step in SettingsStore.Migrate.
/// </summary>
[Collection("AppPaths")] // AppPaths is static state
public class SettingsMigrationTests
{
    [Fact]
    public void V22_OldLodDefaults_WalkOnAndTheNewSpot()
    {
        var lod = LoadAfterEdit(version: 21, lod =>
        {
            lod["TargetX"] = 1191;
            lod["TargetY"] = 2413;
            lod.Remove("WalkToBossSpot"); // a file from before v0.16.0
            lod["TabEveryMs"] = 1700;     // proves the file was read, not replaced by defaults
        });

        Assert.Equal(1700, lod.TabEveryMs);
        Assert.True(lod.WalkToBossSpot);
        Assert.Equal((1199, 1782), (lod.TargetX, lod.TargetY));
    }

    [Fact]
    public void V22_WalkWasOff_TurnsOn_AndAHandSetSpotStays()
    {
        var lod = LoadAfterEdit(version: 21, lod =>
        {
            lod["TargetX"] = 1300;
            lod["TargetY"] = 2000;
            lod["WalkToBossSpot"] = false;
        });

        Assert.True(lod.WalkToBossSpot);
        Assert.Equal((1300, 2000), (lod.TargetX, lod.TargetY));
    }

    [Fact]
    public void AfterV22_TheWalkSwitchTurnedOffStaysOff()
    {
        var lod = LoadAfterEdit(version: 22, lod => lod["WalkToBossSpot"] = false);

        Assert.False(lod.WalkToBossSpot);
        Assert.Equal((1199, 1782), (lod.TargetX, lod.TargetY));
    }

    // ---------------------------------------------------------------- v23: the weapon buffs

    static readonly string[] SixBuffs = ["warbuff1", "warbuff2", "warbuff3", "warbuff4", "warbuff5", "warbuff6"];

    static JsonArray JsonList(params string[] items) => new([.. items.Select(i => JsonValue.Create(i))]);

    [Fact]
    public void V23_TheSixSavedBuffs_GetBothWeaponBuffsAppended()
    {
        var war = LoadAfterEditWar(version: 22, war =>
        {
            war["BuffIcons"] = JsonList(SixBuffs);
            war.Remove("ItemBuffIcons");   // a v0.19.0 file has neither list
            war.Remove("DisabledBuffs");
            war["ShuraHoldMs"] = 1500;     // proves the file was read, not replaced by defaults
        });

        Assert.Equal(1500, war.ShuraHoldMs);
        Assert.Equal([.. SixBuffs, "warweapon1", "warweapon2"], war.BuffIcons);
        Assert.Equal(["waritem1", "waritem2"], war.ItemBuffIcons);
        Assert.Empty(war.DisabledBuffs);
    }

    [Fact]
    public void V23_TheUsersOrderAndEntriesStay_OnlyTheMissingWeaponIsAdded()
    {
        var war = LoadAfterEditWar(version: 22, war => war["BuffIcons"] = JsonList("warbuff3", "WarWeapon1", "mybuff", "warbuff1"));

        Assert.Equal(["warbuff3", "WarWeapon1", "mybuff", "warbuff1", "warweapon2"], war.BuffIcons);
    }

    [Fact]
    public void V23_AnEmptySavedList_IsRefilledWithTheDefaults_NotJustTheWeapons()
    {
        var war = LoadAfterEditWar(version: 22, war => war["BuffIcons"] = JsonList());

        Assert.Equal([.. SixBuffs, "warweapon1", "warweapon2"], war.BuffIcons);
    }

    [Fact]
    public void AfterV23_AWeaponBuffTakenOut_StaysOut()
    {
        var war = LoadAfterEditWar(version: 23, war =>
        {
            war["BuffIcons"] = JsonList([.. SixBuffs, "warweapon1"]);
            war["ItemBuffIcons"] = JsonList();
        });

        Assert.Equal([.. SixBuffs, "warweapon1"], war.BuffIcons);
        Assert.Empty(war.ItemBuffIcons);
    }

    [Fact]
    public void Load_WithoutPersisting_MigratesInMemory_AndLeavesTheFileAsItWas()
    {
        // The offline probes: a newer build must not rewrite the settings.json a running older copy shares - v0.19.0
        // would then demand the weapon buffs on the skill bar at Start.
        string root = Path.Combine(Path.GetTempPath(), "wux-core-" + Guid.NewGuid());
        AppPaths.Configure(root, Path.Combine(root, "assets"));
        try
        {
            SettingsStore.Save(new AppSettings());
            var json = JsonNode.Parse(File.ReadAllText(AppPaths.SettingsFile))!.AsObject();
            json["Version"] = 22;
            json["War"]!["BuffIcons"] = JsonList(SixBuffs);
            string old = json.ToJsonString();
            File.WriteAllText(AppPaths.SettingsFile, old);

            var loaded = SettingsStore.Load(persistMigration: false);

            Assert.Equal([.. SixBuffs, "warweapon1", "warweapon2"], loaded.War.BuffIcons);
            Assert.Equal(old, File.ReadAllText(AppPaths.SettingsFile));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    /// <summary>Saves default settings, edits the file's Lod section and version, loads it back.</summary>
    private static LodSettings LoadAfterEdit(int version, Action<JsonObject> editLod)
        => LoadAfterEdit(version, "Lod", editLod).Lod;

    /// <summary>Saves default settings, edits the file's War section and version, loads it back.</summary>
    private static WarSettings LoadAfterEditWar(int version, Action<JsonObject> editWar)
        => LoadAfterEdit(version, "War", editWar).War;

    /// <summary>Saves default settings, edits one section of the file and its version, loads it back.</summary>
    private static AppSettings LoadAfterEdit(int version, string section, Action<JsonObject> edit)
    {
        string root = Path.Combine(Path.GetTempPath(), "wux-core-" + Guid.NewGuid());
        AppPaths.Configure(root, Path.Combine(root, "assets"));
        try
        {
            SettingsStore.Save(new AppSettings());
            var json = JsonNode.Parse(File.ReadAllText(AppPaths.SettingsFile))!.AsObject();
            json["Version"] = version;
            edit(json[section]!.AsObject());
            File.WriteAllText(AppPaths.SettingsFile, json.ToJsonString());

            var loaded = SettingsStore.Load();
            Assert.True(loaded.Version >= 22, $"version {loaded.Version}: the file did not load");
            return loaded;
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }
}

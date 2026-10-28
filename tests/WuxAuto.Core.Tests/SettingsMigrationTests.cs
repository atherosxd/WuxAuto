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

    // ---------------------------------------------------------------- v23: warbuff2 (the sword buff) moves to the weapons

    static readonly string[] SixBuffs = ["warbuff1", "warbuff2", "warbuff3", "warbuff4", "warbuff5", "warbuff6"];
    static readonly string[] FiveBuffs = ["warbuff1", "warbuff3", "warbuff4", "warbuff5", "warbuff6"];

    static JsonArray JsonList(params string[] items) => new([.. items.Select(i => JsonValue.Create(i))]);

    [Fact]
    public void V23_AV0190File_LosesWarbuff2_AndLoadsTheNewListsDefaults()
    {
        // A v0.19.0 file: BuffIcons = warbuff1-6, no WeaponBuffIcons / ItemBuffIcons / DisabledBuffs. warbuff2 is the sword's
        // buff, a weapon buff now: it leaves BuffIcons (the new WeaponBuffIcons default holds it). Removal only, so the
        // v0.19.0 copy sharing the file just checks five icons.
        var loaded = LoadAfterEdit(version: 22, "War", war =>
        {
            war["BuffIcons"] = JsonList(SixBuffs);
            war.Remove("WeaponBuffIcons");
            war.Remove("ItemBuffIcons");
            war.Remove("DisabledBuffs");
            war["ShuraHoldMs"] = 1500;     // proves the file was read, not replaced by defaults
        });

        Assert.Equal(23, loaded.Version);
        Assert.Equal(1500, loaded.War.ShuraHoldMs);
        Assert.Equal(FiveBuffs, loaded.War.BuffIcons);
        Assert.Equal(["warbuff2", "warweapon1", "warweapon2"], loaded.War.WeaponBuffIcons);
        Assert.Equal(["waritem1", "waritem2"], loaded.War.ItemBuffIcons);
        Assert.Empty(loaded.War.DisabledBuffs);
    }

    [Fact]
    public void V23_AUserEditedList_KeepsItsOrderAndOtherEntries_OnlyWarbuff2Goes()
    {
        var loaded = LoadAfterEdit(version: 22, "War", war => war["BuffIcons"] = JsonList("warbuff5", " WarBuff2 ", "mybuff", "warbuff1"));

        Assert.Equal(["warbuff5", "mybuff", "warbuff1"], loaded.War.BuffIcons);
    }

    [Fact]
    public void V23_IsIdempotent_AListWithoutWarbuff2StaysAsItIs()
    {
        // Already without warbuff2 (a v22 file edited by hand, or the step run before): nothing changes.
        Assert.Equal(FiveBuffs, LoadAfterEdit(version: 22, "War", war => war["BuffIcons"] = JsonList(FiveBuffs)).War.BuffIcons);
        Assert.Equal(["warbuff3", "mybuff"], LoadAfterEdit(version: 22, "War", war => war["BuffIcons"] = JsonList("warbuff3", "mybuff")).War.BuffIcons);
    }

    [Fact]
    public void AfterV23_Warbuff2PutBackByTheUser_Stays()
    {
        var loaded = LoadAfterEdit(version: 23, "War", war => war["BuffIcons"] = JsonList(SixBuffs));

        Assert.Equal(SixBuffs, loaded.War.BuffIcons);
    }

    [Fact]
    public void Load_WithoutPersisting_MigratesInMemory_AndLeavesTheFileAsItWas()
    {
        // The offline probes: a newer build must not rewrite the settings.json a running older copy shares.
        string root = Path.Combine(Path.GetTempPath(), "wux-core-" + Guid.NewGuid());
        AppPaths.Configure(root, Path.Combine(root, "assets"));
        try
        {
            SettingsStore.Save(new AppSettings());
            var json = JsonNode.Parse(File.ReadAllText(AppPaths.SettingsFile))!.AsObject();
            json["Version"] = 21;
            json["Lod"]!["TargetX"] = 1191;      // the v22 step moves the old LOD spot
            json["Lod"]!["TargetY"] = 2413;
            string old = json.ToJsonString();
            File.WriteAllText(AppPaths.SettingsFile, old);

            var loaded = SettingsStore.Load(persistMigration: false);

            Assert.Equal((1199, 1782), (loaded.Lod.TargetX, loaded.Lod.TargetY));
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

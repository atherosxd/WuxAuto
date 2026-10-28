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

    /// <summary>Saves default settings, edits the file's Lod section and version, loads it back.</summary>
    private static LodSettings LoadAfterEdit(int version, Action<JsonObject> editLod)
    {
        string root = Path.Combine(Path.GetTempPath(), "wux-core-" + Guid.NewGuid());
        AppPaths.Configure(root, Path.Combine(root, "assets"));
        try
        {
            SettingsStore.Save(new AppSettings());
            var json = JsonNode.Parse(File.ReadAllText(AppPaths.SettingsFile))!.AsObject();
            json["Version"] = version;
            editLod(json["Lod"]!.AsObject());
            File.WriteAllText(AppPaths.SettingsFile, json.ToJsonString());

            var loaded = SettingsStore.Load();
            Assert.True(loaded.Version >= 22, $"version {loaded.Version}: the file did not load");
            return loaded.Lod;
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }
}

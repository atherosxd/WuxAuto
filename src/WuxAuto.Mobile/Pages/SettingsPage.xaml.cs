using WuxAuto.Core;
using WuxAuto.Mobile.Engine;

namespace WuxAuto.Mobile.Pages;

/// <summary>The settings the desktop panel shows that still apply on the phone; Save writes settings.json.</summary>
public partial class SettingsPage : ContentPage
{
    public SettingsPage()
    {
        InitializeComponent();
        AttackMode.ItemsSource = Enum.GetNames<PhoneAttackMode>();
    }

    private static AppSettings Settings => EngineHost.Engine.Settings;

    protected override void OnAppearing()
    {
        base.OnAppearing();
        var s = Settings;
        Loop.IsToggled = s.Sequence.Loop;
        RestartOnError.IsToggled = s.Sequence.RestartOnError;
        AutoReconnect.IsToggled = s.Sequence.AutoReconnect;
        AttackMode.SelectedItem = s.Phone.AttackMode.ToString();
        LodApproach.IsToggled = s.Lod.ApproachBoss;
        DryRun.IsToggled = s.Sequence.DryRun;
        SkillKey.Text = s.Dungeon.SkillKey;
        HoldSkillMs.Text = s.Dungeon.HoldSkillMs.ToString();
        LodLootMs.Text = s.Lod.LootMs.ToString();
        LodBuffEveryMs.Text = s.Lod.BuffEveryMs.ToString();
        AttackTapEveryMs.Text = s.Phone.AttackTapEveryMs.ToString();
        bool running = EngineHost.Engine.State == EngineState.Running;
        SaveButton.IsEnabled = !running;
        Saved.Text = running ? "Stop the pilot to change settings." : string.Empty;
    }

    private void OnSave(object? sender, EventArgs e)
    {
        var engine = EngineHost.Engine;
        if (engine.State == EngineState.Running)
        {
            Saved.Text = "Stop the pilot to change settings.";
            return;
        }

        if (!TryMs(HoldSkillMs, out int hold) || !TryMs(LodLootMs, out int loot) || !TryMs(LodBuffEveryMs, out int buff)
            || !TryMs(AttackTapEveryMs, out int tapEvery))
        {
            Saved.Text = "Times are whole milliseconds (0 or more).";
            return;
        }

        string skill = (SkillKey.Text ?? string.Empty).Trim();
        if (skill is not ("1" or "2" or "3"))
        {
            Saved.Text = "The skill is the wheel button 1, 2 or 3.";
            return;
        }

        var s = engine.Settings;
        s.Sequence.Loop = Loop.IsToggled;
        s.Sequence.RestartOnError = RestartOnError.IsToggled;
        s.Sequence.AutoReconnect = AutoReconnect.IsToggled;
        s.Phone.AttackMode = Enum.TryParse<PhoneAttackMode>(AttackMode.SelectedItem as string, out var mode) ? mode : PhoneAttackMode.Tap;
        s.Lod.ApproachBoss = LodApproach.IsToggled;
        s.Sequence.DryRun = DryRun.IsToggled;
        s.Dungeon.SkillKey = skill;
        s.Dungeon.HoldSkillMs = hold;
        s.Lod.LootMs = loot;
        s.Lod.BuffEveryMs = buff;
        s.Phone.AttackTapEveryMs = Math.Clamp(tapEvery, 50, 2000);
        engine.Save();
        Saved.Text = $"Saved {DateTime.Now:HH:mm:ss}.";
    }

    private static bool TryMs(Entry entry, out int value) =>
        int.TryParse((entry.Text ?? string.Empty).Trim(), out value) && value >= 0;
}

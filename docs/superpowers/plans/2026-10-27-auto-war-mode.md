# Auto War Mode Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A third run mode, Auto War, that keeps an AFK alt in its cave, joins every Bohai Outpost (Adept) war, fights it, and takes the alt back to its cave and monster afterwards.

**Architecture:** New pure text rules (`WarText`, `TabTargetOptions`) with xUnit tests; a few new screen readers in `ScreenAnalyzer` checked by SelfTest probes over saved frames; three new `AutomationPilot` partials (`War`, `WarTimer`, `WarFight`) that add ticked phases; small, `IsWar`-gated hooks in the shared Laby/LOD code (settings visit, teleport, map pick, zone guard, 5 s screen check). The keys-out "Regular War" step is only renamed.

**Tech Stack:** .NET 10, C# 13, OpenCvSharp, Windows.Media.Ocr behind `IOcrEngine`, WPF (desktop only), xUnit.

**Spec:** `docs/superpowers/specs/2026-10-27-auto-war-design.md` (approved 2026-10-27). Read it first; section numbers below (§4.1 …) point into it.

## Global Constraints

- `src/WuxAuto.Core` stays OS-free: no WPF, WinRT, Win32/P-Invoke, `System.Windows.*`, registry (CLAUDE.md §3 RULE).
- Every game action is a ticked phase on the vision-loop thread (`PilotPhase` + `Advance`); never block, never `Thread.Sleep` longer than the existing helpers do.
- A held key is always stored in `_heldSkill` so `Advance` / `Stop` / F10 release it (`ReleaseHeldSkill`, via `ReleaseSweepKey`).
- With `Sequence.Mode` ≠ `AutoWar`, Laby and LOD behave exactly as before: every shared-code change is gated on `IsWar` or keeps the old value as its default.
- New regions go in `RegionSettings` (already `[JsonIgnore]`, reset per run). New settings go in a new `WarSettings` (`AppSettings.War`); no `SettingsStore.CurrentVersion` bump, no migration.
- Settings defaults are exactly the spec's §6 table: `Destination` "Bohai Outpost", `TribeRowText` "Bohai Outpost (Adept)", `LeadMs` 10000, `DialogWaitMs` 120000, `TabTargetOption` "Enemy worth CP", `BuffIcons` warbuff1…warbuff6, `XBuffIcon` "warbuff4", `BuffClickGapMs` 1000, `BuffEveryMs` 345000, `ShuraHoldMs` 1200, `TargetGoneMs` 2000, `GateText` "Gate is Open", `ListPage` 3, `ArrivedText` "Arrived", `ArriveWaitMs` 90000, `HomeMap` / `HomeMonster` "".
- Keys are reused, not duplicated: `Dungeon.SkillKey`, `Dungeon.CtrlSkill`, `Dungeon.AutohuntKey`, `Dungeon.TabKey`, `Lod.ShuraKey`, `Lod.BuffKey` (O), `Sequence.AutohuntSettingsKey` (S).
- Only template crops go into git. The user's Tribe Status / Menu screenshots and every saved debug frame stay local (`%LOCALAPPDATA%\WuxAuto\war-inputs`, `%LOCALAPPDATA%\WuxAuto\debug`).
- Never kill or overwrite a running `WuxAuto.exe` and never run `publish.ps1` (it refreshes `dist\WuxAuto\`, the user's live copy). If a build fails on a locked file, ask the user.
- Builds: `dotnet build .\WuxAuto.slnx` (0 warnings), `dotnet build -c Release .\WuxAuto.slnx` before the PR, `dotnet test tests\WuxAuto.Core.Tests`, and `dotnet build tests\WuxAuto.SelfTest` before any `--no-build` probe run (the slnx does not build it).
- Logs and comments follow CLAUDE.md §12: `Success` for a completed action, `Info` for a step, `Warning` for a surprise, `Error` only when the run stops. Snapshots use `DebugArtifacts.SaveFrame(frame, _overlays, "war_<what>")`, at most 3 per tag per Start.

## Review Focus

1. **A noisy countdown read** ("0O:O4:25", "00 : 04 : 25", the row and its time merged in one OCR line): it must still give 4 min 25 s, and a missing row must give "unknown", not zero. Tests: Task 2, `ParseCountdown_*` and `FindWarRow_*`.
2. **Something that is not a target in the target box**: the version/ping line, a system notice ("[PvP] (Snake) iMstrG [Cape Box].") or a number alone must not count as an enemy name. Tests: Task 3, `FindTargetName_*`.
3. **The wrong "0 / N" line read as death**: the "0/3000" bar under the HP bar, or the MP line, must never mean dead. Tests: Task 3, `ParseHp_*` and `IsDeadRead_*`.
4. **Two caves on page 3 sharing a prefix** (the minimap title is cut off): the pick must be the right entry or "ambiguous", never the wrong cave. Tests: Task 2, `BestListEntry_*`.
5. **An old "Arrived: <monster>" line still in the combat log** from an earlier trip: it must not count as arriving. Tests: Task 3, `NewLines_*` and `IsArrivedLine_*`.

---

## File Structure

| File | Responsibility |
| --- | --- |
| `src/WuxAuto.Core/Automation/WarText.cs` (new) | Pure text rules: countdown, Tribe Status row, war dialog line, page-3 entry pick, HP line, target name, "Arrived" line, autobuff scroll line, log diff |
| `src/WuxAuto.Core/Automation/TabTargetOptions.cs` (new) | The four TAB target options and how many chevron clicks reach one from another |
| `src/WuxAuto.Core/Automation/AutomationPilot.RegularWar.cs` (renamed from `AutomationPilot.War.cs`) | Keys-out "Regular War" step, behaviour unchanged |
| `src/WuxAuto.Core/Automation/AutomationPilot.War.cs` (new) | Auto War plumbing: `IsWar`, start dispatch, start checks, remember spot, page-3 list check, return trip, hunt on |
| `src/WuxAuto.Core/Automation/AutomationPilot.WarTimer.cs` (new) | ☰ → Tribe Status → countdown → wait → join (autohunt off, dialog OK) |
| `src/WuxAuto.Core/Automation/AutomationPilot.WarFight.cs` (new) | Inside Bohai: enter, TAB target visit, buffs, fight cycle, death watch, war over |
| `src/WuxAuto.Core/Automation/AutomationPilot.cs` | `PilotPhase` values, `PhaseText`, switch cases, `IsWar` hooks in shared code |
| `src/WuxAuto.Core/Core/AppSettings.cs` | `RunMode.AutoWar`, `WarSettings`, three new regions |
| `src/WuxAuto.Core/Core/SettingsStore.cs` | `Normalize` for `War` |
| `src/WuxAuto.Core/Vision/ScreenAnalyzer.cs` | `ToLines`, `ReadPlayerHpAsync`, `ReadTargetAsync`, `FindSkillBarIcons` |
| `src/WuxAuto/Assets/warbuff1.png` … `warbuff6.png`, `menubutton.png` (new) | Templates |
| `src/WuxAuto/MainWindow.xaml`, `src/WuxAuto/MiniWindow.xaml`, `src/WuxAuto/ViewModels/Converters.cs` | Third mode button, Auto War settings block |
| `tests/WuxAuto.Core.Tests/WarTextTests.cs`, `TabTargetOptionsTests.cs`, `WarSettingsTests.cs` (new) | Unit tests |
| `tests/WuxAuto.SelfTest/WarProbe.cs` (new), `Program.cs` | Probes `--hp`, `--target`, `--warbar`, `--menu`, `--tribe` |
| `README.md`, `CLAUDE.md`, `src/WuxAuto/Assets/QuickStart.md` | Docs |

---

### Task 1: Rename the keys-out step, add the mode and its settings

**Files:**
- Rename: `src/WuxAuto.Core/Automation/AutomationPilot.War.cs` → `src/WuxAuto.Core/Automation/AutomationPilot.RegularWar.cs` (`git mv`)
- Modify: `src/WuxAuto.Core/Automation/AutomationPilot.cs` (enum `PilotPhase` line 36, `PhaseText` line 284, switch case lines 692-693, caller line 4716), `src/WuxAuto.Core/Automation/AutomationPilot.Lod.cs:245`
- Modify: `src/WuxAuto.Core/Core/AppSettings.cs` (`RunMode` line 24, `AppSettings` line 74-79), `src/WuxAuto.Core/Core/SettingsStore.cs` (`Normalize`, next to the `s.Lod ??=` block at line 245)
- Test: `tests/WuxAuto.Core.Tests/WarSettingsTests.cs`

**Interfaces:**
- Produces: `RunMode.AutoWar = 2`; `public WarSettings War { get; set; } = new();` on `AppSettings`; `WarSettings` with the properties and defaults listed in Global Constraints (plus `MenuTileText` "Tribe Status", `IconMatchScore` 0.80); `PilotPhase.RegularWarOn` (was `AutoWar`); `BeginRegularWar(string why)`, `TickRegularWarAsync(Mat)`, `FinishRegularWar(...)` (renamed, bodies unchanged); `private bool IsWar => _settings.Sequence.Mode == RunMode.AutoWar;` in `AutomationPilot.War.cs` (new file holding only this for now).

- [ ] **Step 1: Write the failing test**

```csharp
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
```

`SettingsStore.Normalize` is `private static` today (SettingsStore.cs:195): make it `internal static` (Core's csproj already has `InternalsVisibleTo Include="WuxAuto.Core.Tests"`).

- [ ] **Step 2: Run it to make sure it fails**

Run: `dotnet test tests\WuxAuto.Core.Tests --filter WarSettingsTests`
Expected: FAIL to compile (`RunMode.AutoWar`, `AppSettings.War` do not exist).

- [ ] **Step 3: Rename the keys-out step**

`git mv` the file; rename `PilotPhase.AutoWar` → `RegularWarOn`, `BeginAutoWar` → `BeginRegularWar`, `TickAutoWarAsync` → `TickRegularWarAsync`, `FinishAutoWar` → `FinishRegularWar` at every use (AP:36, 284, 692-693, 4716; LOD:245). Text, log lines and snapshot tag `autowar_unticked` stay as they are.

- [ ] **Step 4: Add `RunMode.AutoWar = 2`, `WarSettings`, `AppSettings.War`, and its `Normalize` block**

Each `WarSettings` property gets a one-line `<summary>` saying what it is and that it is user-given or my call, as in the spec table. `Normalize`: `s.War ??= new WarSettings();`; trim every string; empty `Destination` / `TribeRowText` / `MenuTileText` / `TabTargetOption` / `GateText` / `ArrivedText` → their defaults; `BuffIcons` null or empty → the six defaults (trimmed, empties dropped); clamps: `LeadMs` 0–120000, `DialogWaitMs` 10000–600000, `BuffClickGapMs` 100–10000, `BuffEveryMs` 30000–3600000, `ShuraHoldMs` 0–10000, `TargetGoneMs` 500–30000, `ListPage` 1–20, `ArriveWaitMs` 5000–600000, `IconMatchScore` 0.3–1.0.

- [ ] **Step 5: Run the tests and the build**

Run: `dotnet test tests\WuxAuto.Core.Tests` then `dotnet build .\WuxAuto.slnx`
Expected: all tests PASS (9), build 0 warnings 0 errors.

- [ ] **Step 6: Commit**

```bash
git add -A src/WuxAuto.Core tests/WuxAuto.Core.Tests
git commit -m "Auto War: add the mode and its settings; rename the keys-out step to Regular War on"
```

---

### Task 2: War text rules — countdown, Tribe Status row, war dialog, page-3 entry, TAB target options

**Files:**
- Create: `src/WuxAuto.Core/Automation/WarText.cs`, `src/WuxAuto.Core/Automation/TabTargetOptions.cs`
- Test: `tests/WuxAuto.Core.Tests/WarTextTests.cs`, `tests/WuxAuto.Core.Tests/TabTargetOptionsTests.cs`

**Interfaces:**
- Produces (namespace `WuxAuto.Automation`):
  - `public readonly record struct WarCountdown(TimeSpan? Remaining, bool InProgress);`
  - `public static class WarText` with
    - `static TimeSpan? ParseCountdown(string text)` — the first `h:mm:ss` in the text, OCR-tolerant
    - `static bool IsInProgress(string text)`
    - `static WarCountdown? FindWarRow(IReadOnlyList<(string Text, PixelRect Bounds)> lines, string rowText)`
    - `static bool IsWarDialogLine(string line, string warKeyword)`
    - `static string? BestListEntry(string minimapTitle, IReadOnlyList<string> entries, out bool ambiguous)`
  - `public static class TabTargetOptions` with `static readonly string[] All = ["Default", "Nearest enemy", "Enemy worth CP", "Nearest monster"];`, `static int? IndexOf(string value)`, `static int? StepsTo(string current, string wanted)` (wanted index − current index; null when either is unrecognised; negative = left chevron clicks).

- [ ] **Step 1: Write the failing tests**

```csharp
public class WarTextTests
{
    static (string, PixelRect) L(string t, int x, int y, int w = 120, int h = 16) => (t, new PixelRect(x, y, w, h));

    [Theory]
    [InlineData("00:04:25", 265)]
    [InlineData("00 : 04 : 25", 265)]
    [InlineData("0O:O4:25", 265)]
    [InlineData("01:00:00", 3600)]
    [InlineData("Bohai Outpost (Adept) 00:04:25", 265)]
    public void ParseCountdown_ReadsNoisyTimes(string text, int seconds)
        => Assert.Equal(TimeSpan.FromSeconds(seconds), WarText.ParseCountdown(text));

    [Theory]
    [InlineData("In progress")]
    [InlineData("00:61:00")]
    [InlineData("")]
    public void ParseCountdown_RejectsNonTimes(string text) => Assert.Null(WarText.ParseCountdown(text));

    [Fact]
    public void FindWarRow_SeparateLinesOnOneRow()
    {
        var lines = new[] { L("NEXT WARS", 560, 212), L("Bohai Outpost (Adept)", 448, 241), L("00:04:25", 704, 241, 68),
                            L("Dragonfall Outpost", 448, 270), L("In progress", 694, 270, 78) };
        Assert.Equal(new WarCountdown(TimeSpan.FromSeconds(265), false), WarText.FindWarRow(lines, "Bohai Outpost (Adept)"));
    }

    [Fact]
    public void FindWarRow_MergedLineAndInProgress()
    {
        Assert.Equal(new WarCountdown(TimeSpan.FromSeconds(265), false),
            WarText.FindWarRow([L("Bohai Outpost (Adept) 00:04:25", 448, 241, 330)], "Bohai Outpost (Adept)"));
        Assert.Equal(new WarCountdown(null, true),
            WarText.FindWarRow([L("Bohai Outpost (Adept)", 448, 241), L("In progress", 694, 242, 78)], "Bohai Outpost (Adept)"));
    }

    [Fact]
    public void FindWarRow_MissingRowOrTimeIsNull()
    {
        Assert.Null(WarText.FindWarRow([L("Dragonfall Outpost", 448, 270), L("00:04:25", 704, 270, 68)], "Bohai Outpost (Adept)"));
        Assert.Null(WarText.FindWarRow([L("Bohai Outpost (Adept)", 448, 241)], "Bohai Outpost (Adept)"));
    }

    [Theory]
    [InlineData("[Bohai Outpost (Adept)] Confirm to move?", true)]
    [InlineData("[Bohai Outpost (Adept)] [10] minute(s) to opening of the entrance", false)]
    [InlineData("Fist Scroll Box Confirm to use?", false)]
    public void IsWarDialogLine(string line, bool expected) => Assert.Equal(expected, WarText.IsWarDialogLine(line, "Bohai"));

    [Fact]
    public void BestListEntry_CutTitlePicksTheEntry()
    {
        var entries = new[] { "[Dark Soul Cave]", "[Maya Cliff]", "[Coliseum]" };
        Assert.Equal("[Dark Soul Cave]", WarText.BestListEntry("[Dark Soul Ca", entries, out bool amb));
        Assert.False(amb);
    }

    [Fact]
    public void BestListEntry_NoisySuffixStillPicksTheLongerMatch()
    {
        var entries = new[] { "[Dark Soul Cave]", "[Dark Soul Cave X]" };
        Assert.Equal("[Dark Soul Cave X]", WarText.BestListEntry("[Dark Soul Cave X", entries, out bool amb));
        Assert.False(amb);
    }

    [Fact]
    public void BestListEntry_SharedPrefixIsAmbiguous()
    {
        var entries = new[] { "[Dark Soul Cave I]", "[Dark Soul Cave II]" };
        WarText.BestListEntry("[Dark Soul Cave IF", entries, out bool amb);
        Assert.True(amb);
    }

    [Fact]
    public void BestListEntry_NothingCloseIsNull()
        => Assert.Null(WarText.BestListEntry("[Piercing City]", ["[Dark Soul Cave]", "[Maya Cliff]"], out _));
}

public class TabTargetOptionsTests
{
    [Theory]
    [InlineData("Default", "Enemy worth CP", 2)]
    [InlineData("Nearest monster", "Enemy worth CP", -1)]
    [InlineData("Enemy worth CP", "Enemy worth CP", 0)]
    [InlineData("Nearest enemy", "Default", -1)]
    [InlineData("Enemy worth C", "Default", -2)]
    public void StepsTo(string current, string wanted, int steps) => Assert.Equal(steps, TabTargetOptions.StepsTo(current, wanted));

    [Fact] public void StepsTo_UnreadValueIsNull() => Assert.Null(TabTargetOptions.StepsTo("Sound", "Default"));
}
```

- [ ] **Step 2: Run them to make sure they fail**

Run: `dotnet test tests\WuxAuto.Core.Tests --filter "WarTextTests|TabTargetOptionsTests"`
Expected: FAIL to compile.

- [ ] **Step 3: Implement `WarText` (these five members) and `TabTargetOptions`**

- `ParseCountdown`: in each whitespace-free run of `[0-9Oo:lI|]`, map O/o→0 and l/I/|→1 after spaces round `:` are removed; first `(\d{1,2}):(\d{2}):(\d{2})` with minutes and seconds < 60.
- `IsInProgress`: `TextMatcher.Similarity(text, "In progress") >= 0.80` or the normalised text contains `"inprogress"`.
- `FindWarRow`: the row = the first line whose normalised text contains the normalised `rowText`, or whose `Similarity` to `rowText` ≥ 0.80. Its time = the rest of that line after the row text, else the lines whose vertical centre is within the row's height of the row's centre and which start right of it, left to right; the first that `ParseCountdown`s or `IsInProgress` decides. No row or no time → null.
- `IsWarDialogLine`: normalised line contains normalised `warKeyword` and `"confirm"`.
- `BestListEntry`: `t` = normalised title, `e` = normalised entry, `p` = their common-prefix length; score = `p / max(t.Length, e.Length)`; candidates need `p >= Math.Min(10, t.Length)` and score ≥ 0.75. Best score wins; `ambiguous` = a second candidate within 0.05 of the best (then still return the best).
- `TabTargetOptions.IndexOf`: the option with the highest `TextMatcher.Similarity` ≥ 0.75 (ties: the first). Keep `AutomationPilot.IsDefaultTabTarget` as is.

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests\WuxAuto.Core.Tests`
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add src/WuxAuto.Core/Automation/WarText.cs src/WuxAuto.Core/Automation/TabTargetOptions.cs tests/WuxAuto.Core.Tests
git commit -m "Auto War: text rules for the war timer, the war dialog, the page-3 entry and TAB target options"
```

---

### Task 3: Fight text rules — HP, target name, "Arrived", autobuff scroll, log diff

**Files:**
- Modify: `src/WuxAuto.Core/Automation/WarText.cs`
- Test: `tests/WuxAuto.Core.Tests/WarTextTests.cs`

**Interfaces:**
- Produces (on `WarText`):
  - `static (int Cur, int Max)? ParseHp(IReadOnlyList<(string Text, PixelRect Bounds)> lines)`
  - `static bool IsDeadRead((int Cur, int Max) hp, int? aliveMax)`
  - `static string? FindTargetName(IReadOnlyList<(string Text, PixelRect Bounds)> lines)`
  - `static bool IsArrivedLine(string line, string arrivedText, string monster)`
  - `static bool IsAutoBuffScrollLine(string line)`
  - `static IReadOnlyList<string> NewLines(IReadOnlyList<string> before, IReadOnlyList<string> after)`

- [ ] **Step 1: Write the failing tests** (append to `WarTextTests`)

```csharp
    [Fact]
    public void ParseHp_TopmostPairIsHp()
    {
        // 1920x1080 lod_no_entries frame: HP, MP, the item counter 10/10, the 0/3000 bar.
        var lines = new[] { L("9990 / 11467", 195, 61), L("42833 / 42833", 184, 34), L("10/10", 500, 50, 40), L("0/3000", 328, 110, 52) };
        Assert.Equal((42833, 42833), WarText.ParseHp(lines));
    }

    [Fact]
    public void ParseHp_ZeroAndOcrO()
    {
        Assert.Equal((0, 42833), WarText.ParseHp([L("0 / 42833", 184, 34), L("9990 / 11467", 195, 61)]));
        Assert.Equal((0, 42833), WarText.ParseHp([L("O / 42833", 184, 34)]));
        Assert.Null(WarText.ParseHp([L("[The Labyrinth (Adep", 1000, 70)]));
    }

    [Theory]
    [InlineData(0, 42833, 42833, true)]
    [InlineData(0, 3000, 42833, false)]    // the bar under the HP bar
    [InlineData(0, 11467, 42833, false)]   // MP read alone
    [InlineData(120, 42833, 42833, false)]
    [InlineData(0, 42833, null, true)]
    [InlineData(0, 300, null, false)]
    public void IsDeadRead(int cur, int max, int? aliveMax, bool dead) => Assert.Equal(dead, WarText.IsDeadRead((cur, max), aliveMax));

    [Fact]
    public void FindTargetName_NameAboveItsHpNumbers()
    {
        // 1273x1024 Laby frame: target bar "Spectral" with 9070530/47372000 under it, version line above.
        var lines = new[] { L("Ver: 0.2.492.1 — 10/25/2026 02:29:00", 675, 20, 330), L("Spectral", 630, 64, 70),
                            L("9070530/47372000", 590, 92, 140, 14), L("297", 890, 66, 30) };
        Assert.Equal("Spectral", WarText.FindTargetName(lines));
    }

    [Fact]
    public void FindTargetName_NoticesAndVersionLineAreNotTargets()
    {
        Assert.Null(WarText.FindTargetName([L("Ver: 0.2.492.1 — 10/24/2026 09:03:10", 1282, 20, 250), L("62ms — FPS: 41", 1610, 20, 120)]));
        Assert.Null(WarText.FindTargetName([L("[PvP] (Snake) iMstrG [Cape Box].", 833, 110, 250)]));
        Assert.Null(WarText.FindTargetName([L("297", 890, 66, 30), L("100/100", 880, 92, 60)]));
    }

    [Theory]
    [InlineData("Arrived: Cave Bat", "Cave Bat", true)]
    [InlineData("Arived: Cave Bat", "Cave Bat", true)]
    [InlineData("Arrived: Dark Spider", "Cave Bat", false)]
    [InlineData("Attack succeeded!Normal damage(+50)", "Cave Bat", false)]
    public void IsArrivedLine(string line, string monster, bool expected) => Assert.Equal(expected, WarText.IsArrivedLine(line, "Arrived", monster));

    [Theory]
    [InlineData("[Auto buff scroll]  02.11.2026", true)]
    [InlineData("[Auto buff scrol] 02.11.2026", true)]
    [InlineData("AUTO BUFF", false)]
    [InlineData("[Slayer Scroll] 171 (time(s))", false)]
    public void IsAutoBuffScrollLine(string line, bool expected) => Assert.Equal(expected, WarText.IsAutoBuffScrollLine(line));

    [Fact]
    public void NewLines_IsAMultisetDifference()
    {
        Assert.Equal(new[] { "arrivedcavebat" }, WarText.NewLines(["a", "b"], ["a", "b", "arrivedcavebat"]));
        Assert.Empty(WarText.NewLines(["arrivedcavebat", "a"], ["a", "arrivedcavebat"]));   // an old line moved up
        Assert.Equal(new[] { "a" }, WarText.NewLines(["a"], ["a", "a"]));
    }
```

- [ ] **Step 2: Run them to make sure they fail**

Run: `dotnet test tests\WuxAuto.Core.Tests --filter WarTextTests`
Expected: FAIL to compile.

- [ ] **Step 3: Implement the six members**

- `ParseHp`: per line, remove spaces, map O/o→0 next to digits or `/`, match `^\D{0,2}(\d{1,7})/(\d{2,7})\D{0,2}$`; keep pairs with `Max >= 100` and `Cur <= Max`; return the one with the smallest `Bounds.Y`.
- `IsDeadRead`: `Cur == 0` and (`aliveMax` null → `Max >= 1000`; else `|Max − aliveMax| <= aliveMax / 100`).
- `FindTargetName`: a name line has ≥ 3 letters after `TextMatcher.Normalize`, no `:` and is not a sentence (does not end with `.`); it counts only with a numbers line (`\d+\s*/\s*\d+`) whose top is below the name's bottom by at most 3 × the name's height and which overlaps it horizontally. First such name, top to bottom, trimmed.
- `IsArrivedLine`: with `a` = normalised `arrivedText` (length L) and `n` = normalised line, some start offset 0–2 and prefix length L−1, L or L+1 of `n` is within `TextMatcher.Levenshtein` 1 of `a` ("arived" passes for "arrived"); the rest of `n` after that prefix contains the normalised `monster` or has `Similarity` ≥ 0.80 to it.
- `IsAutoBuffScrollLine`: normalised text contains `"autobuff"` and the rest contains `"scro"`.
- `NewLines`: lines of `after` not used up by an equal line of `before` (counts per string), in `after`'s order.

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests\WuxAuto.Core.Tests`
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add src/WuxAuto.Core/Automation/WarText.cs tests/WuxAuto.Core.Tests/WarTextTests.cs
git commit -m "Auto War: text rules for HP, the target name, Arrived and the autobuff scroll"
```

---

### Task 4: Templates, screen readers and probes

**Files:**
- Create: `src/WuxAuto/Assets/warbuff1.png` … `warbuff6.png`, `src/WuxAuto/Assets/menubutton.png`, `tests/WuxAuto.SelfTest/WarProbe.cs`
- Modify: `src/WuxAuto.Core/Core/AppSettings.cs` (`RegionSettings`), `src/WuxAuto.Core/Vision/ScreenAnalyzer.cs`, `tests/WuxAuto.SelfTest/Program.cs`

**Interfaces:**
- Consumes: `WarText.ParseHp`, `WarText.FindTargetName`, `WarText.FindWarRow` (Tasks 2-3).
- Produces:
  - `RegionSettings.PlayerHp = new(0.0, 0.0, 0.30, 0.10)`, `RegionSettings.TargetBox = new(0.25, 0.03, 0.50, 0.12)`, `RegionSettings.MenuButton = new(0.30, 0.0, 0.40, 0.08)` (each with a summary naming the frames it was measured on).
  - `public static IReadOnlyList<(string Text, PixelRect Bounds)> ScreenAnalyzer.ToLines(IReadOnlyList<OcrWord> words)` — one entry per `LineBounds`, top to bottom.
  - `public async Task<(int Cur, int Max)?> ReadPlayerHpAsync(Mat frame)` — OCR of `PlayerHp` at ×2.0 → `ParseHp`.
  - `public async Task<(string? Name, TemplateMatch? Chest)> ReadTargetAsync(Mat frame)` — OCR of `TargetBox` at ×1.5 → `FindTargetName`; chest = `FindIcon(frame, "monstericon", new NormRect(0.25, 0, 0.50, 0.16).ToPixels(...), 0.80)`.
  - `public IReadOnlyList<(string Template, TemplateMatch? Match)> FindSkillBarIcons(Mat frame, IReadOnlyList<string> templates, double minScore)` — per template the best `FindIcons(..., Regions.Hotbar, minScore, 4)` hit (score, then leftmost), null when missing.

- [ ] **Step 0: Probe baseline before any vision change** (Tasks 1-3 touch no vision code)

Run in the background: `.\tests\probe-baseline.ps1 -Out "$env:TEMP\war-base-before"` (~12-14 min, exits 1 on a probe failure). It reads the live `settings.json` and `templates\` overrides, so do not change either until Step 5's second run.

- [ ] **Step 1: Add the templates**

Copy `%LOCALAPPDATA%\WuxAuto\war-inputs\1.png` … `6.png` to `src/WuxAuto/Assets/warbuff1.png` … `warbuff6.png` (same order; 4 = the bluish-white X buff). Crop the ☰ button from `%LOCALAPPDATA%\WuxAuto\debug\20261024_170342_166_lod_no_entries.png` (1920×1080, button ≈ 1155–1212 × 3–55 — confirm on the frame with `dotnet run --project .\tests\WuxAuto.SelfTest -- --frame <png> --crop 1140 0 90 64 4` and adjust so the crop is the button's dark square and its three bars only) into `src/WuxAuto/Assets/menubutton.png`, e.g.:

```powershell
Add-Type -AssemblyName System.Drawing
$b = [Drawing.Bitmap]::FromFile("$env:LOCALAPPDATA\WuxAuto\debug\20261024_170342_166_lod_no_entries.png")
$b.Clone([Drawing.Rectangle]::new(1155, 3, 57, 52), $b.PixelFormat).Save("$PWD\src\WuxAuto\Assets\menubutton.png")
```

- [ ] **Step 2: Add the regions and the four `ScreenAnalyzer` members** (signatures above)

- [ ] **Step 3: Add `WarProbe` and its `Program.cs` blocks**

`WarProbe.Hp(dir)`, `Target(dir)`, `WarBar(dir)`, `Menu(dir)` (☰ via `FindIconAnyScale(frame, "menubutton", MenuButton, 0.30)` and, when the frame shows the menu, the box of a "Tribe Status" OCR hit), `TribeAsync(path)` (file or folder; whole-frame OCR → `ToLines` → `FindWarRow(lines, settings.War.TribeRowText)`). One `Array.IndexOf(args, "--hp" | "--target" | "--warbar" | "--menu" | "--tribe")` block each, in the style of `--shura` (Program.cs:85-87), each printing one line per PNG: file name, client size, the read value or "not found", score/box/ms where there is one.

- [ ] **Step 4: Run the probes over the saved frames**

```powershell
dotnet build tests\WuxAuto.SelfTest
$d = "$env:LOCALAPPDATA\WuxAuto\debug"
dotnet run --no-build --project .\tests\WuxAuto.SelfTest -- --hp $d     > "$env:TEMP\war-hp.txt"
dotnet run --no-build --project .\tests\WuxAuto.SelfTest -- --target $d > "$env:TEMP\war-target.txt"
dotnet run --no-build --project .\tests\WuxAuto.SelfTest -- --menu $d   > "$env:TEMP\war-menu.txt"
dotnet run --no-build --project .\tests\WuxAuto.SelfTest -- --warbar $d > "$env:TEMP\war-bar.txt"
dotnet run --no-build --project .\tests\WuxAuto.SelfTest -- --tribe "$env:LOCALAPPDATA\WuxAuto\war-inputs\7.webp"
```

Expected (record the real numbers in the commit message and CLAUDE.md):
- `--hp`: in-game frames read `cur / max` with `cur == max` (e.g. `20261026_042225_228_lod_no_entries.png` → 42833 / 42833); no frame reads `0 / …` (none shows a death); login / covered frames → not found. Fix `PlayerHp` or the parse if a frame with a visible HUD does not read.
- `--target`: `chevron_s*` / `rowclick_s*` / `attack_s*` frames with a boss targeted → its name (e.g. "Sanctified Unicorn", "Spectral"); town frames without a target → not found. No frame reads the version line, a chat line or a notice as a name.
- `--menu`: ☰ found ≥ 0.80 on every in-game frame at 1024, 1273 and 1920 wide; set the threshold the pilot uses (`MenuButtonScore` const in `AutomationPilot.WarTimer.cs`, Task 6) just under the lowest real hit and above every non-game frame.
- `--warbar`: the saved frames are the main character's bar, so most `warbuffN` read below 0.80; any hit ≥ 0.80 must be the real icon (check the box on the frame) — otherwise raise `IconMatchScore`'s default.
- `--tribe`: `Bohai Outpost (Adept)` → 00:04:25 (265 s). If OpenCV cannot read `.webp`, convert it first (`ffmpeg -i 7.webp 7.png`, in the same local folder).

- [ ] **Step 5: Laby/LOD parity of the shared vision code**

Run `.\tests\probe-baseline.ps1 -Out "$env:TEMP\war-base-after"` in the background, then `git diff --no-index "$env:TEMP\war-base-before" "$env:TEMP\war-base-after"`.
Expected: the only differences are added lines for the new templates (`warbuff1`…`warbuff6`, `menubutton`) where a probe lists every template (`--frame`); no existing line changes. Anything else is a regression in Laby/LOD vision — fix before committing.

- [ ] **Step 6: Build, tests, commit**

Run: `dotnet build .\WuxAuto.slnx`, `dotnet test tests\WuxAuto.Core.Tests` → 0 warnings, all PASS.

```bash
git add src/WuxAuto/Assets/warbuff*.png src/WuxAuto/Assets/menubutton.png src/WuxAuto.Core tests/WuxAuto.SelfTest
git commit -m "Auto War: buff and menu templates, HP / target / skill bar readers, probes"
```

---

### Task 5: Shared-code hooks (settings visit, autohunt on, map pick, teleport, zone guard, 5 s check)

**Files:**
- Modify: `src/WuxAuto.Core/Automation/AutomationPilot.cs`, `src/WuxAuto.Core/Automation/AutomationPilot.Lod.cs:42`, `src/WuxAuto.Core/Automation/AutomationPilot.War.cs`

**Interfaces:**
- Consumes: `TabTargetOptions.StepsTo` (Task 2), `IsWar` (Task 1).
- Produces (all `private`, on `AutomationPilot`):
  - Settings visit parameters: `string? _visitTabTarget`, `bool _visitSkipFont`, `PilotPhase _visitNext`, set by `void BeginSettingsVisit(string? tabTarget, bool skipFont, PilotPhase next)` (calls `Advance(PilotPhase.FontSize, 200)` after resetting `_fontStep = 0`). `ResetRunState` sets the Laby values: `_visitTabTarget = Sequence.TabTargetDefault ? "Default" : null`, `_visitSkipFont = false`, `_visitNext = PilotPhase.OpenInventory`.
  - `Task<bool> EnsureAutohuntAsync(Mat frame, string where, bool wantOn, bool trustVisibleLine)`; `EnsureAutohuntOffAsync(frame, where, trust)` becomes `=> EnsureAutohuntAsync(frame, where, false, trust)`; `void ResetAutohuntCheck()` (clears `_huntOffPresses`, `_huntOffPressedAt`, `_huntOffBaseline`).
  - Map pick: `string? MapPickName` (War: `_warMapVerify ? _warPendingMonster : _settings.War.HomeMonster`; else `Stages[_stageIndex]` as today), `PilotPhase AfterMapPick` (War verify → `WarRemember`, War → `WarArrive`, else `WaitBoss`), fields `bool _warMapVerify`, `string? _warPendingMonster`, `string? _warPendingMap`, `bool _warMonsterConfirmed`.
  - `string WarHomeMap => _warPendingMap ?? _settings.War.HomeMap;` and `Destination` → `IsWar ? WarHomeMap : IsLod ? … : …`.
  - Teleport list check: field `bool _warListCheck`; here only the first line of `TickFindDestinationAsync`: `if (_warListCheck) { await TickWarListCheckAsync(frame); return; }`.
  - **Stubs**, so every task builds on its own: in `AutomationPilot.War.cs`, under a `// stubs - replaced by Tasks 6-8` comment, `Task TickWarListCheckAsync(Mat frame)`, `void BeginWarReturn(Mat? frame, string why)`, `void WarStop(Mat? frame, string why)` (replaced in Task 7), `void BeginWarJoin(string why)` (Task 6) and `void BeginWarEnter(string why)` (Task 8), each `=> Fail(null, $"Auto War step not built yet: {why}")` (the Task stub for `TickWarListCheckAsync` returns `Task.CompletedTask` after the `Fail`). The task that builds the real method deletes its stub; after Task 8 the comment and no stub are left.

- [ ] **Step 1: Settings visit takes a wanted TAB option, an optional font step and a next phase**

In `TickFontSizeAsync`: `AfterFont()` tests `_visitTabTarget != null` instead of `Sequence.TabTargetDefault`; `FontStep` calls `AfterFont()` at once when `_visitSkipFont`; the `default:` case ends with `Advance(_visitNext, 300)`; in `TabStep` replace the `IsDefaultTabTarget` test and the left click with: `int? steps = TabTargetOptions.StepsTo(value, _visitTabTarget!)`; `steps == 0` → done (log "already <wanted>" / add `$"TAB target {_visitTabTarget}"` to `_settingsChanges`); `steps < 0` or (`steps == null` and wanted is "Default" — today's behaviour) → click `row.Left`; `steps > 0` → click `row.Right`; a null chevron, `steps == null` for any other wanted value, or `_tabClicks >= 5` → the existing warning with the wanted value in place of "Default". Detail text and logs say the wanted value. `AfterTownChecks` (AP:892-902) keeps entering `FontSize` exactly as before (its condition `TabTargetDefault` maps to `_visitTabTarget != null`).

- [ ] **Step 2: `EnsureAutohuntAsync` with `wantOn`**

Same body as today with `on == wantOn` / `visible == wantOn` as success and the opposite state as "tap again"; log wording uses "on"/"off" from `wantOn`; the "still reads" snapshot tag becomes `wantOn ? "autohunt_off" : "autohunt_on"`; `autohunt_unread` stays. Existing callers are unchanged.

- [ ] **Step 3: Map pick by name**

`TickReadStageAsync` / `TickAdvanceStageAsync`: `expected` comes from `MapPickName` (null → `Advance(AfterMapPick, 300)`), the "other stage names" loop (AP:2667-2677) runs only `!IsWar`, every `Advance(PilotPhase.WaitBoss, …)` in these two methods becomes `Advance(AfterMapPick, …)`, and the `dungeon.Stages.Count == 0` early exit applies only `!IsWar`. Verify mode (`_warMapVerify`): in `TickReadStageAsync`, once the name reads (`IsFullName`) with the map open, do not click the row: click the map button again to close it (`mapicon` in `Regions.MapIcon`, as `TickClickMapAsync` finds it), set `_warMonsterConfirmed = true`, `Advance(PilotPhase.WarRemember, 700)`; every exit of `TickReadStageAsync` that would `Fail` while `_warMapVerify` instead calls `WarStop(frame, $"'{expected}' is not in this map's monster list - target a monster of this cave and press Start again")` (declared in Task 7; stub here). `TickClickMapAsync`'s next phase: `IsWar || Dungeon.Enabled ? ReadStage : Done`.

- [ ] **Step 4: Teleport and load**

`NextAfterLoad()`: `_returning ? Done : IsWar ? PilotPhase.WarBack : …` (rest unchanged). Add the `_warListCheck` first line to `TickFindDestinationAsync` (Interfaces above). `Destination` gets the `IsWar` branch.

- [ ] **Step 5: 5 s whole-screen check and zone guard**

In `TickAsync`: `cancelDialogs` is also false when `IsWar`; the block runs when `IsWar` too, and after the reconnect check: `if (IsWar && _phase is PilotPhase.WarWait or PilotPhase.WarRemember && ScreenAnalyzer.ToLines(screenWords).Any(l => WarText.IsWarDialogLine(l.Text, _settings.War.Destination))) { BeginWarJoin("the war dialog is up"); return; }`. Zone guard (AP:543-565): before the LOD branch, `if (IsWar) { if (inTown) BeginWarReturn(frame, $"in {HomeTown} during {_phase}"); else { Log warning "In '{title}' instead of {Destination} during {_phase} - starting over."; ResetRunState(); Advance(PilotPhase.Focus, 1500); } return; }`. Add the new `PilotPhase` values now (`WarRemember, WarWait, WarJoin, WarEnter, WarBuff, WarFight, WarDead, WarBack, WarArrive, WarHuntOn`) with `PhaseText` entries ("remembering the cave and monster", "waiting for the Bohai war", "joining the war", "entering Bohai Outpost", "buffing", "fighting in the war", "dead - waiting to revive", "teleporting back to the cave", "walking to the monster", "turning the autohunt on"); switch cases are added with their tick methods in Tasks 6-8.

- [ ] **Step 6: Build and tests**

Run: `dotnet build .\WuxAuto.slnx`, `dotnet test tests\WuxAuto.Core.Tests`
Expected: 0 warnings, all PASS. Then re-read the diff of `AutomationPilot.cs` with `IsWar == false` in mind: every changed line must reduce to today's behaviour (record that check in the commit message).

- [ ] **Step 7: Commit**

```bash
git add src/WuxAuto.Core/Automation
git commit -m "Auto War: hooks in the shared pilot code (TAB option, autohunt on, map pick by name, teleport, zone guard)"
```

---

### Task 6: War timer and join

**Files:**
- Create: `src/WuxAuto.Core/Automation/AutomationPilot.WarTimer.cs`
- Modify: `src/WuxAuto.Core/Automation/AutomationPilot.cs` (switch cases `WarWait`, `WarJoin`)

**Interfaces:**
- Consumes: `WarText.FindWarRow`, `WarText.IsWarDialogLine`, `ScreenAnalyzer.ToLines`, `FindConfirmDialogAsync`, `FindCloseButtonRightOf`, `EnsureAutohuntAsync`, `ResetAutohuntCheck`.
- Produces: `void BeginWarWait(string why)` (→ `WarWait`, timer step 0, `_warAt = null`), `void BeginWarJoin(string why)` (→ `WarJoin`; delete its Task 5 stub), fields `long? _warAt` (TickCount64 of the war start), `long _warNextReadAt`, `int _warTimerStep`, `int _warTimerFails`, `bool _warJoinHuntOff`, `const double MenuButtonScore` (from Task 4's probe).

- [ ] **Step 1: `TickWarWaitAsync(Mat frame)`** — the §4.2 loop as sub-steps of `_warTimerStep`:
  - **0 idle:** `_detail` = "waiting for the Bohai war in mm:ss - back to {HomeMap} / {HomeMonster}" (or "timer not read yet"). Every 10 s read the minimap title: home town → `BeginWarReturn(frame, "sent to town while waiting")`; `ZoneMatches(title, War.Destination)` → `BeginWarEnter("already in Bohai Outpost")` (Task 7). When `_warAt − now <= LeadMs` → `BeginWarJoin("the war starts in 10 s")`. When `now >= _warNextReadAt` → step 1.
  - **1 open menu:** ☰ (`menubutton` ≥ `MenuButtonScore` in `Regions.MenuButton`) → click → step 2, 900 ms. Not found 3× → snapshot `war_menu_missing`, `_warNextReadAt = now + 120000`, step 0.
  - **2 open Tribe Status:** whole-frame `FindTextAsync(frame, full, War.MenuTileText)` with `CoversMost` → click → step 3, 1200 ms. Missing after 3 reads → close the menu (step 4) and count a fail.
  - **3 read:** whole-frame OCR ×1.0 → `ToLines` → `FindWarRow(lines, War.TribeRowText)`. Countdown → `_warAt = now + remaining`; `_warNextReadAt` = `_warAt − 120000` when more than 3 min away (then also no later than `now + 15 min`), else `long.MaxValue`; log `Info` "Next Bohai war in hh:mm:ss (at HH:mm:ss PC time)". In progress → `_warAt = null`, `_warNextReadAt = now + 5 min`, log once. Null → `_warTimerFails++`; 3 in a row → snapshot `tribe_unread`, `_warNextReadAt = now + 2 min`. → step 4.
  - **4 close:** OCR finds the "TRIBE STATUS" title → click `FindCloseButtonRightOf(frame, title)`; else the "MENU" title → its close button; neither → step 0. One click per tick, 700 ms apart, at most 4 clicks, each decided on a fresh frame.
- [ ] **Step 2: `TickWarJoinAsync(Mat frame)`** — §4.3:
  - First `EnsureAutohuntAsync(frame, "before the war", wantOn: false, trustVisibleLine: false)` until true (`ResetAutohuntCheck()` in `BeginWarJoin`).
  - Then poll ≤ every 700 ms: `FindConfirmDialogAsync(frame, Sequence.AutoConfirmText, Sequence.AutoConfirmButton)` whose `Message` passes `WarText.IsWarDialogLine(msg, War.Destination)` → click `Button`, log `Success`, snapshot `war_dialog` (≤ 3), `BeginWarEnter("clicked OK on the war dialog")`.
  - No dialog by `(_warAt ?? joinStart) + DialogWaitMs` → warning, `ResetAutohuntCheck()`, then `EnsureAutohuntAsync(..., wantOn: true, ...)` until true, then `BeginWarWait("no war dialog")` with `_warNextReadAt = now`.
  - Dry run: log what it would click, do not click.
- [ ] **Step 3: Build** — `dotnet build .\WuxAuto.slnx` → 0 warnings.
- [ ] **Step 4: Commit**

```bash
git add src/WuxAuto.Core/Automation
git commit -m "Auto War: read the Bohai war timer from Tribe Status and join the war"
```

---

### Task 7: Start checks, remembering the spot, the trip back

**Files:**
- Modify: `src/WuxAuto.Core/Automation/AutomationPilot.War.cs`, `src/WuxAuto.Core/Automation/AutomationPilot.cs` (`TickCheckZoneAsync` first lines, `Start`, `ResetRunState`, switch cases `WarRemember`, `WarBack`, `WarArrive`, `WarHuntOn`)

**Interfaces:**
- Consumes: `WarText.BestListEntry`, `WarText.IsArrivedLine`, `WarText.NewLines`, `ScreenAnalyzer.ReadTargetAsync`, `FindSkillBarIcons`, map pick (Task 5), `BeginWarWait` (Task 6), `BeginWarEnter` (Task 8).
- Produces: `Task TickWarCheckZoneAsync(Mat frame)`, `Task TickWarListCheckAsync(Mat frame)`, `Task TickWarRememberAsync(Mat frame)`, `void BeginWarReturn(Mat? frame, string why)`, `Task TickWarBackAsync(Mat)`, `Task TickWarArriveAsync(Mat)`, `Task TickWarHuntOnAsync(Mat)`, `void WarStop(Mat? frame, string why)` (warning + snapshot `war_stop` + `Stop(why)`) — these replace their Task 5 stubs; field `string? _warTitle` (the minimap title read at Start); per-Start fields `bool _warSpotChecked`, `int _warRefusedTeleports`, `Dictionary<string,int> _warShots` (snapshot counts per tag).

- [ ] **Step 1: Start dispatch** — first lines of `TickCheckZoneAsync`: `if (IsWar) { await TickWarCheckZoneAsync(frame); return; }`. `TickWarCheckZoneAsync` reads the minimap title and decides (§4.1, §5):
  - Bohai (`ZoneMatches(title, War.Destination)`) → `BeginWarEnter("Start inside Bohai Outpost")`.
  - home town (`TownIn`) → `HomeMap` set → `BeginWarReturn(frame, "Start in town")`; else `WarStop(frame, "Auto War must be started inside a cave on page 3 of the teleport scroll")`.
  - Labyrinth / LOD destination / unreadable title (3 reads, 1 s apart) → same `WarStop`.
  - otherwise (a cave): keep the title in `_warTitle`; **skill bar** `FindSkillBarIcons(frame, [.. War.BuffIcons, Lod.ShuraTemplate], War.IconMatchScore)` — any missing → `WarStop` naming them ("warbuff2, shura"); empty `Lod.ShuraKey` → `WarStop("Set the Shura key ...")`. Then: `_warSpotChecked` and `ZoneMatches(title, HomeMap)` with `HomeMonster` set → `BeginWarWait("back in the cave")`; else `_warPendingMap = null`, `_warListCheck = true`, `_returning = false`, `Advance(PilotPhase.OpenInventory, 300)` (the teleport phases then page to page 3 and call `TickWarListCheckAsync`).
- [ ] **Step 2: `TickWarListCheckAsync`** — `_pages + 1 < War.ListPage` → `Advance(PilotPhase.FindChevron, RetryDelayMs)`; else OCR `Regions.Ocr` → `ToLines` → entries = lines that start with `[` → `BestListEntry(title, entries, out ambiguous)` (title = the one read in Step 1, kept in `_warTitle`). Close the list first (the "TELEPORT SCROLL" title → `FindCloseButtonRightOf`; check on the next frame that the title is gone; never click an entry), then: null → `WarStop("'{title}' is not on page {ListPage} of the teleport scroll")`; ambiguous → `WarStop` listing the two best; else `_warPendingMap = entry`, `_warListCheck = false`, `Advance(PilotPhase.WarRemember, 500)`.
- [ ] **Step 3: `TickWarRememberAsync`** — Tab (≤ 3, 1 s apart) until `ReadTargetAsync` gives a `Name` → `_warPendingMonster = name`, `_warMapVerify = true`, `Advance(PilotPhase.ClickMap, 200)`. After the map step returns with `_warMonsterConfirmed`: `War.HomeMap = _warPendingMap`, `War.HomeMonster = _warPendingMonster`, `SettingsStore.Save(_settings)`, clear the pending fields and `_warMapVerify`, `_warSpotChecked = true`, log `Success` "Remembered {map} / {monster}", `BeginWarWait("spot remembered")`. No name after 3 Tabs → `WarStop("No monster targeted - let the autohunt target one and press Start again")`.
- [ ] **Step 4: `BeginWarReturn` / `TickWarBackAsync`** — `BeginWarReturn`: `ReleaseSweepKey()`, log `Info` why, `_returning = false`, `_warListCheck = false`, `_pages = 0`, `Advance(PilotPhase.OpenInventory, 1500)` (the teleport finds `Destination` = `HomeMap` and its load step goes to `WarBack`). `TickWarBackAsync`: minimap title `ZoneMatches(title, HomeMap)` → `_warRefusedTeleports = 0`, `Advance(PilotPhase.ClickMap, 500)`; still in a home town after 60 s → `_warRefusedTeleports++`; 2 → `WarStop("Could not go back to {HomeMap}")`, else `BeginWarReturn(frame, "the teleport did not go through")`; neither for 60 s → `Fail(frame, ...)` (restart path).
- [ ] **Step 5: `TickWarArriveAsync` / `TickWarHuntOnAsync`** — on entering `WarArrive` take the baseline: normalised lines of `Regions.AttackMessage` (OCR ×1.5). Each 700 ms: `NewLines(baseline, now)` contains a line with `IsArrivedLine(line, War.ArrivedText, HomeMonster)` → log `Success`, `ResetAutohuntCheck()`, `Advance(WarHuntOn)`; after `ArriveWaitMs` → warning + snapshot `war_noarrive`, same advance. `TickWarHuntOnAsync`: `EnsureAutohuntAsync(frame, "at the monster", wantOn: true, trustVisibleLine: false)` until true → `BeginWarWait("hunting again")` with `_warNextReadAt = now`.
- [ ] **Step 6: Per-Start / per-run state** — `Start` clears `_warSpotChecked`, `_warShots`, `_warRefusedTeleports`; `ResetRunState` clears `_warListCheck`, `_warMapVerify`, `_warMonsterConfirmed`, pending fields, `_warTimerStep`. Add the four switch cases.
- [ ] **Step 7: Build** — `dotnet build .\WuxAuto.slnx` → 0 warnings.
- [ ] **Step 8: Commit**

```bash
git add src/WuxAuto.Core/Automation
git commit -m "Auto War: start checks, remember the cave and monster, teleport back and hunt"
```

---

### Task 8: Inside Bohai — enter, TAB target, buffs, fight, death, war over

**Files:**
- Create: `src/WuxAuto.Core/Automation/AutomationPilot.WarFight.cs`
- Modify: `src/WuxAuto.Core/Automation/AutomationPilot.cs` (switch cases `WarEnter`, `WarBuff`, `WarFight`, `WarDead`)

**Interfaces:**
- Consumes: `BeginSettingsVisit` (Task 5), `ReadPlayerHpAsync`, `ReadTargetAsync`, `FindSkillBarIcons`, `WarText.IsDeadRead`, `WarText.IsAutoBuffScrollLine`, `TapSkill`, `ReleaseHeldSkill`, `_heldSkill`, `BeginWarReturn`.
- Produces: `void BeginWarEnter(string why)` (delete its Task 5 stub, and the empty stub comment), `Task TickWarEnterAsync(Mat)`, `Task TickWarBuffAsync(Mat)`, `Task TickWarFightAsync(Mat)`, `Task TickWarDeadAsync(Mat)`, `Task<bool> WarZoneCheckAsync(Mat frame)` (true = the phase may act this tick), fields `bool? _warHasAutoBuff`, `int _warBuffStep`, `long _warBuffedAt`, `bool _warStarted`, `int? _warAliveMax`, `int _warFightStep`, `long _warStepAt`, `long _warTargetSeenAt`, `long _warHpUnreadSince`.

- [ ] **Step 1: `WarZoneCheckAsync`** — every 2 s, only with `_input.IsTargetForeground`: title in a home town → `BeginWarReturn(frame, "the war is over")`, false; `ZoneMatches(title, HomeMap)` → `Advance(PilotPhase.ClickMap, 500)`, false; another zone read 3× → snapshot `wrong_zone`, `BeginWarReturn(...)`, false; Bohai or unread → true. Called first in `WarBuff`, `WarFight`, `WarDead`, and in `WarEnter` after arrival.
- [ ] **Step 2: `BeginWarEnter` / `TickWarEnterAsync`** — `BeginWarEnter` resets `_warStarted`, `_warHasAutoBuff = null`, `_warBuffStep`, `_warAliveMax` (keeps it if already known this Start) and advances to `WarEnter`. Tick: wait for the minimap title to match `War.Destination` (60 s → `Fail`, restart path); then `BeginSettingsVisit(War.TabTargetOption, skipFont: true, next: PilotPhase.WarBuff)`.
- [ ] **Step 3: `TickWarBuffAsync`** — §4.4 as `_warBuffStep` sub-steps, each on a fresh frame:
  - **0 autobuff known?** `_warHasAutoBuff` set → step 3. Item list (`new NormRect(0.62, 0.50, 0.38, 0.50)`, OCR ×1.5 then ×2.0) has a line `IsAutoBuffScrollLine` → true, step 3. The list shows ≥ 3 lines but none → step 1 (ask S). Nothing readable → step 1.
  - **1 press S** (`Sequence.AutohuntSettingsKey`, ≤ 3 presses 2.5 s apart): whole-screen OCR has the AUTO BUFF title (a line `== "autobuff"` or `Levenshtein <= 1`, as `TickRegularWarAsync` reads it) → `_warHasAutoBuff = true`, click its `FindCloseButtonRightOf`, step 2; a "Regular War" line but no AUTO BUFF → `_warHasAutoBuff = false`, step 2; nothing after 3 presses → `_warHasAutoBuff = false`, warning + snapshot `war_autobuff_unread`, step 3.
  - **2 close the autohunt settings:** a "Regular War" line still shows → press S once more (one press per 1.2 s, ≤ 2), then step 3.
  - **3 buff:** autobuff → `KeyTap(Lod.BuffKey)`; else for each `War.BuffIcons` in order, one per `BuffClickGapMs`, click its `FindSkillBarIcons` hit (missing → warning once per war, skip). Then `_warBuffedAt = now`, `_warBuffStep = 0`, `Advance(PilotPhase.WarFight, 300)`.
- [ ] **Step 4: `TickWarFightAsync`** — §4.5, sub-steps of `_warFightStep`:
  - **every tick, first:** HP (`ReadPlayerHpAsync`, ≤ once per 1 s): a read with `Cur > 0` sets `_warAliveMax = Max`; `IsDeadRead(hp, _warAliveMax)` twice in a row → `ReleaseHeldSkill()`, log `Warning` "Died in the war", `Advance(PilotPhase.WarDead, 500)`; unread for 10 s → snapshot `war_hp_unread` (≤ 3) and carry on as alive. `now − _warBuffedAt >= BuffEveryMs` and step is 0 → `_warBuffStep = 3` (autobuff state kept), `Advance(WarBuff)`.
  - **0 Tab:** `KeyTap(Dungeon.TabKey)`, step 1, 700 ms.
  - **1 target?** `ReadTargetAsync` → a name or the chest → `_warStarted = true`, `_warTargetSeenAt = now`, click the `XBuffIcon` hit (skip silently if missing), step 2. Else: not `_warStarted` → whole-screen `FindTextAsync(full, War.GateText)` every 2nd poll with `CoversMost` → `_warStarted = true`; step 0 after 1 s.
  - **2 Shura hold:** `_input.KeyDown(Lod.ShuraKey)`, `_heldSkill = Lod.ShuraKey`, `_warStepAt = now + ShuraHoldMs`, step 3.
  - **3 release:** at `_warStepAt` → `ReleaseHeldSkill()`, step 4.
  - **4 attack:** every `Lod.SkillEveryMs` (300) `TapSkill(Dungeon.SkillKey)`; every 700 ms `ReadTargetAsync`: any name or chest → `_warTargetSeenAt = now`; `now − _warTargetSeenAt >= TargetGoneMs` → step 0.
  - `_detail` names the step, the target and the next rebuff.
- [ ] **Step 5: `TickWarDeadAsync`** — HP read every 1 s; `Cur > 0` → log `Info` "Back on our feet", `_warBuffStep = 3` (rebuff all, autobuff state kept), `Advance(PilotPhase.WarBuff, 1000)`; still dead after 30 s → snapshot `war_dead` (≤ 3 per Start), keep waiting; `WarZoneCheckAsync` handles a move to town.
- [ ] **Step 6: Build** — `dotnet build .\WuxAuto.slnx` → 0 warnings; `dotnet test tests\WuxAuto.Core.Tests` → all PASS.
- [ ] **Step 7: Commit**

```bash
git add src/WuxAuto.Core/Automation
git commit -m "Auto War: inside Bohai - TAB target, buffs, Shura + skill fight, death watch, war over"
```

---

### Task 9: UI, docs, final verification, PR

**Files:**
- Modify: `src/WuxAuto/MainWindow.xaml` (lines 125-130 radio buttons; a new "Auto War" grid after the Dungeon block, ~line 413), `src/WuxAuto/MiniWindow.xaml` (lines 19-23), `src/WuxAuto/ViewModels/Converters.cs:49-53` (`OptionLists.RunModes` gets `RunMode.AutoWar`), `README.md`, `CLAUDE.md`, `src/WuxAuto/Assets/QuickStart.md`

- [ ] **Step 1: Mode buttons** — a third `RadioButton` "Auto War" (`ConverterParameter=AutoWar`, tooltip: "Bohai Outpost war: wait in a page-3 cave, join every war, fight, come back to the same cave and monster.") in MainWindow, and "War" in the Mini window (tooltip "Auto War").
- [ ] **Step 2: Settings block** — a "Auto War" `TextBlock` section title with a 4-column grid: "Shura hold (ms)" → `Settings.War.ShuraHoldMs`, "Rebuff every (ms)" → `Settings.War.BuffEveryMs`, "Lead time (ms)" → `Settings.War.LeadMs`, "TAB target" → `Settings.War.TabTargetOption`, and a read-only line "Back to: {HomeMap} / {HomeMonster}" (`TextBlock` bound one-way). Tooltips say what each does in one sentence.
- [ ] **Step 3: Render the window** — `dotnet run --project .\tests\WuxAuto.UiShot -- "$env:TEMP\war-ui.png" --settings --bottom` and `-- "$env:TEMP\war-mini.png" --mini`; look at both PNGs: three mode buttons, no clipped text, the Auto War block readable at the default and the minimum window size. (UiShot restores `settings.json` afterwards.)
- [ ] **Step 4: Docs** — `README.md`: an "Auto War" section (what it does, how to start it in the cave, the skill-bar requirement, what is not verified yet). `QuickStart.md`: three lines on starting Auto War. `CLAUDE.md`: §1 mention of the third mode, §3 repo map (new partials, `WarText`, `TabTargetOptions`, probes), §2 the five probe commands, a §4.8 "Auto War" section summarising the phases with the probe numbers from Task 4 and the "Not seen live" list from the spec §9, §6 the `WarSettings` table, and the rename of the keys-out step in §4.5b.
- [ ] **Step 5: Final verification**

```powershell
dotnet build .\WuxAuto.slnx                     # 0 warnings
dotnet build -c Release .\WuxAuto.slnx          # the release build the package uses
dotnet test tests\WuxAuto.Core.Tests            # all pass
dotnet build tests\WuxAuto.SelfTest
dotnet run --no-build --project .\tests\WuxAuto.SelfTest   # synthetic self test still passes
```

Then a dry run against nothing live: start the Debug exe **only if no `WuxAuto.exe` is running** (`Get-Process WuxAuto -ErrorAction SilentlyContinue` must be empty — never stop the user's copy), pick Auto War with Dry run ticked and no game bound, and check that it reports the missing bound window instead of acting. A live war run is the user's (spec §9).

- [ ] **Step 6: Self-review loop** (global CLAUDE.md) — review the whole branch diff as someone else's PR, fix, re-read, repeat until a pass finds nothing; dispatch one fresh reviewer for the whole branch.
- [ ] **Step 7: Commit, push, PR**

```bash
git add -A
git commit -m "Auto War: mode buttons, settings block, docs"
git push -u origin worktree-auto-war-mode
gh pr create --title "Auto War mode" --body-file <body.md>   # body: what it does, how it was verified, the spec's "not verified live" list; ends with the Claude Code line
```

Print the PR URL. Do not run `publish.ps1` or `release.ps1`.

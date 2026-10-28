# WuxAuto - quick start

WuxAuto plays the **[The Labyrinth (Adept)]** run in Wuxen2 for you: teleport in, clear the 4
stages, go to the 5th boss (Sacred Sky Lord), kill it, teleport back to your home town, repeat.
Everything it does is drawn on its preview and written to its log.

## Before you start

* Windows 10 (2004 or newer) or Windows 11, 64-bit. Nothing else to install.
* Recommended game settings:

  | Setting | Value |
  | --- | --- |
  | Resolution | Any (tested best at **1024 x 768**) |
  | Display mode | **Windowed** (optimized for it; not exclusive fullscreen) |
  | Settings > VISUAL > Interface Scale | **Auto** |
  | Settings > VISUAL > Font Size | **110 - 120 %** (the pilot checks it on start and fixes it if outside) |
  | Settings > VISUAL > TAB target | **Default** for Laby / LOD (the pilot checks it on start and sets it); Auto War sets **Enemy worth CP** itself |
  | Skill bar | the **Run** skill on it (the pilot turns it on in town before every teleport - a ring round the icon = on; icon not found -> it presses the "Run key", default `W`, once) |

* In the game: a
  **teleport scroll** in the inventory with The Labyrinth (Adept) available, and **skill 1** on the
  key `1` (used to attack the last boss). Every attack press is `1` followed by `Ctrl+1` (setting
  "Ctrl + skill key too", on by default; untick it for the plain key only).
* Tick **Remember me** on the login screen: after a disconnect the pilot logs back in by clicking
  through the screens, it never types a password.
* Start in your **home town** - Piercing City, Molten City or Wandering City (or in the Labyrinth with a boss targeted - it picks up from there).

## Three modes (the Mode buttons above Start)

* **AutoLaby** - The Labyrinth (Adept): 4 stages + Sacred Sky Lord, back to your home town, repeat.
* **AutoLod** - Ultimate Den of Rebirth: turns south, walks a little forward to 1199, 1782 and waits there,
  pressing Tab until the boss **Bai Gu Great** shows (untick "Walk to the boss spot (LOD)" to stay where
  the teleport put you), then spams skill `1` until it is dead (it auto-buffs with `O` on arrival and every ~5 min). After the first hit of
  each fight it stops attacking, Ctrl+clicks the pink ring round the boss's feet to walk up to
  it, then attacks again (setting "Walk up to the boss (LOD)"); after that it checks every 3 s and walks
  back to the ring whenever the character is off it. The attack is stopped with **Shura**:
  its icon is found on the skill bar and clicked 3 times; not found -> the "Shura key (LOD)" (default
  `E`, set it to the slot you put Shura in); empty -> Space as before. When the boss says "see you in hell" (or the boss's treasure icon is gone for 2 s) it loots with the `` ` `` key for 10 s, without pressing Tab.
  The boss respawns, so it keeps fighting until the map's timer runs out;
  then the game sends you to your home town and the pilot teleports back in. Killed by the boss, it turns south again at the start of the map and presses Tab.
  Out of keys is noticed at the teleport: LOD not on page 2 of the teleport list -> AutoLaby; still
  in town 2 minutes after a Labyrinth teleport (setting "Laby key wait") -> AutoLod; both out -> opens the autohunt settings (S),
  closes the AUTO BUFF dialog if it is in the way, ticks Regular War, presses J (the game's auto war)
  and stops.
  New and not well tested yet.

* **AutoWar** - Bohai Outpost tribe wars for an alt. Start it **inside the cave** you want to hunt in (the cave must be on
  **page 3** of the teleport scroll, the autohunt on). It reads the war timer from Menu > Tribe Status, joins every war,
  buffs and fights (a player is a target when its name or its HP numbers show in the target box; with no one to Tab
  for 5 s once the war has started it walks toward the nearest player name on screen, 6 steps at a time and 18 per
  war at most), then teleports back to the same cave and monster. Not tested live yet.
  The buff icons and Shura are optional: in Settings > Auto War > **Buffs** untick the buffs this character does not have
  (Buff 1-5; Weapon 1 (sword), Weapon 2, Weapon 3 - a character uses one weapon, so usually only one of these; Item 1-2,
  items on the skill bar that are clicked with every rebuff, auto-buff too); a ticked one missing from the bar is only a
  warning. No Shura key = no Shura hold in the fight.
  Started inside Bohai Outpost it fights right away; with no cave saved yet it stops after that war.
  Update every WuxAuto on the PC before choosing it: an older copy cannot read the Auto War setting and resets all settings
  (and does not know the Buffs check boxes).

## Run it

1. Unzip the folder anywhere and start `WuxAuto.exe`. Windows may warn about an unknown app:
   *More info > Run anyway*.
2. Start the game and log in, then in WuxAuto pick `Wuxen2.exe` in the list and press **Bind**.
3. Press **Start** (or **F9** with the game in front). Tick **Loop** to keep running.
4. **Keep the game window in front** - the pilot only sends keys and clicks to the game while it
   is the active window, so it never types into anything else. If you switch away it waits.
5. **F10** stops everything immediately.
   **Mini** (next to Stop) shrinks WuxAuto to a small always-on-top bar (bottom left of the
   screen; Settings > Mini mode > Position picks another spot) with Laby / LOD / War, Start and Stop; **Full** brings the big window back.
6. **Restart on error** (on): after a problem the pilot starts over by itself, up to 5 times in 30 min.

## Did it loot?

Every boss kill saves two plain pictures to `%LOCALAPPDATA%\WuxAuto\kills`: one when the boss dies,
one when the pilot moves on - LOD after the 10 s loot (`..._looted`), Labyrinth about 3 s later, just
before the map or the inventory opens (`..._after`). The **Kill screenshots** button opens the folder.
Only the newest 500 pictures are kept - copy any you want to keep somewhere else.

**Kill chime:** when a boss dies (Labyrinth: every stage; LOD: after the 10 s loot) WuxAuto plays a
chime and shows a Windows notification - a reminder to go and check the loot. The last one also
shows under the status. **Test alert** (under Loop) plays it now so you can check the volume; the
**Kill chime** / **Kill notification** boxes turn them off. Want another sound? **Choose...** next to
*Sound:* picks any MP3, WAV or OGG file (keep it short - it plays in full on every kill); **Default** goes
back to the built-in chime.

## Updates

WuxAuto checks for a new version every time it starts (top of the window: version, **Check for
updates**). When there is one it asks first; **Yes** downloads it, closes WuxAuto, replaces the files
and opens it again. Your settings stay. Stop the pilot before updating. Keep the WuxAuto folder
somewhere you can write to (Desktop, Documents - not Program Files). New versions are also listed
at https://github.com/atherosxd/WuxAuto/releases.

## If something goes wrong

* **F11** saves a picture of what the pilot sees to `%LOCALAPPDATA%\WuxAuto\debug`.
* Logs: `%LOCALAPPDATA%\WuxAuto\logs` (one file per start). Sending the latest log plus the
  pictures from the debug folder is the quickest way to get a problem fixed.
* The app does not start at all: run `Diagnose.cmd` in the same folder and send `diagnose.txt`.
* Settings are kept in `%LOCALAPPDATA%\WuxAuto\settings.json`; delete it to go back to defaults.

# BeastHelper

Dalamud API 15 / .NET 10 plugin prototype for Beastmaster navigation.

## Installation via Custom Repository

Add this custom repository URL in Dalamud's custom plugin repositories:

```text
https://raw.githubusercontent.com/Rin-0617/RinPlugins/main/pluginmaster.json
```

After adding the repository, search for `BeastHelper` in Dalamud Plugin Installer
and install it from the custom repository entry. Rin's other plugins, such as
RaidFlow and PartySieve, are available from the same URL.

If you added the old `https://raw.githubusercontent.com/Rin-0617/BeastHelper/main/beasthelper.json`
URL, replace it with the URL above. With both registered, BeastHelper appears twice.

Release downloads are served from GitHub Releases:

```text
https://github.com/Rin-0617/BeastHelper/releases
```

## 闘獣練 (Crucible of the Unbroken) Assist

Optional, off-by-default automation for the Beastmaster-only "Crucible of the
Unbroken" solo duty. Every toggle lives on the 闘獣練 tab in the main window
and defaults to off; nothing here runs unless explicitly enabled.

What is implemented:

- Auto-approaches and talks to the entry NPC in Central Shroud, confirms
  participation, and (as a separate toggle) selects the configured board on
  `XBMStageList`.
- Hands combat off to RotationSolverReborn: tracks one nearby enemy at a
  time, walks to it, and enables RSR's Auto mode once close so it handles
  target and action selection.
- Can tune BossModReborn's own movement-only autorotation module (on top of
  the existing dodge on/off toggle) to keep melee range while dodging and
  keep a safety margin from danger zones.
- Auto-uses the duty's own "魔獣回復薬" recovery item below a configurable
  HP threshold.
- Auto-picks the first item on the mid-board treasure screen, closes the
  shop screen without buying anything, advances the post-battle result
  screen, and claims all loot.
- Can be capped to a fixed number of entry→clear cycles, after which it
  turns itself back off.

What stays manual:

- Movement from one board tile to the next — the tile-trigger points aren't
  detectable from a distance, so a reactive/recorded-route approach couldn't
  be made reliable enough.
- The whistle/beast-party loadout screen and the "戦闘開始" (start battle)
  button.

All of the above is unattended movement, NPC interaction, UI interaction,
and (where noted) combat automation — against the FFXIV ToS, use at your own
discretion.

## Development Status

What is implemented:

- Loads the patch-day `XBMPet` sheet through Lumina raw rows.
- Resolves pet display names from `Pet`.
- Reads user-provided enemy destinations from `beast-destinations.json`.
- Includes built-in Beastmaster map coordinates for the field entries.
- Stores target enemy names and enemy level ranges for built-in entries.
- Mirrors the in-game monster note ("魔物図鑑") capture state into `Configuration.TamedPetRowIds`, kept separate from the user's manual "not needed" list (`MarkedPetRowIds`). The primary source is `XBMManager.IsPetUnlocked`, read directly from game memory — no need to have `XBMMonsterNotebook` open, and auto-sync runs in the background every 5 s by default. The old addon-scrape (reading `XBMMonsterNotebook`'s `AtkValue` grid while it's open) is kept as a fallback: each grid cell is an 8-value `AtkValue` group anchored by a `String` "No.&lt;n&gt;"; the value just before the anchor is the portrait icon id, captured entries have a unique portrait, and uncaptured entries all share one "locked" placeholder icon (`242051`). The grid only feeds the current page (25 rows), so `/beasthelper sync` folds each page into the list and self-validates the running total against the "&lt;captured&gt;/&lt;total&gt;" string once every page has been seen.
- Sends movement requests through vnavmesh IPC and calls Mount Roulette (general action 9) after teleporting to a destination in another zone.

What still needs game-side confirmation:

- Whether `XBMMonsterNotebook`'s category / element filters change the AtkValue feed in a way that breaks the contiguous-page assumption (filtered pages are merged, never cleared).
- The `XBMNoteModule` / `XBMModule` layout — `WriteFile` currently returns 0 bytes or an unloaded-looking size, so the module path is diagnostic only.
- Exact terrain height for map-only coordinates. The built-in source stores player-visible map X/Y; BeastHelper converts them to world X/Z and uses Y=0 for vnavmesh.

Commands:

- `/beasthelper` opens the main window (always starts closed on login).
- `/beasthelper sync` folds the monster-note page(s) currently shown into `TamedPetRowIds`.
- `/beasthelper autosync` toggles periodic (every 5 s) automatic 図鑑 sync while logged in. On by default.
- `/beasthelper dumpnote` logs the monster-note addons' `AtkValue` arrays and node trees to `/xllog`.
- `/beasthelper debug` opens the 図鑑 sync diagnostics window.
- `/beasthelper reload` reloads Lumina rows and destination JSON.
- `/beasthelper stop` stops the current vnavmesh path.
- `/beasthelper resetnote` clears `TamedPetRowIds` so the next auto-sync re-derives it from `XBMManager` from scratch.

Destination file:

`beast-destinations.json` is created in the plugin config directory. Manual entries override the built-in coordinates. Add entries like:

```json
{
  "destinations": [
    {
      "petRowId": 48,
      "xbmRowId": 2,
      "mapId": 4,
      "mapX": 23.4,
      "mapY": 16.3,
      "enemyLevelMin": 1,
      "enemyLevelMax": 2,
      "targetNames": [ "スクウィレル" ],
      "radius": 7.5,
      "label": "example",
      "source": "manual"
    }
  ]
}
```

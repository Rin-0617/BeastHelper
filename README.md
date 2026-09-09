# BeastHelper

Dalamud API 15 / .NET 10 plugin prototype for Beastmaster navigation.

## Installation via Custom Repository

Add this custom repository URL in Dalamud's custom plugin repositories:

```text
https://raw.githubusercontent.com/Rin-0617/BeastHelper/main/beasthelper.json
```

After adding the repository, search for `BeastHelper` in Dalamud Plugin Installer
and install it from the custom repository entry.

The repository metadata is kept in `beasthelper.json`. Release downloads are served from
GitHub Releases:

```text
https://github.com/Rin-0617/BeastHelper/releases
```

Current release:

- Version: `0.1.0.0`
- Tag: `v0.1.0`
- Artifact: `BeastHelper-v0.1.0.zip`
- Author: `Rin`
- Repository owner: `Rin-0617`

## Development Status

What is implemented:

- Loads the patch-day `XBMPet` sheet through Lumina raw rows.
- Resolves pet display names from `Pet`.
- Reads user-provided enemy destinations from `beast-destinations.json`.
- Includes built-in Beastmaster map coordinates for the field entries.
- Stores target enemy names and enemy level ranges for built-in entries.
- Mirrors the in-game monster note ("魔物図鑑") capture state into `Configuration.TamedPetRowIds`, kept separate from the user's manual "not needed" list (`MarkedPetRowIds`). While `XBMMonsterNotebook` is open, each grid cell is an 8-value `AtkValue` group anchored by a `String` "No.&lt;n&gt;"; the value just before the anchor is the portrait icon id. Captured entries have a unique portrait, uncaptured entries all share one "locked" placeholder icon (`242051`). The grid only feeds the current page (25 rows), so `/beasthelper sync` folds each page into the list and self-validates the running total against the "&lt;captured&gt;/&lt;total&gt;" string once every page has been seen.
- Sends movement requests through vnavmesh IPC and calls Mount Roulette (general action 9) after teleporting to a destination in another zone.

What still needs game-side confirmation:

- Whether `XBMMonsterNotebook`'s category / element filters change the AtkValue feed in a way that breaks the contiguous-page assumption (filtered pages are merged, never cleared).
- The `XBMNoteModule` / `XBMModule` layout — `WriteFile` currently returns 0 bytes or an unloaded-looking size, so the module path is diagnostic only.
- Exact terrain height for map-only coordinates. The built-in source stores player-visible map X/Y; BeastHelper converts them to world X/Z and uses Y=0 for vnavmesh.

Commands:

- `/beasthelper` opens the main window (always starts closed on login).
- `/beasthelper sync` folds the monster-note page(s) currently shown into `TamedPetRowIds`.
- `/beasthelper autosync` toggles periodic (every 5 s) automatic 図鑑 sync while logged in.
- `/beasthelper dumpnote` logs the monster-note addons' `AtkValue` arrays and node trees to `/xllog`.
- `/beasthelper debug` opens the 図鑑 sync diagnostics window.
- `/beasthelper reload` reloads Lumina rows and destination JSON.
- `/beasthelper stop` stops the current vnavmesh path.

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

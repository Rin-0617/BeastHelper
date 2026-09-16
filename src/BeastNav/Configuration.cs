using Dalamud.Configuration;

namespace BeastNav;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 2;

    public bool Fly { get; set; }

    public float StopDistance { get; set; } = 7.5f;

    // --- BossModReborn dodge hand-off ------------------------------------------
    // EXPERIMENTAL. Toggles BMR's own movement-only autorotation module
    // (NormalMovement) via IPC — BeastHelper never computes danger geometry or
    // picks a destination itself, it just asks BMR to move. Only does anything
    // useful while BMR has a module loaded for the current fight. In-combat
    // movement automation (against the FFXIV ToS); off by default.
    public bool BossModDodgeEnabled { get; set; }

    // --- Crucible auto-entry (the whole NPC → board → battle → loot loop) -----
    // EXPERIMENTAL. One master switch covering the unattended loop: walks to
    // and talks to the Crucible entry NPC (Central Shroud), selects the
    // configured board and confirms it once XBMStageList is open, and
    // auto-clicks "すべて入手" (plus its Yes/No confirmation) on the
    // post-battle loot screen (XBMContentsBooty). Never picks a content
    // variant beyond the configured board, a beast loadout, or a route
    // within a board.
    // Unattended movement, NPC interaction, and UI interaction (against the
    // FFXIV ToS); off by default.
    public bool AutoEnterCrucibleEnabled { get; set; }

    // --- Crucible combat hand-off -----------------------------------------------
    // EXPERIMENTAL. Once a hostile enemy is nearby, walks the character to it
    // (sticking to that one tracked enemy — see CrucibleCombatService's own
    // remarks for why this replaces the earlier, removed approach-movement
    // that re-picked "nearest" every tick and was unstable) and enables
    // RotationSolverReborn's own Auto mode via its chat command so it picks
    // targets and uses actions; disables RSR and stops moving again once no
    // enemy is tracked. Force-using "ひきつけろ" at low HP was also tried and
    // stays removed (unreliable in practice) — BeastHelper never chooses
    // rotation settings, positionals, or actions itself here; that stays
    // entirely RSR's job once in range.
    // Unattended movement and combat automation (against the FFXIV ToS); off
    // by default, and only does anything once AutoEnterCrucibleEnabled is
    // also on. Requires RotationSolverReborn installed and enabled; does
    // nothing (safely) otherwise.
    public bool AutoCombatEnabled { get; set; }

    // --- Crucible auto-entry loop count ------------------------------------------
    // How many entry→クリア→戦利品受取→NPC前に戻る cycles AutoEnterCrucibleEnabled
    // runs before CrucibleLoopService turns it back off by itself. Counted off
    // CrucibleEntryService.NpcDetected transitioning to true again — the entry
    // NPC being genuinely found once more, not just a screen closing — since a
    // run isn't really over until the character is actually back in front of
    // it. The very first detection only marks the start of loop 1 (nothing to
    // complete yet); the second detection closes loop 1, the third closes
    // loop 2, and so on.
    // 0 = unlimited — loops forever until turned off by hand, which was the
    // only behavior before this field existed, so old configs (where this is
    // absent and defaults to 0) keep working unchanged. 1 = run once, no repeat.
    // The completed count resets whenever AutoEnterCrucibleEnabled transitions
    // from off to on, so re-enabling by hand always starts a fresh count.
    public int CrucibleLoopCount { get; set; }

    // --- Crucible in-duty recovery item auto-use --------------------------------
    // EXPERIMENTAL. Once the player's HP drops below AutoRecoveryItemHpThreshold
    // while fighting inside a Crucible board, auto-uses the duty's own
    // "魔獣回復薬" (a content-distributed item, not a normal inventory potion) via
    // its dedicated hotbar-slot mapping. Ported from BOCXIV/Beastmaster-JP's
    // BeastmasterCrucibleItemService. Never touches any other item.
    // Unattended item use (against the FFXIV ToS); off by default, and only
    // does anything once AutoEnterCrucibleEnabled is also on.
    public bool AutoRecoveryItemEnabled { get; set; }

    public float AutoRecoveryItemHpThreshold { get; set; } = 30f;

    // --- Crucible board auto-select ---------------------------------------------
    // EXPERIMENTAL. Was removed entirely after two real game crashes were
    // traced to the board-select callback firing the instant XBMStageList
    // became visible (see CrucibleBoardService's remarks). Reintroduced with
    // a fixed delay (CrucibleBoardService.BoardSelectDelay, currently 2s)
    // after the list becomes visible before firing once, on the theory that
    // the list's own data settling was part of the original problem — this
    // is unverified, so kept as its own toggle rather than folded back into
    // AutoEnterCrucibleEnabled.
    // Unattended UI interaction (against the FFXIV ToS); off by default, and
    // only does anything once AutoEnterCrucibleEnabled is also on.
    public bool AutoSelectBoardEnabled { get; set; }

    // Which board CrucibleBoardService auto-selects (via the matching entry
    // in CrucibleBoardScripts) whenever XBMStageList is open, as long as
    // AutoEnterCrucibleEnabled and AutoSelectBoardEnabled are both on.
    // Chosen from the 闘獣練 tab's dropdown.
    public string SelectedCrucibleBoard { get; set; } = "第一盤";

    // --- Crucible per-board Lua scripts ----------------------------------------
    // EXPERIMENTAL. User-authored MoonSharp Lua scripts, one per board
    // variant. Whichever one matches SelectedCrucibleBoard is run by
    // CrucibleBoardService once XBMStageList (the board-select screen) has
    // been visible for BoardSelectDelay (see LuaAutomationService for what a
    // script can call). Lets board-specific click sequences be captured and
    // edited without hardcoding a C# branch per board.
    // Unattended UI/game interaction (against the FFXIV ToS); off unless
    // AutoEnterCrucibleEnabled and AutoSelectBoardEnabled are both on.
    //
    // XBMStageList (the board-select list) confirms a highlighted row via
    // callback("XBMStageList", 1, N) where N is the 1-based board number —
    // confirmed for boards 1-3 (第一盤/第二盤/第三盤) by recording an actual
    // manual selection with LuaRecorderService. 特一盤/特二盤 (N=4,5) follow
    // the same numbering by extrapolation only — not yet confirmed the same
    // way, so their entries carry a "未検証" Lua comment as a standing
    // reminder before enabling 盤面を自動選択 on them.
    //
    // That opens the beast-party screen (XBMPetParty), where the 10-beast
    // roster and the per-fight 3-whistle loadout both still need to be
    // picked by hand: the game's own "おすすめ編成" (recommend) button
    // optimizes for the fight rather than for which beasts still need
    // rank-up XP, and hand-picking specific beasts safely wasn't possible
    // (the natural click target turned out to be a Collision/FocusNode,
    // which crashed the game when its event was replayed without real
    // AtkEventData) — so this stops at board selection and leaves the rest
    // of that screen, plus "戦闘開始" (its own separate toggle), to the
    // player.
    public Dictionary<string, string> CrucibleBoardScripts { get; set; } = new()
    {
        ["第一盤"] = "callback(\"XBMStageList\", 1, 1)",
        ["第二盤"] = "callback(\"XBMStageList\", 1, 2)",
        ["第三盤"] = "callback(\"XBMStageList\", 1, 3)",
        ["特一盤"] = "-- 未検証: 実際に選択して確認できていません\ncallback(\"XBMStageList\", 1, 4)",
        ["特二盤"] = "-- 未検証: 実際に選択して確認できていません\ncallback(\"XBMStageList\", 1, 5)",
    };

    // --- Manual "I don't need this pet" list (user controlled) ---------------
    // Toggled from the pet list. Never written by the bestiary sync.
    public List<uint> MarkedPetRowIds { get; set; } = [];

    // When set, the pet list hides entries that are captured in the 図鑑 or
    // manually marked.
    public bool HideCompletedPets { get; set; }

    // --- Bestiary ("図鑑") capture state, synced from the game ---------------
    // Populated by BeastTamingStateService. Treat as read-only mirror of the
    // in-game monster note; editing by hand will be overwritten on the next sync.
    // On by default: the XBMManager source makes this cheap and it needs no
    // open 魔物図鑑.
    public bool AutoSyncBeastNote { get; set; } = true;

    public List<uint> TamedPetRowIds { get; set; } = [];

    // Addon names that render the monster-note list. Used by the bestiary sync
    // to scrape capture state while the window is open.
    public List<string> BeastNoteAddonCandidates { get; set; } =
    [
        "XBMMonsterNotebook",
        "XBMMonsterList",
        "XBMMonsterBook",
        "XBMNote",
        "XBMNotebook",
    ];

}

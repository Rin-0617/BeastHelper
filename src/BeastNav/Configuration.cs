using BeastNav.Models;
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

    // --- AutoDuty full hand-off (experimental) -----------------------------------
    // EXPERIMENTAL. When on, BeastHelper's own Crucible entry/board/team/
    // whistle/treasure/shop/result/loot automation all stay off (they'd
    // fight AutoDuty for the same UI — double-clicking buttons, racing
    // FireCallbacks) and this plugin instead just calls
    // AutoDuty.Run(territoryType, loops) via IPC once, handing the whole
    // board loop to AutoDuty's own generic duty-path-replay engine — the
    // same one that solves board-tile movement, which this plugin
    // deliberately doesn't implement itself (see CrucibleAutoDutyIpcService's
    // own remarks). Only the genuinely independent, read-mostly services
    // (図鑑/level sync, BMR dodge) keep running regardless. Requires AutoDuty
    // installed with its own Crucible support already set up on its side.
    // Unattended UI interaction and movement (against the FFXIV ToS); off by
    // default, and only does anything once AutoEnterCrucibleEnabled is also on.
    public bool CrucibleAutoDutyModeEnabled { get; set; }

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

    // --- Crucible mid-board treasure screen (XBMContentsTreasure) ---------------
    // EXPERIMENTAL. Whenever the treasure screen opens, reads all 4 offered
    // items' full effect text (confirmed via NearbyObjectDumpService/Addon
    // Inspector: each card is a top-level Button Component Node; its
    // multi-line effect description lives one component deeper inside it —
    // both read-only, no risk) and compares it against this list in order.
    // The first keyword (by list order) that appears on ANY of the 4 cards
    // wins; among cards matching that same keyword, the one earlier in
    // CrucibleTreasureService's own NodeId order is picked. If nothing in
    // this list matches any of the 4 cards (including when all 4 are
    // "このアイテムは獲得済みです。" — already obtained, nothing useful shown),
    // it clicks "終了" and takes nothing, rather than guessing.
    // One keyword per line, most-preferred first, matched as a plain
    // substring against the raw displayed text (e.g. "被ダメージ", "最大HP",
    // "クリティカル").
    // Unattended UI interaction (against the FFXIV ToS); only does anything
    // once AutoEnterCrucibleEnabled is on. An empty list always clicks 終了.
    public List<string> CrucibleTreasureKeywords { get; set; } = [];

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

    // EXPERIMENTAL. Whenever off its shared cooldown, auto-uses one of the
    // duty's own self-buff items (回避の書/反射の書/時の砂/魔獣剛力薬) via the
    // same hotbar-slot mapping as the recovery item, in a fixed priority
    // order. This is a simple "use it the moment it's available" policy —
    // no attempt is made to time it against an incoming attack or a damage
    // window. Item ids and the mechanism itself ported from
    // BOCXIV/Beastmaster-JP's BeastmasterCrucibleItemService.
    // Unattended item use (against the FFXIV ToS); off by default.
    public bool AutoSelfBuffItemsEnabled { get; set; }

    // EXPERIMENTAL. Same as AutoSelfBuffItemsEnabled, but for the duty's
    // offensive items used on the current hostile target (吸血鬼の牙 and the
    // six elemental 牙) — tries them in a fixed priority order, not matched
    // against the target's actual elemental weakness (that data isn't wired
    // up yet). Does nothing without a live, targetable enemy.
    // Unattended item use (against the FFXIV ToS); off by default.
    public bool AutoOffensiveItemsEnabled { get; set; }

    // EXPERIMENTAL. User-defined "if these conditions hold, use this item"
    // rules (see Models/CrucibleItemRule.cs), evaluated every tick in list
    // order before the blanket AutoSelfBuffItemsEnabled/AutoOffensiveItemsEnabled
    // policies — the first enabled rule whose conditions are satisfied and
    // whose item is actually usable wins. A scaled-down port of
    // BOCXIV/Beastmaster-JP's own rule engine (condition evaluation only,
    // no skill actions — items only). Unattended item use (against the
    // FFXIV ToS); off by default, and does nothing with an empty rule list.
    public bool AutoCrucibleItemRulesEnabled { get; set; }

    public List<CrucibleItemRule> CrucibleItemRules { get; set; } = [];

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

    // Per-beast level/exp, opportunistically read from the 図鑑 detail panel
    // ("XBMMonsterBookDetail") whenever it happens to be open — never
    // actively opened or paged through, so a beast never viewed in detail
    // has no entry here even if already captured. Keyed by Pet row id, same
    // as TamedPetRowIds. Populated by BeastTamingStateService.TickDetailPanel,
    // ported (as a read pattern, not code) from BOCXIV/Beastmaster-JP's own
    // passive notebook-progress watcher.
    public Dictionary<uint, BeastProgress> BeastLevelProgress { get; set; } = [];

    // EXPERIMENTAL. Whenever 魔獣手帳 (XBMMonsterNotebook) transitions from
    // closed to open, automatically pages through both of its pages and
    // selects each of the 50 entries in turn (a small, semantic
    // ReceiveEvent call to the notebook's own agent — the same technique
    // BeastmasterPetPartyService uses to add a captured beast, not a raw UI
    // click) to read every beast's level/exp into BeastLevelProgress without
    // the player needing to hover each row by hand. Ported (as a technique,
    // not code) from BOCXIV/Beastmaster-JP's own BeastmasterNotebookSyncService.
    // Unattended UI interaction (against the FFXIV ToS); off by default.
    public bool AutoSyncBeastLevelEnabled { get; set; }

    // --- Crucible pet-party (呼び笛) auto-formation -----------------------------
    // EXPERIMENTAL. Whenever 呼び笛の設定 (XBMPetParty) is open, automatically
    // removes any current member not in the desired team and adds any
    // desired member not yet in the team, using the technique confirmed
    // live in-game this session (ported from erdelf/AutoDuty's own Crucible
    // automation): FireCallback to open/select, plus a MouseDown ReceiveEvent
    // with a zeroed (not null) AtkEventData to actually pick a bestiary row —
    // see CruciblePetPartyService and LuaAutomationService's own remarks for
    // why this succeeds where three earlier native-click attempts crashed or
    // did nothing. Unattended UI interaction (against the FFXIV ToS); off by
    // default, and only does anything once AutoEnterCrucibleEnabled is on.
    public bool AutoCruciblePetPartyEnabled { get; set; }

    // EXPERIMENTAL. On the pre-battle screen (XBMStageDetailList shown
    // alongside XBMPetParty), automatically assigns up to 3 current team
    // members to the 3 whistle slots via callback("XBMPetParty", 1, row) —
    // the same row-index convention already used for removal — then, once
    // all picks land, also confirms "戦闘開始" itself via
    // callback("XBMStageDetailList", 8). No separate toggle for the battle-
    // start confirm: confirmed safe live, then folded in here rather than
    // given its own checkbox. Ported (as a technique) from erdelf/AutoDuty's
    // own StartFight. Unattended UI interaction (against the FFXIV ToS);
    // off by default.
    public bool AutoCrucibleWhistleAssignEnabled { get; set; }

    // Leveling picks the lowest-ranked owned beasts first (so the whole
    // roster climbs toward the rank cap over repeated runs); Manual uses
    // exactly CrucibleManualTeamPetRowIds, checked off in 魔獣リスト.
    public CrucibleTeamMode CrucibleTeamMode { get; set; } = CrucibleTeamMode.Leveling;

    public List<uint> CrucibleManualTeamPetRowIds { get; set; } = [];

    // --- Crucible shop auto-buy -------------------------------------------------
    // EXPERIMENTAL. Off by default: the shop still just closes without buying,
    // same as before. When on, buys in a fixed priority order every visit —
    // healing items first (up to CrucibleShopHealingCap held), then gear not
    // already owned, then at most one feed item — mirroring erdelf/AutoDuty's
    // own ChooseBuy exactly (its own item-id categories, ported as data, are
    // in CrucibleShopService). Each category has its own on/off switch rather
    // than a free-text keyword list like CrucibleTreasureKeywords: the shop's
    // own UI doesn't expose a readable item-name text node the way the
    // treasure cards do, so matching against arbitrary user text isn't
    // reliably buildable — these fixed, pre-verified id lists are the
    // trustworthy alternative. Unattended UI interaction and purchases
    // (against the FFXIV ToS); off by default.
    public bool AutoCrucibleShopBuyEnabled { get; set; }

    public bool CrucibleShopBuyHealing { get; set; } = true;

    public bool CrucibleShopBuyGear { get; set; } = true;

    public bool CrucibleShopBuyFeed { get; set; } = true;

    // Stop buying more healing items once this many are already held —
    // mirrors AutoDuty's own ItemCap (10).
    public int CrucibleShopHealingCap { get; set; } = 10;
}

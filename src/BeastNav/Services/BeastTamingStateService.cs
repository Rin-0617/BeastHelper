using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BeastNav.Models;
using Dalamud.Memory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace BeastNav.Services;

/// <summary>
/// Reads the Beastmaster monster note ("図鑑") capture state and mirrors it into
/// <see cref="Configuration.TamedPetRowIds"/>.
/// </summary>
/// <remarks>
/// Strategies are attempted most reliable first:
/// <list type="number">
///   <item>
///     <b>xbmmanager</b> – read <c>XBMManager.IsPetUnlocked</c> for every
///     <c>XBMPet</c> row. Available any time the pet-list packet has arrived
///     (<c>State == Received</c>), so the 魔物図鑑 does not need to be open.
///     This is the normal path; the two below are fallbacks.
///   </item>
///   <item>
///     <b>addon</b> – while <c>XBMMonsterNotebook</c> is open, read its grid from
///     the AtkValues. Each cell's portrait icon id tells captured (unique art)
///     from uncaptured (a shared "locked" placeholder). The grid only feeds the
///     current page, so pages are folded into the list one at a time and the
///     running total is checked against the "&lt;captured&gt;/&lt;total&gt;" string.
///   </item>
///   <item>
///     <b>module</b> – serialize <see cref="XBMNoteModule"/> / <see cref="XBMModule"/>
///     and try conservative payload layouts as a fallback (currently diagnostic
///     only – <c>WriteFile</c> does not return usable data yet).
///   </item>
/// </list>
/// The manual <see cref="Configuration.MarkedPetRowIds"/> list is never touched.
/// </remarks>
public unsafe sealed class BeastTamingStateService
{
    private const uint MaxSnapshotBytes = 128 * 1024;
    private const int PreviewBytes = 96;

    private readonly IGameGui gameGui;
    private readonly BeastDataService beastData;
    private readonly Configuration configuration;
    private readonly IPluginLog log;

    public BeastTamingStateService(
        IGameGui gameGui,
        BeastDataService beastData,
        Configuration configuration,
        IPluginLog log)
    {
        this.gameGui = gameGui;
        this.beastData = beastData;
        this.configuration = configuration;
        this.log = log;
    }

    public BeastTamingSyncResult LastResult { get; private set; } = BeastTamingSyncResult.NotRun;

    /// <summary>Runs both strategies and applies the first confident result.</summary>
    /// <param name="allowUnverifiedMerge">
    /// When true (manual sync), a monster-note page whose count cannot be
    /// validated against the "x/total" string is still merged in. When false
    /// (auto sync), only a fully validated read is applied.
    /// </param>
    public BeastTamingSyncResult TrySync(bool allowUnverifiedMerge = true)
    {
        var direct = this.TryReadFromXBMManager();
        if (direct.Success)
        {
            return this.Apply(direct);
        }

        var addon = this.TryReadFromAddon(allowUnverifiedMerge);
        var result = addon.Success ? addon : this.MergeWithModule(addon);
        return this.Apply(result);
    }

    /// <summary>Folds a successful read into the configuration; passes failures straight through.</summary>
    private BeastTamingSyncResult Apply(BeastTamingSyncResult result)
    {
        if (!result.Success)
        {
            this.LastResult = result;
            return result;
        }

        var desired = this.OrderByNo(result.TamedPetRowIds);
        var changed = !this.configuration.TamedPetRowIds.SequenceEqual(desired);
        if (changed)
        {
            this.configuration.TamedPetRowIds = desired;
        }

        // The strategy already built a precise message; only fill one in if it
        // left the field blank.
        result = result with
        {
            Synced = changed,
            Message = string.IsNullOrWhiteSpace(result.Message)
                ? (changed
                    ? $"Synced {desired.Count} captured monsters from {result.Strategy}."
                    : $"図鑑 already up to date ({desired.Count} captured).")
                : result.Message,
        };
        this.LastResult = result;
        return result;
    }

    // -------------------------------------------------------- detail panel --

    private const string DetailAddonName = "XBMMonsterBookDetail";

    /// <summary>
    /// Opportunistically reads whichever beast's level/exp is currently shown
    /// on the 図鑑 detail panel, if it happens to be open — never opens it,
    /// never pages through the notebook itself. Call every tick; it's a
    /// cheap no-op (one addon-visibility check) whenever the panel is
    /// closed. Returns true if <see cref="Configuration.BeastLevelProgress"/>
    /// actually changed and needs saving.
    /// </summary>
    /// <remarks>
    /// EXP comes from <c>XBMMonsterBookDetail</c>'s officially-mapped struct
    /// (<see cref="FFXIVClientStructs.FFXIV.Client.UI.AddonXBMMonsterBookDetail"/>):
    /// <c>EntryExpGaugeBar</c>'s raw <c>Values[0].ValueInt</c>/<c>MaxValue</c>
    /// give the gauge's true current/max with no text parsing, confirmed
    /// correct by the user.
    /// </remarks>
    /// <remarks>
    /// RANK took four attempts. It isn't in that struct at all — the three
    /// candidate fields tried (<c>EntryExpCount</c>, <c>EntrySatietyCount</c>,
    /// a shallow node-tree text scan) were all confirmed wrong or empty. A
    /// full "Dump note addons to log" with the panel open showed RANK
    /// actually lives in <c>XBMMonsterNotebook</c>'s own AtkValues (a
    /// completely different addon from XBMMonsterBookDetail) as a bare
    /// <c>ManagedString</c> ("11") a fixed few slots after the *second*
    /// "No.&lt;n&gt;" marker in that array. The notebook's own grid also has
    /// 25 "No.&lt;n&gt;" cells (one per row), but those are all plain
    /// <c>String</c> type; only the currently-shown detail entry's marker is
    /// <c>ManagedString</c>, which is what disambiguates it. The exact slot
    /// offset from that marker to RANK isn't assumed fixed (a beast with a
    /// different number of learnable actions would shift it) — instead RANK
    /// is the first bare-integer <c>ManagedString</c> found scanning forward
    /// from the marker, which in the confirmed dump was immediately followed
    /// by HP ("608/608"), EXP text ("6/100"), EXP's own current/max as plain
    /// UInts, and 満腹度 ("0/3"), in that order — a distinctive enough
    /// neighborhood that a stray match is unlikely.
    /// </remarks>
    public bool TickDetailPanel()
    {
        var addon = this.FindAddon(DetailAddonName);
        if (addon is null || !addon->IsVisible)
        {
            return false;
        }

        try
        {
            var detail = (AddonXBMMonsterBookDetail*)addon;
            var noNode = detail->EntryNumberText;
            if (noNode is null)
            {
                return false;
            }

            var noMatch = Regex.Match(noNode->NodeText.ToString(), @"\d+");
            if (!noMatch.Success)
            {
                return false;
            }

            var no = uint.Parse(noMatch.Value);
            BeastPetLike? pet = null;
            foreach (var candidate in this.OrderedPets())
            {
                if (candidate.No == no)
                {
                    pet = candidate;
                    break;
                }
            }

            if (pet is null)
            {
                return false;
            }

            this.configuration.BeastLevelProgress.TryGetValue(pet.Value.PetRowId, out var existing);
            var level = existing?.Level ?? 0;

            var gaugeBar = detail->EntryExpGaugeBar;
            if (gaugeBar is null)
            {
                return false;
            }

            var exp = gaugeBar->Values[0].ValueInt;
            var maxExp = gaugeBar->MaxValue;

            var rank = this.TryReadRankFromNotebook(no);
            if (rank is not null)
            {
                level = rank.Value;
            }

            var updated = new BeastProgress { Level = level, Exp = exp, MaxExp = maxExp };
            if (updated == existing)
            {
                return false;
            }

            this.configuration.BeastLevelProgress[pet.Value.PetRowId] = updated;
            return true;
        }
        catch (Exception ex)
        {
            this.log.Debug(ex, "[BeastHelper] Beast detail-panel progress read failed.");
            return false;
        }
    }

    private int? TryReadRankFromNotebook(uint no)
    {
        var notebook = this.FindAddon("XBMMonsterNotebook");
        if (notebook is null)
        {
            return null;
        }

        var values = ReadAtkValues(notebook);
        var wantedMarker = $"No.{no} ";
        var markerIndex = -1;
        for (var i = 0; i < values.Count; i++)
        {
            // The grid's own 25 row markers are plain "String"; only the
            // currently-shown detail entry's marker is "ManagedString" —
            // that's what tells the two apart.
            if (values[i].Type.Equals("ManagedString", StringComparison.Ordinal)
                && string.Equals((values[i].Text ?? string.Empty).Trim(), wantedMarker.Trim(), StringComparison.Ordinal))
            {
                markerIndex = i;
                break;
            }
        }

        if (markerIndex < 0)
        {
            return null;
        }

        for (var i = markerIndex + 2; i < values.Count && i < markerIndex + 60; i++)
        {
            if (values[i].Type.Equals("ManagedString", StringComparison.Ordinal)
                && Regex.IsMatch(values[i].Text ?? string.Empty, @"^\s*\d+\s*$"))
            {
                return int.Parse(values[i].Text!.Trim());
            }
        }

        return null;
    }

    // ------------------------------------------------- notebook auto-sync (level/exp) --

    private const int NotebookPageValueIndex = 10;
    private const int NotebookNumberIndex = 229;
    private const int NotebookIconIndex = 231;
    private const int NotebookLevelIndex = 258;
    private const int NotebookExpIndex = 261;
    private const int NotebookExpRequiredIndex = 262;
    private const int NotebookEntriesPerPage = 25;
    private const int NotebookPageCount = 2;
    private const uint NotebookIconBase = 242000;
    private const long NotebookScanTimeoutMs = 20000;

    private bool notebookWasOpen;
    private bool notebookScanning;
    private long notebookScanStartedAt;
    private long notebookNextActionAt;
    private int notebookMemberIndex;
    private bool notebookSelectSent;
    private string notebookPendingSnapshot = string.Empty;
    private string notebookStableSnapshot = string.Empty;

    /// <summary>Short human status for the main window.</summary>
    public string NotebookSyncStatus { get; private set; } = "off";

    /// <summary>
    /// Whenever 魔獣手帳 transitions from closed to open, automatically pages
    /// through both of its pages and selects each of the 50 entries in turn
    /// to read every beast's level/exp — the same underlying indices
    /// <see cref="TryReadRankFromNotebook"/> already reads passively, just
    /// driven instead of waited for. Selection uses a small, semantic
    /// ReceiveEvent call to the notebook's own agent (AgentId 500, event
    /// kind 0, args [5, pageIndex]) — the exact technique confirmed safe by
    /// BOCXIV/Beastmaster-JP's own BeastmasterNotebookSyncService and
    /// BeastmasterPetPartyService, not a raw UI click replay. Page flips use
    /// the addon's own FireCallback, the same mechanism already used
    /// elsewhere in this codebase (e.g. CrucibleEntryService's dialogue
    /// choice). Call every tick; cheap no-op when the notebook is closed.
    /// Returns true if <see cref="Configuration.BeastLevelProgress"/>
    /// actually changed and needs saving.
    /// </summary>
    public bool TickNotebookAutoSync()
    {
        var addon = this.FindAddon("XBMMonsterNotebook");
        var isOpen = addon is not null && addon->IsVisible;

        if (!isOpen)
        {
            if (this.notebookScanning)
            {
                this.notebookScanning = false;
                this.NotebookSyncStatus = "中断(手帳が閉じられました)";
            }

            this.notebookWasOpen = false;
            return false;
        }

        if (!this.notebookWasOpen)
        {
            this.notebookWasOpen = true;
            this.notebookMemberIndex = 0;
            this.notebookSelectSent = false;
            this.notebookPendingSnapshot = string.Empty;
            this.notebookStableSnapshot = string.Empty;
            this.notebookScanStartedAt = Environment.TickCount64;
            this.notebookNextActionAt = 0;
            this.notebookScanning = true;
            this.NotebookSyncStatus = "自動同期中…";
        }

        if (!this.notebookScanning)
        {
            return false;
        }

        return this.AdvanceNotebookScan(addon);
    }

    private bool AdvanceNotebookScan(AtkUnitBase* addon)
    {
        if (Environment.TickCount64 - this.notebookScanStartedAt >= NotebookScanTimeoutMs)
        {
            this.notebookScanning = false;
            this.NotebookSyncStatus = $"タイムアウト({this.notebookMemberIndex}/{NotebookEntriesPerPage * NotebookPageCount}体まで読み込み)";
            return false;
        }

        if (this.notebookMemberIndex >= NotebookEntriesPerPage * NotebookPageCount)
        {
            this.notebookScanning = false;
            this.NotebookSyncStatus = $"完了({this.notebookMemberIndex}体)";
            return false;
        }

        var values = ReadAtkValues(addon);
        if (values.Count <= NotebookExpRequiredIndex)
        {
            return false;
        }

        var pageValue = values[NotebookPageValueIndex];
        if (pageValue.Number is not (0 or 1))
        {
            return false;
        }

        var page = (int)pageValue.Number;
        var targetPage = this.notebookMemberIndex / NotebookEntriesPerPage;
        if (page != targetPage)
        {
            this.notebookSelectSent = false;
            this.notebookPendingSnapshot = string.Empty;
            this.notebookStableSnapshot = string.Empty;
            if (Environment.TickCount64 >= this.notebookNextActionAt)
            {
                RequestNotebookPage(addon, (uint)targetPage);
                this.notebookNextActionAt = Environment.TickCount64 + 200;
                this.NotebookSyncStatus = $"第{targetPage + 1}ページへ移動中…";
            }

            return false;
        }

        var pageIndex = this.notebookMemberIndex % NotebookEntriesPerPage;
        if (!this.notebookSelectSent && Environment.TickCount64 >= this.notebookNextActionAt)
        {
            SendNotebookSelectEvent((uint)pageIndex);
            this.notebookSelectSent = true;
            this.notebookNextActionAt = Environment.TickCount64 + 100;
        }

        var expectedNo = (uint)(this.notebookMemberIndex + 1);
        var no = (uint)values[NotebookNumberIndex].Number;
        var icon = (uint)values[NotebookIconIndex].Number;
        if (no != expectedNo || icon != NotebookIconBase + expectedNo)
        {
            return false;
        }

        var level = (int)values[NotebookLevelIndex].Number;
        if (level is < 1 or > 25)
        {
            return false;
        }

        var exp = (int)values[NotebookExpIndex].Number;
        var expRequired = (int)values[NotebookExpRequiredIndex].Number;
        if (level == 25)
        {
            exp = 0;
            expRequired = 0;
        }
        else if (expRequired is < 1 or > 999999 || exp >= expRequired)
        {
            return false;
        }

        // Require the same reading twice in a row before trusting it — the
        // UI is still mid-transition for a frame or two right after the
        // select event lands.
        var snapshot = $"{no}:{level}:{exp}:{expRequired}";
        if (snapshot != this.notebookPendingSnapshot)
        {
            this.notebookPendingSnapshot = snapshot;
            this.notebookStableSnapshot = string.Empty;
            return false;
        }

        if (snapshot == this.notebookStableSnapshot)
        {
            return false;
        }

        this.notebookStableSnapshot = snapshot;

        BeastPetLike? pet = null;
        foreach (var candidate in this.OrderedPets())
        {
            if (candidate.No == no)
            {
                pet = candidate;
                break;
            }
        }

        this.notebookMemberIndex++;
        this.notebookSelectSent = false;
        this.notebookPendingSnapshot = string.Empty;
        this.notebookStableSnapshot = string.Empty;
        this.notebookNextActionAt = 0;
        this.NotebookSyncStatus = $"読込中 {this.notebookMemberIndex}/{NotebookEntriesPerPage * NotebookPageCount}体…";

        if (pet is null)
        {
            return false;
        }

        var updated = new BeastProgress { Level = level, Exp = exp, MaxExp = expRequired };
        this.configuration.BeastLevelProgress.TryGetValue(pet.Value.PetRowId, out var existing);
        if (updated == existing)
        {
            return false;
        }

        this.configuration.BeastLevelProgress[pet.Value.PetRowId] = updated;
        return true;
    }

    private static void RequestNotebookPage(AtkUnitBase* addon, uint page)
    {
        var values = stackalloc AtkValue[2];
        values[0] = new AtkValue { Type = (AtkValueType)3, Int = 3 };
        values[1] = new AtkValue { Type = (AtkValueType)5, UInt = page };
        addon->FireCallback(2, values, true);
    }

    private static void SendNotebookSelectEvent(uint pageIndex)
    {
        var module = AgentModule.Instance();
        var agent = module == null ? null : module->GetAgentByInternalId((AgentId)500);
        if (agent == null)
        {
            return;
        }

        var args = stackalloc AtkValue[2];
        args[0] = new AtkValue { Type = (AtkValueType)3, Int = 5 };
        args[1] = new AtkValue { Type = (AtkValueType)3, Int = (int)pageIndex };
        var result = new AtkValue();
        agent->ReceiveEvent(&result, args, 2, 0);
    }

    // ------------------------------------------------ party rank (passive) --

    private const uint PartyTeamListNodeId = 11;
    private const uint PartyRowNameTextId = 17;
    private const uint PartyRowRankTextId = 9;

    /// <summary>
    /// Passively reads RANK for whichever beasts are currently in the party
    /// on 呼び笛の設定 (XBMPetParty) straight from that screen's own already-
    /// open UI — never opens anything itself, just reads whatever's already
    /// visible whenever the player (or CruciblePetPartyService) has this
    /// screen up anyway. Ported (as a technique) from erdelf/AutoDuty's own
    /// CrucibleUi.Team(): the party list is AtkComponentList NodeId 11
    /// (confirmed independently by a second community plugin, FFXIVPetRenamer,
    /// which reads the same list for an unrelated reason), each row an
    /// AtkComponentListItemRenderer whose own text node 17 holds the beast's
    /// name and node 9 its rank. Matched against name here instead of
    /// AutoDuty's own scan-and-remember cache, since BeastDataService's
    /// Lumina-loaded names are already on hand. EXP isn't shown on this
    /// screen — only the notebook detail panel has it (see
    /// <see cref="TickDetailPanel"/>) — so this only ever updates Level.
    /// </summary>
    public bool TickPartyRanks()
    {
        var addon = this.FindAddon("XBMPetParty");
        if (addon is null || !addon->IsVisible)
        {
            return false;
        }

        var listNode = addon->GetNodeById(PartyTeamListNodeId);
        var list = (AtkComponentList*)(listNode is null ? null : listNode->GetComponent());
        if (list is null)
        {
            return false;
        }

        var changed = false;
        for (var i = 0; i < list->ListLength; i++)
        {
            var renderer = list->GetItemRenderer(i);
            if (renderer is null)
            {
                break;
            }

            var uld = &((AtkComponentBase*)renderer)->UldManager;
            var name = ReadNodeText(uld, PartyRowNameTextId);
            if (string.IsNullOrWhiteSpace(name))
            {
                break;
            }

            var rankMatch = Regex.Match(ReadNodeText(uld, PartyRowRankTextId), @"\d+");
            if (!rankMatch.Success)
            {
                continue;
            }

            var level = int.Parse(rankMatch.Value);
            var pet = this.beastData.Pets.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));
            if (pet is null)
            {
                continue;
            }

            this.configuration.BeastLevelProgress.TryGetValue(pet.PetRowId, out var existing);
            var updated = existing is null
                ? new BeastProgress { Level = level, Exp = 0, MaxExp = 0 }
                : existing with { Level = level };

            if (updated != existing)
            {
                this.configuration.BeastLevelProgress[pet.PetRowId] = updated;
                changed = true;
            }
        }

        return changed;
    }

    private static string ReadNodeText(AtkUldManager* uld, uint nodeId)
    {
        var nodeList = uld->NodeList;
        var count = uld->NodeListCount;
        for (var i = 0; i < count; i++)
        {
            var node = nodeList[i];
            if (node is not null && node->NodeId == nodeId && node->Type == NodeType.Text)
            {
                return ((AtkTextNode*)node)->NodeText.ToString().Trim();
            }
        }

        return string.Empty;
    }

    // -------------------------------------------------------------- xbmmanager --

    /// <summary>
    /// Reads capture state straight from <c>XBMManager</c>. Works whenever the
    /// pet-list packet has been received, so the 魔物図鑑 does not need to be open.
    /// </summary>
    private BeastTamingSyncResult TryReadFromXBMManager()
    {
        try
        {
            var manager = XBMManager.Instance();
            if (manager is null)
            {
                return new BeastTamingSyncResult { Strategy = "xbmmanager", Message = "XBMManager is unavailable." };
            }

            if (manager->State != XBMManager.DataState.Received)
            {
                return new BeastTamingSyncResult
                {
                    Strategy = "xbmmanager",
                    Message = $"XBMManager pet list not received yet (State={manager->State}).",
                };
            }

            var pets = this.OrderedPets();
            if (pets.Length == 0)
            {
                return new BeastTamingSyncResult { Strategy = "xbmmanager", Message = "No XBMPet rows loaded." };
            }

            var tamed = new List<uint>();
            foreach (var pet in pets)
            {
                bool unlocked;
                try
                {
                    // Keyed on the XBMPet row id (図鑑 "No."), not the Pet row id.
                    unlocked = manager->IsPetUnlocked(pet.No);
                }
                catch
                {
                    unlocked = false;
                }

                if (unlocked)
                {
                    tamed.Add(pet.PetRowId);
                }
            }

            var ordered = this.OrderByNo(tamed);
            return new BeastTamingSyncResult
            {
                Success = true,
                Strategy = "xbmmanager",
                Source = "XBMManager",
                Method = "IsPetUnlocked(XBMPet row id)",
                TamedPetRowIds = ordered,
                Message = $"図鑑 synced from XBMManager: {ordered.Count} captured (NumUnlockedPets={manager->NumUnlockedPets}).",
            };
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "Failed to read XBMManager pet-unlock state.");
            return new BeastTamingSyncResult
            {
                Strategy = "xbmmanager",
                Message = $"XBMManager read failed: {ex.GetType().Name}.",
            };
        }
    }

    private BeastTamingSyncResult MergeWithModule(BeastTamingSyncResult addonAttempt)
    {
        var module = this.TryReadFromModule();
        var diagnostics = addonAttempt.Diagnostics.Concat(module.Diagnostics).ToArray();
        if (module.Success)
        {
            return module with
            {
                Diagnostics = diagnostics,
            };
        }

        return module with
        {
            Diagnostics = diagnostics,
            Message = $"addon: {Fallback(addonAttempt.Message)} | module: {Fallback(module.Message)}",
        };

        static string Fallback(string value) => string.IsNullOrWhiteSpace(value) ? "-" : value;
    }

    // ------------------------------------------------------------------ addon --

    private BeastTamingSyncResult TryReadFromAddon(bool allowUnverifiedMerge)
    {
        var pets = this.OrderedPets();
        if (pets.Length == 0)
        {
            return new BeastTamingSyncResult { Strategy = "addon", Message = "No XBMPet rows loaded." };
        }

        foreach (var name in this.configuration.BeastNoteAddonCandidates)
        {
            var addon = this.FindAddon(name);
            if (addon is null || !addon->IsVisible)
            {
                continue;
            }

            var values = ReadAtkValues(addon);
            var diagnostics = this.DescribeAddon(name, addon, includeNodes: true);
            return this.DecodeMonsterNotebook(name, values, pets, diagnostics, allowUnverifiedMerge);
        }

        return new BeastTamingSyncResult
        {
            Strategy = "addon",
            Message = "魔物図鑑が開いていません。図鑑を開いて再度同期してください。",
        };
    }

    // Shared "unknown monster" portrait shown for every uncaptured entry.
    private const long LockedIconFallback = 242051;

    /// <summary>
    /// Decodes the monster-note grid from its AtkValues. Each grid cell is an
    /// 8-value group anchored by a <c>String</c> "No.&lt;n&gt;" whose third value
    /// is the constant <c>UInt 3</c>. The value just before the anchor is the
    /// portrait icon id: captured entries have a unique portrait, uncaptured
    /// entries all share one "locked" placeholder icon. The grid only feeds the
    /// current page (25 rows), so results are folded into
    /// <see cref="Configuration.TamedPetRowIds"/> page by page.
    /// </summary>
    private BeastTamingSyncResult DecodeMonsterNotebook(
        string name,
        IReadOnlyList<AtkValueSnapshot> values,
        BeastPetLike[] pets,
        List<string> diagnostics,
        bool allowUnverifiedMerge)
    {
        var progress = values
            .Select(v => v.Text)
            .FirstOrDefault(t => !string.IsNullOrEmpty(t) && Regex.IsMatch(t, @"^\s*\d+\s*/\s*\d+\s*$"));
        int? expectedCaptured = null;
        int? total = null;
        if (progress is not null)
        {
            var m = Regex.Match(progress, @"(\d+)\s*/\s*(\d+)");
            expectedCaptured = int.Parse(m.Groups[1].Value);
            total = int.Parse(m.Groups[2].Value);
            diagnostics.Add($"progress string: {expectedCaptured}/{total}");
        }

        var rows = new List<(uint No, long Icon)>();
        for (var i = 1; i < values.Count; i++)
        {
            if (!values[i].Type.Equals("String", StringComparison.Ordinal))
            {
                continue;
            }

            var m = Regex.Match(values[i].Text ?? string.Empty, @"^\s*No\.\s*(\d+)\s*$");
            if (!m.Success || i + 2 >= values.Count || values[i + 2].Number != 3)
            {
                continue; // not a grid cell (e.g. the detail panel's ManagedString "No.1")
            }

            rows.Add((uint.Parse(m.Groups[1].Value), values[i - 1].Number));
        }

        if (rows.Count == 0)
        {
            return new BeastTamingSyncResult
            {
                Strategy = "addon",
                Source = name,
                Diagnostics = diagnostics,
                Message = $"{name} is open but no monster-note grid cells were found. Share /xllog.",
            };
        }

        // The locked placeholder icon is whichever icon id is shared by two or
        // more cells (real portraits are unique). Fall back to the known id.
        var lockedIcon = rows
            .GroupBy(r => r.Icon)
            .Where(g => g.Count() >= 2)
            .OrderByDescending(g => g.Count())
            .Select(g => (long?)g.Key)
            .FirstOrDefault() ?? (rows.Any(r => r.Icon == LockedIconFallback) ? LockedIconFallback : (long?)null);

        diagnostics.Add($"parsed {rows.Count} grid cells No.{rows.Min(r => r.No)}-{rows.Max(r => r.No)}, lockedIcon={lockedIcon?.ToString() ?? "none"}");

        if (lockedIcon is null && !allowUnverifiedMerge)
        {
            return new BeastTamingSyncResult
            {
                Strategy = "addon",
                Source = name,
                Diagnostics = diagnostics,
                Message = $"{name}: every portrait on this page is unique; cannot tell captured from uncaptured. Open a page with uncaptured entries.",
            };
        }

        var capturedNos = rows
            .Where(r => lockedIcon is null || r.Icon != lockedIcon)
            .Select(r => r.No)
            .ToHashSet();
        var pageNos = rows.Select(r => r.No).ToHashSet();

        // A contiguous full run means this is an unfiltered page, so absent-from-
        // captured entries in that range can be authoritatively cleared.
        var min = pageNos.Min();
        var max = pageNos.Max();
        var contiguous = pageNos.Count == (int)(max - min + 1);

        var current = this.configuration.TamedPetRowIds.ToHashSet();
        foreach (var pet in pets)
        {
            if (capturedNos.Contains(pet.No))
            {
                current.Add(pet.PetRowId);
            }
            else if (contiguous && pet.No >= min && pet.No <= max)
            {
                current.Remove(pet.PetRowId);
            }
        }

        var merged = this.OrderByNo(current);
        var known = total is int t ? merged.Count(id => (this.beastData.FindPet(id)?.XbmRowId ?? uint.MaxValue) <= t) : merged.Count;
        var verified = expectedCaptured is int ec && known == ec;

        return new BeastTamingSyncResult
        {
            Success = true,
            Strategy = "addon",
            Source = name,
            Method = $"notebook portraits, page No.{min}-{max}{(contiguous ? string.Empty : " (filtered)")}",
            TamedPetRowIds = merged,
            Diagnostics = diagnostics,
            Message = verified
                ? $"図鑑 synced: {known}/{total} captured."
                : expectedCaptured is int ec2
                    ? $"Page No.{min}-{max} folded in: {known} known, 図鑑 says {ec2}/{total}. Flip to the other page(s) to finish."
                    : $"Page No.{min}-{max} folded in: {known} known so far.",
        };
    }

    // ----------------------------------------------------------------- module --

    private BeastTamingSyncResult TryReadFromModule()
    {
        try
        {
            var uiModulePtr = this.gameGui.GetUIModule();
            if (uiModulePtr.IsNull)
            {
                return new BeastTamingSyncResult { Strategy = "module", Message = "UIModule is unavailable." };
            }

            var uiModule = (UIModule*)uiModulePtr.Address;

            var noteModule = uiModule->GetXBMNoteModule();
            var note = noteModule is null
                ? new BeastTamingSyncResult { Strategy = "module", Source = "XBMNoteModule", Message = "XBMNoteModule is unavailable." }
                : this.ReadModuleSnapshot("XBMNoteModule", &noteModule->UserFileEvent);
            if (note.Success)
            {
                return note;
            }

            var mainModule = uiModule->GetXBMModule();
            var main = mainModule is null
                ? new BeastTamingSyncResult { Strategy = "module", Source = "XBMModule", Message = "XBMModule is unavailable." }
                : this.ReadModuleSnapshot("XBMModule", &mainModule->UserFileEvent);
            if (main.Success)
            {
                return main;
            }

            var diagnostics = note.Diagnostics.Concat(main.Diagnostics).ToArray();
            var richer = note.Diagnostics.Count >= main.Diagnostics.Count ? note : main;
            return richer with
            {
                Success = false,
                Diagnostics = diagnostics,
                Message = $"XBMNoteModule: {note.Message} | XBMModule: {main.Message}",
            };
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "Failed to read Beastmaster module state.");
            return new BeastTamingSyncResult
            {
                Strategy = "module",
                Message = $"Failed to read XBM module: {ex.GetType().Name}.",
            };
        }
    }

    private BeastTamingSyncResult ReadModuleSnapshot(string source, UserFileManager.UserFileEvent* module)
    {
        if (module is null)
        {
            return new BeastTamingSyncResult { Strategy = "module", Source = source, Message = $"{source} is unavailable." };
        }

        uint dataSize = 0;
        uint fileSize = 0;
        ushort fileTypeId = 0;
        ushort fileVersionId = 0;
        var hasChanges = false;
        var isVirtual = false;
        uint tempBytes = 0;
        var tempPtrSet = false;
        try
        {
            dataSize = module->GetDataSize();
            fileSize = module->GetFileSize();
            fileTypeId = module->GetFileType();
            fileVersionId = module->GetFileVersion();
            hasChanges = module->GetHasChanges();
            isVirtual = module->IsVirtual;
            tempBytes = module->TempDataBytesWritten;
            tempPtrSet = module->TempDataPtr != nint.Zero;
        }
        catch
        {
            // Fall through with whatever we managed to read.
        }

        var diagnostics = new List<string>
        {
            $"{source}: data={dataSize} file={fileSize} type=0x{fileTypeId:X4} ver={fileVersionId} "
            + $"hasChanges={hasChanges} virtual={isVirtual} tempBytes={tempBytes} tempPtr={tempPtrSet}",
        };

        // GetDataSize / GetFileSize return junk before the module has ever loaded
        // its server data. Pick a sane buffer: a reported size in range, else a
        // fixed probe buffer, and trust WriteFile's return value.
        uint bufferSize = dataSize is > 0 and <= MaxSnapshotBytes ? dataSize
            : fileSize is > 0 and <= MaxSnapshotBytes ? fileSize
            : 64 * 1024;

        var buffer = new byte[bufferSize];
        uint written = 0;
        try
        {
            fixed (byte* p = buffer)
            {
                written = module->WriteFile(p, bufferSize);
            }
        }
        catch (Exception ex)
        {
            diagnostics.Add($"  WriteFile threw {ex.GetType().Name}");
            return new BeastTamingSyncResult
            {
                Strategy = "module",
                Source = source,
                FileSize = fileSize,
                DataSize = dataSize,
                Diagnostics = diagnostics,
                Message = "図鑑 module data is not available yet (WriteFile failed).",
            };
        }

        if (written == 0 || written > bufferSize)
        {
            diagnostics.Add($"  WriteFile returned {written} (buffer {bufferSize})");
            return new BeastTamingSyncResult
            {
                Strategy = "module",
                Source = source,
                FileSize = fileSize,
                DataSize = dataSize,
                Diagnostics = diagnostics,
                Message = "図鑑 module data is not loaded yet. Open the 魔物図鑑 and try again.",
            };
        }

        var data = buffer.AsSpan(0, (int)written).ToArray();
        diagnostics.Add($"  WriteFile wrote {written} bytes");
        diagnostics.Add($"  first 128 : {Convert.ToHexString(data.AsSpan(0, Math.Min(data.Length, 128)))}");

        var baseResult = new BeastTamingSyncResult
        {
            Strategy = "module",
            Source = source,
            FileSize = fileSize,
            DataSize = dataSize,
            FileType = SafeAscii(data.AsSpan(0, Math.Min(data.Length, 4))),
            FileVersion = fileVersionId,
            HeaderLength = 0,
            DataHash = Convert.ToHexString(SHA256.HashData(data)),
            PreviewHex = Convert.ToHexString(data.AsSpan(0, Math.Min(data.Length, PreviewBytes))),
            PayloadHex = Convert.ToHexString(data.AsSpan(0, Math.Min(data.Length, 256))),
            Diagnostics = diagnostics,
        };

        var pets = this.OrderedPets();

        // The exact header length is unknown, so try a few plausible offsets and
        // require every layout that matches to agree on the same monster set.
        var candidateHeaders = new List<int> { 0, 4, 8, 12, 16, 32 };
        if (fileSize is > 0 and <= MaxSnapshotBytes && dataSize is > 0 and < MaxSnapshotBytes
            && fileSize > dataSize && fileSize - dataSize < written)
        {
            candidateHeaders.Insert(0, (int)(fileSize - dataSize));
        }

        var layouts = new List<(string Method, List<uint> Tamed)>();
        foreach (var header in candidateHeaders.Distinct().Where(h => h >= 0 && h < data.Length))
        {
            var payload = data.AsSpan(header).ToArray();
            if (TryPackedBitset(payload, pets, out var byBits))
            {
                layouts.Add(($"packed bits, header {header}", byBits));
            }

            if (TryBytePerEntry(payload, pets, out var byBytes))
            {
                layouts.Add(($"byte per entry, header {header}", byBytes));
            }
        }

        var distinct = layouts
            .GroupBy(layout => string.Join(",", this.OrderByNo(layout.Tamed)))
            .ToArray();

        if (distinct.Length == 1)
        {
            var winner = distinct[0].First();
            return baseResult with
            {
                Success = true,
                Method = winner.Method,
                TamedPetRowIds = this.OrderByNo(winner.Tamed),
                Message = $"Decoded {winner.Tamed.Count} captured monsters ({winner.Method}).",
            };
        }

        if (distinct.Length == 0)
        {
            return baseResult with { Message = "Module data read but no known payload layout matched. Share /xllog." };
        }

        return baseResult with
        {
            Message = $"Ambiguous payload: {distinct.Length} candidate sets. Share /xllog.",
        };
    }

    private static bool TryPackedBitset(byte[] payload, BeastPetLike[] pets, out List<uint> tamed)
    {
        tamed = [];
        if (pets.Length == 0)
        {
            return false;
        }

        var maxIndex = pets.Max(pet => pet.No) - 1;
        var requiredBytes = (int)(maxIndex / 8) + 1;
        if (payload.Length < requiredBytes || AllEqual(payload.AsSpan(0, requiredBytes), 0xFF))
        {
            return false;
        }

        var hits = new List<uint>();
        foreach (var pet in pets)
        {
            var index = pet.No - 1;
            if ((payload[index / 8] & (1 << (int)(index % 8))) != 0)
            {
                hits.Add(pet.PetRowId);
            }
        }

        if (hits.Count is 0 || hits.Count == pets.Length)
        {
            return false;
        }

        tamed = hits;
        return true;
    }

    private static bool TryBytePerEntry(byte[] payload, BeastPetLike[] pets, out List<uint> tamed)
    {
        tamed = [];
        if (pets.Length == 0)
        {
            return false;
        }

        var maxIndex = (int)pets.Max(pet => pet.No) - 1;
        if (payload.Length <= maxIndex)
        {
            return false;
        }

        var hits = new List<uint>();
        foreach (var pet in pets)
        {
            var value = payload[(int)pet.No - 1];
            if (value > 1)
            {
                return false;
            }

            if (value == 1)
            {
                hits.Add(pet.PetRowId);
            }
        }

        if (hits.Count is 0 || hits.Count == pets.Length)
        {
            return false;
        }

        tamed = hits;
        return true;
    }

    // --------------------------------------------------------------- dump only --

    /// <summary>Logs every candidate addon's AtkValues and node tree for reverse engineering.</summary>
    public void DumpNoteAddons()
    {
        var any = false;
        var names = this.configuration.BeastNoteAddonCandidates
            .Append("XBMMonsterBookDetail")
            .Distinct();
        foreach (var name in names)
        {
            var addon = this.FindAddon(name);
            if (addon is null)
            {
                continue;
            }

            any = true;
            foreach (var line in this.DescribeAddon(name, addon, includeNodes: true))
            {
                this.log.Information("[BeastHelper] dumpnote {Line}", line);
            }
        }

        if (!any)
        {
            this.log.Information("[BeastHelper] dumpnote: none of the candidate addons are loaded. Open the 魔物図鑑 first.");
        }
    }

    private List<string> DescribeAddon(string name, AtkUnitBase* addon, bool includeNodes)
    {
        var lines = new List<string> { $"{name}: visible={addon->IsVisible}" };

        var values = ReadAtkValues(addon);
        lines.Add($"  {values.Count} AtkValues");
        for (var i = 0; i < values.Count; i++)
        {
            lines.Add($"    v[{i}] {values[i]}");
        }

        if (includeNodes)
        {
            try
            {
                WalkComponentNodes(&addon->UldManager, 1, lines);
            }
            catch (Exception ex)
            {
                lines.Add($"  node walk failed: {ex.GetType().Name}");
            }
        }

        return lines;
    }

    private static void WalkComponentNodes(AtkUldManager* uld, int depth, List<string> lines)
    {
        if (uld is null || depth > 6)
        {
            return;
        }

        var indent = new string(' ', depth * 2);
        for (var i = 0; i < uld->NodeListCount; i++)
        {
            var node = uld->NodeList[i];
            if (node is null)
            {
                continue;
            }

            var typeValue = (uint)node->Type;
            if (typeValue == (uint)NodeType.Text)
            {
                var textNode = (AtkTextNode*)node;
                var text = textNode->NodeText.ToString();
                lines.Add(string.IsNullOrWhiteSpace(text)
                    ? $"{indent}text#{node->NodeId} <empty> visible={node->IsVisible()} pos=({node->X:0},{node->Y:0})"
                    : $"{indent}text#{node->NodeId} '{text.Replace('\n', ' ')}'");
                continue;
            }

            if (typeValue < 1000)
            {
                // A raw, non-component, non-text node (Image, NineGrid,
                // Counter, Collision, Res, ...) — e.g. FFXIV often renders a
                // stylized stat number as a digit-texture NineGrid/Counter
                // rather than plain text, which would otherwise vanish from
                // this dump entirely. Logged so those aren't silently lost.
                lines.Add($"{indent}node#{node->NodeId} type={node->Type} visible={node->IsVisible()} pos=({node->X:0},{node->Y:0}) size=({node->Width}x{node->Height})");
                continue;
            }

            var componentNode = (AtkComponentNode*)node;
            var component = componentNode->Component;
            if (component is null)
            {
                continue;
            }

            var componentType = component->GetComponentType();
            lines.Add($"{indent}component#{node->NodeId} {componentType} visible={node->IsVisible()}");

            switch (componentType)
            {
                case ComponentType.List:
                {
                    var list = (AtkComponentList*)component;
                    lines.Add($"{indent}  ListLength={list->ListLength} visibleRows={list->NumVisibleRows} visibleItems={list->NumVisibleItems}");
                    break;
                }

                case ComponentType.XBMItem:
                {
                    var item = (AtkComponentXBMItem*)component;
                    lines.Add($"{indent}  XBMItemId={item->XBMItemId} loaded={item->IsItemLoaded} name='{item->NameText.ToString().Replace('\n', ' ')}' type='{item->TypeText.ToString().Replace('\n', ' ')}'");
                    break;
                }
            }

            WalkComponentNodes(&component->UldManager, depth + 1, lines);
        }
    }

    // ------------------------------------------------------------------ shared --

    private AtkUnitBase* FindAddon(string name)
    {
        try
        {
            var ptr = this.gameGui.GetAddonByName(name, 1);
            return ptr.IsNull ? null : (AtkUnitBase*)ptr.Address;
        }
        catch
        {
            return null;
        }
    }

    private BeastPetLike[] OrderedPets()
        => this.beastData.Pets
            .OrderBy(pet => pet.XbmRowId)
            .Select(pet => new BeastPetLike(pet.XbmRowId, pet.PetRowId))
            .ToArray();

    private List<uint> OrderByNo(IEnumerable<uint> petRowIds)
        => petRowIds
            .Distinct()
            .OrderBy(rowId => this.beastData.FindPet(rowId)?.XbmRowId ?? uint.MaxValue)
            .ThenBy(rowId => rowId)
            .ToList();

    private static List<AtkValueSnapshot> ReadAtkValues(AtkUnitBase* addon)
    {
        var result = new List<AtkValueSnapshot>();
        var count = addon->AtkValuesCount;
        var values = addon->AtkValues;
        if (values is null)
        {
            return result;
        }

        for (var i = 0; i < count && i < 4096; i++)
        {
            result.Add(AtkValueSnapshot.From(&values[i]));
        }

        return result;
    }

    private static string SafeAscii(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder(bytes.Length);
        foreach (var b in bytes)
        {
            sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
        }

        return sb.ToString();
    }

    private static bool AllEqual(ReadOnlySpan<byte> values, byte expected)
    {
        foreach (var value in values)
        {
            if (value != expected)
            {
                return false;
            }
        }

        return values.Length > 0;
    }

    private readonly record struct BeastPetLike(uint No, uint PetRowId);

    private readonly record struct AtkValueSnapshot(string Type, long Number, string Text)
    {
        public static AtkValueSnapshot From(AtkValue* value)
        {
            if (value is null)
            {
                return new AtkValueSnapshot("null", long.MinValue, string.Empty);
            }

            // The AtkValue.Type enum has moved namespaces between FFXIVClientStructs
            // versions, so switch on its name instead of the enum members.
            var type = value->Type.ToString();
            try
            {
                switch (type)
                {
                    case "Int":
                        return new AtkValueSnapshot(type, value->Int, value->Int.ToString());
                    case "UInt":
                        return new AtkValueSnapshot(type, value->UInt, value->UInt.ToString());
                    case "Int64":
                        return new AtkValueSnapshot(type, value->Int64, value->Int64.ToString());
                    case "UInt64":
                        return new AtkValueSnapshot(type, (long)value->UInt64, value->UInt64.ToString());
                    case "Bool":
                        return new AtkValueSnapshot(type, value->Byte, value->Byte != 0 ? "true" : "false");
                    case "Float":
                        return new AtkValueSnapshot(type, (long)value->Float, value->Float.ToString("0.###"));
                    case "String":
                    case "String8":
                    case "ConstString":
                    case "ManagedString":
                        var raw = (byte*)value->String;
                        var text = raw != null ? MemoryHelper.ReadStringNullTerminated((nint)raw) : string.Empty;
                        return new AtkValueSnapshot(type, long.MinValue, text.Replace('\n', ' '));
                    default:
                        return new AtkValueSnapshot(type, long.MinValue, string.Empty);
                }
            }
            catch
            {
                return new AtkValueSnapshot(type, long.MinValue, "<error>");
            }
        }

        public override string ToString()
            => Number == long.MinValue
                ? $"{Type}={(string.IsNullOrEmpty(Text) ? "?" : Text)}"
                : $"{Type}={Number}";
    }
}

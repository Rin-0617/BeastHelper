using BeastNav.Models;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace BeastNav.Services;

/// <summary>
/// Auto-forms the whistle party on 呼び笛の設定 (XBMPetParty): removes any
/// current member not in the desired team, then adds any desired member not
/// yet in the team via the notebook. The desired team is either "Leveling"
/// (lowest-ranked owned beasts first, so the whole roster climbs toward the
/// rank cap over repeated runs) or "Manual" (exactly the beasts checked off
/// in 魔獣リスト) — see <see cref="Configuration.CrucibleTeamMode"/>.
/// </summary>
/// <remarks>
/// The add-a-beast technique was the one thing three earlier attempts this
/// session couldn't crack (see LuaAutomationService's own remarks for the
/// full history: FireCallback replay alone carries no beast identity,
/// ButtonClick/ListItemClick with a real AtkEventData.ListItemData had no
/// effect, and a null AtkEventData crashed the game outright). Reading
/// erdelf/AutoDuty's own published Crucible automation (2026-09) found the
/// actual working sequence — confirmed live in this session before this
/// file was written: FireCallback(notebook, true, 5, slotOnPage) to select
/// the row, then a plain MouseDown ReceiveEvent with a non-null pointer to
/// a value-initialized (all-zero) AtkEventData, the event found by a full
/// recursive search of the notebook's node tree since it isn't on any
/// specific node's own chain. Removal uses XBMPetParty's own row-context-menu
/// FireCallback (open menu, pick the first option) — the same FireCallback-
/// only style already used safely elsewhere in this codebase.
/// </remarks>
/// <remarks>
/// Unattended UI interaction — against the FFXIV ToS, self-responsibility.
/// Off by default. Only acts while XBMPetParty is open, and only once both
/// AutoEnterCrucibleEnabled and AutoCruciblePetPartyEnabled are on.
/// </remarks>
public sealed unsafe class CruciblePetPartyService
{
    private const string PartyAddonName = "XBMPetParty";
    private const string NotebookAddonName = "XBMMonsterNotebook";
    private const string ContextMenuAddonName = "ContextMenu";

    // Party list's own AtkValues layout — the same indices the read-only
    // investigation earlier this session validated against a live 12-beast
    // board-2 roster, read here from AtkValues rather than raw agent memory.
    private const int MemberCountIndex = 5;
    private const int FirstMemberIndex = 6;
    private const int MemberStride = 77;
    private const int CapacityIndex = 1162;
    private const uint MemberIconBase = 242000;
    private const int DefaultCapacity = 10;

    // Notebook indices confirmed live this session — see BeastTamingStateService's own remarks.
    private const int NotebookPageValueIndex = 10;
    private const int NotebookNumberIndex = 229;
    private const int NotebookEntriesPerPage = 25;
    private const uint NotebookFirstEntryParam = 4;

    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ActionResend = TimeSpan.FromSeconds(1);

    private enum Step
    {
        Idle,
        AwaitRemoveMenu,
        AwaitRemoved,
        AwaitBestiary,
        Adding,
    }

    private readonly Configuration configuration;
    private readonly BeastDataService beastData;
    private readonly IGameGui gameGui;
    private readonly IPluginLog log;

    private Step step = Step.Idle;
    private DateTime stepSince = DateTime.MinValue;
    private DateTime nextActionAt = DateTime.MinValue;
    private uint removingNumber;
    private uint addingNumber;
    private bool addSent;

    private int whistleStep = -1;
    private DateTime nextWhistleActionAt = DateTime.MinValue;
    private bool startBattleSent;

    public CruciblePetPartyService(Configuration configuration, BeastDataService beastData, IGameGui gameGui, IPluginLog log)
    {
        this.configuration = configuration;
        this.beastData = beastData;
        this.gameGui = gameGui;
        this.log = log;
    }

    /// <summary>Short human status for the main window.</summary>
    public string Status { get; private set; } = "off";

    /// <summary>Short human status for the main window (whistle assignment).</summary>
    public string WhistleStatus { get; private set; } = "off";

    /// <summary>
    /// On the pre-battle screen (XBMStageDetailList shown alongside
    /// XBMPetParty), assigns up to 3 current team members to the 3 whistle
    /// slots — via <c>callback("XBMPetParty", 1, row)</c>, one per member,
    /// a short delay apart. This is the exact same row-index convention
    /// already confirmed working for removal (callback param 2); ported
    /// (as a technique) from erdelf/AutoDuty's own <c>StartFight</c>. Once
    /// confirmed safe live (2026-09), the user asked for the battle-start
    /// confirm itself to come back too — <c>callback("XBMStageDetailList", 8)</c>
    /// — fired automatically once all whistle picks land. No separate
    /// toggle for it: it's folded into <see cref="Configuration.AutoCrucibleWhistleAssignEnabled"/>
    /// itself, per the user's own call that this didn't need its own checkbox.
    /// </summary>
    public void TickWhistleAssignment()
    {
        if (!this.configuration.AutoEnterCrucibleEnabled || !this.configuration.AutoCrucibleWhistleAssignEnabled)
        {
            this.WhistleStatus = "off";
            this.whistleStep = -1;
            this.startBattleSent = false;
            return;
        }

        var layout = this.FindAddon("XBMStageDetailList");
        var party = this.FindAddon(PartyAddonName);
        if (layout is null || !layout->IsVisible || party is null || !party->IsVisible)
        {
            this.WhistleStatus = "戦闘開始画面待ち";
            this.whistleStep = -1;
            this.startBattleSent = false;
            return;
        }

        var (team, _) = ReadTeam(party);
        if (team is null || team.Count == 0)
        {
            this.WhistleStatus = "編成情報の読み取り待ち";
            return;
        }

        var picks = this.ComputeWhistlePicks(team);
        if (picks.Count == 0)
        {
            this.WhistleStatus = "呼び笛に割り当てる魔獣がいません";
            return;
        }

        if (this.whistleStep < 0)
        {
            this.whistleStep = 0;
            this.nextWhistleActionAt = DateTime.MinValue;
        }

        if (this.whistleStep >= picks.Count)
        {
            this.WhistleStatus = $"呼び笛の割り当て完了({picks.Count}体)";
            if (!this.startBattleSent)
            {
                this.startBattleSent = true;
                FireCallback(layout, 8);
                this.WhistleStatus = "戦闘開始を確認しました";
                this.log.Information("[BeastHelper] Crucible whistle: confirmed 戦闘開始.");
            }

            return;
        }

        var now = DateTime.UtcNow;
        if (now < this.nextWhistleActionAt)
        {
            return;
        }

        var target = picks[this.whistleStep];
        var row = team.IndexOf(target);
        if (row < 0)
        {
            this.whistleStep++;
            return;
        }

        FireCallback(party, 1, row);
        this.WhistleStatus = $"{this.NameOf(target)}を呼び笛{this.whistleStep + 1}に割り当て中…";
        this.log.Information("[BeastHelper] Crucible whistle: assigned {Name} (row {Row}) to whistle {Slot}.", this.NameOf(target), row, this.whistleStep + 1);
        this.whistleStep++;
        this.nextWhistleActionAt = now.AddMilliseconds(400);
    }

    private const int WhistleSize = 3;

    private List<uint> ComputeWhistlePicks(List<uint> team)
    {
        if (this.configuration.CrucibleTeamMode == CrucibleTeamMode.Leveling)
        {
            return team
                .OrderBy(no => this.beastData.FindPetByXbmRowId(no) is { } pet && this.configuration.BeastLevelProgress.TryGetValue(pet.PetRowId, out var progress) ? progress.Level : 0)
                .Take(WhistleSize)
                .ToList();
        }

        return team.Take(WhistleSize).ToList();
    }

    public void Tick()
    {
        if (!this.configuration.AutoEnterCrucibleEnabled || !this.configuration.AutoCruciblePetPartyEnabled)
        {
            this.Status = "off";
            this.step = Step.Idle;
            return;
        }

        var party = this.FindAddon(PartyAddonName);
        if (party is null || !party->IsVisible)
        {
            this.Status = "呼び笛設定画面待ち";
            this.step = Step.Idle;
            return;
        }

        var (team, capacity) = ReadTeam(party);
        if (team is null)
        {
            this.Status = "編成情報の読み取り待ち";
            return;
        }

        var goal = this.ComputeGoal(team, capacity);
        if (goal.Count == 0)
        {
            this.Status = this.configuration.CrucibleTeamMode == CrucibleTeamMode.Manual
                ? "魔獣リストで編成する魔獣を選択してください"
                : "所持している魔獣がいません";
            this.step = Step.Idle;
            return;
        }

        this.Advance(party, team, goal, DateTime.UtcNow);
    }

    private void Advance(AtkUnitBase* party, List<uint> team, List<uint> goal, DateTime now)
    {
        var waitedTooLong = now - this.stepSince > StepTimeout;

        switch (this.step)
        {
            case Step.AwaitRemoveMenu:
            {
                var menu = this.FindAddon(ContextMenuAddonName);
                if (menu is not null && menu->IsVisible)
                {
                    FireCallback(menu, 0, 0, 0);
                    this.SetStep(Step.AwaitRemoved, now);
                }
                else if (waitedTooLong)
                {
                    this.SetStep(Step.Idle, now);
                }

                return;
            }

            case Step.AwaitRemoved:
                if (!team.Contains(this.removingNumber) || waitedTooLong)
                {
                    this.SetStep(Step.Idle, now);
                }

                return;

            case Step.AwaitBestiary:
            {
                var openingNotebook = this.FindAddon(NotebookAddonName);
                if (openingNotebook is not null && openingNotebook->IsVisible)
                {
                    this.addSent = false;
                    this.SetStep(Step.Adding, now);
                }
                else if (waitedTooLong)
                {
                    this.SetStep(Step.Idle, now);
                }
                else if (now >= this.nextActionAt)
                {
                    FireCallback(party, 5);
                    this.nextActionAt = now + ActionResend;
                }

                return;
            }

            case Step.Adding:
                if (team.Contains(this.addingNumber))
                {
                    this.SetStep(Step.Idle, now);
                    return;
                }

                this.AdvanceAdd(now, waitedTooLong);
                return;
        }

        // Idle: decide the single next action.
        var extra = team.FirstOrDefault(x => !goal.Contains(x));
        if (extra != 0)
        {
            var row = team.IndexOf(extra);
            this.removingNumber = extra;
            this.Status = $"{this.NameOf(extra)}を外しています…";
            FireCallback(party, 2, row);
            this.SetStep(Step.AwaitRemoveMenu, now);
            return;
        }

        var missing = goal.FirstOrDefault(x => !team.Contains(x));
        if (missing == 0)
        {
            this.Status = $"編成完了({team.Count}/{goal.Count})";
            return;
        }

        this.addingNumber = missing;
        this.Status = $"{this.NameOf(missing)}を追加しています…";
        var notebook = this.FindAddon(NotebookAddonName);
        if (notebook is null || !notebook->IsVisible)
        {
            FireCallback(party, 5);
            this.nextActionAt = now + ActionResend;
            this.SetStep(Step.AwaitBestiary, now);
            return;
        }

        this.addSent = false;
        this.SetStep(Step.Adding, now);
    }

    private void AdvanceAdd(DateTime now, bool waitedTooLong)
    {
        var notebook = this.FindAddon(NotebookAddonName);
        if (notebook is null || !notebook->IsVisible || notebook->AtkValuesCount <= NotebookPageValueIndex)
        {
            this.SetStep(Step.Idle, now);
            return;
        }

        var slot = (int)((this.addingNumber - 1) % NotebookEntriesPerPage);
        var targetPage = (int)((this.addingNumber - 1) / NotebookEntriesPerPage);
        var currentPage = (int)ReadNumber(notebook->AtkValues[NotebookPageValueIndex]);

        if (currentPage != targetPage)
        {
            if (waitedTooLong)
            {
                this.SetStep(Step.Idle, now);
                return;
            }

            if (now >= this.nextActionAt)
            {
                FireCallback(notebook, 3, targetPage);
                this.nextActionAt = now + ActionResend;
                this.addSent = false;
            }

            return;
        }

        if (!this.addSent)
        {
            FireCallback(notebook, 5, slot);
            var param = NotebookFirstEntryParam + (uint)slot;
            var evt = FindMouseDownEvent(&notebook->UldManager, param, 0);
            if (evt is not null)
            {
                var data = new AtkEventData();
                notebook->ReceiveEvent(AtkEventType.MouseDown, (int)param, evt, &data);
            }
            else
            {
                this.log.Debug("[BeastHelper] Crucible pet party: no MouseDown event found for slot {Slot}.", slot);
            }

            this.addSent = true;
            this.stepSince = now;
            return;
        }

        if (waitedTooLong)
        {
            this.log.Information("[BeastHelper] Crucible pet party: {Name} didn't appear in the team after adding; will retry.", this.NameOf(this.addingNumber));
            this.SetStep(Step.Idle, now);
        }
    }

    private void SetStep(Step next, DateTime now)
    {
        this.step = next;
        this.stepSince = now;
        this.nextActionAt = DateTime.MinValue;
    }

    private List<uint> ComputeGoal(List<uint> team, int capacity)
    {
        if (this.configuration.CrucibleTeamMode == CrucibleTeamMode.Manual)
        {
            var ownedNumbers = this.configuration.TamedPetRowIds
                .Select(rowId => this.beastData.FindPet(rowId)?.XbmRowId)
                .Where(no => no is > 0)
                .Select(no => no!.Value)
                .ToHashSet();

            return this.configuration.CrucibleManualTeamPetRowIds
                .Select(rowId => this.beastData.FindPet(rowId)?.XbmRowId)
                .Where(no => no is > 0 && ownedNumbers.Contains(no.Value))
                .Select(no => no!.Value)
                .Distinct()
                .Take(capacity)
                .ToList();
        }

        return this.configuration.TamedPetRowIds
            .Select(rowId => (RowId: rowId, No: this.beastData.FindPet(rowId)?.XbmRowId))
            .Where(x => x.No is > 0)
            .OrderBy(x => this.configuration.BeastLevelProgress.TryGetValue(x.RowId, out var p) ? p.Level : 0)
            .ThenBy(x => x.No)
            .Select(x => x.No!.Value)
            .Take(capacity)
            .ToList();
    }

    private string NameOf(uint xbmRowId) => this.beastData.FindPetByXbmRowId(xbmRowId)?.Name ?? $"No.{xbmRowId}";

    /// <returns>Current team member numbers in row order, and the board's own capacity — or (null, 0) if the party addon's data hasn't loaded yet.</returns>
    private static (List<uint>? Team, int Capacity) ReadTeam(AtkUnitBase* party)
    {
        if (party->AtkValuesCount <= CapacityIndex)
        {
            return (null, DefaultCapacity);
        }

        var memberCount = (int)ReadNumber(party->AtkValues[MemberCountIndex]);
        var capacity = (int)ReadNumber(party->AtkValues[CapacityIndex]);
        if (capacity <= 0)
        {
            capacity = DefaultCapacity;
        }

        var team = new List<uint>(memberCount);
        for (var position = 0; position < memberCount; position++)
        {
            var start = FirstMemberIndex + position * MemberStride;
            if (start + MemberStride > party->AtkValuesCount)
            {
                return (null, capacity);
            }

            var icon = ReadNumber(party->AtkValues[start + 1]);
            var number = ReadNumber(party->AtkValues[start + 76]);
            if (number is < 1 or > 50 || icon != MemberIconBase + number)
            {
                return (null, capacity);
            }

            team.Add(number);
        }

        return (team, capacity);
    }

    private static AtkEvent* FindMouseDownEvent(AtkUldManager* uld, uint param, int depth)
    {
        if (uld is null || depth > 6)
        {
            return null;
        }

        for (var i = 0; i < uld->NodeListCount; i++)
        {
            var node = uld->NodeList[i];
            if (node is null || !node->IsVisible())
            {
                continue;
            }

            for (var evt = node->AtkEventManager.Event; evt is not null; evt = evt->NextEvent)
            {
                if (evt->State.EventType == AtkEventType.MouseDown && evt->Param == param)
                {
                    return evt;
                }
            }

            var component = node->GetComponent();
            if (component is null)
            {
                continue;
            }

            var found = FindMouseDownEvent(&component->UldManager, param, depth + 1);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static uint ReadNumber(AtkValue value) => value.Type.ToString() switch
    {
        "Int" => value.Int >= 0 ? (uint)value.Int : 0,
        "UInt" => value.UInt,
        _ => 0,
    };

    private static void FireCallback(AtkUnitBase* addon, params int[] values)
    {
        var atkValues = stackalloc AtkValue[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            atkValues[i] = new AtkValue();
            atkValues[i].SetInt(values[i]);
        }

        addon->FireCallback((uint)values.Length, atkValues, true);
    }

    private AtkUnitBase* FindAddon(string name)
    {
        var ptr = this.gameGui.GetAddonByName(name);
        return ptr.IsNull ? null : (AtkUnitBase*)ptr.Address;
    }
}

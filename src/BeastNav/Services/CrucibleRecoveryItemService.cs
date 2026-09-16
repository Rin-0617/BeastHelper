using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using HotbarSlot = FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureHotbarModule.HotbarSlot;
using HotbarSlotType = FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureHotbarModule.HotbarSlotType;

namespace BeastNav.Services;

/// <summary>
/// Auto-uses the Crucible's own in-duty recovery item ("魔獣回復薬") on the
/// player once their HP drops below <see cref="Configuration.AutoRecoveryItemHpThreshold"/>,
/// mid-fight.
/// </summary>
/// <remarks>
/// Ported from BOCXIV/Beastmaster-JP's BeastmasterCrucibleItemService. This
/// item isn't a normal inventory potion — it's distributed inside the duty
/// and can only be used through the instance's own hotbar-slot mapping (a
/// dedicated agent, AgentId 497, remaps inventory slot to hotbar display
/// slot every time it's refreshed), not a plain ActionManager.UseAction
/// call. The two native offsets it needs are found by signature scan; if
/// either fails to resolve (e.g. a future game patch changes the pattern)
/// the feature quietly disables itself instead of acting on a stale address.
/// </remarks>
/// <remarks>
/// Unattended item use — against the FFXIV ToS, self-responsibility. Off by
/// default. Only acts inside the Crucible arena territories, only in
/// combat, and only below the configured HP threshold; never targets
/// anyone but the local player.
/// </remarks>
public sealed unsafe class CrucibleRecoveryItemService
{
    private const string UseStatusSignature = "48 89 5C 24 08 48 89 74 24 10 57 48 83 EC 20 41 8B F8 48 8B D9 83 FA 0A 0F 83 ?? ?? ?? ?? 8B C2 48 8D 14 40 48 8D 34 91 0F B7 86 84 23 00 00";
    private const string RefreshMappingSignature = "48 89 5C 24 18 57 48 83 EC 30 48 8B D9 E8 ?? ?? ?? ?? 48 8B C8 E8 ?? ?? ?? ?? 48 8B F8 48 85 C0 0F 84 ?? ?? ?? ?? 48 89 6C 24 40 33 ED";
    private const int SlotCount = 10;
    private const int MappingOffset = 80;
    private const int MappingStride = 8;
    private const int InventoryOffset = 9092;
    private const int InventoryStride = 12;
    private const uint FirstRecoveryActionId = 46959;
    private static readonly ushort[] RecoveryItemPriority = [78, 77, 76];

    // 第一盤〜特二盤 (ported from IsArenaTerritory in the source repo).
    private const uint ArenaTerritoryMin = 1339;
    private const uint ArenaTerritoryMax = 1343;

    private readonly Configuration configuration;
    private readonly IObjectTable objectTable;
    private readonly ICondition condition;
    private readonly IClientState clientState;
    private readonly ISigScanner sigScanner;
    private readonly IPluginLog log;

    private delegate* unmanaged<byte*, uint, byte, uint> getUseStatus;
    private delegate* unmanaged<byte*, void> refreshMapping;
    private bool nativeInitializationAttempted;
    private bool nativeAvailable;
    private DateTime nextUseUtc = DateTime.MinValue;

    public CrucibleRecoveryItemService(Configuration configuration, IObjectTable objectTable, ICondition condition, IClientState clientState, ISigScanner sigScanner, IPluginLog log)
    {
        this.configuration = configuration;
        this.objectTable = objectTable;
        this.condition = condition;
        this.clientState = clientState;
        this.sigScanner = sigScanner;
        this.log = log;
    }

    /// <summary>Short human status for the main window.</summary>
    public string Status { get; private set; } = "off";

    public void Tick()
    {
        if (!this.configuration.AutoEnterCrucibleEnabled || !this.configuration.AutoRecoveryItemEnabled)
        {
            this.Status = "off";
            return;
        }

        if (!IsArenaTerritory(this.clientState.TerritoryType))
        {
            this.Status = "対象ゾーン外";
            return;
        }

        if (!this.condition[ConditionFlag.InCombat])
        {
            this.Status = "待機(戦闘外)";
            return;
        }

        var player = this.objectTable.LocalPlayer;
        if (player is null || player.IsDead || player.CurrentHp == 0)
        {
            return;
        }

        var hpPercent = player.MaxHp == 0 ? 100f : player.CurrentHp * 100f / player.MaxHp;
        if (hpPercent >= this.configuration.AutoRecoveryItemHpThreshold)
        {
            this.Status = $"待機中 (HP {hpPercent:0.#}% / 閾値 {this.configuration.AutoRecoveryItemHpThreshold:0.#}%)";
            return;
        }

        if (this.TryUseBestRecoveryItem(player, DateTime.UtcNow, out var itemId))
        {
            this.Status = $"回復薬を使用 (HP {hpPercent:0.#}%、item {itemId})";
            this.log.Information("[BeastHelper] Crucible recovery item: used item {ItemId} at HP {Hp:0.#}%.", itemId, hpPercent);
        }
        else
        {
            this.Status = $"回復薬が使用できません (HP {hpPercent:0.#}%)";
        }
    }

    private bool TryUseBestRecoveryItem(IPlayerCharacter player, DateTime now, out ushort itemId)
    {
        itemId = 0;
        if (now < this.nextUseUtc || !this.InitializeNative())
        {
            return false;
        }

        var actionManager = ActionManager.Instance();
        var hotbar = RaptureHotbarModule.Instance();
        var targets = TargetSystem.Instance();
        var agentModule = AgentModule.Instance();
        var agent = agentModule == null ? null : (byte*)agentModule->GetAgentByInternalId((AgentId)497);
        var eventFramework = EventFramework.Instance();
        var director = eventFramework == null ? null : eventFramework->GetInstanceContentDirector();
        if (actionManager == null || actionManager->AnimationLock > 0f
            || hotbar == null || targets == null || agent == null || director == null
            || (int)director->InstanceContentType != 22)
        {
            return false;
        }

        var self = (GameObject*)player.Address;
        this.refreshMapping(agent);
        foreach (var recoveryItemId in RecoveryItemPriority)
        {
            var displaySlot = FindDisplaySlot(agent, (byte*)director, recoveryItemId, out var inventorySlot);
            if (displaySlot < 0
                || this.getUseStatus((byte*)director, inventorySlot, 0) != 0
                || !CanUseOnSelf(recoveryItemId, self))
            {
                continue;
            }

            var slot = new HotbarSlot
            {
                CommandType = (HotbarSlotType)36,
                CommandId = (uint)displaySlot,
            };
            var previousSoftTarget = targets->SoftTarget;
            try
            {
                targets->SoftTarget = self;
                hotbar->ExecuteSlot(&slot);
                itemId = recoveryItemId;
                this.nextUseUtc = now.AddSeconds(2);
                return true;
            }
            finally
            {
                targets->SoftTarget = previousSoftTarget;
            }
        }

        return false;
    }

    private static int FindDisplaySlot(byte* agent, byte* director, ushort wantedItemId, out uint inventorySlot)
    {
        inventorySlot = SlotCount;
        for (var displaySlot = 0; displaySlot < SlotCount; displaySlot++)
        {
            var entry = agent + MappingOffset + displaySlot * MappingStride;
            var candidateInventorySlot = *(uint*)entry;
            var itemId = ((ushort*)entry)[2];
            if (candidateInventorySlot < SlotCount
                && itemId == wantedItemId
                && *(ushort*)(director + InventoryOffset + candidateInventorySlot * InventoryStride) == itemId)
            {
                inventorySlot = candidateInventorySlot;
                return displaySlot;
            }
        }

        return -1;
    }

    private bool InitializeNative()
    {
        if (this.nativeAvailable)
        {
            return true;
        }

        if (this.nativeInitializationAttempted)
        {
            return false;
        }

        this.nativeInitializationAttempted = true;
        if (!this.sigScanner.TryScanText(UseStatusSignature, out var statusAddress)
            || !this.sigScanner.TryScanText(RefreshMappingSignature, out var refreshAddress))
        {
            this.log.Warning("[BeastHelper] Crucible recovery item: native signature scan failed; disabling auto-use.");
            return false;
        }

        this.getUseStatus = (delegate* unmanaged<byte*, uint, byte, uint>)statusAddress;
        this.refreshMapping = (delegate* unmanaged<byte*, void>)refreshAddress;
        this.nativeAvailable = true;
        return true;
    }

    private static bool CanUseOnSelf(ushort itemId, GameObject* self)
    {
        var actionId = FirstRecoveryActionId + itemId - RecoveryItemPriority[^1];
        return ActionManager.CanUseActionOnTarget(actionId, self)
            && ActionManager.GetActionInRangeOrLoS(actionId, self, self) == 0;
    }

    private static bool IsArenaTerritory(uint territoryId)
        => territoryId is >= ArenaTerritoryMin and <= ArenaTerritoryMax;
}

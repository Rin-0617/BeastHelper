using BeastNav.Models;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using DalamudObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;
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
/// Auto-uses the Crucible's own in-duty items — the recovery potion
/// ("魔獣回復薬" and its variants) on the player below
/// <see cref="Configuration.AutoRecoveryItemHpThreshold"/>, self-buff items
/// (回避の書/反射の書/時の砂/魔獣剛力薬) on the player as soon as they're off
/// cooldown, and offensive items (吸血鬼の牙 and the six elemental 牙) on the
/// current hostile target as soon as they're off cooldown.
/// </summary>
/// <remarks>
/// None of these are normal inventory potions — they're distributed inside
/// the duty and can only be used through the instance's own hotbar-slot
/// mapping (a dedicated agent, AgentId 497, remaps inventory slot to hotbar
/// display slot every time it's refreshed), not a plain ActionManager.UseAction
/// call. Ported from BOCXIV/Beastmaster-JP's BeastmasterCrucibleItemService,
/// whose 2026-09-17 update widened its own item list from just the 3 recovery
/// tiers to the full set below — the item ids, the shared 2-second cooldown
/// across all of them, and the "no CanUseOnSelf gate on non-recovery items"
/// behavior are all carried over as-is. The two native offsets it needs are
/// found by signature scan; if either fails to resolve (e.g. a future game
/// patch changes the pattern) the feature quietly disables itself instead of
/// acting on a stale address.
/// </remarks>
/// <remarks>
/// Self-buff and offensive items use a simple "use it the instant it's
/// available" policy — no attempt is made to time a buff against an
/// incoming attack, or to match an elemental 牙 against the target's actual
/// weakness (unlike GluttonyCombo's BST_CrucibleAdvisor, which does score
/// targets by elemental weakness offline; that data isn't wired up here).
/// </remarks>
/// <remarks>
/// Unattended item use — against the FFXIV ToS, self-responsibility. Off by
/// default. Only acts inside the Crucible arena territories, only in
/// combat, and only against the local player (recovery/self-buff) or the
/// player's own current hostile target (offensive); never anyone else.
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
    private const ushort FirstRecoveryItemId = 76;
    private static readonly TimeSpan UseInterval = TimeSpan.FromSeconds(2);

    // 1級/2級/3級/4級魔獣回復薬、魔獣回復薬セット、1〜3級魔獣薬粉、魔獣吸血薬 — best (most valuable) first.
    private static readonly ushort[] RecoveryItemPriority = [140, 79, 78, 77, 76, 82, 81, 80, 135];

    // 回避の書、反射の書、時の砂、魔獣剛力薬 — self-targeted, no HP condition, just "use when available".
    private static readonly ushort[] SelfBuffItemPriority = [137, 136, 138, 104];

    // 吸血鬼の牙、風/土/雷/水/氷/火の牙 — target-facing; tried in a fixed order, not weakness-matched.
    private static readonly ushort[] OffensiveItemPriority = [134, 133, 132, 131, 130, 129, 128];

    /// <summary>Every Crucible item id BeastHelper knows the name of — for building a rule-editor dropdown.</summary>
    public static readonly ushort[] KnownItemIds =
        [..RecoveryItemPriority, ..SelfBuffItemPriority, ..OffensiveItemPriority];

    // 第一盤〜特二盤 (ported from IsArenaTerritory in the source repo).
    private const uint ArenaTerritoryMin = 1339;
    private const uint ArenaTerritoryMax = 1343;

    private readonly Configuration configuration;
    private readonly IObjectTable objectTable;
    private readonly ITargetManager targetManager;
    private readonly ICondition condition;
    private readonly IClientState clientState;
    private readonly ISigScanner sigScanner;
    private readonly IPluginLog log;

    private delegate* unmanaged<byte*, uint, byte, uint> getUseStatus;
    private delegate* unmanaged<byte*, void> refreshMapping;
    private bool nativeInitializationAttempted;
    private bool nativeAvailable;
    private DateTime nextUseUtc = DateTime.MinValue;

    public CrucibleRecoveryItemService(Configuration configuration, IObjectTable objectTable, ITargetManager targetManager, ICondition condition, IClientState clientState, ISigScanner sigScanner, IPluginLog log)
    {
        this.configuration = configuration;
        this.objectTable = objectTable;
        this.targetManager = targetManager;
        this.condition = condition;
        this.clientState = clientState;
        this.sigScanner = sigScanner;
        this.log = log;
    }

    /// <summary>Short human status for the main window.</summary>
    public string Status { get; private set; } = "off";

    public void Tick()
    {
        if (!this.configuration.AutoEnterCrucibleEnabled
            || (!this.configuration.AutoRecoveryItemEnabled && !this.configuration.AutoSelfBuffItemsEnabled
                && !this.configuration.AutoOffensiveItemsEnabled && !this.configuration.AutoCrucibleItemRulesEnabled))
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

        var now = DateTime.UtcNow;

        if (this.configuration.AutoCrucibleItemRulesEnabled && this.TryUseRules(player, now, out var ruleItemId))
        {
            this.Status = $"{GetItemName(ruleItemId)}を使用(ルール)";
            this.log.Information("[BeastHelper] Crucible item: used item {ItemId} via rule.", ruleItemId);
            return;
        }

        if (this.configuration.AutoRecoveryItemEnabled)
        {
            var hpPercent = player.MaxHp == 0 ? 100f : player.CurrentHp * 100f / player.MaxHp;
            if (hpPercent < this.configuration.AutoRecoveryItemHpThreshold)
            {
                if (this.TryUseItem(RecoveryItemPriority, player, null, now, requireActionRangeCheck: true, out var itemId))
                {
                    this.Status = $"{GetItemName(itemId)}を使用 (HP {hpPercent:0.#}%)";
                    this.log.Information("[BeastHelper] Crucible item: used recovery item {ItemId} at HP {Hp:0.#}%.", itemId, hpPercent);
                    return;
                }

                this.Status = $"回復薬が使用できません (HP {hpPercent:0.#}%)";
            }
        }

        if (this.configuration.AutoSelfBuffItemsEnabled
            && this.TryUseItem(SelfBuffItemPriority, player, null, now, requireActionRangeCheck: false, out var buffItemId))
        {
            this.Status = $"{GetItemName(buffItemId)}を使用";
            this.log.Information("[BeastHelper] Crucible item: used self-buff item {ItemId}.", buffItemId);
            return;
        }

        if (this.configuration.AutoOffensiveItemsEnabled)
        {
            var target = this.targetManager.Target;
            if (target is IBattleChara { IsDead: false } enemy && enemy.ObjectKind == DalamudObjectKind.BattleNpc)
            {
                if (this.TryUseItem(OffensiveItemPriority, player, enemy, now, requireActionRangeCheck: false, out var offensiveItemId))
                {
                    this.Status = $"{GetItemName(offensiveItemId)}を{enemy.Name}に使用";
                    this.log.Information("[BeastHelper] Crucible item: used offensive item {ItemId} on {Target}.", offensiveItemId, enemy.Name);
                    return;
                }
            }
        }

        this.Status = "待機中";
    }

    /// <summary>
    /// Tries each item in <paramref name="priority"/>, in order, against
    /// <paramref name="target"/> (or the player themself when null), using
    /// the shared cooldown and the same hotbar-slot-mapping mechanism for
    /// all item kinds. <paramref name="requireActionRangeCheck"/> mirrors
    /// upstream's own behavior: only the recovery-item path additionally
    /// checks <see cref="CanUseOnSelf"/> before committing.
    /// </summary>
    private bool TryUseItem(ushort[] priority, IPlayerCharacter player, IBattleChara? target, DateTime now, bool requireActionRangeCheck, out ushort itemId)
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
        var effectiveTarget = target is null ? self : (GameObject*)target.Address;

        this.refreshMapping(agent);
        foreach (var candidateItemId in priority)
        {
            var displaySlot = FindDisplaySlot(agent, (byte*)director, candidateItemId, out var inventorySlot);
            if (displaySlot < 0 || this.getUseStatus((byte*)director, inventorySlot, 0) != 0)
            {
                continue;
            }

            if (requireActionRangeCheck && !CanUseOnSelf(candidateItemId, self))
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
                targets->SoftTarget = effectiveTarget;
                hotbar->ExecuteSlot(&slot);
                itemId = candidateItemId;
                this.nextUseUtc = now.Add(UseInterval);
                return true;
            }
            finally
            {
                targets->SoftTarget = previousSoftTarget;
            }
        }

        return false;
    }

    /// <summary>
    /// Tries each enabled rule in list order; the first whose conditions are
    /// satisfied against the current player/target and whose item is
    /// actually usable wins. Rules for an item in <see cref="OffensiveItemPriority"/>
    /// require a live, targetable hostile target; everything else targets
    /// the player. Shares the same cooldown as every other item use.
    /// </summary>
    private bool TryUseRules(IPlayerCharacter player, DateTime now, out ushort itemId)
    {
        itemId = 0;
        if (this.configuration.CrucibleItemRules.Count == 0)
        {
            return false;
        }

        var target = this.targetManager.Target as IBattleChara;
        foreach (var rule in this.configuration.CrucibleItemRules)
        {
            if (!rule.Enabled || rule.ItemId == 0 || rule.Conditions.Count == 0 || !EvaluateRule(rule, player, target))
            {
                continue;
            }

            var offensive = Array.IndexOf(OffensiveItemPriority, rule.ItemId) >= 0;
            if (offensive && (target is null || target.IsDead))
            {
                continue;
            }

            if (this.TryUseItem([rule.ItemId], player, offensive ? target : null, now, requireActionRangeCheck: false, out itemId))
            {
                return true;
            }
        }

        return false;
    }

    private static bool EvaluateRule(CrucibleItemRule rule, IPlayerCharacter player, IBattleChara? target)
    {
        var all = rule.ConditionJoinMode == CrucibleItemRuleConditionJoinMode.All;
        foreach (var condition in rule.Conditions)
        {
            var result = EvaluateCondition(condition, player, target);
            if (all && !result)
            {
                return false;
            }

            if (!all && result)
            {
                return true;
            }
        }

        return all;
    }

    private static bool EvaluateCondition(CrucibleItemRuleCondition condition, IPlayerCharacter player, IBattleChara? target)
        => condition.Type switch
        {
            CrucibleItemRuleConditionType.SelfStatus => EvaluateStatus(player, condition),
            CrucibleItemRuleConditionType.TargetStatus => target is not null && EvaluateStatus(target, condition),
            CrucibleItemRuleConditionType.SelfHp => EvaluateHp(player.CurrentHp, player.MaxHp, condition),
            CrucibleItemRuleConditionType.TargetHp => target is not null && EvaluateHp(target.CurrentHp, target.MaxHp, condition),
            CrucibleItemRuleConditionType.TargetDataId => target is not null && target.BaseId == condition.DataId,
            _ => false,
        };

    private static bool EvaluateStatus(IBattleChara character, CrucibleItemRuleCondition condition)
    {
        var present = false;
        foreach (var status in character.StatusList)
        {
            if (status.StatusId == condition.StatusId)
            {
                present = true;
                break;
            }
        }

        return condition.StatusCondition == CrucibleItemRuleStatusCondition.Present ? present : !present;
    }

    private static bool EvaluateHp(uint currentHp, uint maxHp, CrucibleItemRuleCondition condition)
    {
        var percent = maxHp == 0 ? 100f : currentHp * 100f / maxHp;
        return condition.HpCondition == CrucibleItemRuleHpCondition.Above ? percent > condition.HpThreshold : percent < condition.HpThreshold;
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
            this.log.Warning("[BeastHelper] Crucible item: native signature scan failed; disabling auto-use.");
            return false;
        }

        this.getUseStatus = (delegate* unmanaged<byte*, uint, byte, uint>)statusAddress;
        this.refreshMapping = (delegate* unmanaged<byte*, void>)refreshAddress;
        this.nativeAvailable = true;
        return true;
    }

    private static bool CanUseOnSelf(ushort itemId, GameObject* self)
    {
        var actionId = FirstRecoveryActionId + itemId - FirstRecoveryItemId;
        return ActionManager.CanUseActionOnTarget(actionId, self)
            && ActionManager.GetActionInRangeOrLoS(actionId, self, self) == 0;
    }

    private static bool IsArenaTerritory(uint territoryId)
        => territoryId is >= ArenaTerritoryMin and <= ArenaTerritoryMax;

    public static string GetItemName(ushort itemId) => itemId switch
    {
        76 => "1級魔獣回復薬",
        77 => "2級魔獣回復薬",
        78 => "3級魔獣回復薬",
        79 => "4級魔獣回復薬",
        80 => "1級魔獣薬粉",
        81 => "2級魔獣薬粉",
        82 => "3級魔獣薬粉",
        104 => "魔獣剛力薬",
        128 => "火の牙",
        129 => "氷の牙",
        130 => "水の牙",
        131 => "雷の牙",
        132 => "土の牙",
        133 => "風の牙",
        134 => "吸血鬼の牙",
        135 => "魔獣吸血薬",
        136 => "反射の書",
        137 => "回避の書",
        138 => "時の砂",
        140 => "魔獣回復薬セット",
        _ => $"アイテム{itemId}",
    };
}

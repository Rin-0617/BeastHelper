using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace BeastNav.Crucible;

/// <summary>
/// The opt-in combat autopilot: targets the nearest live enemy and fires the
/// first ready action from action bar 1 each opening. Off by default; dodging
/// takes priority over it.
/// </summary>
/// <remarks>
/// This drives combat actions automatically, which is against the FFXIV ToS. It
/// exists so the whole first-stage loop (travel, dodge, fight) can run hands-off
/// while the dodge assist is being tuned.
/// </remarks>
public sealed unsafe class CrucibleCombatAssist
{
    // Action bar 1 (0-indexed), all twelve slots.
    private const uint HotbarIndex = 0;
    private const uint SlotCount = 12;

    private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(150);

    private readonly Configuration configuration;
    private readonly IObjectTable objectTable;
    private readonly ITargetManager targetManager;
    private readonly ICondition condition;
    private readonly IPluginLog log;

    private DateTime lastAttempt;

    public CrucibleCombatAssist(
        Configuration configuration,
        IObjectTable objectTable,
        ITargetManager targetManager,
        ICondition condition,
        IPluginLog log)
    {
        this.configuration = configuration;
        this.objectTable = objectTable;
        this.targetManager = targetManager;
        this.condition = condition;
        this.log = log;
    }

    public void Tick(CrucibleState state, bool dodging)
    {
        if (!this.configuration.CrucibleAutoCombat || dodging)
        {
            return;
        }

        if (!state.InCrucible || !state.HasPlayer || !state.InCombat)
        {
            return;
        }

        if (this.condition[ConditionFlag.BetweenAreas]
            || this.condition[ConditionFlag.BetweenAreas51]
            || this.condition[ConditionFlag.Occupied38]
            || this.condition[ConditionFlag.Casting])
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (now - this.lastAttempt < MinInterval)
        {
            return;
        }

        this.lastAttempt = now;

        var target = state.Enemies
            .Where(static enemy => enemy.CurrentHp > 0)
            .OrderBy(static enemy => enemy.Distance)
            .FirstOrDefault();
        if (target is null)
        {
            return;
        }

        if (this.targetManager.Target?.GameObjectId != target.GameObjectId)
        {
            var obj = this.objectTable.FirstOrDefault(o => o.GameObjectId == target.GameObjectId);
            if (obj is not null)
            {
                this.targetManager.Target = obj;
            }
        }

        var actions = ActionManager.Instance();
        if (actions is null || actions->AnimationLock > 0.1f)
        {
            return;
        }

        var hotbar = RaptureHotbarModule.Instance();
        if (hotbar is null)
        {
            return;
        }

        for (var slot = 0u; slot < SlotCount; slot++)
        {
            var s = hotbar->GetSlotById(HotbarIndex, slot);
            if (s is null || s->ApparentSlotType != RaptureHotbarModule.HotbarSlotType.Action)
            {
                continue;
            }

            var id = s->ApparentActionId;
            if (id == 0 || actions->GetActionStatus(ActionType.Action, id, target.GameObjectId) != 0)
            {
                continue;
            }

            actions->UseAction(ActionType.Action, id, target.GameObjectId);
            return;
        }
    }
}

using System.Numerics;
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

    /// <summary>
    /// Targets the nearest enemy and fires an action. Returns what it wants from
    /// the movement brain: whether it has a target and whether that target is in
    /// range of anything on the bar (if not, the autopilot should close the gap).
    /// </summary>
    public CombatIntent Tick(CrucibleState state, bool suppressed)
    {
        if (!this.configuration.CrucibleAutoCombat || suppressed
            || !state.InCrucible || !state.HasPlayer || !state.InCombat)
        {
            return CombatIntent.None;
        }

        var target = state.Enemies
            .Where(static enemy => enemy.CurrentHp > 0)
            .OrderBy(static enemy => enemy.Distance)
            .FirstOrDefault();
        if (target is null)
        {
            return CombatIntent.None;
        }

        if (this.targetManager.Target?.GameObjectId != target.GameObjectId)
        {
            var obj = this.objectTable.FirstOrDefault(o => o.GameObjectId == target.GameObjectId);
            if (obj is not null)
            {
                this.targetManager.Target = obj;
            }
        }

        var intent = new CombatIntent { HasTarget = true, TargetPosition = target.Position, TargetDistance = target.Distance };

        if (this.condition[ConditionFlag.BetweenAreas]
            || this.condition[ConditionFlag.BetweenAreas51]
            || this.condition[ConditionFlag.Casting])
        {
            return intent;
        }

        var now = DateTime.UtcNow;
        if (now - this.lastAttempt < MinInterval)
        {
            return intent;
        }

        this.lastAttempt = now;

        var actions = ActionManager.Instance();
        var hotbar = RaptureHotbarModule.Instance();
        if (actions is null || hotbar is null || actions->AnimationLock > 0.1f)
        {
            return intent;
        }

        var anyUsable = false;
        for (var slot = 0u; slot < SlotCount; slot++)
        {
            var s = hotbar->GetSlotById(HotbarIndex, slot);
            if (s is null || s->ApparentSlotType != RaptureHotbarModule.HotbarSlotType.Action)
            {
                continue;
            }

            var id = s->ApparentActionId;
            if (id == 0)
            {
                continue;
            }

            var status = actions->GetActionStatus(ActionType.Action, id, target.GameObjectId);
            if (status == 0)
            {
                actions->UseAction(ActionType.Action, id, target.GameObjectId);
                return intent with { InActionRange = true };
            }

            // 566 = "target out of range"; anything else means it's just on cooldown.
            if (status != 566)
            {
                anyUsable = true;
            }
        }

        intent = intent with { InActionRange = anyUsable };
        return intent;
    }
}

public readonly record struct CombatIntent
{
    public static CombatIntent None => default;

    public bool HasTarget { get; init; }

    public Vector3 TargetPosition { get; init; }

    public float TargetDistance { get; init; }

    /// <summary>The target is in range of at least one bar action (no need to close in).</summary>
    public bool InActionRange { get; init; }
}

using System.Numerics;
using BeastNav.Services;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using LuminaAction = Lumina.Excel.Sheets.Action;

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
    private readonly IDataManager dataManager;
    private readonly WrathComboBridge wrath;
    private readonly IPluginLog log;
    private readonly Dictionary<uint, bool> aoeCache = [];

    private DateTime lastAttempt;
    private bool wrathRotationOn;

    /// <summary>Short human status for the overlay, e.g. "firing 46921" or "no ready action".</summary>
    public string Status { get; private set; } = "idle";

    public CrucibleCombatAssist(
        Configuration configuration,
        IObjectTable objectTable,
        ITargetManager targetManager,
        ICondition condition,
        IDataManager dataManager,
        WrathComboBridge wrath,
        IPluginLog log)
    {
        this.configuration = configuration;
        this.objectTable = objectTable;
        this.targetManager = targetManager;
        this.condition = condition;
        this.dataManager = dataManager;
        this.wrath = wrath;
        this.log = log;
    }

    /// <summary>Turn off WrathCombo auto-rotation if we had turned it on.</summary>
    public void Release()
    {
        if (this.wrathRotationOn)
        {
            this.wrath.SetAutoRotation(false);
            this.wrathRotationOn = false;
        }

        this.wrath.Release();
    }

    /// <summary>
    /// Targets the nearest enemy and fires an action. Returns what it wants from
    /// the movement brain: whether it has a target and whether that target is in
    /// range of anything on the bar (if not, the autopilot should close the gap).
    /// </summary>
    public CombatIntent Tick(CrucibleState state, bool suppressed)
    {
        var enabled = this.configuration.CrucibleAutoCombat && !suppressed && state.InCrucible && state.HasPlayer;
        var want = enabled && state.InCombat;

        // Delegate the rotation to WrathCombo only when explicitly asked.
        if (this.configuration.CrucibleAutoCombat && this.configuration.CrucibleUseWrathCombo && this.wrath.Available)
        {
            if (want != this.wrathRotationOn)
            {
                this.wrath.SetAutoRotation(want);
                this.wrathRotationOn = want;
            }

            this.Status = want ? "WrathCombo rotation on" : "WrathCombo (out of combat)";
            var t = NearestLive(state);
            return t is null
                ? CombatIntent.None
                : new CombatIntent
                {
                    HasTarget = true,
                    TargetPosition = t.Position,
                    TargetDistance = t.Distance,
                    InActionRange = t.Distance <= 22f,
                };
        }

        if (this.wrathRotationOn)
        {
            this.wrath.SetAutoRotation(false);
            this.wrathRotationOn = false;
        }

        if (!this.configuration.CrucibleAutoCombat)
        {
            this.Status = "off";
            return CombatIntent.None;
        }

        if (!want)
        {
            this.Status = suppressed ? "paused (dodge)" : "waiting for combat";
            return NearestLive(state) is { } near
                ? new CombatIntent { HasTarget = true, TargetPosition = near.Position, TargetDistance = near.Distance }
                : CombatIntent.None;
        }

        var target = NearestLive(state);
        if (target is null)
        {
            this.Status = "no enemy";
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
            this.Status = "occupied";
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
        if (actions is null || hotbar is null)
        {
            this.Status = "no ActionManager";
            return intent;
        }

        if (actions->AnimationLock > 0.1f)
        {
            return intent;
        }

        // Prefer AoE actions when the target sits in a cluster.
        var clustered = state.Enemies.Count(e =>
            e.CurrentHp > 0 && Vector3.DistanceSquared(e.Position, target.Position) <= 25f) >= 3;

        var anyUsable = false;
        for (var pass = 0; pass < 2; pass++)
        {
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

                // First pass: only the preferred kind (AoE if clustered, else single-target).
                if (pass == 0 && this.IsAoe(id) != clustered)
                {
                    continue;
                }

                var status = actions->GetActionStatus(ActionType.Action, id, target.GameObjectId);
                if (status == 0)
                {
                    actions->UseAction(ActionType.Action, id, target.GameObjectId);
                    this.Status = $"firing {id}";
                    return intent with { InActionRange = true };
                }

                // 566 = "target out of range"; anything else is just a cooldown.
                if (status != 566)
                {
                    anyUsable = true;
                }
            }
        }

        this.Status = anyUsable ? "all on cooldown" : "nothing in range / on bar";
        intent = intent with { InActionRange = anyUsable };
        return intent;
    }

    private bool IsAoe(uint actionId)
    {
        if (this.aoeCache.TryGetValue(actionId, out var cached))
        {
            return cached;
        }

        var aoe = false;
        try
        {
            var row = this.dataManager.GetExcelSheet<LuminaAction>()?.GetRowOrDefault(actionId);
            if (row is not null)
            {
                aoe = row.Value.CastType != 1 || row.Value.EffectRange > 0;
            }
        }
        catch
        {
            // Treat unknown actions as single-target.
        }

        this.aoeCache[actionId] = aoe;
        return aoe;
    }

    private static CrucibleEnemy? NearestLive(CrucibleState state)
        => state.Enemies
            .Where(static enemy => enemy.CurrentHp > 0)
            .OrderBy(static enemy => enemy.Distance)
            .FirstOrDefault();
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

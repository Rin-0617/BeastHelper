using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace BeastNav.Services;

/// <summary>
/// Walks the character to a nearby hostile enemy (like AutoDuty's own
/// MovementHelper), then hands combat off to RotationSolverReborn (RSR) via
/// its own chat commands once close.
/// </summary>
/// <remarks>
/// Approach movement was tried once before and removed for being unstable
/// ("動きが安定しない") — that version re-picked "the nearest enemy" fresh
/// every tick and re-issued NavmeshService.MoveCloseTo with whatever
/// position came back, which resets NavmeshService's whole request (forces
/// a re-snap-to-mesh, resets its retry count) on every call. If the nearest
/// candidate flickered between two enemies, or just jittered slightly frame
/// to frame, the destination kept changing and the path kept restarting.
/// This version sticks to one tracked enemy by GameObjectId once picked
/// (<see cref="trackedEnemyId"/>) and keeps re-issuing MoveCloseTo toward
/// that same enemy every tick while approaching — matching the exact
/// pattern CrucibleEntryService already uses safely to walk to the entry
/// NPC (and the same guarded-repeated-call shape AutoDuty's own
/// MovementHelper.Move uses: call every tick, but the destination doesn't
/// jump around). Only forgets the tracked enemy when it actually dies/
/// disappears, or on any of the safety gates below — never because a
/// nearer one showed up.
/// </remarks>
/// <remarks>
/// Movement stops (and RSR takes over positioning, if it has any) as soon
/// as <see cref="ConditionFlag.InCombat"/> is set — this service only gets
/// the character into engage range, not further.
/// </remarks>
/// <remarks>
/// Also used to force-use "ひきつけろ" at low HP via RSR's ActionCommand
/// IPC, but that turned out unreliable in practice (per user testing) and
/// stays removed — this only ever flips RSR's Auto/Off state and drives
/// approach movement; emergency defensives are on the player.
/// </remarks>
/// <remarks>
/// On/off goes through RSR's own chat commands ("/rotation Auto" /
/// "/rotation Off", each registered as its own exact command — confirmed
/// against RotationSolverReborn's own source) via <see cref="ICommandManager"/>,
/// the same technique used for SND macros elsewhere in this plugin. An
/// earlier version called RSR's ChangeOperatingMode IPC directly with a
/// locally mirrored copy of its StateCommandType enum (the same trick
/// AutoDuty-adjacent plugins use to call another plugin's IPC without adding
/// it as a project reference), but that silently never actually changed
/// RSR's own state — Dalamud's IPC dispatch didn't accept a same-shaped but
/// differently-typed enum the way primitives interoperate.
/// </remarks>
/// <remarks>
/// Unattended movement and combat automation — against the FFXIV ToS,
/// self-responsibility. Off by default. Never changes rotation settings,
/// positionals, or actions directly — that's entirely RSR's own job once
/// engaged.
/// </remarks>
public sealed class CrucibleCombatService
{
    // Wide enough to cover a whole board segment, not just melee-adjacent —
    // a board's move→fight→move→fight flow (each stage often separated by
    // its own "イベント中" cutscene) relies on the next enemy being pickable
    // as soon as it's found, however far away it first appears. NavmeshService
    // already walks the full length of a zone elsewhere (destination
    // teleport-and-walk), so distance itself isn't the limiting factor here —
    // this is just a sanity cap against stray object-table entries.
    private const float DetectionRange = 200f;

    // How close to get before letting RSR/the fight itself take over
    // positioning — a tight melee-adjacent buffer, not the wider detection net above.
    private const float EngageRange = 4f;

    // "イベント中" (confirmed against Lumina's Status sheet: LockActions and
    // LockControl are both set) — the status the board's intro applies while
    // it plays, before the player is actually able to move or act.
    private const uint EventBuffStatusId = 1268;

    // Exact chat commands RSR registers for these two states (RotationSolver.Basic/Service.cs).
    private const string RsrAutoCommand = "/rotation Auto";
    private const string RsrOffCommand = "/rotation Off";

    // 第一盤〜特二盤 — the Crucible arena territories (same range as
    // CrucibleRecoveryItemService / CrucibleAutoDutyIpcService). Only act
    // inside these: anywhere else (towns, the overworld, the entry zone) any
    // targetable BattleNpc — a striking dummy (木人), wildlife — was getting
    // picked up as "the nearest enemy", turning RSR on and dragging the
    // player toward it with vnavmesh every frame.
    private const uint ArenaTerritoryMin = 1339;
    private const uint ArenaTerritoryMax = 1343;

    private readonly Configuration configuration;
    private readonly IObjectTable objectTable;
    private readonly ICondition condition;
    private readonly IClientState clientState;
    private readonly ICommandManager commandManager;
    private readonly NavmeshService navmesh;
    private readonly IPluginLog log;
    private readonly ICallGateSubscriber<bool> autorotationActive;

    private bool weEnabledRsr;
    private ulong trackedEnemyId;

    public CrucibleCombatService(Configuration configuration, IObjectTable objectTable, ICondition condition, IClientState clientState, ICommandManager commandManager, NavmeshService navmesh, IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.configuration = configuration;
        this.objectTable = objectTable;
        this.condition = condition;
        this.clientState = clientState;
        this.commandManager = commandManager;
        this.navmesh = navmesh;
        this.log = log;
        this.autorotationActive = pluginInterface.GetIpcSubscriber<bool>("RotationSolverReborn.AutorotationActive");
    }

    /// <summary>Short human status for the main window.</summary>
    public string Status { get; private set; } = "off";

    /// <summary>
    /// RSR's own report of whether its autorotation is active — not just
    /// whether we asked it to be. Lets the UI show when the IPC call
    /// "succeeded" but RSR itself never actually switched on (e.g. it's not
    /// installed, or refuses this content for a reason of its own).
    /// </summary>
    public bool RsrReportsActive
    {
        get
        {
            try
            {
                return this.autorotationActive.InvokeFunc();
            }
            catch
            {
                return false;
            }
        }
    }

    public void Tick()
    {
        if (!this.configuration.AutoEnterCrucibleEnabled || !this.configuration.AutoCombatEnabled)
        {
            this.DisableRsrIfWeEnabledIt();
            this.ForgetTrackedEnemy();
            this.Status = "off";
            return;
        }

        if (!IsArenaTerritory(this.clientState.TerritoryType))
        {
            this.DisableRsrIfWeEnabledIt();
            this.ForgetTrackedEnemy();
            this.Status = "対象ゾーン外";
            return;
        }

        var player = this.objectTable.LocalPlayer;
        if (player is null)
        {
            return;
        }

        // Leaving content mid-fight (withdraw, or the fight just ending)
        // starts a zone transition where objects are being torn down —
        // touching a stale enemy reference in that window risked crashing
        // the game.
        if (this.condition[ConditionFlag.BetweenAreas]
            || this.condition[ConditionFlag.BetweenAreas51]
            || this.condition[ConditionFlag.OccupiedInCutSceneEvent])
        {
            this.DisableRsrIfWeEnabledIt();
            this.ForgetTrackedEnemy();
            this.Status = "待機(ゾーン移動中)";
            return;
        }

        // "イベント中" (status 1268, LockActions/LockControl both set) — the
        // board's intro plays with this on; nothing here should act yet.
        if (HasStatus(player, EventBuffStatusId))
        {
            this.ForgetTrackedEnemy();
            this.Status = "待機(イベント中)";
            return;
        }

        var tracked = this.FindTrackedEnemy();
        if (tracked is null && this.trackedEnemyId != 0)
        {
            // Was tracking someone; they're dead or gone now — stop moving
            // toward wherever they used to be.
            this.ForgetTrackedEnemy();
        }

        if (tracked is null)
        {
            tracked = this.FindNearestEnemy(player.Position);
            if (tracked is not null)
            {
                this.trackedEnemyId = tracked.GameObjectId;
                this.log.Information("[BeastHelper] Crucible combat: now tracking {Name}.", tracked.Name);
            }
        }

        if (tracked is null)
        {
            this.DisableRsrIfWeEnabledIt();
            this.Status = "敵未検出";
            return;
        }

        // Enabled as soon as a target is tracked (even before in range) so
        // RSR is ready the moment a pet-summon cast or the fight itself
        // starts — it stays idle on an out-of-range target on its own (see
        // CrucibleCombatService's own remarks on RSR's range-gated targeting).
        this.EnableRsrIfNeeded();

        if (this.condition[ConditionFlag.InCombat])
        {
            this.Status = $"戦闘中: {tracked.Name}";
            return;
        }

        this.navmesh.MoveCloseTo(tracked.Position, false, EngageRange);
        this.Status = $"接近中: {tracked.Name}";
    }

    /// <summary>Stops re-picking "the tracked enemy" once it dies or leaves the object table; also cancels any in-flight approach toward it.</summary>
    private void ForgetTrackedEnemy()
    {
        if (this.trackedEnemyId == 0)
        {
            return;
        }

        this.trackedEnemyId = 0;
        this.navmesh.Stop();
    }

    /// <summary>Resolves the currently tracked enemy by id, or null if it's gone/dead/no longer targetable.</summary>
    private IGameObject? FindTrackedEnemy()
    {
        if (this.trackedEnemyId == 0)
        {
            return null;
        }

        foreach (var obj in this.objectTable)
        {
            if (obj.GameObjectId != this.trackedEnemyId)
            {
                continue;
            }

            return obj.ObjectKind == ObjectKind.BattleNpc && obj.IsTargetable && !obj.IsDead ? obj : null;
        }

        return null;
    }

    private void EnableRsrIfNeeded()
    {
        if (this.weEnabledRsr)
        {
            return;
        }

        try
        {
            this.commandManager.ProcessCommand(RsrAutoCommand);
            this.weEnabledRsr = true;
            this.log.Information("[BeastHelper] Crucible combat: enabled RotationSolverReborn (Auto).");
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "[BeastHelper] Crucible combat: failed to enable RotationSolverReborn (is it installed?).");
        }
    }

    private void DisableRsrIfWeEnabledIt()
    {
        if (!this.weEnabledRsr)
        {
            return;
        }

        try
        {
            this.commandManager.ProcessCommand(RsrOffCommand);
            this.log.Information("[BeastHelper] Crucible combat: disabled RotationSolverReborn.");
        }
        catch (Exception ex)
        {
            this.log.Debug(ex, "[BeastHelper] Crucible combat: failed to disable RotationSolverReborn.");
        }
        finally
        {
            this.weEnabledRsr = false;
        }
    }

    private static bool IsArenaTerritory(uint territoryId)
        => territoryId is >= ArenaTerritoryMin and <= ArenaTerritoryMax;

    private static bool HasStatus(IPlayerCharacter character, uint statusId)
    {
        foreach (var status in character.StatusList)
        {
            if (status.StatusId == statusId)
            {
                return true;
            }
        }

        return false;
    }

    private IGameObject? FindNearestEnemy(Vector3 playerPosition)
    {
        IGameObject? nearest = null;
        var nearestDistanceSquared = float.MaxValue;

        foreach (var obj in this.objectTable)
        {
            if (obj.ObjectKind != ObjectKind.BattleNpc || !obj.IsTargetable)
            {
                continue;
            }

            var distanceSquared = Vector3.DistanceSquared(playerPosition, obj.Position);
            if (distanceSquared > DetectionRange * DetectionRange || distanceSquared >= nearestDistanceSquared)
            {
                continue;
            }

            nearestDistanceSquared = distanceSquared;
            nearest = obj;
        }

        return nearest;
    }
}

using System.Numerics;
using BeastNav.Services;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;

namespace BeastNav.Crucible;

/// <summary>
/// The movement brain for the Crucible autopilot. Every tick it picks one goal
/// in priority order — dodge a cast, close on the combat target, or advance
/// along the recorded route — and walks the character there via vnavmesh's path
/// follower. Nothing else in BeastHelper moves the character while this runs.
/// </summary>
/// <remarks>
/// In-combat / route movement automation, against the FFXIV ToS. Gated behind
/// <see cref="Configuration.CrucibleAutoDodge"/> (dodge) and
/// <see cref="Configuration.CrucibleAutoRoute"/> (travel); both off by default.
/// Never runs during a BeastHelper destination move.
/// </remarks>
public sealed class CrucibleActuator
{
    private const float DodgeArrive = 1.5f;
    private const float MeleeRange = 3.5f;
    private const float WaypointArrive = 3f;
    private const float EngageRange = 20f;
    private static readonly TimeSpan Reissue = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan DodgeTimeout = TimeSpan.FromSeconds(6);

    private readonly Configuration configuration;
    private readonly NavmeshService navmesh;
    private readonly CrucibleRoute route;
    private readonly ICondition condition;
    private readonly IPluginLog log;

    private MoveGoal goal = MoveGoal.None;
    private Vector3 target;
    private DateTime lastIssue;
    private DateTime dodgeUntil;
    private int waypointIndex;

    public CrucibleActuator(
        Configuration configuration,
        NavmeshService navmesh,
        CrucibleRoute route,
        ICondition condition,
        IPluginLog log)
    {
        this.configuration = configuration;
        this.navmesh = navmesh;
        this.route = route;
        this.condition = condition;
        this.log = log;
    }

    public bool IsDodging => this.goal == MoveGoal.Dodge;

    public MoveGoal Goal => this.goal;

    /// <summary>Short human status for the overlay.</summary>
    public string Status { get; private set; } = "off";

    public int RouteIndex => this.waypointIndex;

    public void Tick(CrucibleState state, DodgePlan plan, CombatIntent combat)
    {
        var anyAuto = this.configuration.CrucibleAutoDodge || this.configuration.CrucibleAutoRoute;

        // Safety: never drive a near-dead character anywhere.
        if (state.HasPlayer && state.PlayerMaxHp > 0 && state.PlayerHpFraction <= 0.1f)
        {
            this.Halt("player critical");
            this.Status = "halted (HP critical)";
            return;
        }

        if (!anyAuto
            || this.navmesh.HasActiveRequest
            || !state.InCrucible
            || !state.HasPlayer
            || this.condition[ConditionFlag.BetweenAreas]
            || this.condition[ConditionFlag.BetweenAreas51]
            || this.condition[ConditionFlag.Unconscious])
        {
            this.Halt("not actionable");
            this.Status = anyAuto ? "waiting" : "off";
            return;
        }

        var now = DateTime.UtcNow;

        // 1 — dodge.
        if (this.configuration.CrucibleAutoDodge && plan.ShouldMove && !plan.NoSafeSpot)
        {
            this.Drive(MoveGoal.Dodge, plan.TargetXZ, now);
            this.dodgeUntil = this.goal == MoveGoal.Dodge && this.dodgeUntil > now ? this.dodgeUntil : now + DodgeTimeout;
            return;
        }

        if (this.goal == MoveGoal.Dodge)
        {
            var arrived = Vector3.Distance(state.PlayerPosition, this.target) <= DodgeArrive;
            if (!arrived && now < this.dodgeUntil && plan.ShouldMove)
            {
                return; // still resolving
            }

            this.Halt(arrived ? "dodge arrived" : "dodge clear");
        }

        // 2 — close on the combat target.
        if (combat.HasTarget && !combat.InActionRange && combat.TargetDistance > MeleeRange)
        {
            this.Drive(MoveGoal.Approach, combat.TargetPosition, now);
            return;
        }

        // Standing and fighting — hold position.
        if (combat.HasTarget || this.NearbyEnemy(state))
        {
            this.Halt("in combat");
            return;
        }

        // 3 — follow the route to the next pack.
        if (this.configuration.CrucibleAutoRoute && !state.InCombat)
        {
            this.FollowRoute(state, now);
            return;
        }

        this.Halt("idle");
    }

    private void FollowRoute(CrucibleState state, DateTime now)
    {
        this.route.EnsureLoaded(state.TerritoryId);
        var points = this.route.Waypoints;
        if (points.Count == 0)
        {
            this.Halt("no route");
            return;
        }

        // Advance past any waypoints we're already at or behind.
        while (this.waypointIndex < points.Count
               && Vector3.Distance(state.PlayerPosition, points[this.waypointIndex]) <= WaypointArrive)
        {
            this.waypointIndex++;
        }

        if (this.waypointIndex >= points.Count)
        {
            this.Halt("route complete");
            return;
        }

        this.Drive(MoveGoal.Route, points[this.waypointIndex], now);
    }

    private bool NearbyEnemy(CrucibleState state)
        => state.Enemies.Any(e => e.CurrentHp > 0 && e.Distance <= EngageRange);

    private void Drive(MoveGoal newGoal, Vector3 to, DateTime now)
    {
        var changed = this.goal != newGoal || Vector3.Distance(to, this.target) > 2f;
        if (changed || now - this.lastIssue >= Reissue)
        {
            this.navmesh.WalkDirectlyTo(to);
            this.target = to;
            this.lastIssue = now;
        }

        if (this.goal != newGoal)
        {
            this.goal = newGoal;
            this.log.Debug("[BeastHelper] Crucible autopilot: {Goal} → {Target}.", newGoal, to);
        }

        this.Status = newGoal switch
        {
            MoveGoal.Dodge => "dodging",
            MoveGoal.Approach => "closing on target",
            MoveGoal.Route => $"route wp {this.waypointIndex + 1}",
            _ => this.Status,
        };
    }

    private void Halt(string why)
    {
        if (this.goal == MoveGoal.None)
        {
            return;
        }

        this.goal = MoveGoal.None;
        this.navmesh.StopPath();
        this.Status = why;
        this.log.Debug("[BeastHelper] Crucible autopilot: stop ({Why}).", why);
    }
}

public enum MoveGoal
{
    None,
    Dodge,
    Approach,
    Route,
}

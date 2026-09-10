using System.Numerics;
using BeastNav.Services;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;

namespace BeastNav.Crucible;

/// <summary>
/// The opt-in movement actuator. While a dangerous cast is up it walks the
/// character to the dodge solver's safe point (straight-line, via vnavmesh's
/// path follower), then stops.
/// </summary>
/// <remarks>
/// This is automation of movement in combat. It is off by default and gated
/// behind <see cref="Configuration.CrucibleAutoDodge"/>. It never runs while a
/// BeastHelper travel move is active or while the player is not in control.
/// </remarks>
public sealed class CrucibleActuator
{
    private const float ArriveDistance = 1.5f;
    private const float RetargetDistance = 2f;
    private static readonly TimeSpan MaxDodge = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan ReissueInterval = TimeSpan.FromMilliseconds(350);

    private readonly Configuration configuration;
    private readonly NavmeshService navmesh;
    private readonly ICondition condition;
    private readonly IPluginLog log;

    private bool dodging;
    private Vector3 target;
    private DateTime lastIssue;
    private DateTime dodgeUntil;

    public CrucibleActuator(Configuration configuration, NavmeshService navmesh, ICondition condition, IPluginLog log)
    {
        this.configuration = configuration;
        this.navmesh = navmesh;
        this.condition = condition;
        this.log = log;
    }

    public bool IsDodging => this.dodging;

    public void Tick(CrucibleState state, DodgePlan plan)
    {
        if (!this.configuration.CrucibleAutoDodge)
        {
            this.Cancel("disabled");
            return;
        }

        if (this.navmesh.HasActiveRequest
            || !state.InCrucible
            || !state.HasPlayer
            || this.condition[ConditionFlag.BetweenAreas]
            || this.condition[ConditionFlag.BetweenAreas51]
            || this.condition[ConditionFlag.Unconscious])
        {
            this.Cancel("not actionable");
            return;
        }

        var now = DateTime.UtcNow;

        if (plan.ShouldMove && !plan.NoSafeSpot)
        {
            var moved = plan.TargetXZ;
            var retarget = !this.dodging || Vector3.Distance(moved, this.target) > RetargetDistance;
            if (retarget || now - this.lastIssue >= ReissueInterval)
            {
                this.target = moved;
                this.navmesh.WalkDirectlyTo(moved);
                this.lastIssue = now;
            }

            if (!this.dodging)
            {
                this.dodging = true;
                this.dodgeUntil = now + MaxDodge;
                this.log.Information(
                    "[BeastHelper] Crucible auto-dodge → {Target} ({Threats} threat(s), {Sec:0.0}s left).",
                    moved,
                    plan.ThreatCount,
                    plan.SecondsLeft);
            }

            return;
        }

        if (this.dodging)
        {
            var arrived = Vector3.Distance(state.PlayerPosition, this.target) <= ArriveDistance;
            if (arrived || now >= this.dodgeUntil)
            {
                this.Cancel(arrived ? "arrived" : "timeout");
            }
            else
            {
                this.Cancel("clear");
            }
        }
    }

    private void Cancel(string why)
    {
        if (!this.dodging)
        {
            return;
        }

        this.dodging = false;
        this.navmesh.StopPath();
        this.log.Debug("[BeastHelper] Crucible auto-dodge stopped ({Why}).", why);
    }
}
